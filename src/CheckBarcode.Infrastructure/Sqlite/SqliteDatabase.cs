using System.Globalization;
using CheckBarcode.Application.Ports;
using static CheckBarcode.Infrastructure.Sqlite.SqliteNative;

namespace CheckBarcode.Infrastructure.Sqlite;

public sealed class SqliteException : Exception
{
    public int ResultCode { get; }
    public SqliteException(int code, string message) : base($"SQLite error {code}: {message}") => ResultCode = code;
}

/// <summary>One result row; values are accessed by column name.</summary>
public sealed class SqliteRow
{
    private readonly Dictionary<string, object?> _values;
    public SqliteRow(Dictionary<string, object?> values) => _values = values;

    public object? this[string column] => _values.TryGetValue(column, out var v) ? v : null;
    public bool IsNull(string column) => this[column] is null;
    public string Str(string column) => this[column] switch { null => "", string s => s, var o => Convert.ToString(o, CultureInfo.InvariantCulture) ?? "" };
    public string? StrOrNull(string column) => this[column] is null ? null : Str(column);
    public long Long(string column) => this[column] switch { null => 0, long l => l, double d => (long)d, string s => long.Parse(s, CultureInfo.InvariantCulture), var o => Convert.ToInt64(o, CultureInfo.InvariantCulture) };
    public long? LongOrNull(string column) => this[column] is null ? null : Long(column);
    public int Int(string column) => (int)Long(column);
    public double? DoubleOrNull(string column) => this[column] switch { null => null, double d => d, long l => l, string s => double.Parse(s, CultureInfo.InvariantCulture), var o => Convert.ToDouble(o, CultureInfo.InvariantCulture) };
    public bool Bool(string column) => Long(column) != 0;
    public DateTime Date(string column) => SqliteDatabase.ParseUtc(Str(column));
    public DateTime? DateOrNull(string column) => this[column] is null || Str(column).Length == 0 ? null : Date(column);
    public T Enum<T>(string column) where T : struct, System.Enum => System.Enum.Parse<T>(Str(column), true);
}

/// <summary>
/// Single serialized SQLite connection (WAL, synchronous=FULL, foreign keys on).
/// Positional parameters: <c>Execute("INSERT INTO t(a,b) VALUES(?,?)", a, b)</c>.
/// </summary>
public sealed class SqliteDatabase : ITransactionRunner, IDisposable
{
    private readonly object _gate = new();
    private IntPtr _db;
    private int _txDepth;

    public string Path { get; }

    public SqliteDatabase(string path, bool readOnly = false)
    {
        Path = path;
        EnsureLoaded();
        if (readOnly)
        {
            var ro = sqlite3_open_v2(Utf8Z(path), out _db, 0x00000001 | SQLITE_OPEN_FULLMUTEX, IntPtr.Zero);
            if (ro != SQLITE_OK) throw new SqliteException(ro, "cannot open " + path);
            sqlite3_busy_timeout(_db, 5000);
            return;
        }
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var rc = sqlite3_open_v2(Utf8Z(path), out _db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, IntPtr.Zero);
        if (rc != SQLITE_OK) throw new SqliteException(rc, "cannot open " + path);
        sqlite3_busy_timeout(_db, 5000);
        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA synchronous=FULL");
        Execute("PRAGMA foreign_keys=ON");
    }

    public static string Version => libversion();

