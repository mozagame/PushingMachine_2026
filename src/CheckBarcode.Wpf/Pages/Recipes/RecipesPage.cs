using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CheckBarcode.Application.Ports;
using CheckBarcode.Application.Recipes;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure;
using CheckBarcode.Wpf.Mvvm;
using CheckBarcode.Wpf.Shell;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Pages.Recipes;

public sealed class RecipeRow
{
    public RecipeRow(Recipe r)
    {
        Recipe = r;
        Type = Loc.T("camera." + r.Type);
        Speed = r.Type == CameraRole.Box ? $"{r.SpeedSetpoint:0.#} ± {r.SpeedTolerance:0.#}" : "";
        Temp = r.Type == CameraRole.Box ? $"{r.TempSetpoint:0.#} ± {r.TempTolerance:0.#}" : "";
        Updated = r.UpdatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    }

    public Recipe Recipe { get; }
    public string Name => Recipe.Name;
    public string Type { get; }
    public string Barcode => Recipe.Barcode;
    public string Format => Recipe.BarcodeFormat.ToString();
    public string Speed { get; }
    public string Temp { get; }
    public int Version => Recipe.Version;
    public string Updated { get; }
}

/// <summary>Recipe management: create / edit / delete, read sample code from the camera (REC-01..05).</summary>
public sealed class RecipesViewModel : ObservableObject, IPageViewModel
{
    private readonly AppHost _host;
    private RecipeRow? _selected;
    private long? _editingId;
    private string _name = "", _barcode = "", _speed = "", _speedTol = "", _temp = "", _tempTol = "";
    private CameraRole _type = CameraRole.Box;
    private BarcodeFormat _format = BarcodeFormat.Pharmacode;
    private TaskCompletionSource<string>? _sampleWaiter;

