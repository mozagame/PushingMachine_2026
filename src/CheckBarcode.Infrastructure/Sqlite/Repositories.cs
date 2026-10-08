using System.Text;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;

namespace CheckBarcode.Infrastructure.Sqlite;

public sealed class SqliteUserRepository : IUserRepository
{
    private readonly SqliteDatabase _db;
    public SqliteUserRepository(SqliteDatabase db) => _db = db;

    public User? FindByUsername(string username) => _db.QuerySingle("SELECT * FROM User WHERE Username = ? COLLATE NOCASE", username) is { } r ? Map(r) : null;
    public User? FindById(long id) => _db.QuerySingle("SELECT * FROM User WHERE Id = ?", id) is { } r ? Map(r) : null;
    public IReadOnlyList<User> List() => _db.Query("SELECT * FROM User ORDER BY Username").Select(Map).ToList();

    public long Insert(User u) => _db.Insert(@"INSERT INTO User(Username, FullName, RoleCode, Status, PasswordHash, PasswordSalt, PasswordChangedUtc,
            MustChangePassword, FailedCount, LockedUtc, LockReason, CreatedUtc, CreatedBy) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)",
        u.Username, u.FullName, u.RoleCode, u.Status, u.PasswordHash, u.PasswordSalt, u.PasswordChangedUtc, u.MustChangePassword,
        u.FailedCount, u.LockedUtc, u.LockReason, u.CreatedUtc, u.CreatedBy);

    public void Update(User u) => _db.Execute(@"UPDATE User SET FullName=?, RoleCode=?, Status=?, PasswordHash=?, PasswordSalt=?, PasswordChangedUtc=?,
            MustChangePassword=?, FailedCount=?, LockedUtc=?, LockReason=? WHERE Id=?",
        u.FullName, u.RoleCode, u.Status, u.PasswordHash, u.PasswordSalt, u.PasswordChangedUtc, u.MustChangePassword, u.FailedCount,
        u.LockedUtc, u.LockReason, u.Id);

    public void AddPasswordHistory(long userId, string hash, string salt, DateTime utc) =>
        _db.Execute("INSERT INTO PasswordHistory(UserId, Hash, Salt, CreatedUtc) VALUES(?,?,?,?)", userId, hash, salt, utc);

    public IReadOnlyList<(string Hash, string Salt)> RecentPasswords(long userId, int count) =>
        count <= 0 ? Array.Empty<(string, string)>() :
        _db.Query("SELECT Hash, Salt FROM PasswordHistory WHERE UserId = ? ORDER BY Id DESC LIMIT ?", userId, count)
            .Select(r => (r.Str("Hash"), r.Str("Salt"))).ToList();

    private static User Map(SqliteRow r) => new()
    {
        Id = r.Long("Id"),
        Username = r.Str("Username"),
        FullName = r.Str("FullName"),
        RoleCode = r.Str("RoleCode"),
        Status = r.Enum<UserStatus>("Status"),
        PasswordHash = r.Str("PasswordHash"),
        PasswordSalt = r.Str("PasswordSalt"),
        PasswordChangedUtc = r.Date("PasswordChangedUtc"),
        MustChangePassword = r.Bool("MustChangePassword"),
        FailedCount = r.Int("FailedCount"),
        LockedUtc = r.DateOrNull("LockedUtc"),
        LockReason = r.Enum<LockReason>("LockReason"),
        CreatedUtc = r.Date("CreatedUtc"),
        CreatedBy = r.Str("CreatedBy"),
    };
}

public sealed class SqliteRoleRepository : IRoleRepository
{
    private readonly SqliteDatabase _db;
    public SqliteRoleRepository(SqliteDatabase db) => _db = db;

    public IReadOnlyList<Role> List() => _db.Query("SELECT * FROM Role ORDER BY rowid").Select(r => new Role
    {
        Code = r.Str("Code"),
        NameVi = r.Str("NameVi"),
        NameEn = r.Str("NameEn"),
        IsSystem = r.Bool("IsSystem"),
    }).ToList();

