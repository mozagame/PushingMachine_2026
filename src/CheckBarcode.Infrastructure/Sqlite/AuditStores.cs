using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;

namespace CheckBarcode.Infrastructure.Sqlite;

/// <summary>SHA-256 hash chain helpers shared by the audit and alarm stores (AUD-03).</summary>
public static class HashChain
{
    public const string Genesis = "GENESIS";
    private const char Sep = '\u001F';

    public static string Compute(params object?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            sb.Append(p switch
            {
                null => "",
                DateTime dt => SqliteDatabase.FormatUtc(dt),
                _ => Convert.ToString(p, CultureInfo.InvariantCulture),
            });
            sb.Append(Sep);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

/// <summary>Append-only, hash-chained audit trail (AUD-01..03).</summary>
public sealed class SqliteAuditStore : IAuditStore
{
    private readonly SqliteDatabase _db;
    private readonly object _gate = new();

    public SqliteAuditStore(SqliteDatabase db) => _db = db;

    private static string RecordHash(AuditRecord r) => HashChain.Compute(r.Seq, r.Utc, r.Username, r.Role, r.SessionState.ToString(),
        r.ActionCode, r.ObjectType, r.ObjectId, r.Field, r.OldValue, r.NewValue, r.Reason, r.MessageArgs, r.BatchId, r.Workstation, r.PrevHash);

    public AuditRecord Append(AuditRecord record)
    {
        lock (_gate)
        {
            return _db.InTransaction(() =>
            {
                var last = _db.QuerySingle("SELECT Seq, Hash FROM AuditTrail ORDER BY Seq DESC LIMIT 1");
                record.Seq = (last?.Long("Seq") ?? 0) + 1;
                record.PrevHash = last?.Str("Hash") ?? HashChain.Genesis;
                record.Hash = RecordHash(record);
                _db.Execute(@"INSERT INTO AuditTrail(Seq, Utc, Username, Role, SessionState, ActionCode, ObjectType, ObjectId, Field, OldValue, NewValue,
                    Reason, MessageArgs, BatchId, Workstation, PrevHash, Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                    record.Seq, record.Utc, record.Username, record.Role, record.SessionState, record.ActionCode, record.ObjectType, record.ObjectId,
                    record.Field, record.OldValue, record.NewValue, record.Reason, record.MessageArgs, record.BatchId, record.Workstation,
                    record.PrevHash, record.Hash);
                return record;
            });
        }
    }

    public void AppendSignature(long auditSeq, string username, string meaning, DateTime utc)
    {
        var auditHash = _db.QuerySingle("SELECT Hash FROM AuditTrail WHERE Seq = ?", auditSeq)?.Str("Hash")
                        ?? throw new InvalidOperationException($"Audit record {auditSeq} not found");
        var hash = HashChain.Compute(auditSeq, username, meaning, utc, auditHash);
        _db.Execute("INSERT INTO ESignature(AuditSeq, Username, Meaning, Utc, Hash) VALUES(?,?,?,?,?)", auditSeq, username, meaning, utc, hash);
    }

    public IReadOnlyList<AuditRecord> Query(AuditQuery q)
    {
        var sql = new StringBuilder(@"SELECT a.*, (SELECT s.Meaning FROM ESignature s WHERE s.AuditSeq = a.Seq ORDER BY s.Id LIMIT 1) AS SignatureMeaning
                                      FROM AuditTrail a WHERE 1=1");
        var args = new List<object?>();
        if (q.FromUtc is { } f) { sql.Append(" AND a.Utc >= ?"); args.Add(f); }
        if (q.ToUtc is { } t) { sql.Append(" AND a.Utc <= ?"); args.Add(t); }
        if (q.BatchId is { } b) { sql.Append(" AND a.BatchId = ?"); args.Add(b); }
        if (!string.IsNullOrEmpty(q.Username)) { sql.Append(" AND a.Username = ? COLLATE NOCASE"); args.Add(q.Username); }
        if (!string.IsNullOrEmpty(q.ActionPrefix)) { sql.Append(" AND a.ActionCode LIKE ?"); args.Add(q.ActionPrefix + "%"); }
        sql.Append(" ORDER BY a.Seq LIMIT ?");
        args.Add(q.Limit);
        return _db.Query(sql.ToString(), args.ToArray()).Select(Map).ToList();
    }

    public DateTime? LastUtc() => _db.QuerySingle("SELECT Utc FROM AuditTrail ORDER BY Seq DESC LIMIT 1")?.Date("Utc");

    /// <summary>Recomputes every hash; reports the first broken or missing record.</summary>
    public IntegrityResult Verify()
    {
        var prev = HashChain.Genesis;
        long expectedSeq = 1, count = 0;
        var hashes = new Dictionary<long, string>();
        foreach (var row in _db.Query("SELECT * FROM AuditTrail ORDER BY Seq"))
        {
            var r = Map(row);
            if (r.Seq != expectedSeq || r.PrevHash != prev || RecordHash(r) != r.Hash)
                return new IntegrityResult(false, count, r.Seq, "AuditTrail");
            hashes[r.Seq] = r.Hash;
            prev = r.Hash;
            expectedSeq++;
            count++;
        }
        foreach (var s in _db.Query("SELECT * FROM ESignature ORDER BY Id"))
        {
            var seq = s.Long("AuditSeq");
            if (!hashes.TryGetValue(seq, out var auditHash) ||
                HashChain.Compute(seq, s.Str("Username"), s.Str("Meaning"), s.Date("Utc"), auditHash) != s.Str("Hash"))
                return new IntegrityResult(false, count, seq, "ESignature");
        }
        return IntegrityResult.Good("AuditTrail", count);
    }

    private static AuditRecord Map(SqliteRow r) => new()
    {
        Seq = r.Long("Seq"),
        Utc = r.Date("Utc"),
        Username = r.Str("Username"),
        Role = r.Str("Role"),
        SessionState = r.Enum<SessionState>("SessionState"),
        ActionCode = r.Str("ActionCode"),
        ObjectType = r.Str("ObjectType"),
        ObjectId = r.Str("ObjectId"),
        Field = r.Str("Field"),
        OldValue = r.Str("OldValue"),
        NewValue = r.Str("NewValue"),
        Reason = r.Str("Reason"),
        MessageArgs = r.Str("MessageArgs"),
        BatchId = r.LongOrNull("BatchId"),
        Workstation = r.Str("Workstation"),
        PrevHash = r.Str("PrevHash"),
        Hash = r.Str("Hash"),
        SignatureMeaning = r.StrOrNull("SignatureMeaning"),
    };
}

/// <summary>Append-only, hash-chained alarm transitions (ALM-01).</summary>
public sealed class SqliteAlarmStore : IAlarmStore
{
    private readonly SqliteDatabase _db;
    private readonly object _gate = new();

    public SqliteAlarmStore(SqliteDatabase db) => _db = db;

    private static string RecordHash(AlarmRecord r) =>
        HashChain.Compute(r.Seq, r.AlarmId, r.Severity.ToString(), r.State.ToString(), r.Utc, r.Username, r.BatchId, r.PrevHash);

    public AlarmRecord Append(AlarmRecord record)
    {
        lock (_gate)
        {
            return _db.InTransaction(() =>
            {
                var last = _db.QuerySingle("SELECT Seq, Hash FROM AlarmEvent ORDER BY Seq DESC LIMIT 1");
                record.Seq = (last?.Long("Seq") ?? 0) + 1;
                record.PrevHash = last?.Str("Hash") ?? HashChain.Genesis;
                record.Hash = RecordHash(record);
                _db.Execute("INSERT INTO AlarmEvent(Seq, AlarmId, Severity, State, Utc, Username, BatchId, PrevHash, Hash) VALUES(?,?,?,?,?,?,?,?,?)",
                    record.Seq, record.AlarmId, record.Severity, record.State, record.Utc, record.Username, record.BatchId, record.PrevHash, record.Hash);
                return record;
            });
        }
    }

    public IReadOnlyList<AlarmRecord> Query(DateTime? fromUtc, DateTime? toUtc, long? batchId, int limit = 100_000)
    {
        var sql = new StringBuilder("SELECT * FROM AlarmEvent WHERE 1=1");
        var args = new List<object?>();
        if (fromUtc is { } f) { sql.Append(" AND Utc >= ?"); args.Add(f); }
        if (toUtc is { } t) { sql.Append(" AND Utc <= ?"); args.Add(t); }
        if (batchId is { } b) { sql.Append(" AND BatchId = ?"); args.Add(b); }
        sql.Append(" ORDER BY Seq LIMIT ?");
        args.Add(limit);
        return _db.Query(sql.ToString(), args.ToArray()).Select(Map).ToList();
    }

    public IReadOnlyList<AlarmRecord> LatestPerAlarm() =>
        _db.Query("SELECT a.* FROM AlarmEvent a JOIN (SELECT AlarmId, MAX(Seq) AS MaxSeq FROM AlarmEvent GROUP BY AlarmId) m ON a.Seq = m.MaxSeq ORDER BY a.Seq")
            .Select(Map).ToList();

    public IntegrityResult Verify()
    {
        var prev = HashChain.Genesis;
        long expected = 1, count = 0;
        foreach (var row in _db.Query("SELECT * FROM AlarmEvent ORDER BY Seq"))
        {
            var r = Map(row);
            if (r.Seq != expected || r.PrevHash != prev || RecordHash(r) != r.Hash) return new IntegrityResult(false, count, r.Seq, "AlarmEvent");
            prev = r.Hash;
            expected++;
            count++;
        }
        return IntegrityResult.Good("AlarmEvent", count);
    }

    private static AlarmRecord Map(SqliteRow r) => new()
    {
        Seq = r.Long("Seq"),
        AlarmId = r.Str("AlarmId"),
        Severity = r.Enum<AlarmSeverity>("Severity"),
        State = r.Enum<AlarmState>("State"),
        Utc = r.Date("Utc"),
        Username = r.Str("Username"),
        BatchId = r.LongOrNull("BatchId"),
        PrevHash = r.Str("PrevHash"),
        Hash = r.Str("Hash"),
    };
}
