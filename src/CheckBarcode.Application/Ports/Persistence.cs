using CheckBarcode.Domain;

namespace CheckBarcode.Application.Ports;

public interface IUserRepository
{
    User? FindByUsername(string username);
    User? FindById(long id);
    IReadOnlyList<User> List();
    long Insert(User user);
    void Update(User user);
    void AddPasswordHistory(long userId, string hash, string salt, DateTime utc);
    IReadOnlyList<(string Hash, string Salt)> RecentPasswords(long userId, int count);
}

public interface IRoleRepository
{
    IReadOnlyList<Role> List();
    void Upsert(Role role);
    IReadOnlyCollection<string> PermissionsOf(string roleCode);
    void SetPermissions(string roleCode, IEnumerable<string> permissions);
}

public interface IRecipeRepository
{
    Recipe? Find(long id);
    /// <summary>Active recipe with this name for the camera type (names are unique per type).</summary>
    Recipe? FindActiveByName(string name, CameraRole type);
    IReadOnlyList<Recipe> ListActive(CameraRole? type = null);
    long Insert(Recipe recipe);
    void Update(Recipe recipe);
}

public interface IBatchRepository
{
    Batch? Find(long id);
    Batch? FindByNumber(string batchNo);
    /// <summary>Batch in state Created, Running or Paused (at most one).</summary>
    Batch? FindOpen();
    IReadOnlyList<Batch> List(DateTime? fromUtc, DateTime? toUtc);
    long Insert(Batch batch);
    void Update(Batch batch);

    BatchCounter GetCounter(long batchId, CameraRole camera);
    void SaveCounter(BatchCounter counter);

    long AddOperator(BatchOperator op);
    void CloseOperator(long batchId, string username, DateTime toUtc);
    IReadOnlyList<BatchOperator> Operators(long batchId);

    long AddResult(InspectionResult result);
    IReadOnlyList<InspectionResult> Results(long batchId, CameraRole? camera, InspectionOutcome? outcome);
    IReadOnlyList<InspectionResult> SearchResults(DateTime? fromUtc, DateTime? toUtc, string? barcode, InspectionOutcome? outcome, int limit);
}

public interface IAuditStore
{
    /// <summary>Appends a record; fills Seq, PrevHash and Hash.</summary>
    AuditRecord Append(AuditRecord record);
    void AppendSignature(long auditSeq, string username, string meaning, DateTime utc);
    IReadOnlyList<AuditRecord> Query(AuditQuery query);
    /// <summary>Timestamp of the last record (by sequence), the reference for clock-rollback detection at start-up.</summary>
    DateTime? LastUtc();
    IntegrityResult Verify();
}

public sealed record AuditQuery(DateTime? FromUtc = null, DateTime? ToUtc = null, long? BatchId = null, string? Username = null, string? ActionPrefix = null, int Limit = 100_000);

public sealed record IntegrityResult(bool Ok, long Checked, long? FirstBrokenSeq, string Table)
{
    public static IntegrityResult Good(string table, long count) => new(true, count, null, table);
}

public interface IAlarmStore
{
    AlarmRecord Append(AlarmRecord record);
    IReadOnlyList<AlarmRecord> Query(DateTime? fromUtc, DateTime? toUtc, long? batchId, int limit = 100_000);
    /// <summary>Latest transition per alarm id (used to rebuild the active list after restart).</summary>
    IReadOnlyList<AlarmRecord> LatestPerAlarm();
    IntegrityResult Verify();
}

public interface IEventLog
{
    void Write(string source, string code, string data);
    IReadOnlyList<EventLogEntry> Query(DateTime? fromUtc, DateTime? toUtc, int limit = 100_000);
}

public interface ISettingsStore
{
    string? Get(string key);
    void Set(string key, string value);
    IReadOnlyDictionary<string, string> All();
}

public interface IReportFileRepository
{
    long Insert(ReportFileRecord record);
    IReadOnlyList<ReportFileRecord> ForBatch(long batchId);
}

/// <summary>Runs a unit of work atomically.</summary>
public interface ITransactionRunner
{
    T InTransaction<T>(Func<T> work);
}