    public void Upsert(Role role)
    {
        if (_db.Execute("UPDATE Role SET NameVi=?, NameEn=?, IsSystem=? WHERE Code=?", role.NameVi, role.NameEn, role.IsSystem, role.Code) == 0)
            _db.Execute("INSERT INTO Role(Code, NameVi, NameEn, IsSystem) VALUES(?,?,?,?)", role.Code, role.NameVi, role.NameEn, role.IsSystem);
    }

    public IReadOnlyCollection<string> PermissionsOf(string roleCode) =>
        _db.Query("SELECT Permission FROM RolePermission WHERE RoleCode = ? ORDER BY Permission", roleCode).Select(r => r.Str("Permission")).ToList();

    public void SetPermissions(string roleCode, IEnumerable<string> permissions) => _db.InTransaction(() =>
    {
        _db.Execute("DELETE FROM RolePermission WHERE RoleCode = ?", roleCode);
        foreach (var p in permissions.Distinct(StringComparer.OrdinalIgnoreCase))
            _db.Execute("INSERT INTO RolePermission(RoleCode, Permission) VALUES(?,?)", roleCode, p);
    });
}

public sealed class SqliteRecipeRepository : IRecipeRepository
{
    private readonly SqliteDatabase _db;
    public SqliteRecipeRepository(SqliteDatabase db) => _db = db;

    public Recipe? Find(long id) => _db.QuerySingle("SELECT * FROM Recipe WHERE Id = ?", id) is { } r ? Map(r) : null;

    public Recipe? FindActiveByName(string name, CameraRole type) =>
        _db.QuerySingle("SELECT * FROM Recipe WHERE Name = ? COLLATE NOCASE AND Type = ? AND Status = 'Active'", name, type) is { } r ? Map(r) : null;

    public IReadOnlyList<Recipe> ListActive(CameraRole? type = null) => type is { } t
        ? _db.Query("SELECT * FROM Recipe WHERE Status = 'Active' AND Type = ? ORDER BY Name COLLATE NOCASE", t).Select(Map).ToList()
        : _db.Query("SELECT * FROM Recipe WHERE Status = 'Active' ORDER BY Type, Name COLLATE NOCASE").Select(Map).ToList();

    public long Insert(Recipe r) => _db.Insert(@"INSERT INTO Recipe(Name, Type, Barcode, BarcodeFormat, SpeedSetpoint, SpeedTolerance, TempSetpoint, TempTolerance,
            Version, Status, CreatedUtc, UpdatedUtc) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
        r.Name, r.Type, r.Barcode, r.BarcodeFormat, r.SpeedSetpoint, r.SpeedTolerance, r.TempSetpoint, r.TempTolerance, r.Version, r.Status, r.CreatedUtc, r.UpdatedUtc);

    public void Update(Recipe r) => _db.Execute(@"UPDATE Recipe SET Name=?, Type=?, Barcode=?, BarcodeFormat=?, SpeedSetpoint=?, SpeedTolerance=?, TempSetpoint=?,
            TempTolerance=?, Version=?, Status=?, UpdatedUtc=? WHERE Id=?",
        r.Name, r.Type, r.Barcode, r.BarcodeFormat, r.SpeedSetpoint, r.SpeedTolerance, r.TempSetpoint, r.TempTolerance, r.Version, r.Status, r.UpdatedUtc, r.Id);

    private static Recipe Map(SqliteRow r) => new()
    {
        Id = r.Long("Id"),
        Name = r.Str("Name"),
        Type = r.Enum<CameraRole>("Type"),
        Barcode = r.Str("Barcode"),
        BarcodeFormat = r.Enum<BarcodeFormat>("BarcodeFormat"),
        SpeedSetpoint = r.DoubleOrNull("SpeedSetpoint"),
        SpeedTolerance = r.DoubleOrNull("SpeedTolerance"),
        TempSetpoint = r.DoubleOrNull("TempSetpoint"),
        TempTolerance = r.DoubleOrNull("TempTolerance"),
        Version = r.Int("Version"),
        Status = r.Enum<RecordStatus>("Status"),
        CreatedUtc = r.Date("CreatedUtc"),
        UpdatedUtc = r.Date("UpdatedUtc"),
    };
}

