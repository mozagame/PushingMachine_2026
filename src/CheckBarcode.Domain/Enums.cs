namespace CheckBarcode.Domain;

/// <summary>Which camera / packaging item a recipe or result belongs to.</summary>
public enum CameraRole
{
    /// <summary>Carton (hộp).</summary>
    Box = 1,
    /// <summary>Leaflet (toa).</summary>
    Leaflet = 2,
}

/// <summary>Symbology of the printed code. Pharmacode today, DataMatrix/serialization later.</summary>
public enum BarcodeFormat
{
    Pharmacode = 0,
    DataMatrix = 1,
    Code128 = 2,
    QrCode = 3,
    Any = 99,
}

public enum RecordStatus
{
    Active = 0,
    Deleted = 1,
}

public enum UserStatus
{
    Active = 0,
    Locked = 1,
    Inactive = 2,
}

public enum LockReason
{
    None = 0,
    WrongPassword = 1,
    LockedByAdmin = 2,
}

public enum BatchStatus
{
    Created = 0,
    Running = 1,
    Paused = 2,
    Completed = 3,
}

public enum InspectionOutcome
{
    Pass = 0,
    Fail = 1,
    NoRead = 2,
}

public enum AlarmSeverity
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

public enum AlarmState
{
    Active = 0,
    Acknowledged = 1,
    Cleared = 2,
}

/// <summary>Category of a PLC tag; decides where a value change is routed.</summary>
public enum TagCategory
{
    /// <summary>Alarm bit/word → AlarmEvent table.</summary>
    Alarm = 0,
    /// <summary>Machine parameter (cam angle, setpoint) → AuditTrail old→new.</summary>
    Parameter = 1,
    /// <summary>Machine state (running, e-stop) → EventLog (+ audit when flagged).</summary>
    State = 2,
    /// <summary>Process value (actual speed, temperature) → display + tolerance check.</summary>
    Process = 3,
    /// <summary>Internal tag (heartbeat, command) → not logged.</summary>
    Internal = 4,
}

/// <summary>State of the user session at the time an event was recorded (USR-07).</summary>
public enum SessionState
{
    /// <summary>A user is logged in.</summary>
    Active = 0,
    /// <summary>No active session; event attributed to the last user whose session expired.</summary>
    Expired = 1,
    /// <summary>Nobody has logged in since start-up; event attributed to SYSTEM.</summary>
    None = 2,
}

public enum SaveImageMode
{
    FailOnly = 0,
    All = 1,
    None = 2,
}
