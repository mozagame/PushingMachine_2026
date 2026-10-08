using System.Reflection;
using System.Runtime.InteropServices;

namespace CheckBarcode.Infrastructure.Sqlite;

/// <summary>
/// Thin P/Invoke binding to the SQLite C API. No NuGet package is needed:
/// Windows loads e_sqlite3.dll / sqlite3.dll / SQLite.Interop.dll from the application folder,
/// Linux (CI, tests) loads libsqlite3.so.0.
/// </summary>
internal static unsafe class SqliteNative
{
    private const string Lib = "sqlite3";

    public const int SQLITE_OK = 0;
    public const int SQLITE_BUSY = 5;
    public const int SQLITE_ROW = 100;
    public const int SQLITE_DONE = 101;
    public const int SQLITE_INTEGER = 1;
    public const int SQLITE_FLOAT = 2;
    public const int SQLITE_TEXT = 3;
    public const int SQLITE_BLOB = 4;
    public const int SQLITE_NULL = 5;

    public const int SQLITE_OPEN_READWRITE = 0x00000002;
    public const int SQLITE_OPEN_CREATE = 0x00000004;
    public const int SQLITE_OPEN_FULLMUTEX = 0x00010000;

    public static readonly IntPtr SQLITE_TRANSIENT = new(-1);

    static SqliteNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(SqliteNative).Assembly, Resolve);
    }

    /// <summary>Forces the static constructor (resolver registration) before the first call.</summary>
    public static void EnsureLoaded() => _ = libversion();

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        var dir = AppContext.BaseDirectory;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "e_sqlite3.dll", "sqlite3.dll", "SQLite.Interop.dll", Path.Combine("x64", "SQLite.Interop.dll") }
            : OperatingSystem.IsMacOS()
                ? new[] { "libsqlite3.dylib", "/usr/lib/libsqlite3.dylib" }
                : new[] { "libsqlite3.so.0", "libsqlite3.so", "libe_sqlite3.so" };
        foreach (var c in candidates)
        {
            var full = Path.IsPathRooted(c) ? c : Path.Combine(dir, c);
            if (File.Exists(full) && NativeLibrary.TryLoad(full, out var h)) return h;
            if (NativeLibrary.TryLoad(c, assembly, path, out h)) return h;
        }
        return IntPtr.Zero;
    }

    [DllImport(Lib, EntryPoint = "sqlite3_libversion")] private static extern IntPtr libversion_ptr();
    public static string libversion() => Marshal.PtrToStringAnsi(libversion_ptr()) ?? "";

    [DllImport(Lib)] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, out IntPtr tail);
    [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_reset(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_bind_parameter_index(IntPtr stmt, byte[] name);
    [DllImport(Lib)] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport(Lib)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib)] public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);
    [DllImport(Lib)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte* text, int n, IntPtr destructor);
    [DllImport(Lib)] public static extern int sqlite3_bind_blob(IntPtr stmt, int index, byte* blob, int n, IntPtr destructor);
    [DllImport(Lib)] public static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern double sqlite3_column_double(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern long sqlite3_last_insert_rowid(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_changes(IntPtr db);
    [DllImport(Lib)] public static extern IntPtr sqlite3_backup_init(IntPtr dest, byte[] destName, IntPtr source, byte[] sourceName);
    [DllImport(Lib)] public static extern int sqlite3_backup_step(IntPtr backup, int pages);
    [DllImport(Lib)] public static extern int sqlite3_backup_finish(IntPtr backup);

    public static byte[] Utf8Z(string s)
    {
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(s) + 1];
        System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }

    public static string? PtrToUtf8(IntPtr p, int length) =>
        p == IntPtr.Zero ? null : System.Text.Encoding.UTF8.GetString((byte*)p, length);

    public static string ErrorMessage(IntPtr db) => Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "unknown error";
}
