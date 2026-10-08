using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure;
using CheckBarcode.Wpf.Mvvm;
using CheckBarcode.Wpf.Shell;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Pages.Machine;

/// <summary>One cam (ON + OFF angle) in the editor.</summary>
public sealed class CamRow : ObservableObject
{
    private string _onNew = "", _offNew = "";
    private double? _onCurrent, _offCurrent;

    public CamRow(string name, TagDefinition on, TagDefinition off)
    {
        Name = name;
        OnTag = on;
        OffTag = off;
    }

    public string Name { get; }
    public TagDefinition OnTag { get; }
    public TagDefinition OffTag { get; }
    public double? OnCurrent { get => _onCurrent; set { if (Set(ref _onCurrent, value)) Raise(nameof(OnCurrentText)); } }
    public double? OffCurrent { get => _offCurrent; set { if (Set(ref _offCurrent, value)) Raise(nameof(OffCurrentText)); } }
    public string OnCurrentText => OnCurrent?.ToString("0", CultureInfo.InvariantCulture) ?? "—";
    public string OffCurrentText => OffCurrent?.ToString("0", CultureInfo.InvariantCulture) ?? "—";
    public string OnNew { get => _onNew; set => Set(ref _onNew, value); }
    public string OffNew { get => _offNew; set => Set(ref _offNew, value); }
}

public sealed record TagRow(string Id, string Text, string Address, string Category, string Value);

/// <summary>Maintenance screen: cam angles entered on the PC (PLC-04) and a live view of all PLC tags.</summary>
public sealed class MachineViewModel : ObservableObject, IPageViewModel
{
    private readonly AppHost _host;

    public MachineViewModel(AppHost host)
    {
        _host = host;
        WriteCommand = new AsyncCommand(WriteAsync, () => host.Machine.IsOnline && host.Permissions.Has(Permissions.MachineCamEdit));
        RefreshCommand = new RelayCommand(Refresh);
        var lang = host.Localizer.Language;
        var cams = host.Machine.CamSetpointTags();
        foreach (var on in cams.Where(t => t.Id.EndsWith("_ON_SP", StringComparison.Ordinal)))
        {
            var off = cams.FirstOrDefault(t => t.Id == on.Id.Replace("_ON_SP", "_OFF_SP"));
            if (off is null) continue;
            var name = on.Text.Get(lang).Replace(" ON", "");
            Cams.Add(new CamRow(name, on, off));
        }
        host.TagEngine.TagChanged += (_, c) => { if (c.Tag.Group == "CamRead") OnUi(RefreshCams); };
        Refresh();
    }

    public ObservableCollection<CamRow> Cams { get; } = new();
    public ObservableCollection<TagRow> TagValues { get; } = new();
    public ICommand WriteCommand { get; }
    public ICommand RefreshCommand { get; }

    public void OnShown() => Refresh();

    private void Refresh()
    {
        RefreshCams();
        var lang = _host.Localizer.Language;
        TagValues.Clear();
        foreach (var t in _host.TagEngine.Tags.OrderBy(t => t.IsWrite).ThenBy(t => t.Address).ThenBy(t => t.Bit ?? -1))
        {
            var value = t.IsWrite ? _host.TagEngine.LastWritten(t.Id) : _host.TagEngine.Value(t.Id);
            var address = t.Bit is { } b ? $"{t.Address}.{b}" : t.Address.ToString(CultureInfo.InvariantCulture);
            TagValues.Add(new TagRow(t.Id, t.Text.Get(lang), (t.IsWrite ? "W " : "R ") + address, t.Category.ToString(), value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—"));
        }
    }

    private void RefreshCams()
    {
        foreach (var c in Cams)
        {
            c.OnCurrent = _host.Machine.CurrentValue(c.OnTag);
            c.OffCurrent = _host.Machine.CurrentValue(c.OffTag);
        }
    }

    private async Task WriteAsync()
    {
        var values = new Dictionary<string, double>();
        foreach (var c in Cams)
        {
            if (TryParse(c.OnNew, out var on)) values[c.OnTag.Id] = on;
            if (TryParse(c.OffNew, out var off)) values[c.OffTag.Id] = off;
        }
        if (values.Count == 0)
        {
            Dialogs.Dialogs.Warn("ui.noCamChanges");
            return;
        }
        if (Dialogs.Dialogs.Show(await _host.Machine.WriteCamSetpointsAsync(values), "msg.saved"))
        {
            foreach (var c in Cams) c.OnNew = c.OffNew = "";
            await Task.Delay(500);
            Refresh();
        }
    }

    private static bool TryParse(string s, out double value) =>
        double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

public sealed class MachineView : UserControl
{
    public MachineView(MachineViewModel vm)
    {
        DataContext = vm;
        var cams = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            FontSize = Theme.FontNormal,
            RowHeight = 46,
            ColumnHeaderHeight = 44,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionUnit = DataGridSelectionUnit.Cell,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };
        cams.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MachineViewModel.Cams)));
        cams.Columns.Add(Column("ui.cam", nameof(CamRow.Name), 2, true));
        cams.Columns.Add(Column("ui.onCurrent", nameof(CamRow.OnCurrentText), 1, true));
        cams.Columns.Add(Column("ui.onNew", nameof(CamRow.OnNew), 1, false));
        cams.Columns.Add(Column("ui.offCurrent", nameof(CamRow.OffCurrentText), 1, true));
        cams.Columns.Add(Column("ui.offNew", nameof(CamRow.OffNew), 1, false));
        cams.PreparingCellForEdit += (_, e) => { if (e.EditingElement is TextBox tb) TouchKeyboard.Show(); };

        var buttons = UiKit.Row(
            UiKit.Button("ui.writeToPlc", null!, Theme.Success, 240).Bind(ButtonBase.CommandProperty, nameof(MachineViewModel.WriteCommand)),
            UiKit.Button("ui.refresh", null!, Theme.Neutral, 160).Bind(ButtonBase.CommandProperty, nameof(MachineViewModel.RefreshCommand)));
        var camPanel = new DockPanel();
        var hint = UiKit.Text("ui.camHint", Theme.FontSmall, color: Theme.TextMuted);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(hint, Dock.Top);
        camPanel.Children.Add(hint);
        camPanel.Children.Add(buttons);
        camPanel.Children.Add(cams);

        var tags = UiKit.Table(nameof(MachineViewModel.TagValues), ("ui.tag", nameof(TagRow.Id), 1.6), ("ui.description", nameof(TagRow.Text), 2),
            ("ui.address", nameof(TagRow.Address), 0.6), ("field.Type", nameof(TagRow.Category), 0.8), ("ui.value", nameof(TagRow.Value), 0.6));

        var root = UiKit.Layout("*,*");
        root.Add(UiKit.Card(camPanel, "ui.camSettings").At(0, 0), UiKit.Card(tags, "ui.plcTags").At(0, 1));
        Content = root;
    }

    private static DataGridTextColumn Column(string headerKey, string path, double width, bool readOnly)
    {
        var header = new TextBlock { FontWeight = FontWeights.SemiBold };
        header.SetBinding(TextBlock.TextProperty, Loc.Bind(headerKey));
        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            IsReadOnly = readOnly,
            Width = new DataGridLength(width, DataGridLengthUnitType.Star),
        };
    }
}
