using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CheckBarcode.Application.Alarms;
using CheckBarcode.Infrastructure;
using CheckBarcode.Wpf.Mvvm;
using CheckBarcode.Wpf.Shell;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Pages.Alarms;

public sealed record ActiveAlarmRow(string Id, string Time, string Text, string Severity, string State, string AckBy);
public sealed record AlarmHistoryRow(string Time, string Text, string Severity, string State, string User);

/// <summary>Active alarms with acknowledge, and alarm history (ALM-01, ALM-02). Separate from the audit trail.</summary>
public sealed class AlarmsViewModel : ObservableObject, IPageViewModel
{
    private readonly AppHost _host;
    private ActiveAlarmRow? _selected;
    private DateTime? _from = DateTime.Today.AddDays(-1);
    private DateTime? _to = DateTime.Today;

    public AlarmsViewModel(AppHost host)
    {
        _host = host;
        AckCommand = new AsyncCommand(async () => Dialogs.Dialogs.Show(await host.Alarms.AcknowledgeAsync(Selected?.Id)), () => Selected is not null);
        AckAllCommand = new AsyncCommand(async () => Dialogs.Dialogs.Show(await host.Alarms.AcknowledgeAsync(null)), () => Active.Count > 0);
        LoadHistoryCommand = new RelayCommand(LoadHistory);
        host.Alarms.Changed += (_, _) => OnUi(LoadActive);
        host.Localizer.LanguageChanged += (_, _) => OnUi(OnShown);
        OnShown();
    }

    public ObservableCollection<ActiveAlarmRow> Active { get; } = new();
    public ObservableCollection<AlarmHistoryRow> History { get; } = new();
    public ActiveAlarmRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public DateTime? From { get => _from; set => Set(ref _from, value); }
    public DateTime? To { get => _to; set => Set(ref _to, value); }
    public ICommand AckCommand { get; }
    public ICommand AckAllCommand { get; }
    public ICommand LoadHistoryCommand { get; }

    public void OnShown()
    {
        LoadActive();
        LoadHistory();
    }

    private void LoadActive()
    {
        var lang = _host.Localizer.Language;
        Active.Clear();
        foreach (var a in _host.Alarms.Active)
        {
            var state = a.IsActive ? (a.Acknowledged ? "alarmState.Acknowledged" : "alarmState.Active") : "alarmState.Cleared";
            Active.Add(new ActiveAlarmRow(a.Definition.Id, _host.Time.DateTime(a.ActiveUtc), a.Definition.Text.Get(lang),
                Loc.T("severity." + a.Definition.Severity), Loc.T(state), a.AckBy ?? ""));
        }
    }

    private void LoadHistory()
    {
        var from = _host.Time.ToUtc((From ?? DateTime.Today).Date);
        var to = _host.Time.ToUtc((To ?? DateTime.Today).Date.AddDays(1));
        History.Clear();
        foreach (var r in _host.AlarmStore.Query(from, to, null, 5000).Reverse())
        {
            var text = _host.Alarms.Definition(r.AlarmId)?.Text.Get(_host.Localizer.Language) ?? r.AlarmId;
            History.Add(new AlarmHistoryRow(_host.Time.DateTime(r.Utc), text, Loc.T("severity." + r.Severity), Loc.T("alarmState." + r.State), r.Username));
        }
    }
}

public sealed class AlarmsView : UserControl
{
    public AlarmsView(AlarmsViewModel vm)
    {
        DataContext = vm;
        var active = UiKit.Table(nameof(AlarmsViewModel.Active), ("col.time", nameof(ActiveAlarmRow.Time), 1.2), ("col.alarm", nameof(ActiveAlarmRow.Text), 3),
            ("col.severity", nameof(ActiveAlarmRow.Severity), 0.8), ("col.state", nameof(ActiveAlarmRow.State), 1), ("ui.ackBy", nameof(ActiveAlarmRow.AckBy), 1));
        active.SetBinding(Selector.SelectedItemProperty, new System.Windows.Data.Binding(nameof(AlarmsViewModel.Selected)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        var activeButtons = UiKit.Row(
            UiKit.Button("ui.ack", null!, Theme.Warning, 200).Bind(ButtonBase.CommandProperty, nameof(AlarmsViewModel.AckCommand)),
            UiKit.Button("ui.ackAll", null!, Theme.Danger, 200).Bind(ButtonBase.CommandProperty, nameof(AlarmsViewModel.AckAllCommand)));
        var activePanel = new DockPanel();
        DockPanel.SetDock(activeButtons, Dock.Bottom);
        activePanel.Children.Add(activeButtons);
        activePanel.Children.Add(active);

        var filter = UiKit.Row(UiKit.Text("col.from"), UiKit.Date(nameof(AlarmsViewModel.From)), UiKit.Text("col.to"), UiKit.Date(nameof(AlarmsViewModel.To)),
            UiKit.Button("ui.filter", null!, Theme.Primary, 140).Bind(ButtonBase.CommandProperty, nameof(AlarmsViewModel.LoadHistoryCommand)));
        var history = UiKit.Table(nameof(AlarmsViewModel.History), ("col.time", nameof(AlarmHistoryRow.Time), 1.2), ("col.alarm", nameof(AlarmHistoryRow.Text), 3),
            ("col.severity", nameof(AlarmHistoryRow.Severity), 0.8), ("col.state", nameof(AlarmHistoryRow.State), 1), ("col.user", nameof(AlarmHistoryRow.User), 1));
        var historyPanel = new DockPanel();
        DockPanel.SetDock(filter, Dock.Top);
        historyPanel.Children.Add(filter);
        historyPanel.Children.Add(history);

        var root = UiKit.Layout("*", "*,*");
        root.Add(UiKit.Card(activePanel, "ui.activeAlarms").At(0), UiKit.Card(historyPanel, "ui.alarmHistory").At(1));
        Content = root;
    }
}