public sealed class SqliteBatchRepository : IBatchRepository
{
    private readonly SqliteDatabase _db;
    public SqliteBatchRepository(SqliteDatabase db) => _db = db;

    public Batch? Find(long id) => _db.QuerySingle("SELECT * FROM Batch WHERE Id = ?", id) is { } r ? Map(r) : null;
    public Batch? FindByNumber(string batchNo) => _db.QuerySingle("SELECT * FROM Batch WHERE BatchNo = ? COLLATE NOCASE", batchNo.Trim()) is { } r ? Map(r) : null;
    public Batch? FindOpen() => _db.QuerySingle("SELECT * FROM Batch WHERE Status <> 'Completed' ORDER BY Id DESC LIMIT 1") is { } r ? Map(r) : null;

    public IReadOnlyList<Batch> List(DateTime? fromUtc, DateTime? toUtc)
    {
        var sql = new StringBuilder("SELECT * FROM Batch WHERE 1=1");
        var args = new List<object?>();
        if (fromUtc is { } f) { sql.Append(" AND IFNULL(EndUtc, '9999') >= ?"); args.Add(f); }
        if (toUtc is { } t) { sql.Append(" AND IFNULL(StartUtc, CreatedUtc) <= ?"); args.Add(t); }
        sql.Append(" ORDER BY Id");
        return _db.Query(sql.ToString(), args.ToArray()).Select(Map).ToList();
    }

