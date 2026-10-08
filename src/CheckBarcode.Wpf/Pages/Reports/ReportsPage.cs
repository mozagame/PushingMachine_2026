using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using CheckBarcode.Application.Reports;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure;
using CheckBarcode.Wpf.Mvvm;
using CheckBarcode.Wpf.Pages.Audit;
using CheckBarcode.Wpf.Shell;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Pages.Reports;

public enum ReportKind
{
    Batch,
    BatchList,
    Audit,
    Alarm,
    LogBox,
    LogLeaflet,
}

public sealed record ReportKindOption(ReportKind Kind, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// Report selection, preview, print and export (RPT-01..07). Audit/Alarm period defaults to the selected batch
/// (BTĐ item 8) and can be changed.
/// </summary>
public sealed class ReportsViewModel : ObservableObject, IPageViewModel
{
    private readonly AppHost _host;
    private ReportKindOption? _kind;
    private BatchOption? _batch;
    private DateTime? _from = DateTime.Today;
    private DateTime? _to = DateTime.Today;
    private string _fromTime = "00:00";
    private string _toTime = "23:59";
    private FlowDocument? _preview;
    private ReportDocument? _current;

    public ReportsViewModel(AppHost host)
    {
        _host = host;
        PreviewCommand = new RelayCommand(Preview);
        PrintCommand = new AsyncCommand(PrintAsync, () => _current is not null);
        SaveCommand = new AsyncCommand(() => ExportAsync(false), () => _current is not null);
        UsbCommand = new AsyncCommand(() => ExportAsync(true), () => _current is not null);
        host.Localizer.LanguageChanged += (_, _) => OnUi(OnShown);
    }

    public ObservableCollection<ReportKindOption> Kinds { get; } = new();
    public ObservableCollection<BatchOption> Batches { get; } = new();
    public ICommand PreviewCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand UsbCommand { get; }
    public FlowDocument? PreviewDocument { get => _preview; private set => Set(ref _preview, value); }
    public DateTime? From { get => _from; set => Set(ref _from, value); }
    public DateTime? To { get => _to; set => Set(ref _to, value); }
    public string FromTime { get => _fromTime; set => Set(ref _fromTime, value); }
    public string ToTime { get => _toTime; set => Set(ref _toTime, value); }
    public ReportKindOption? Kind { get => _kind; set => Set(ref _kind, value); }

    public BatchOption? Batch
    {
        get => _batch;
        set
        {
            if (!Set(ref _batch, value) || value?.Batch is not { } b) return;
            var (f, t) = _host.Reports.BatchPeriod(b);
            var lf = _host.Time.ToLocal(f);
            var lt = _host.Time.ToLocal(t);
            From = lf.Date;
            FromTime = lf.ToString("HH:mm");
            To = lt.Date;
            ToTime = lt.AddMinutes(1).ToString("HH:mm");
        }
    }

    public void OnShown()
    {
        var kind = Kind?.Kind ?? ReportKind.Batch;
        Kinds.Clear();
        foreach (var k in Enum.GetValues<ReportKind>()) Kinds.Add(new ReportKindOption(k, Loc.T("ui.report." + k)));
        Kind = Kinds.First(k => k.Kind == kind);
        Batches.Clear();
        foreach (var b in BatchOption.Load(_host)) Batches.Add(b);
        _batch = Batches.Skip(1).FirstOrDefault() ?? Batches.FirstOrDefault();
        Raise(nameof(Batch));
        if (_batch?.Batch is not null) Batch = _batch;
    }

    private (DateTime From, DateTime To) Range()
    {
        static TimeSpan T(string s, TimeSpan d) => TimeSpan.TryParse(s, out var t) ? t : d;
        var from = (From ?? DateTime.Today).Date + T(FromTime, TimeSpan.Zero);
        var to = (To ?? DateTime.Today).Date + T(ToTime, new TimeSpan(23, 59, 59));
        return (_host.Time.ToUtc(from), _host.Time.ToUtc(to));
    }

    private ReportDocument? Build()
    {
        var by = _host.Session.CurrentUser?.Username ?? "";
        var (from, to) = Range();
        var batch = Batch?.Batch;
        switch (Kind?.Kind)
        {
            case ReportKind.Batch:
                if (batch is null) { Dialogs.Dialogs.Warn("ui.selectBatch"); return null; }
                return _host.Reports.BatchReport(batch.Id, by);
            case ReportKind.BatchList:
                return _host.Reports.BatchListReport(from, to, null, batch?.BatchNo, by);
            case ReportKind.Audit:
                return _host.Reports.AuditReport(from, to, batch?.Id, by);
            case ReportKind.Alarm:
                return _host.Reports.AlarmReport(from, to, batch?.Id, by);
            case ReportKind.LogBox:
                return _host.Reports.InspectionLogReport(CameraRole.Box, from, to, by);
            case ReportKind.LogLeaflet:
                return _host.Reports.InspectionLogReport(CameraRole.Leaflet, from, to, by);
            default:
                return null;
        }
    }

    private void Preview()
    {
        _current = Build();
        PreviewDocument = _current is null ? null : FlowReportRenderer.Render(_current);
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task PrintAsync()
    {
        if (_current is null) return;
        if (FlowReportRenderer.Print(_current)) Dialogs.Dialogs.Show(await _host.Export.RecordPrintAsync(_current));
    }

    private async Task ExportAsync(bool usb)
    {
        if (_current is null) return;
        var (outcome, path) = await _host.Export.ExportAsync(_current, usb);
        Dialogs.Dialogs.Show(outcome, "msg.exported", path ?? "");
    }
}

public sealed class ReportsView : UserControl
{
    public ReportsView(ReportsViewModel vm)
    {
        DataContext = vm;
        var filter = UiKit.Form(
            ("ui.reportType", UiKit.Combo(nameof(ReportsViewModel.Kinds), nameof(ReportsViewModel.Kind), nameof(ReportKindOption.Display))),
            ("col.batchNo", UiKit.Combo(nameof(ReportsViewModel.Batches), nameof(ReportsViewModel.Batch), nameof(BatchOption.Display))),
            ("col.from", UiKit.Row(UiKit.Date(nameof(ReportsViewModel.From)), UiKit.TextBox(nameof(ReportsViewModel.FromTime), 90))),
            ("col.to", UiKit.Row(UiKit.Date(nameof(ReportsViewModel.To)), UiKit.TextBox(nameof(ReportsViewModel.ToTime), 90))));
        var buttons = UiKit.Wrap(
            UiKit.Button("ui.preview", null!, Theme.Primary, 180).Bind(ButtonBase.CommandProperty, nameof(ReportsViewModel.PreviewCommand)),
            UiKit.Button("ui.print", null!, Theme.Neutral, 180).Bind(ButtonBase.CommandProperty, nameof(ReportsViewModel.PrintCommand)),
            UiKit.Button("ui.savePdf", null!, Theme.Neutral, 180).Bind(ButtonBase.CommandProperty, nameof(ReportsViewModel.SaveCommand)),
            UiKit.Button("ui.exportUsb", null!, Theme.Success, 220).Bind(ButtonBase.CommandProperty, nameof(ReportsViewModel.UsbCommand)));
        var left = UiKit.Column(filter, buttons, UiKit.Text("ui.reportHint", Theme.FontSmall, color: Theme.TextMuted).Margin(4, 12, 4, 0));

        var viewer = new FlowDocumentScrollViewer { Background = System.Windows.Media.Brushes.White, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Zoom = 90 };
        viewer.SetBinding(FlowDocumentScrollViewer.DocumentProperty, new System.Windows.Data.Binding(nameof(ReportsViewModel.PreviewDocument)));

        var root = UiKit.Layout("560,*");
        root.Add(UiKit.Card(left, "ui.nav.reports").At(0, 0), UiKit.Card(viewer, "ui.preview").At(0, 1));
        Content = root;
    }
}
