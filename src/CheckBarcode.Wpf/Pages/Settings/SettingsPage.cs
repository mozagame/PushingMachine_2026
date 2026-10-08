using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CheckBarcode.Application.Ports;
using CheckBarcode.Application.Settings;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure;
using CheckBarcode.Infrastructure.Sqlite;
using CheckBarcode.Wpf.Mvvm;
using CheckBarcode.Wpf.Shell;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Pages.Settings;

public sealed record ImageModeOption(SaveImageMode Mode, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Operation settings (image storage, retention, disk), camera parameters and system info (CAM-05, CAM-06, SYS-03).</summary>
public sealed class SettingsViewModel : ObservableObject, IPageViewModel
{
    private readonly AppHost _host;
    private ImageModeOption? _imageMode;
    private string _imageDays = "", _backupDays = "", _diskWarn = "";
    private CameraRole _camera = CameraRole.Box;
    private string _exposure = "", _gain = "", _delay = "", _interval = "", _timeout = "", _burst = "";

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        SaveCommand = new AsyncCommand(SaveAsync, () => host.Permissions.Has(Permissions.MachineSettings));
        ReadCameraCommand = new AsyncCommand(ReadCameraAsync, () => host.Cameras.IsConnected(Camera));
        WriteCameraCommand = new AsyncCommand(WriteCameraAsync, () => host.Cameras.IsConnected(Camera) && host.Permissions.Has(Permissions.CameraSettings));
        BackupCommand = new RelayCommand(Backup, () => host.Permissions.Has(Permissions.MachineSettings));
        ImportCommand = new AsyncCommand(ImportAsync, () => host.Permissions.Has(Permissions.RecipeManage));
        host.Localizer.LanguageChanged += (_, _) => OnUi(OnShown);
    }

    public ObservableCollection<ImageModeOption> ImageModes { get; } = new();
    public IReadOnlyList<CameraRole> CameraRoles { get; } = new[] { CameraRole.Box, CameraRole.Leaflet };
    public ICommand SaveCommand { get; }
    public ICommand ReadCameraCommand { get; }
    public ICommand WriteCameraCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand ImportCommand { get; }

    public ImageModeOption? ImageMode { get => _imageMode; set => Set(ref _imageMode, value); }
    public string ImageDays { get => _imageDays; set => Set(ref _imageDays, value); }
    public string BackupDays { get => _backupDays; set => Set(ref _backupDays, value); }
    public string DiskWarn { get => _diskWarn; set => Set(ref _diskWarn, value); }
    public CameraRole Camera { get => _camera; set => Set(ref _camera, value); }
    public string Exposure { get => _exposure; set => Set(ref _exposure, value); }
    public string Gain { get => _gain; set => Set(ref _gain, value); }
    public string Delay { get => _delay; set => Set(ref _delay, value); }
    public string Interval { get => _interval; set => Set(ref _interval, value); }
    public string Timeout { get => _timeout; set => Set(ref _timeout, value); }
    public string Burst { get => _burst; set => Set(ref _burst, value); }

    public string SystemInfo =>
        $"{Loc.T("ui.version")}: {typeof(SettingsViewModel).Assembly.GetName().Version}\n" +
        $"SQLite: {SqliteDatabase.Version}\n" +
        $"PLC: {_host.Config.Plc.Ip}:{_host.Config.Plc.Port} (unit {_host.Config.Plc.UnitId}, {_host.Config.Plc.Driver})\n" +
        string.Join("\n", _host.Config.Cameras.Select(c => $"{c.Name}: {c.Ip} ({c.Driver})")) + "\n" +
        $"{Loc.T("ui.dataFolder")}: {Path.GetFullPath(Path.Combine(_host.BaseDirectory, _host.Config.Paths.Data))}";

    public void OnShown()
    {
        ImageModes.Clear();
        foreach (var m in Enum.GetValues<SaveImageMode>()) ImageModes.Add(new ImageModeOption(m, Loc.T("ui.imageMode." + m)));
        var s = _host.Settings.Current;
        ImageMode = ImageModes.First(m => m.Mode == s.ImageMode);
        ImageDays = s.ImageRetentionDays.ToString(CultureInfo.InvariantCulture);
        BackupDays = s.BackupRetentionDays.ToString(CultureInfo.InvariantCulture);
        DiskWarn = s.DiskWarnFreePercent.ToString(CultureInfo.InvariantCulture);
        Raise(nameof(SystemInfo));
    }

    private async Task SaveAsync()
    {
        static int I(string v) => int.TryParse(v, out var x) ? x : -1;
        var next = new OperationSettings { ImageMode = ImageMode?.Mode ?? SaveImageMode.FailOnly, ImageRetentionDays = I(ImageDays), BackupRetentionDays = I(BackupDays), DiskWarnFreePercent = I(DiskWarn) };
        Dialogs.Dialogs.Show(await _host.Settings.SaveAsync(next), "msg.saved");
        OnShown();
    }

    private async Task ReadCameraAsync()
    {
        var reader = _host.Cameras.Reader(Camera);
        if (reader is null) return;
        var s = await reader.GetSettingsAsync();
        Exposure = s.ExposureUs.ToString(CultureInfo.InvariantCulture);
        Gain = s.Gain.ToString(CultureInfo.InvariantCulture);
        Delay = s.TriggerDelayMs.ToString(CultureInfo.InvariantCulture);
        Interval = s.IntervalMs.ToString(CultureInfo.InvariantCulture);
        Timeout = s.DecoderTimeoutMs.ToString(CultureInfo.InvariantCulture);
        Burst = s.BurstLength.ToString(CultureInfo.InvariantCulture);
    }

    private async Task WriteCameraAsync()
    {
        static int I(string v) => int.TryParse(v, out var x) ? x : 0;
        var gain = double.TryParse(Gain, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ? g : 0;
        var settings = new CameraSettings(I(Exposure), gain, I(Delay), I(Interval), I(Timeout), Math.Max(1, I(Burst)));
        Dialogs.Dialogs.Show(await _host.Cameras.SaveSettingsAsync(Camera, settings), "msg.saved");
    }

    /// <summary>Imports recipes from the v1 MyDatabase.db (each recipe is an audited RECIPE_CREATE).</summary>
    private async Task ImportAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "SQLite (*.db)|*.db", FileName = "MyDatabase.db" };
        if (dialog.ShowDialog() != true) return;
        var result = await new Infrastructure.Migration.LegacyImporter(_host.Recipes).ImportRecipesAsync(dialog.FileName);
        Dialogs.Dialogs.Info("ui.importDone", result.Imported, result.Skipped.Count, string.Join("\n", result.Skipped.Take(15)));
    }

    private void Backup()
    {
        var path = _host.Backup.RunDaily(_host.Settings.Current.BackupRetentionDays);
        Dialogs.Dialogs.Info(path is null ? "ui.backupExists" : "ui.backupDone", path ?? "");
    }
}