    public RecipesViewModel(AppHost host)
    {
        _host = host;
        NewCommand = new RelayCommand(Clear);
        SaveCommand = new AsyncCommand(SaveAsync, () => CanManage);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => CanManage && _editingId.HasValue);
        ReadSampleCommand = new AsyncCommand(ReadSampleAsync, () => CanManage && _host.Cameras.IsConnected(Type) && !_host.Batches.IsRunning);
        host.Recipes.Changed += (_, _) => OnUi(Load);
        host.Cameras.ResultArrived += (_, e) => { if (e.Role == Type) _sampleWaiter?.TrySetResult(e.Result.ReadString); };
        Load();
    }

    public ObservableCollection<RecipeRow> Rows { get; } = new();
    public IReadOnlyList<CameraRole> Types { get; } = new[] { CameraRole.Box, CameraRole.Leaflet };
    public IReadOnlyList<BarcodeFormat> Formats { get; } = Enum.GetValues<BarcodeFormat>();
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ReadSampleCommand { get; }
    public bool CanManage => _host.Permissions.Has(Permissions.RecipeManage);

    public RecipeRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value) || value is null) return;
            var r = value.Recipe;
            _editingId = r.Id;
            Name = r.Name;
            Type = r.Type;
            Barcode = r.Barcode;
            Format = r.BarcodeFormat;
            Speed = Num(r.SpeedSetpoint);
            SpeedTol = Num(r.SpeedTolerance);
            Temp = Num(r.TempSetpoint);
            TempTol = Num(r.TempTolerance);
            Raise(nameof(EditingTitleKey));
        }
    }

    public string EditingTitleKey => _editingId is null ? "ui.newRecipe" : "ui.editRecipe";
    public string Name { get => _name; set => Set(ref _name, value); }
    public CameraRole Type { get => _type; set { if (Set(ref _type, value)) Raise(nameof(IsBox)); } }
    public bool IsBox => Type == CameraRole.Box;
    public BarcodeFormat Format { get => _format; set => Set(ref _format, value); }
    public string Barcode { get => _barcode; set => Set(ref _barcode, value); }
    public string Speed { get => _speed; set => Set(ref _speed, value); }
    public string SpeedTol { get => _speedTol; set => Set(ref _speedTol, value); }
    public string Temp { get => _temp; set => Set(ref _temp, value); }
    public string TempTol { get => _tempTol; set => Set(ref _tempTol, value); }

    public void OnShown() => Load();

    private void Load()
    {
        Rows.Clear();
        foreach (var r in _host.Recipes.List()) Rows.Add(new RecipeRow(r));
        Raise(nameof(CanManage));
    }

    private void Clear()
    {
        _editingId = null;
        _selected = null;
        Raise(nameof(Selected));
        Name = Barcode = Speed = SpeedTol = Temp = TempTol = "";
        Raise(nameof(EditingTitleKey));
    }

    private RecipeInput Input() => new(Name, Type, Barcode, Format, Parse(Speed), Parse(SpeedTol), Parse(Temp), Parse(TempTol));

    private async Task SaveAsync()
    {
        var outcome = _editingId is { } id ? await _host.Recipes.EditAsync(id, Input()) : await _host.Recipes.CreateAsync(Input());
        if (Dialogs.Dialogs.Show(outcome, "msg.saved")) Clear();
    }

    private async Task DeleteAsync()
    {
        if (_editingId is not { } id || !Dialogs.Dialogs.Confirm("ui.deleteRecipeConfirm", Name)) return;
        if (Dialogs.Dialogs.Show(await _host.Recipes.DeleteAsync(id))) Clear();
    }

    /// <summary>Triggers the camera and copies the read code into the form (REC-05).</summary>
    private async Task ReadSampleAsync()
    {
        _sampleWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _host.Cameras.TriggerAsync(Type);
        var done = await Task.WhenAny(_sampleWaiter.Task, Task.Delay(5000));
        if (done == _sampleWaiter.Task && !string.IsNullOrEmpty(_sampleWaiter.Task.Result)) Barcode = _sampleWaiter.Task.Result;
        else Dialogs.Dialogs.Warn("ui.noSampleRead");
        _sampleWaiter = null;
    }

    private static double? Parse(string s) =>
        double.TryParse(s?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string Num(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
}

public sealed class RecipesView : UserControl
{
    public RecipesView(RecipesViewModel vm)
    {
        DataContext = vm;
        var grid = UiKit.Table(nameof(RecipesViewModel.Rows),
            ("ui.recipeName", nameof(RecipeRow.Name), 2), ("field.Type", nameof(RecipeRow.Type), 0.8), ("col.barcode", nameof(RecipeRow.Barcode), 1),
            ("field.BarcodeFormat", nameof(RecipeRow.Format), 1), ("col.speed", nameof(RecipeRow.Speed), 1), ("col.temp", nameof(RecipeRow.Temp), 1),
            ("field.Version", nameof(RecipeRow.Version), 0.6), ("ui.updated", nameof(RecipeRow.Updated), 1.2));
        grid.SetBinding(Selector.SelectedItemProperty, new System.Windows.Data.Binding(nameof(RecipesViewModel.Selected)) { Mode = System.Windows.Data.BindingMode.TwoWay });

        var type = UiKit.Combo(nameof(RecipesViewModel.Types), nameof(RecipesViewModel.Type));
        var boxOnly = new FuncConverter(v => v is true ? Visibility.Visible : Visibility.Collapsed);
        var form = UiKit.Form(
            ("ui.recipeName", UiKit.TextBox(nameof(RecipesViewModel.Name))),
            ("field.Type", type),
            ("col.barcode", UiKit.Row(UiKit.TextBox(nameof(RecipesViewModel.Barcode), 220),
                UiKit.Button("ui.readSample", null!, Theme.Neutral, 170).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(RecipesViewModel.ReadSampleCommand)))),
            ("field.BarcodeFormat", UiKit.Combo(nameof(RecipesViewModel.Formats), nameof(RecipesViewModel.Format))),
            ("field.SpeedSetpoint", UiKit.TextBox(nameof(RecipesViewModel.Speed)).Bind(VisibilityProperty, nameof(RecipesViewModel.IsBox), boxOnly)),
            ("field.SpeedTolerance", UiKit.TextBox(nameof(RecipesViewModel.SpeedTol)).Bind(VisibilityProperty, nameof(RecipesViewModel.IsBox), boxOnly)),
            ("field.TempSetpoint", UiKit.TextBox(nameof(RecipesViewModel.Temp)).Bind(VisibilityProperty, nameof(RecipesViewModel.IsBox), boxOnly)),
            ("field.TempTolerance", UiKit.TextBox(nameof(RecipesViewModel.TempTol)).Bind(VisibilityProperty, nameof(RecipesViewModel.IsBox), boxOnly)));
        var title = UiKit.Text("", Theme.FontLarge, true).Bind(TextBlock.TextProperty, nameof(RecipesViewModel.EditingTitleKey), new FuncConverter(k => Loc.T((string)k!)));
        var buttons = UiKit.Wrap(
            UiKit.Button("ui.new", null!, Theme.Neutral, 150).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(RecipesViewModel.NewCommand)),
            UiKit.Button("ui.save", null!, Theme.Success, 150).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(RecipesViewModel.SaveCommand)),
            UiKit.Button("ui.delete", null!, Theme.Danger, 150).Bind(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, nameof(RecipesViewModel.DeleteCommand)));
        var editor = UiKit.Column(title, form, buttons, UiKit.Text("ui.recipeHint", Theme.FontSmall, color: Theme.TextMuted).Margin(4, 12, 4, 0));
        editor.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(RecipesViewModel.CanManage)));

        var root = UiKit.Layout("*,620");
        root.Add(UiKit.Card(grid, "ui.nav.recipes").At(0, 0), UiKit.Card(editor).At(0, 1));
        Content = root;
    }
}
