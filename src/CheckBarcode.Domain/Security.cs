namespace CheckBarcode.Domain;

/// <summary>Built-in role codes. Roles are data; Admin can add more (e.g. Maintenance).</summary>
public static class Roles
{
    public const string Operator = "Operator";
    public const string Supervisor = "Supervisor";
    public const string Admin = "Admin";
}

/// <summary>Permission codes checked by the ActionDispatcher and by the UI.</summary>
public static class Permissions
{
    public const string BatchRun = "Batch.Run";
    public const string BatchEnd = "Batch.End";
    public const string BatchResetCounters = "Batch.ResetCounters";
    public const string AlarmAck = "Alarm.Ack";
    public const string RecipeManage = "Recipe.Manage";
    public const string CameraConnect = "Camera.Connect";
    public const string CameraSettings = "Camera.Settings";
    public const string MachineCamEdit = "Machine.CamEdit";
    public const string MachineSettings = "Machine.Settings";
    public const string ReportView = "Report.View";
    public const string ReportExport = "Report.Export";
    public const string AuditView = "Audit.View";
    public const string AuditReview = "Audit.Review";
    public const string ImageView = "Image.View";
    public const string UserManage = "User.Manage";
    public const string SecuritySettings = "Security.Settings";
    public const string SystemExit = "System.Exit";

    public static readonly IReadOnlyList<string> All = new[]
    {
        BatchRun, BatchEnd, BatchResetCounters, AlarmAck, RecipeManage, CameraConnect, CameraSettings,
        MachineCamEdit, MachineSettings, ReportView, ReportExport, AuditView, AuditReview, ImageView,
        UserManage, SecuritySettings, SystemExit,
    };

    /// <summary>Default permission matrix seeded on first start (PLAN.md §2 USR).</summary>
    public static IReadOnlyDictionary<string, string[]> DefaultMatrix { get; } = new Dictionary<string, string[]>
    {
        [Roles.Operator] = new[]
        {
            BatchRun, BatchEnd, BatchResetCounters, AlarmAck, CameraConnect, ReportView, ReportExport, AuditView, ImageView,
        },
        [Roles.Supervisor] = new[]
        {
            BatchRun, BatchEnd, BatchResetCounters, AlarmAck, CameraConnect, ReportView, ReportExport, AuditView, ImageView,
            RecipeManage, CameraSettings, AuditReview,
        },
        [Roles.Admin] = All.ToArray(),
    };
}

/// <summary>PLC login level written to the PLC so the HMI can enable parameters / start (USR-09).</summary>
public static class LoginLevel
{
    public static int For(string? roleCode) => roleCode switch
    {
        null or "" => 0,
        Roles.Operator => 1,
        Roles.Supervisor => 2,
        Roles.Admin => 3,
        _ => 1,
    };
}

/// <summary>Business rule violation with a localizable message code.</summary>
public sealed class DomainException : Exception
{
    public string Code { get; }
    public object[] Args { get; }

    public DomainException(string code, params object[] args) : base(code + (args.Length > 0 ? ": " + string.Join(", ", args) : ""))
    {
        Code = code;
        Args = args;
    }
}
