using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Alarms;
using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Batches;
using CheckBarcode.Application.Cameras;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Localization;
using CheckBarcode.Application.Plc;
using CheckBarcode.Application.Ports;
using CheckBarcode.Application.Recipes;
using CheckBarcode.Application.Reports;
using CheckBarcode.Application.Security;
using CheckBarcode.Application.Settings;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure.Files;
using CheckBarcode.Infrastructure.Pdf;
using CheckBarcode.Infrastructure.Plc;
using CheckBarcode.Infrastructure.Sqlite;

namespace CheckBarcode.Infrastructure;

public sealed class AppHostOptions
{
    /// <summary>Folder that contains config/ (and, by default, data/).</summary>
    public string BaseDirectory { get; set; } = AppContext.BaseDirectory;
    public IClock Clock { get; set; } = new SystemClock();
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;
    public string Workstation { get; set; } = Environment.MachineName;
    /// <summary>Creates a code reader for a configured camera (Cognex driver lives in the WPF project).</summary>
    public Func<CameraConfig, ICodeReader>? ReaderFactory { get; set; }
    /// <summary>Overrides the PLC client (tests / simulator). Default: Modbus TCP from appsettings.</summary>
    public Func<PlcConfig, IPlcClient>? PlcFactory { get; set; }
    public IRemovableDriveProvider? DriveProvider { get; set; }
    public IPasswordHasher? PasswordHasher { get; set; }
}

