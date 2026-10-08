using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure;
using CheckBarcode.Wpf.Mvvm;
using CheckBarcode.Wpf.Shell;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Pages.Audit;

public sealed record AuditRow(long Seq, string Time, string User, string Role, string Event, string Reason, string Signature);

/// <summary>Batch selector entry shared by the audit and report pages. Batch = null means "all".</summary>
public sealed record BatchOption(Batch? Batch, string Display)
{
    public override string ToString() => Display;

    public static List<BatchOption> Load(AppHost host)
    {
        var list = new List<BatchOption> { new(null, Loc.T("ui.allBatches")) };
        list.AddRange(host.BatchesRepo.List(null, null).OrderByDescending(b => b.Id)
            .Select(b => new BatchOption(b, $"{b.BatchNo} — {b.ProductName} ({host.Time.Date(b.StartUtc ?? b.CreatedUtc)})")));
        return list;
    }
}

/// <summary>Audit trail viewer, integrity check and signed review (AUD-02, AUD-03, AUD-05).</summary>
public sealed class AuditViewModel : ObservableObject, IPageViewModel
{
    private readonly AppHost _host;
    private DateTime? _from = DateTime.Today;
    private DateTime? _to = DateTime.Today;
    private BatchOption? _batch;
    private string _user = "";
    private string _integrity = "";

    public AuditViewModel(AppHost host)
    {
        _host = host;
        FilterCommand = new RelayCommand(Load);
        VerifyCommand = new RelayCommand(Verify);
        ReviewCommand = new AsyncCommand(ReviewAsync, () => host.Permissions.Has(Permissions.AuditReview));
        host.Localizer.LanguageChanged += (_, _) => OnUi(OnShown);
    }

    public ObservableCollection<AuditRow> Rows { get; } = new();
    public ObservableCollection<BatchOption> Batches { get; } = new();
    public ICommand FilterCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand ReviewCommand { get; }
    public DateTime? From { get => _from; set => Set(ref _from, value); }
    public DateTime? To { get => _to; set => Set(ref _to, value); }
    public string User { get => _user; set => Set(ref _user, value); }
    public string Integrity { get => _integrity; private set => Set(ref _integrity, value); }

    public BatchOption? Batch
    {
        get => _batch;
        set
        {
            if (!Set(ref _batch, value) || value?.Batch is not { } b) return;
            var (f, t) = _host.Reports.BatchPeriod(b);
            From = _host.Time.ToLocal(f).Date;
            To = _host.Time.ToLocal(t).Date;
        }
    }

    public void OnShown()
    {
        Batches.Clear();
        foreach (var b in BatchOption.Load(_host)) Batches.Add(b);
        _batch = Batches.FirstOrDefault();
        Raise(nameof(Batch));
        Load();
    }

    private (DateTime From, DateTime To) Range() =>
        (_host.Time.ToUtc((From ?? DateTime.Today).Date), _host.Time.ToUtc((To ?? DateTime.Today).Date.AddDays(1)));

    private void Load()
    {
        var (from, to) = Range();
        Rows.Clear();
        var records = _host.AuditStore.Query(new AuditQuery(from, to, Batch?.Batch?.Id, string.IsNullOrWhiteSpace(User) ? null : User.Trim(), Limit: 20_000));
        foreach (var r in records.Reverse())
        {
            var user = r.Username + (r.SessionState == SessionState.Expired ? " *" : "");
            Rows.Add(new AuditRow(r.Seq, _host.Time.DateTime(r.Utc), user, r.Role.Length == 0 ? "" : Loc.T("value." + r.Role), _host.Formatter.Message(r), r.Reason, r.SignatureMeaning ?? ""));
        }
    }

    private void Verify()
    {
        var a = _host.AuditStore.Verify();
        var b = _host.AlarmStore.Verify();
        Integrity = a.Ok && b.Ok ? Loc.T("report.integrityOk", a.Checked + b.Checked) : Loc.T("report.integrityBroken", a.FirstBrokenSeq ?? b.FirstBrokenSeq ?? 0);
    }

    private async Task ReviewAsync()
    {
        var (from, to) = Range();
        if (Dialogs.Dialogs.Show(await _host.Review.ReviewAsync(from, to, Batch?.Batch?.Id), "ui.reviewDone")) Load();
    }
}

public sealed class AuditView : UserControl
{
    public AuditView(AuditViewModel vm)
    {
        DataContext = vm;
        var filter = UiKit.Wrap(
            UiKit.Text("col.from"), UiKit.Date(nameof(AuditViewModel.From)), UiKit.Text("col.to"), UiKit.Date(nameof(AuditViewModel.To)),
            UiKit.Text("col.batchNo"), UiKit.Combo(nameof(AuditViewModel.Batches), nameof(AuditViewModel.Batch), nameof(BatchOption.Display), 320),
            UiKit.Text("col.user"), UiKit.TextBox(nameof(AuditViewModel.User), 160),
            UiKit.Button("ui.filter", null!, Theme.Primary, 130).Bind(ButtonBase.CommandProperty, nameof(AuditViewModel.FilterCommand)),
            UiKit.Button("ui.verifyIntegrity", null!, Theme.Neutral, 220).Bind(ButtonBase.CommandProperty, nameof(AuditViewModel.VerifyCommand)),
            UiKit.Button("ui.review", null!, Theme.Success, 220).Bind(ButtonBase.CommandProperty, nameof(AuditViewModel.ReviewCommand)));
        var integrity = UiKit.Value(nameof(AuditViewModel.Integrity), Theme.FontNormal, true, Theme.Primary).Margin(6);
        var grid = UiKit.Table(nameof(AuditViewModel.Rows), ("#", nameof(AuditRow.Seq), 0.4), ("col.time", nameof(AuditRow.Time), 1.1),
            ("col.user", nameof(AuditRow.User), 0.8), ("col.role", nameof(AuditRow.Role), 0.7), ("col.event", nameof(AuditRow.Event), 3.5),
            ("col.reason", nameof(AuditRow.Reason), 1.2), ("col.signature", nameof(AuditRow.Signature), 1.1));
        var legend = UiKit.Text("report.audit.legend", Theme.FontSmall, color: Theme.TextMuted).Margin(6);
        var dock = new DockPanel();
        DockPanel.SetDock(filter, Dock.Top);
        DockPanel.SetDock(integrity, Dock.Top);
        DockPanel.SetDock(legend, Dock.Bottom);
        dock.Children.Add(filter);
        dock.Children.Add(integrity);
        dock.Children.Add(legend);
        dock.Children.Add(grid);
        Content = UiKit.Card(dock, "ui.nav.audit");
    }
}