    public long Insert(Batch b) => _db.Insert(@"INSERT INTO Batch(BatchNo, ProductName, BoxRecipeId, BoxRecipeVersion, LeafletRecipeId, LeafletRecipeVersion,
            Status, CreatedUtc, StartUtc, EndUtc, CreatedBy, EndedBy) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
        b.BatchNo, b.ProductName, b.BoxRecipeId, b.BoxRecipeVersion, b.LeafletRecipeId, b.LeafletRecipeVersion, b.Status, b.CreatedUtc,
        b.StartUtc, b.EndUtc, b.CreatedBy, b.EndedBy);

    public void Update(Batch b) => _db.Execute("UPDATE Batch SET Status=?, StartUtc=?, EndUtc=?, EndedBy=? WHERE Id=?",
        b.Status, b.StartUtc, b.EndUtc, b.EndedBy, b.Id);

    public BatchCounter GetCounter(long batchId, CameraRole camera)
    {
        var r = _db.QuerySingle("SELECT * FROM BatchCounter WHERE BatchId = ? AND Camera = ?", batchId, camera);
        return r is null
            ? new BatchCounter { BatchId = batchId, Camera = camera }
            : new BatchCounter { BatchId = batchId, Camera = camera, Scanned = r.Long("Scanned"), Pass = r.Long("Pass"), Fail = r.Long("Fail"), NoRead = r.Long("NoRead") };
    }

    public void SaveCounter(BatchCounter c)
    {
        if (_db.Execute("UPDATE BatchCounter SET Scanned=?, Pass=?, Fail=?, NoRead=? WHERE BatchId=? AND Camera=?", c.Scanned, c.Pass, c.Fail, c.NoRead, c.BatchId, c.Camera) == 0)
            _db.Execute("INSERT INTO BatchCounter(BatchId, Camera, Scanned, Pass, Fail, NoRead) VALUES(?,?,?,?,?,?)", c.BatchId, c.Camera, c.Scanned, c.Pass, c.Fail, c.NoRead);
    }

    public long AddOperator(BatchOperator op) =>
        _db.Insert("INSERT INTO BatchOperator(BatchId, Username, FromUtc, ToUtc) VALUES(?,?,?,?)", op.BatchId, op.Username, op.FromUtc, op.ToUtc);

    /// <summary>Closes open operator sessions; username "*" closes all of the batch.</summary>
    public void CloseOperator(long batchId, string username, DateTime toUtc)
    {
        if (username == "*") _db.Execute("UPDATE BatchOperator SET ToUtc = ? WHERE BatchId = ? AND ToUtc IS NULL", toUtc, batchId);
        else _db.Execute("UPDATE BatchOperator SET ToUtc = ? WHERE BatchId = ? AND Username = ? COLLATE NOCASE AND ToUtc IS NULL", toUtc, batchId, username);
    }

    public IReadOnlyList<BatchOperator> Operators(long batchId) =>
        _db.Query("SELECT * FROM BatchOperator WHERE BatchId = ? ORDER BY Id", batchId).Select(r => new BatchOperator
        {
            Id = r.Long("Id"),
            BatchId = r.Long("BatchId"),
            Username = r.Str("Username"),
            FromUtc = r.Date("FromUtc"),
            ToUtc = r.DateOrNull("ToUtc"),
        }).ToList();

    public long AddResult(InspectionResult x) =>
        _db.Insert("INSERT INTO InspectionResult(BatchId, Camera, Utc, ReadString, Outcome, ImagePath) VALUES(?,?,?,?,?,?)",
            x.BatchId, x.Camera, x.Utc, x.ReadString, x.Outcome, x.ImagePath);

    public IReadOnlyList<InspectionResult> Results(long batchId, CameraRole? camera, InspectionOutcome? outcome)
    {
        var sql = new StringBuilder("SELECT * FROM InspectionResult WHERE BatchId = ?");
        var args = new List<object?> { batchId };
        if (camera is { } c) { sql.Append(" AND Camera = ?"); args.Add(c); }
        if (outcome is { } o) { sql.Append(" AND Outcome = ?"); args.Add(o); }
        sql.Append(" ORDER BY Id");
        return _db.Query(sql.ToString(), args.ToArray()).Select(MapResult).ToList();
    }

    public IReadOnlyList<InspectionResult> SearchResults(DateTime? fromUtc, DateTime? toUtc, string? barcode, InspectionOutcome? outcome, int limit)
    {
        var sql = new StringBuilder("SELECT * FROM InspectionResult WHERE 1=1");
        var args = new List<object?>();
        if (fromUtc is { } f) { sql.Append(" AND Utc >= ?"); args.Add(f); }
        if (toUtc is { } t) { sql.Append(" AND Utc <= ?"); args.Add(t); }
        if (!string.IsNullOrWhiteSpace(barcode)) { sql.Append(" AND ReadString LIKE ?"); args.Add("%" + barcode.Trim() + "%"); }
        if (outcome is { } o) { sql.Append(" AND Outcome = ?"); args.Add(o); }
        sql.Append(" ORDER BY Id DESC LIMIT ?");
        args.Add(limit);
        return _db.Query(sql.ToString(), args.ToArray()).Select(MapResult).ToList();
    }

    private static InspectionResult MapResult(SqliteRow r) => new()
    {
        Id = r.Long("Id"),
        BatchId = r.Long("BatchId"),
        Camera = r.Enum<CameraRole>("Camera"),
        Utc = r.Date("Utc"),
        ReadString = r.Str("ReadString"),
        Outcome = r.Enum<InspectionOutcome>("Outcome"),
        ImagePath = r.StrOrNull("ImagePath"),
    };

    private static Batch Map(SqliteRow r) => new()
    {
        Id = r.Long("Id"),
        BatchNo = r.Str("BatchNo"),
        ProductName = r.Str("ProductName"),
        BoxRecipeId = r.LongOrNull("BoxRecipeId"),
        BoxRecipeVersion = (int?)r.LongOrNull("BoxRecipeVersion"),
        LeafletRecipeId = r.LongOrNull("LeafletRecipeId"),
        LeafletRecipeVersion = (int?)r.LongOrNull("LeafletRecipeVersion"),
        Status = r.Enum<BatchStatus>("Status"),
        CreatedUtc = r.Date("CreatedUtc"),
        StartUtc = r.DateOrNull("StartUtc"),
        EndUtc = r.DateOrNull("EndUtc"),
        CreatedBy = r.Str("CreatedBy"),
        EndedBy = r.StrOrNull("EndedBy"),
    };
}