/// <summary>
/// Composition root: builds every service from configuration (PLAN.md §3).
/// Used by the WPF application, the simulator and the integration tests, so the wiring itself is tested.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _plcLoop;
    private SecurityPolicy _policy;

    public AppConfig Config { get; }
    public string BaseDirectory { get; }
    public string ConfigDirectory { get; }
    public IReadOnlyList<TagDefinition> TagDefinitions { get; }
    public IReadOnlyList<ActionDefinition> ActionDefinitions { get; }
    public IClock Clock { get; }
    public SqliteDatabase Db { get; }
    public ISettingsStore SettingsStore { get; }
    public IEventLog EventLog { get; }
    public IAuditStore AuditStore { get; }
    public IAlarmStore AlarmStore { get; }
    public IUserRepository UsersRepo { get; }
    public IRoleRepository RolesRepo { get; }
    public IRecipeRepository RecipesRepo { get; }
    public IBatchRepository BatchesRepo { get; }
    public Localizer Localizer { get; }
    public TimeFormat Time { get; }
    public SessionService Session { get; }
    public AuditTrail Audit { get; }
    public PermissionService Permissions { get; }
    public AuthService Auth { get; }
    public ActionDispatcher Dispatcher { get; }
    public UserService Users { get; }
    public RecipeService Recipes { get; }
    public AlarmService Alarms { get; }
    public TagEngine TagEngine { get; }
    public MachineService Machine { get; }
    public CameraService Cameras { get; }
    public SettingsService Settings { get; }
    public IImageStore Images { get; }
    public BatchService Batches { get; }
    public AuditFormatter Formatter { get; }
    public ReportService Reports { get; }
    public ExportService Export { get; }
    public ReportArchiver Archiver { get; }
    public AuditReviewService Review { get; }
    public BackupService Backup { get; }
    public FileLogger Logger { get; }
    public SecurityPolicy Policy => _policy;

    public AppHost(AppHostOptions options)
    {
        BaseDirectory = options.BaseDirectory;
        ConfigDirectory = Path.Combine(BaseDirectory, "config");
        Clock = options.Clock;
        Config = ConfigLoader.Load<AppConfig>(Path.Combine(ConfigDirectory, "appsettings.json"));
        TagDefinitions = ConfigLoader.LoadList<TagDefinition>(Path.Combine(ConfigDirectory, Config.Plc.TagsFile));
        ActionDefinitions = ConfigLoader.LoadList<ActionDefinition>(Path.Combine(ConfigDirectory, "actions.json"));
        string P(string path) => Path.IsPathRooted(path) ? path : Path.Combine(BaseDirectory, path);

        Logger = new FileLogger(P(Config.Paths.Logs));
        Db = new SqliteDatabase(Path.Combine(P(Config.Paths.Data), "checkbarcode.db"));
        Schema.Migrate(Db);

        SettingsStore = new SqliteSettingsStore(Db);
        EventLog = new SqliteEventLog(Db, Clock);
        AuditStore = new SqliteAuditStore(Db);
        AlarmStore = new SqliteAlarmStore(Db);
        UsersRepo = new SqliteUserRepository(Db);
        RolesRepo = new SqliteRoleRepository(Db);
        RecipesRepo = new SqliteRecipeRepository(Db);
        BatchesRepo = new SqliteBatchRepository(Db);

        Localizer = Localizer.FromFolder(Path.Combine(ConfigDirectory, "lang"), Config.DefaultLanguage);
        Time = new TimeFormat(options.TimeZone);
        _policy = SecurityPolicy.Load(SettingsStore);
        Session = new SessionService(Clock, () => _policy);
        Audit = new AuditTrail(AuditStore, Clock, Session, options.Workstation);
        var hasher = options.PasswordHasher ?? new Pbkdf2PasswordHasher();

        PermissionService.SeedDefaults(RolesRepo);
        UserService.SeedAdmin(UsersRepo, hasher, Clock, _policy, Audit);
        Permissions = new PermissionService(RolesRepo, Session);
        Auth = new AuthService(UsersRepo, hasher, Session, Audit, Clock, () => _policy);
        Dispatcher = new ActionDispatcher(ActionDefinitions, Permissions, Session, Auth, Audit, Localizer);
        Users = new UserService(UsersRepo, RolesRepo, SettingsStore, hasher, Session, Permissions, Dispatcher, Clock, () => _policy, p => _policy = p);
        Recipes = new RecipeService(RecipesRepo, BatchesRepo, Dispatcher, Clock);
        Alarms = new AlarmService(AlarmStore, Clock, Session, Dispatcher);
        Alarms.Define(SoftwareAlarms.AuditIntegrity, AlarmSeverity.Critical, LocalizedText.Of("Phát hiện dữ liệu audit bị thay đổi", "Audit data integrity failure"));
        Alarms.Define(SoftwareAlarms.DiskSpaceLow, AlarmSeverity.Medium, LocalizedText.Of("Ổ cứng sắp đầy", "Disk space low"));

        var plc = options.PlcFactory?.Invoke(Config.Plc) ?? new ModbusTcpClient(Config.Plc.Ip, Config.Plc.Port, Config.Plc.UnitId, Config.Plc.TimeoutMs);
        TagEngine = new TagEngine(plc, TagDefinitions, Config.Plc, Clock);
        Machine = new MachineService(TagEngine, Audit, Alarms, Session, Dispatcher, EventLog, Config.Plc.LoginLevels);

        var factory = options.ReaderFactory ?? throw new InvalidOperationException("ReaderFactory is required");
        Cameras = new CameraService(Config.Cameras.Select(factory).ToList(), Dispatcher, Audit, Alarms, EventLog);
        Settings = new SettingsService(SettingsStore, Dispatcher);
        Images = new FileImageStore(P(Config.Paths.Images));
        Batches = new BatchService(BatchesRepo, RecipesRepo, Dispatcher, Audit, Session, Cameras, Machine, Alarms, Images, Settings, new ExactCodeMatcher(), Clock);

        Formatter = new AuditFormatter(ActionDefinitions, TagDefinitions, Localizer);
        Reports = new ReportService(BatchesRepo, RecipesRepo, AuditStore, AlarmStore, Alarms, Formatter, Localizer, Time, Config, Clock);
        var renderer = new PdfReportRenderer(Config.Paths.PdfFonts.Select(P), Config.Paths.PdfBoldFonts.Select(P));
        Export = new ExportService(renderer, new SqliteReportFileRepository(Db), options.DriveProvider ?? new RemovableDriveProvider(), Dispatcher, Audit, Clock, Session, P(Config.Paths.Reports));
        Archiver = new ReportArchiver(Batches, Reports, Export, Audit, EventLog);
        Review = new AuditReviewService(Dispatcher, AuditStore, AlarmStore, Time);
        Backup = new BackupService(Db, P(Config.Paths.Backup), Audit, Clock);
    }

    /// <summary>Start-up sequence: integrity, clock, config hashes, restore, devices.</summary>
    public async Task StartAsync(bool startDevices = true)
    {
        Audit.CheckClock();
        Audit.Write(AuditCodes.AppStarted, "System", Config.MachineCode, attribution: Attribution.System,
            args: new Dictionary<string, string> { ["version"] = typeof(AppHost).Assembly.GetName().Version?.ToString() ?? "", ["sqlite"] = SqliteDatabase.Version });
        foreach (var file in new[] { "appsettings.json", Config.Plc.TagsFile, "actions.json" })
            Audit.Write(AuditCodes.ConfigLoaded, "Config", file, "Sha256", newValue: ConfigLoader.FileHash(Path.Combine(ConfigDirectory, file)), attribution: Attribution.System);

        var audit = AuditStore.Verify();
        var alarms = AlarmStore.Verify();
        Audit.Write(AuditCodes.IntegrityCheck, "Database", "AuditTrail+AlarmEvent", attribution: Attribution.System,
            args: new Dictionary<string, string> { ["result"] = audit.Ok && alarms.Ok ? "OK" : "BROKEN", ["broken"] = (audit.FirstBrokenSeq ?? alarms.FirstBrokenSeq)?.ToString() ?? "" });

        Alarms.Restore();
        if (!audit.Ok || !alarms.Ok) Alarms.Raise(SoftwareAlarms.AuditIntegrity);
        Batches.Restore();

        try
        {
            Backup.RunDaily(Settings.Current.BackupRetentionDays);
            Images.DeleteOlderThan(Clock.UtcNow.AddDays(-Settings.Current.ImageRetentionDays));
        }
        catch (Exception ex)
        {
            Logger.Error("Housekeeping failed", ex);
        }

        if (startDevices)
        {
            _plcLoop = Task.Run(() => TagEngine.RunAsync(_cts.Token));
            _devicesStarted = true;
            // Cameras connect in the background so an unreachable camera does not delay start-up.
            _ = Task.Run(() => Cameras.ConnectAllAsync(_cts.Token));
            await Task.CompletedTask;
        }
    }

    /// <summary>Periodic housekeeping (idle logout, disk space); call every few seconds from the UI timer.</summary>
    public void Tick()
    {
        Auth.CheckIdle();
        if (_devicesStarted && ++_ticks % 10 == 0) _ = Task.Run(() => Cameras.ReconnectLostAsync(_cts.Token));
    }

    private bool _devicesStarted;
    private int _ticks;

    public async Task StopAsync()
    {
        Auth.Logout(LogoutReason.Shutdown);
        Audit.Write(AuditCodes.AppStopped, "System", Config.MachineCode, attribution: Attribution.System);
        _cts.Cancel();
        if (_plcLoop is not null)
        {
            try { await _plcLoop; } catch (OperationCanceledException) { }
        }
        foreach (var reader in Cameras.Readers)
        {
            try { await reader.DisconnectAsync(); } catch { /* shutting down */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        TagEngine.Dispose();
        foreach (var r in Cameras.Readers) r.Dispose();
        Db.Dispose();
        _cts.Dispose();
    }
}
