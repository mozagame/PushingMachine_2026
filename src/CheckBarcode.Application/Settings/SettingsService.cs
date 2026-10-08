using System.Globalization;
using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Settings;

/// <summary>Operational settings stored in the database; every change is audited (CAM-06, SYS-03).</summary>
public sealed class OperationSettings
{
    public const string KeyImageMode = "Image.SaveMode";
    public const string KeyImageRetentionDays = "Image.RetentionDays";
    public const string KeyBackupRetentionDays = "Backup.RetentionDays";
    public const string KeyDiskWarnPercent = "Disk.WarnFreePercent";

    public SaveImageMode ImageMode { get; set; } = SaveImageMode.FailOnly;
    public int ImageRetentionDays { get; set; } = 365;
    public int BackupRetentionDays { get; set; } = 90;
    public int DiskWarnFreePercent { get; set; } = 10;

    public static OperationSettings Load(ISettingsStore store)
    {
        var s = new OperationSettings();
        if (Enum.TryParse<SaveImageMode>(store.Get(KeyImageMode), out var mode)) s.ImageMode = mode;
        if (int.TryParse(store.Get(KeyImageRetentionDays), out var d)) s.ImageRetentionDays = d;
        if (int.TryParse(store.Get(KeyBackupRetentionDays), out var b)) s.BackupRetentionDays = b;
        if (int.TryParse(store.Get(KeyDiskWarnPercent), out var w)) s.DiskWarnFreePercent = w;
        return s;
    }

    public IEnumerable<(string Key, string Value)> ToSettings()
    {
        yield return (KeyImageMode, ImageMode.ToString());
        yield return (KeyImageRetentionDays, ImageRetentionDays.ToString(CultureInfo.InvariantCulture));
        yield return (KeyBackupRetentionDays, BackupRetentionDays.ToString(CultureInfo.InvariantCulture));
        yield return (KeyDiskWarnPercent, DiskWarnFreePercent.ToString(CultureInfo.InvariantCulture));
    }
}

public sealed class SettingsService
{
    public const string SaveAction = "SETTINGS_CHANGE";

    private readonly ISettingsStore _store;
    private readonly ActionDispatcher _dispatcher;

    public SettingsService(ISettingsStore store, ActionDispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;
        Current = OperationSettings.Load(store);
    }

    public OperationSettings Current { get; private set; }

    public Task<ActionOutcome> SaveAsync(OperationSettings next) =>
        _dispatcher.ExecuteAsync(SaveAction, s =>
        {
            if (next.ImageRetentionDays < 1 || next.BackupRetentionDays < 1 || next.DiskWarnFreePercent is < 1 or > 90)
                throw new DomainException("msg.invalidSettings");
            s.Target("Setting", "Operation");
            var old = Current.ToSettings().ToDictionary(x => x.Key, x => x.Value);
            foreach (var (key, value) in next.ToSettings())
            {
                s.Change(key, old[key], value);
                _store.Set(key, value);
            }
            Current = OperationSettings.Load(_store);
        });
}
