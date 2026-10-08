namespace CheckBarcode.Domain;

public sealed class User
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string FullName { get; set; } = "";
    public string RoleCode { get; set; } = Roles.Operator;
    public UserStatus Status { get; set; } = UserStatus.Active;
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public DateTime PasswordChangedUtc { get; set; }
    public bool MustChangePassword { get; set; }
    public int FailedCount { get; set; }
    public DateTime? LockedUtc { get; set; }
    public LockReason LockReason { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";

    public User Clone() => (User)MemberwiseClone();
}

public sealed class Role
{
    public string Code { get; set; } = "";
    public string NameVi { get; set; } = "";
    public string NameEn { get; set; } = "";
    public bool IsSystem { get; set; }
}

public sealed class Recipe
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public CameraRole Type { get; set; }
    public string Barcode { get; set; } = "";
    public BarcodeFormat BarcodeFormat { get; set; } = BarcodeFormat.Pharmacode;
    /// <summary>Line speed setpoint (boxes/min). Box recipes only.</summary>
    public double? SpeedSetpoint { get; set; }
    public double? SpeedTolerance { get; set; }
    /// <summary>In-date heater temperature setpoint (°C). Box recipes only.</summary>
    public double? TempSetpoint { get; set; }
    public double? TempTolerance { get; set; }
    public int Version { get; set; } = 1;
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Recipe Clone() => (Recipe)MemberwiseClone();
}

public sealed class Batch
{
    public long Id { get; set; }
    public string BatchNo { get; set; } = "";
    public string ProductName { get; set; } = "";
    public long? BoxRecipeId { get; set; }
    public int? BoxRecipeVersion { get; set; }
    public long? LeafletRecipeId { get; set; }
    public int? LeafletRecipeVersion { get; set; }
    public BatchStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public string? EndedBy { get; set; }

    public bool UsesCamera(CameraRole role) => role == CameraRole.Box ? BoxRecipeId.HasValue : LeafletRecipeId.HasValue;
    public bool IsOpen => Status is BatchStatus.Running or BatchStatus.Paused or BatchStatus.Created;
}

public sealed class BatchCounter
{
    public long BatchId { get; set; }
    public CameraRole Camera { get; set; }
    public long Scanned { get; set; }
    public long Pass { get; set; }
    public long Fail { get; set; }
    public long NoRead { get; set; }

    public void Add(InspectionOutcome outcome)
    {
        Scanned++;
        switch (outcome)
        {
            case InspectionOutcome.Pass: Pass++; break;
            case InspectionOutcome.Fail: Fail++; break;
            default: NoRead++; break;
        }
    }

    public void Reset() => Scanned = Pass = Fail = NoRead = 0;
}

public sealed class BatchOperator
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public string Username { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
}

public sealed class InspectionResult
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public CameraRole Camera { get; set; }
    public DateTime Utc { get; set; }
    public string ReadString { get; set; } = "";
    public InspectionOutcome Outcome { get; set; }
    public string? ImagePath { get; set; }
}

/// <summary>One immutable, hash-chained audit trail record (21 CFR 11.10(e)).</summary>
public sealed class AuditRecord
{
    public long Seq { get; set; }
    public DateTime Utc { get; set; }
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public SessionState SessionState { get; set; }
    public string ActionCode { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string Field { get; set; } = "";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public string Reason { get; set; } = "";
    /// <summary>JSON object with message parameters for localized rendering.</summary>
    public string MessageArgs { get; set; } = "";
    public long? BatchId { get; set; }
    public string Workstation { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
    /// <summary>Filled when the record carries an electronic signature.</summary>
    public string? SignatureMeaning { get; set; }
}

/// <summary>One immutable, hash-chained alarm transition.</summary>
public sealed class AlarmRecord
{
    public long Seq { get; set; }
    public string AlarmId { get; set; } = "";
    public AlarmSeverity Severity { get; set; }
    public AlarmState State { get; set; }
    public DateTime Utc { get; set; }
    public string Username { get; set; } = "";
    public long? BatchId { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

public sealed class EventLogEntry
{
    public long Id { get; set; }
    public DateTime Utc { get; set; }
    public string Source { get; set; } = "";
    public string Code { get; set; } = "";
    public string Data { get; set; } = "";
}

public sealed class ReportFileRecord
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public long? BatchId { get; set; }
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
}