    public static string FormatUtc(DateTime utc) =>
        (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public static DateTime ParseUtc(string s) =>
        DateTime.ParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public int Execute(string sql, params object?[] args)
    {
        lock (_gate)
        {
            var stmt = Prepare(sql, args);
            try
            {
                var rc = sqlite3_step(stmt);
                while (rc == SQLITE_ROW) rc = sqlite3_step(stmt);
                if (rc != SQLITE_DONE) throw new SqliteException(rc, ErrorMessage(_db) + " | " + sql);
                return sqlite3_changes(_db);
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }
    }

    /// <summary>Runs an INSERT and returns the new rowid.</summary>
    public long Insert(string sql, params object?[] args)
    {
        lock (_gate)
        {
            Execute(sql, args);
            return sqlite3_last_insert_rowid(_db);
        }
    }

    public List<SqliteRow> Query(string sql, params object?[] args)
    {
        lock (_gate)
        {
            var stmt = Prepare(sql, args);
            try
            {
                var rows = new List<SqliteRow>();
                var cols = sqlite3_column_count(stmt);
                var names = new string[cols];
                for (var i = 0; i < cols; i++) names[i] = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(sqlite3_column_name(stmt, i)) ?? $"c{i}";
                int rc;
                while ((rc = sqlite3_step(stmt)) == SQLITE_ROW)
                {
                    var values = new Dictionary<string, object?>(cols, StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < cols; i++) values[names[i]] = ReadColumn(stmt, i);
                    rows.Add(new SqliteRow(values));
                }
                if (rc != SQLITE_DONE) throw new SqliteException(rc, ErrorMessage(_db) + " | " + sql);
                return rows;
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }
    }

    public SqliteRow? QuerySingle(string sql, params object?[] args) => Query(sql, args).FirstOrDefault();

    public object? Scalar(string sql, params object?[] args)
    {
        lock (_gate)
        {
            var stmt = Prepare(sql, args);
            try
            {
                return sqlite3_step(stmt) == SQLITE_ROW ? ReadColumn(stmt, 0) : null;
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }
    }

    public long ScalarLong(string sql, params object?[] args) => Scalar(sql, args) switch { null => 0, long l => l, var o => Convert.ToInt64(o, CultureInfo.InvariantCulture) };

    /// <summary>Atomic unit of work; nested calls join the outer transaction.</summary>
    public T InTransaction<T>(Func<T> work)
    {
        lock (_gate)
        {
            if (_txDepth > 0)
            {
                _txDepth++;
                try { return work(); } finally { _txDepth--; }
            }
            Execute("BEGIN IMMEDIATE");
            _txDepth = 1;
            try
            {
                var result = work();
                Execute("COMMIT");
                return result;
            }
            catch
            {
                try { Execute("ROLLBACK"); } catch { /* keep original exception */ }
                throw;
            }
            finally
            {
                _txDepth = 0;
            }
        }
    }

    public void InTransaction(Action work) => InTransaction(() => { work(); return 0; });

    /// <summary>Online backup to another file (sqlite3_backup API, safe while running).</summary>
    public void BackupTo(string destinationPath)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destinationPath))!);
            var rc = sqlite3_open_v2(Utf8Z(destinationPath), out var dest, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, IntPtr.Zero);
            if (rc != SQLITE_OK) throw new SqliteException(rc, "cannot open backup " + destinationPath);
            try
            {
                var main = Utf8Z("main");
                var backup = sqlite3_backup_init(dest, main, _db, main);
                if (backup == IntPtr.Zero) throw new SqliteException(-1, ErrorMessage(dest));
                sqlite3_backup_step(backup, -1);
                rc = sqlite3_backup_finish(backup);
                if (rc != SQLITE_OK) throw new SqliteException(rc, ErrorMessage(dest));
            }
            finally
            {
                sqlite3_close_v2(dest);
            }
        }
    }

    private IntPtr Prepare(string sql, object?[] args)
    {
        var rc = sqlite3_prepare_v2(_db, Utf8Z(sql), -1, out var stmt, out _);
        if (rc != SQLITE_OK) throw new SqliteException(rc, ErrorMessage(_db) + " | " + sql);
        for (var i = 0; i < args.Length; i++) Bind(stmt, i + 1, args[i]);
        return stmt;
    }

    private static unsafe void Bind(IntPtr stmt, int index, object? value)
    {
        switch (value)
        {
            case null:
                sqlite3_bind_null(stmt, index);
                break;
            case string s:
                // An empty array pins to a null pointer, which SQLite would store as NULL: always pass a real buffer.
                var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
                fixed (byte* p = bytes) sqlite3_bind_text(stmt, index, p, bytes.Length - 1, SQLITE_TRANSIENT);
                break;
            case bool b:
                sqlite3_bind_int64(stmt, index, b ? 1 : 0);
                break;
            case int or long or short or byte or ushort or uint:
                sqlite3_bind_int64(stmt, index, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case double or float or decimal:
                sqlite3_bind_double(stmt, index, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case DateTime dt:
                Bind(stmt, index, FormatUtc(dt));
                break;
            case Enum e:
                Bind(stmt, index, e.ToString());
                break;
            case byte[] blob:
                var buffer = blob.Length == 0 ? new byte[1] : blob;
                fixed (byte* p = buffer) sqlite3_bind_blob(stmt, index, p, blob.Length, SQLITE_TRANSIENT);
                break;
            default:
                Bind(stmt, index, Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static unsafe object? ReadColumn(IntPtr stmt, int col)
    {
        switch (sqlite3_column_type(stmt, col))
        {
            case SQLITE_INTEGER: return sqlite3_column_int64(stmt, col);
            case SQLITE_FLOAT: return sqlite3_column_double(stmt, col);
            case SQLITE_TEXT:
                var p = sqlite3_column_text(stmt, col);
                return PtrToUtf8(p, sqlite3_column_bytes(stmt, col));
            case SQLITE_BLOB:
                var len = sqlite3_column_bytes(stmt, col);
                var data = new byte[len];
                if (len > 0) System.Runtime.InteropServices.Marshal.Copy(sqlite3_column_blob(stmt, col), data, 0, len);
                return data;
            default: return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_db == IntPtr.Zero) return;
            sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }
    }
}
