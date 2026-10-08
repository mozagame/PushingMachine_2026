using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure.Sqlite;
using CheckBarcode.Tests.Framework;

namespace CheckBarcode.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cbdb_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SqliteDatabase _db;

    public StorageTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new SqliteDatabase(Path.Combine(_dir, "t.db"));
        Schema.Migrate(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AuditRecord Rec(string code, string user = "u1") => new()
    {
        Utc = DateTime.UtcNow, Username = user, Role = Roles.Operator, ActionCode = code, ObjectType = "T", ObjectId = "1",
        Field = "F", OldValue = "a", NewValue = "b", Reason = "r", Workstation = "W",
    };

    [Test("SYS")]
    public void Migration_is_idempotent_and_versioned()
    {
        Assert.Equal(Schema.LatestVersion, Schema.Migrate(_db));
        Assert.Equal((long)Schema.LatestVersion, _db.ScalarLong("SELECT MAX(Version) FROM SchemaVersion"));
        Assert.Equal(1L, _db.ScalarLong("SELECT COUNT(*) FROM SchemaVersion"));
    }

    [Test]
    public void Unicode_text_round_trips()
    {
        _db.Execute("INSERT INTO Setting(Key, Value) VALUES(?, ?)", "k", "Trạng thái máy: Đang chạy ✓");
        Assert.Equal("Trạng thái máy: Đang chạy ✓", new SqliteSettingsStore(_db).Get("k"));
    }

    [Test("AUD-03")]
    public void Audit_hash_chain_links_records()
    {
        var store = new SqliteAuditStore(_db);
        var a = store.Append(Rec("A"));
        var b = store.Append(Rec("B"));
        Assert.Equal(1L, a.Seq);
        Assert.Equal(HashChain.Genesis, a.PrevHash);
        Assert.Equal(a.Hash, b.PrevHash);
        var v = store.Verify();
        Assert.True(v.Ok);
        Assert.Equal(2L, v.Checked);
    }

    [Test("AUD-01")]
    public void Audit_table_rejects_update_and_delete()
    {
        var store = new SqliteAuditStore(_db);
        store.Append(Rec("A"));
        var ex = Assert.Throws<SqliteException>(() => _db.Execute("UPDATE AuditTrail SET Username = 'hacker'"));
        Assert.Contains("append-only", ex.Message);
        Assert.Throws<SqliteException>(() => _db.Execute("DELETE FROM AuditTrail"));
        new SqliteAlarmStore(_db).Append(new AlarmRecord { AlarmId = "X", Utc = DateTime.UtcNow, Username = "u" });
        Assert.Throws<SqliteException>(() => _db.Execute("DELETE FROM AlarmEvent"));
    }

    [Test("AUD-03")]
    public void Tampering_is_detected_even_when_triggers_are_dropped()
    {
        var store = new SqliteAuditStore(_db);
        for (var i = 0; i < 5; i++) store.Append(Rec("A" + i));
        _db.Execute("DROP TRIGGER TR_AuditTrail_NoUpdate");
        _db.Execute("UPDATE AuditTrail SET NewValue = 'forged' WHERE Seq = 3");
        var v = store.Verify();
        Assert.False(v.Ok);
        Assert.Equal(3L, v.FirstBrokenSeq);
    }

    [Test("AUD-03")]
    public void Deleted_record_breaks_the_chain()
    {
        var store = new SqliteAuditStore(_db);
        for (var i = 0; i < 4; i++) store.Append(Rec("A" + i));
        _db.Execute("DROP TRIGGER TR_AuditTrail_NoDelete");
        _db.Execute("DELETE FROM AuditTrail WHERE Seq = 2");
        var v = store.Verify();
        Assert.False(v.Ok);
        Assert.Equal(3L, v.FirstBrokenSeq);
    }

    [Test("AUD-03", "USR")]
    public void Forged_signature_is_detected()
    {
        var store = new SqliteAuditStore(_db);
        var r = store.Append(Rec("RECIPE_EDIT"));
        store.AppendSignature(r.Seq, "sup1", "Approve", DateTime.UtcNow);
        Assert.True(store.Verify().Ok);
        _db.Execute("DROP TRIGGER TR_ESignature_NoUpdate");
        _db.Execute("UPDATE ESignature SET Username = 'boss'");
        var v = store.Verify();
        Assert.False(v.Ok);
        Assert.Equal("ESignature", v.Table);
    }

    [Test("AUD-02")]
    public void Audit_query_filters_by_time_batch_and_user()
    {
        var store = new SqliteAuditStore(_db);
        var t0 = DateTime.UtcNow;
        var r1 = Rec("A", "alice"); r1.Utc = t0.AddMinutes(-10); r1.BatchId = 7; store.Append(r1);
        var r2 = Rec("B", "bob"); r2.Utc = t0; store.Append(r2);
        Assert.Equal(1, store.Query(new AuditQuery(BatchId: 7)).Count);
        Assert.Equal(1, store.Query(new AuditQuery(FromUtc: t0.AddMinutes(-1))).Count);
        Assert.Equal(1, store.Query(new AuditQuery(Username: "BOB")).Count);
    }

    [Test("ALM-01")]
    public void Alarm_chain_and_latest_state()
    {
        var store = new SqliteAlarmStore(_db);
        store.Append(new AlarmRecord { AlarmId = "A", State = AlarmState.Active, Utc = DateTime.UtcNow, Username = "x" });
        store.Append(new AlarmRecord { AlarmId = "B", State = AlarmState.Active, Utc = DateTime.UtcNow, Username = "x" });
        store.Append(new AlarmRecord { AlarmId = "A", State = AlarmState.Acknowledged, Utc = DateTime.UtcNow, Username = "x" });
        var latest = store.LatestPerAlarm().ToDictionary(r => r.AlarmId, r => r.State);
        Assert.Equal(AlarmState.Acknowledged, latest["A"]);
        Assert.Equal(AlarmState.Active, latest["B"]);
        Assert.True(store.Verify().Ok);
    }

    [Test("SYS-03")]
    public void Online_backup_creates_a_readable_copy()
    {
        new SqliteAuditStore(_db).Append(Rec("A"));
        var path = Path.Combine(_dir, "backup", "b.db");
        _db.BackupTo(path);
        using var copy = new SqliteDatabase(path);
        Assert.Equal(1L, copy.ScalarLong("SELECT COUNT(*) FROM AuditTrail"));
        Assert.True(new SqliteAuditStore(copy).Verify().Ok);
    }

    [Test("REC-03")]
    public void Recipe_name_unique_only_among_active()
    {
        var repo = new SqliteRecipeRepository(_db);
        var r = new Recipe { Name = "A", Type = CameraRole.Box, Barcode = "1", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        r.Id = repo.Insert(r);
        Assert.Throws<SqliteException>(() => repo.Insert(new Recipe { Name = "a", Type = CameraRole.Box, Barcode = "2", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow }));
        r.Status = RecordStatus.Deleted;
        repo.Update(r);
        repo.Insert(new Recipe { Name = "A", Type = CameraRole.Box, Barcode = "3", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
        Assert.Equal(1, repo.ListActive().Count);
    }

    [Test]
    public void Transaction_rolls_back_on_error()
    {
        Assert.Throws<InvalidOperationException>(() => _db.InTransaction(() =>
        {
            _db.Execute("INSERT INTO Setting(Key, Value) VALUES('x', '1')");
            throw new InvalidOperationException("boom");
        }));
        Assert.Equal(0L, _db.ScalarLong("SELECT COUNT(*) FROM Setting WHERE Key = 'x'"));
    }
}
