using System.Text.Json;
using CheckBarcode.Application.Ports;
using CheckBarcode.Application.Security;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Audit;

/// <summary>Well-known audit action codes not coming from actions.json (system generated).</summary>
public static class AuditCodes
{
    public const string AppStarted = "APP_STARTED";
    public const string AppStopped = "APP_STOPPED";
    public const string ConfigLoaded = "CONFIG_LOADED";
    public const string IntegrityCheck = "INTEGRITY_CHECK";
    public const string ClockRollback = "CLOCK_ROLLBACK";
    public const string LoginOk = "LOGIN_OK";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string LoginRejectedLocked = "LOGIN_REJECTED_LOCKED";
    public const string Logout = "LOGOUT";
    public const string LogoutIdle = "LOGOUT_IDLE";
    public const string PasswordChanged = "PASSWORD_CHANGED";
    public const string PasswordExpired = "PASSWORD_EXPIRED";
    public const string ActionDenied = "ACTION_DENIED";
    public const string ActionFailed = "ACTION_FAILED";
    public const string SignatureFailed = "SIGNATURE_FAILED";
    public const string PlcParameterChanged = "PLC_PARAM_CHANGED";
    public const string PlcParameterChangedOutside = "PLC_PARAM_CHANGED_OUTSIDE";
    public const string PlcStateChanged = "PLC_STATE_CHANGED";
    public const string PlcConnection = "PLC_CONNECTION";
    public const string CameraConnection = "CAMERA_CONNECTION";
    public const string BatchRestored = "BATCH_RESTORED";
    public const string BatchAutoPaused = "BATCH_AUTO_PAUSED";
    public const string ReportGenerated = "REPORT_GENERATED";
    public const string BackupCreated = "BACKUP_CREATED";
}

/// <summary>Builds audit records with attribution, time and workstation and appends them to the store.</summary>
public sealed class AuditTrail
{
    private readonly IAuditStore _store;
    private readonly IClock _clock;
    private readonly SessionService _session;
    private readonly string _workstation;

    private static readonly TimeSpan RollbackTolerance = TimeSpan.FromMinutes(2);
    private readonly object _clockGate = new();
    private DateTime? _maxUtc;

    public AuditTrail(IAuditStore store, IClock clock, SessionService session, string workstation)
    {
        _store = store;
        _clock = clock;
        _session = session;
        _workstation = workstation;
    }

    /// <summary>Batch that new records are linked to (set by BatchService).</summary>
    public Func<long?> CurrentBatchId { get; set; } = () => null;

    public IAuditStore Store => _store;

    public AuditRecord Write(string actionCode, string objectType = "", string objectId = "", string field = "",
        string? oldValue = null, string? newValue = null, string reason = "", IDictionary<string, string>? args = null,
        Attribution? attribution = null, long? batchId = null)
    {
        var who = attribution ?? _session.Attribution;
        var now = _clock.UtcNow;
        lock (_clockGate)
        {
            _maxUtc ??= _store.LastUtc() ?? now;
            if (actionCode != AuditCodes.ClockRollback && now < _maxUtc.Value - RollbackTolerance)
            {
                var previous = _maxUtc.Value;
                _maxUtc = now;
                Write(AuditCodes.ClockRollback, "System", "Clock", "UtcNow", previous.ToString("O"), now.ToString("O"), attribution: Attribution.System);
            }
            if (now > _maxUtc) _maxUtc = now;
        }
        var record = new AuditRecord
        {
            Utc = now,
            Username = who.Username,
            Role = who.Role,
            SessionState = who.State,
            ActionCode = actionCode,
            ObjectType = objectType,
            ObjectId = objectId,
            Field = field,
            OldValue = oldValue ?? "",
            NewValue = newValue ?? "",
            Reason = reason,
            MessageArgs = args is { Count: > 0 } ? JsonSerializer.Serialize(args) : "",
            BatchId = batchId ?? CurrentBatchId(),
            Workstation = _workstation,
        };
        return _store.Append(record);
    }

    public void Sign(AuditRecord record, string username, string meaning)
    {
        _store.AppendSignature(record.Seq, username, meaning, _clock.UtcNow);
        record.SignatureMeaning = meaning;
    }

    /// <summary>AUD-08: a Windows clock set back behind the latest audit record is recorded (checked on every write).</summary>
    public void CheckClock()
    {
        // Reload the reference time from the database; the next Write() compares against it.
        lock (_clockGate) _maxUtc = null;
    }
}