public sealed class SqliteEventLog : IEventLog
{
    private readonly SqliteDatabase _db;
    private readonly IClock _clock;
    public SqliteEventLog(SqliteDatabase db, IClock clock) { _db = db; _clock = clock; }

    public void Write(string source, string code, string data) =>
        _db.Execute("INSERT INTO EventLog(Utc, Source, Code, Data) VALUES(?,?,?,?)", _clock.UtcNow, source, code, data ?? "");

    public IReadOnlyList<EventLogEntry> Query(DateTime? fromUtc, DateTime? toUtc, int limit = 100_000)
    {
        var sql = new StringBuilder("SELECT * FROM EventLog WHERE 1=1");
        var args = new List<object?>();
        if (fromUtc is { } f) { sql.Append(" AND Utc >= ?"); args.Add(f); }
        if (toUtc is { } t) { sql.Append(" AND Utc <= ?"); args.Add(t); }
        sql.Append(" ORDER BY Id LIMIT ?");
        args.Add(limit);
        return _db.Query(sql.ToString(), args.ToArray()).Select(r => new EventLogEntry
        {
            Id = r.Long("Id"), Utc = r.Date("Utc"), Source = r.Str("Source"), Code = r.Str("Code"), Data = r.Str("Data"),
        }).ToList();
    }
}

public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly SqliteDatabase _db;
    public SqliteSettingsStore(SqliteDatabase db) => _db = db;

    public string? Get(string key) => _db.QuerySingle("SELECT Value FROM Setting WHERE Key = ?", key)?.Str("Value");

    public void Set(string key, string value)
    {
        if (_db.Execute("UPDATE Setting SET Value = ? WHERE Key = ?", value, key) == 0)
            _db.Execute("INSERT INTO Setting(Key, Value) VALUES(?,?)", key, value);
    }

    public IReadOnlyDictionary<string, string> All() => _db.Query("SELECT Key, Value FROM Setting").ToDictionary(r => r.Str("Key"), r => r.Str("Value"));
}

public sealed class SqliteReportFileRepository : IReportFileRepository
{
    private readonly SqliteDatabase _db;
    public SqliteReportFileRepository(SqliteDatabase db) => _db = db;

    public long Insert(ReportFileRecord r) =>
        _db.Insert("INSERT INTO ReportFile(Kind, BatchId, Path, Sha256, CreatedUtc, CreatedBy) VALUES(?,?,?,?,?,?)", r.Kind, r.BatchId, r.Path, r.Sha256, r.CreatedUtc, r.CreatedBy);

    public IReadOnlyList<ReportFileRecord> ForBatch(long batchId) =>
        _db.Query("SELECT * FROM ReportFile WHERE BatchId = ? ORDER BY Id", batchId).Select(r => new ReportFileRecord
        {
            Id = r.Long("Id"), Kind = r.Str("Kind"), BatchId = r.LongOrNull("BatchId"), Path = r.Str("Path"),
            Sha256 = r.Str("Sha256"), CreatedUtc = r.Date("CreatedUtc"), CreatedBy = r.Str("CreatedBy"),
        }).ToList();
}
