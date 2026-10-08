using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckBarcode.Application.Alarms;
using CheckBarcode.Application.Batches;
using CheckBarcode.Application.Plc;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure;
using CheckBarcode.Wpf.Dialogs;
using CheckBarcode.Wpf.Mvvm;
using CheckBarcode.Wpf.Shell;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Pages.Operation;

/// <summary>Recipe entry for a combo box; Recipe = null means "not used".</summary>
public sealed record RecipeOption(Recipe? Recipe, string Display)
{
    public override string ToString() => Display;
}

public static class PngImage
{
    public static ImageSource? FromPng(byte[]? png)
    {
        if (png is null || png.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>One camera column on the operation screen.</summary>
public sealed class CameraPanelViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _connected;
    private bool _live;
    private ImageSource? _image;
    private string _lastCode = "";
    private InspectionOutcome? _lastOutcome;
    private long _scanned, _pass, _fail, _noRead;

    public CameraPanelViewModel(AppHost host, CameraRole role)
    {
        _host = host;
        Role = role;
        var reader = host.Cameras.Reader(role);
        Address = reader?.Address ?? "—";
        _connected = reader?.IsConnected == true;
        ConnectCommand = new AsyncCommand(ToggleConnectionAsync, () => reader is not null);
        LiveCommand = new AsyncCommand(ToggleLiveAsync, () => Connected);
        ResetCommand = new AsyncCommand(async () => Dialogs.Dialogs.Show(await host.Batches.ResetCountersAsync(role)), () => host.Batches.Current is not null);
        host.Cameras.ConnectionChanged += (_, e) => { if (e.Role == role) OnUi(() => Connected = e.Connected); };
        host.Cameras.LiveImageArrived += (_, e) => { if (e.Role == role && _live) OnUi(() => Image = PngImage.FromPng(e.Image)); };
        host.Batches.Inspected += (_, e) => { if (e.Camera == role) OnUi(() => OnInspected(e)); };
        host.Batches.Changed += (_, _) => OnUi(Refresh);
        Refresh();
    }

    public CameraRole Role { get; }
    public string TitleKey => "camera.title." + Role;
    public string Address { get; }
    public ICommand ConnectCommand { get; }
    public ICommand LiveCommand { get; }
    public ICommand ResetCommand { get; }
    public ObservableCollection<string> FailList { get; } = new();

    public bool Connected { get => _connected; private set { if (Set(ref _connected, value)) Raise(nameof(ConnectKey)); } }
    public string ConnectKey => Connected ? "ui.disconnect" : "ui.connect";
    public bool Live { get => _live; private set { if (Set(ref _live, value)) Raise(nameof(LiveKey)); } }
    public string LiveKey => Live ? "ui.liveStop" : "ui.liveStart";
    public ImageSource? Image { get => _image; private set => Set(ref _image, value); }
    public string LastCode { get => _lastCode; private set => Set(ref _lastCode, value); }
    public string LastOutcomeText => _lastOutcome is { } o ? Loc.T("outcome." + o) : "—";
    public Brush LastOutcomeBrush => _lastOutcome switch { InspectionOutcome.Pass => Theme.Success, InspectionOutcome.Fail => Theme.Danger, InspectionOutcome.NoRead => Theme.Warning, _ => Theme.Neutral };
    public long Scanned { get => _scanned; private set => Set(ref _scanned, value); }
    public long Pass { get => _pass; private set => Set(ref _pass, value); }
    public long Fail { get => _fail; private set => Set(ref _fail, value); }
    public long NoRead { get => _noRead; private set => Set(ref _noRead, value); }

    private void OnInspected(InspectionEvent e)
    {
        _lastOutcome = e.Outcome;
        LastCode = e.ReadString.Length == 0 ? "N/A" : e.ReadString;
        Raise(nameof(LastOutcomeText));
        Raise(nameof(LastOutcomeBrush));
        if (!_live) Image = PngImage.FromPng(e.Image);
        if (e.Outcome != InspectionOutcome.Pass)
        {
            FailList.Insert(0, $"{e.Utc.ToLocalTime():HH:mm:ss}  {LastCode}  ({LastOutcomeText})");
            while (FailList.Count > 100) FailList.RemoveAt(FailList.Count - 1);
        }
        Refresh();
    }

    public void Refresh()
    {
        var snap = _host.Batches.Snapshot();
        var c = snap is null ? null : Role == CameraRole.Box ? snap.Box : snap.Leaflet;
        Scanned = c?.Scanned ?? 0;
        Pass = c?.Pass ?? 0;
        Fail = c?.Fail ?? 0;
        NoRead = c?.NoRead ?? 0;
        if (snap is null) FailList.Clear();
        else if (FailList.Count == 0)
        {
            // Restore the fail list after a restart (BAT-06).
            foreach (var r in _host.Batches.FailResults(Role).OrderByDescending(r => r.Id).Take(100))
                FailList.Add($"{r.Utc.ToLocalTime():HH:mm:ss}  {(r.ReadString.Length == 0 ? "N/A" : r.ReadString)}  ({Loc.T("outcome." + r.Outcome)})");
        }
    }

    private async Task ToggleConnectionAsync()
    {
        if (Connected) Dialogs.Dialogs.Show(await _host.Cameras.DisconnectAsync(Role, () => _host.Batches.IsRunning));
        else Dialogs.Dialogs.Show(await _host.Cameras.ConnectAsync(Role));
    }

    private async Task ToggleLiveAsync()
    {
        if (Live) { await _host.Cameras.StopLiveAsync(Role); Live = false; }
        else { await _host.Cameras.StartLiveAsync(Role); Live = true; }
    }
}

/// <summary>Batch panel + process values + two camera panels (BAT-*, PLC-02).</summary>
public sealed class OperationViewModel : ObservableObject, IPageViewModel
{
    private readonly AppHost _host;
    private string _productName = "";
    private string _batchNo = "";
    private RecipeOption? _boxRecipe;
    private RecipeOption? _leafletRecipe;
    private string _speed = "—";
    private string _temp = "—";

    public OperationViewModel(AppHost host)
    {
        _host = host;
        BoxCamera = new CameraPanelViewModel(host, CameraRole.Box);
        LeafletCamera = new CameraPanelViewModel(host, CameraRole.Leaflet);
        CreateCommand = new AsyncCommand(async () => Dialogs.Dialogs.Show(await host.Batches.CreateAsync(ProductName, BatchNo, BoxRecipe?.Recipe?.Id, LeafletRecipe?.Recipe?.Id)), () => !HasBatch);
        StartCommand = new AsyncCommand(async () => Dialogs.Dialogs.Show(await host.Batches.StartAsync()), () => Status == BatchStatus.Created);
        PauseCommand = new AsyncCommand(async () => Dialogs.Dialogs.Show(await host.Batches.PauseAsync()), () => Status == BatchStatus.Running);
        ResumeCommand = new AsyncCommand(async () => Dialogs.Dialogs.Show(await host.Batches.ResumeAsync()), () => Status == BatchStatus.Paused);
        EndCommand = new AsyncCommand(EndAsync, () => HasBatch);

        host.Batches.Changed += (_, _) => OnUi(Refresh);
        host.Batches.Inspected += (_, _) => OnUi(() => Raise(nameof(FinishedGoods)));
        host.Recipes.Changed += (_, _) => OnUi(LoadRecipes);
        host.Machine.ProcessValueChanged += (_, c) => OnUi(() => OnProcess(c));
        host.Alarms.Changed += (_, _) => OnUi(() => { Raise(nameof(SpeedBrush)); Raise(nameof(TempBrush)); });
        host.Localizer.LanguageChanged += (_, _) => OnUi(() => { LoadRecipes(); Refresh(); });
        LoadRecipes();
        Refresh();
        OnProcessValue(Tags.SpeedActual, host.TagEngine.Value(Tags.SpeedActual));
        OnProcessValue(Tags.TempActual, host.TagEngine.Value(Tags.TempActual));
    }

    public CameraPanelViewModel BoxCamera { get; }
    public CameraPanelViewModel LeafletCamera { get; }
    public ObservableCollection<RecipeOption> BoxRecipes { get; } = new();
    public ObservableCollection<RecipeOption> LeafletRecipes { get; } = new();
    public ICommand CreateCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand EndCommand { get; }

    public string ProductName { get => _productName; set => Set(ref _productName, value); }
    public string BatchNo { get => _batchNo; set => Set(ref _batchNo, value); }
    public RecipeOption? BoxRecipe { get => _boxRecipe; set { if (Set(ref _boxRecipe, value)) Raise(nameof(SetpointText)); } }
    public RecipeOption? LeafletRecipe { get => _leafletRecipe; set => Set(ref _leafletRecipe, value); }
    public bool HasBatch => _host.Batches.Current is not null;
    public bool CanEditBatch => !HasBatch;
    public BatchStatus? Status => _host.Batches.Current?.Status;
    public string StatusText => Status is { } s ? Loc.T("batchStatus." + s) : Loc.T("ui.noBatch");
    public Brush StatusBrush => Status switch { BatchStatus.Running => Theme.Success, BatchStatus.Paused => Theme.Warning, BatchStatus.Created => Theme.Primary, _ => Theme.Neutral };
    public string StartText => _host.Batches.Current?.StartUtc is { } s ? s.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") : "—";
    public long FinishedGoods => _host.Batches.FinishedGoods();
    public string Speed { get => _speed; private set => Set(ref _speed, value); }
    public string Temperature { get => _temp; private set => Set(ref _temp, value); }
    public Brush SpeedBrush => _host.Alarms.IsRaised(SoftwareAlarms.SpeedOutOfRange) ? Theme.Danger : Theme.Text;
    public Brush TempBrush => _host.Alarms.IsRaised(SoftwareAlarms.TempOutOfRange) ? Theme.Danger : Theme.Text;

    public string SetpointText
    {
        get
        {
            var r = _host.Batches.Snapshot()?.BoxRecipe ?? BoxRecipe?.Recipe;
            return r is null ? "" : $"{Loc.T("col.speed")} {r.SpeedSetpoint:0.#} ± {r.SpeedTolerance:0.#} · {Loc.T("col.temp")} {r.TempSetpoint:0.#} ± {r.TempTolerance:0.#}";
        }
    }

    public void OnShown()
    {
        LoadRecipes();
        Refresh();
    }

    private void LoadRecipes()
    {
        var none = new RecipeOption(null, Loc.T("ui.notUsed"));
        BoxRecipes.Clear();
        BoxRecipes.Add(none);
        foreach (var r in _host.Recipes.List(CameraRole.Box)) BoxRecipes.Add(new RecipeOption(r, $"{r.Name} — {r.Barcode}"));
        LeafletRecipes.Clear();
        LeafletRecipes.Add(none);
        foreach (var r in _host.Recipes.List(CameraRole.Leaflet)) LeafletRecipes.Add(new RecipeOption(r, $"{r.Name} — {r.Barcode}"));
    }

    private void Refresh()
    {
        var snap = _host.Batches.Snapshot();
        if (snap is not null)
        {
            ProductName = snap.Batch.ProductName;
            BatchNo = snap.Batch.BatchNo;
            BoxRecipe = BoxRecipes.FirstOrDefault(o => o.Recipe?.Id == snap.BoxRecipe?.Id);
            LeafletRecipe = LeafletRecipes.FirstOrDefault(o => o.Recipe?.Id == snap.LeafletRecipe?.Id);
        }
        foreach (var name in new[] { nameof(HasBatch), nameof(CanEditBatch), nameof(Status), nameof(StatusText), nameof(StatusBrush), nameof(StartText), nameof(FinishedGoods), nameof(SetpointText) })
            Raise(name);
        BoxCamera.Refresh();
        LeafletCamera.Refresh();
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnProcess(TagChange c) => OnProcessValue(c.Tag.Id, c.New);

    private void OnProcessValue(string tagId, double? value)
    {
        var text = value?.ToString("0.#") ?? "—";
        if (tagId == Tags.SpeedActual) Speed = text;
        if (tagId == Tags.TempActual) Temperature = text;
    }

    private async Task EndAsync()
    {
        if (!Dialogs.Dialogs.Confirm("ui.endBatchConfirm", _host.Batches.Current?.BatchNo ?? "")) return;
        if (Dialogs.Dialogs.Show(await _host.Batches.EndAsync(), "ui.batchEnded"))
        {
            ProductName = "";
            BatchNo = "";
            BoxCamera.FailList.Clear();
            LeafletCamera.FailList.Clear();
            Refresh();
        }
    }
}