public sealed class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel vm)
    {
        DataContext = vm;
        var op = UiKit.Column(UiKit.Form(
                ("field.Image.SaveMode", UiKit.Combo(nameof(SettingsViewModel.ImageModes), nameof(SettingsViewModel.ImageMode), nameof(ImageModeOption.Display))),
                ("field.Image.RetentionDays", UiKit.TextBox(nameof(SettingsViewModel.ImageDays), 140)),
                ("field.Backup.RetentionDays", UiKit.TextBox(nameof(SettingsViewModel.BackupDays), 140)),
                ("field.Disk.WarnFreePercent", UiKit.TextBox(nameof(SettingsViewModel.DiskWarn), 140))),
            UiKit.Row(UiKit.Button("ui.save", null!, Theme.Success, 180).Bind(ButtonBase.CommandProperty, nameof(SettingsViewModel.SaveCommand)),
                UiKit.Button("ui.backupNow", null!, Theme.Neutral, 220).Bind(ButtonBase.CommandProperty, nameof(SettingsViewModel.BackupCommand)),
                UiKit.Button("ui.importV1", null!, Theme.Neutral, 260).Bind(ButtonBase.CommandProperty, nameof(SettingsViewModel.ImportCommand))));

        var cam = UiKit.Column(UiKit.Form(
                ("col.camera", UiKit.Combo(nameof(SettingsViewModel.CameraRoles), nameof(SettingsViewModel.Camera))),
                ("field.ExposureUs", UiKit.TextBox(nameof(SettingsViewModel.Exposure), 160)),
                ("field.Gain", UiKit.TextBox(nameof(SettingsViewModel.Gain), 160)),
                ("field.TriggerDelayMs", UiKit.TextBox(nameof(SettingsViewModel.Delay), 160)),
                ("field.IntervalMs", UiKit.TextBox(nameof(SettingsViewModel.Interval), 160)),
                ("field.DecoderTimeoutMs", UiKit.TextBox(nameof(SettingsViewModel.Timeout), 160)),
                ("field.BurstLength", UiKit.TextBox(nameof(SettingsViewModel.Burst), 160))),
            UiKit.Row(UiKit.Button("ui.readFromCamera", null!, Theme.Neutral, 220).Bind(ButtonBase.CommandProperty, nameof(SettingsViewModel.ReadCameraCommand)),
                UiKit.Button("ui.writeToCamera", null!, Theme.Success, 220).Bind(ButtonBase.CommandProperty, nameof(SettingsViewModel.WriteCameraCommand))));

        var info = UiKit.Value(nameof(SettingsViewModel.SystemInfo), Theme.FontSmall, color: Theme.TextMuted);

        var root = UiKit.Layout("*,*", "auto,*");
        root.Add(UiKit.Card(op, "ui.operationSettings").At(0, 0), UiKit.Card(cam, "ui.cameraSettings").At(0, 1, 2), UiKit.Card(info, "ui.systemInfo").At(1, 0));
        Content = root;
    }
}
