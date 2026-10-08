using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CheckBarcode.Application.Alarms;
using CheckBarcode.Application.Plc;
using CheckBarcode.Application.Security;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure;
using CheckBarcode.Wpf.Dialogs;
using CheckBarcode.Wpf.Mvvm;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf.Shell;

/// <summary>Left navigation entry. Pages are created lazily and kept.</summary>
public sealed class NavItem : ObservableObject
{
    private readonly Func<FrameworkElement> _factory;
    private FrameworkElement? _view;
    private bool _isEnabled;
    private bool _isSelected;

    public NavItem(string titleKey, string icon, string? permission, Func<FrameworkElement> factory)
    {
        TitleKey = titleKey;
        Icon = icon;
        Permission = permission;
        _factory = factory;
    }

    public string TitleKey { get; }
    public string Icon { get; }
    /// <summary>Permission needed to open the page; null = visible to everybody (even without login).</summary>
    public string? Permission { get; }
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public FrameworkElement View => _view ??= _factory();
}

public sealed class ShellViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _timer;
    private int _tick;
    private string _clock = "";
    private string _userText = "";
    private string _roleText = "";
    private bool _isLoggedIn;
    private DateTime _loginDialogClosedUtc = DateTime.MinValue;
    private bool _plcOnline;
    private bool _machineRunning;
    private string _diskText = "";
    private string _alarmText = "";
    private int _alarmCount;
    private NavItem? _current;

    public ShellViewModel(AppHost host, IReadOnlyList<NavItem> pages)
    {
        _host = host;
        Pages = new ObservableCollection<NavItem>(pages);
        NavigateCommand = new RelayCommand(p => Navigate(p as NavItem), p => p is NavItem { IsEnabled: true });
        LoginCommand = new RelayCommand(LoginOrLogout);
        ChangePasswordCommand = new RelayCommand(ChangePassword, () => IsLoggedIn);
        LanguageCommand = new RelayCommand(ToggleLanguage);
        ExitCommand = new AsyncCommand(ExitAsync, () => CanExit);
        AlarmsCommand = new RelayCommand(() => Navigate(Pages.FirstOrDefault(p => p.TitleKey == "ui.nav.alarms")));

        host.Session.Changed += (_, reason) => OnUi(() => OnSessionChanged(reason));
        host.Machine.ConnectionChanged += (_, online) => OnUi(() => PlcOnline = online);
        host.TagEngine.TagChanged += (_, c) => { if (c.Tag.Id == Tags.MachineRunning) OnUi(() => MachineRunning = c.New != 0); };
        host.Alarms.Changed += (_, _) => OnUi(RefreshAlarms);
        host.Localizer.LanguageChanged += (_, _) => OnUi(() => { RefreshUser(); RefreshAlarms(); Raise(nameof(LanguageText)); Raise(nameof(PlcText)); Raise(nameof(MachineText)); });

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        PlcOnline = host.Machine.IsOnline;
        RefreshUser();
        RefreshAlarms();
        RefreshDisk();
        OnTick();
        Navigate(Pages.First());
    }

    public ObservableCollection<NavItem> Pages { get; }
    public ICommand NavigateCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand ChangePasswordCommand { get; }
    public ICommand LanguageCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand AlarmsCommand { get; }

    public string MachineTitle => $"CheckBarcode v2 · {_host.Config.MachineName} · {_host.Config.MachineCode}";
    public string Clock { get => _clock; private set => Set(ref _clock, value); }
    public string UserText { get => _userText; private set => Set(ref _userText, value); }
    public string RoleText { get => _roleText; private set => Set(ref _roleText, value); }
    public bool IsLoggedIn { get => _isLoggedIn; private set { if (Set(ref _isLoggedIn, value)) Raise(nameof(LoginKey)); } }
    public string LoginKey => IsLoggedIn ? "ui.logout" : "ui.login";
    public bool CanExit => _host.Permissions.Has(Permissions.SystemExit);
    public string LanguageText => _host.Localizer.Language == "vi" ? "VI | en" : "vi | EN";
    public string DiskText { get => _diskText; private set => Set(ref _diskText, value); }
    public string AlarmText { get => _alarmText; private set => Set(ref _alarmText, value); }
    public int AlarmCount { get => _alarmCount; private set { if (Set(ref _alarmCount, value)) Raise(nameof(HasAlarm)); } }
    public bool HasAlarm => AlarmCount > 0;
    public FrameworkElement? CurrentView => _current?.View;

    public bool PlcOnline
    {
        get => _plcOnline;
        private set { if (Set(ref _plcOnline, value)) { Raise(nameof(PlcText)); Raise(nameof(PlcBrush)); } }
    }

    public string PlcText => Loc.T(PlcOnline ? "ui.plcOnline" : "ui.plcOffline");
    public Brush PlcBrush => PlcOnline ? Theme.Success : Theme.Danger;

    public bool MachineRunning
    {
        get => _machineRunning;
        private set { if (Set(ref _machineRunning, value)) { Raise(nameof(MachineText)); Raise(nameof(MachineBrush)); } }
    }

    public string MachineText => Loc.T(MachineRunning ? "ui.machineRunning" : "ui.machineStopped");
    public Brush MachineBrush => MachineRunning ? Theme.Success : Theme.Neutral;

    public void Navigate(NavItem? item)
    {
        if (item is null || !item.IsEnabled) return;
        if (_current is not null) _current.IsSelected = false;
        _current = item;
        item.IsSelected = true;
        if (item.View.DataContext is IPageViewModel page) page.OnShown();
        Raise(nameof(CurrentView));
    }

    private void OnTick()
    {
        Clock = DateTime.Now.ToString("dd/MM/yyyy  HH:mm:ss");
        _host.Tick();
        if (++_tick % 60 == 0) RefreshDisk();
    }

    private void OnSessionChanged(LogoutReason? reason)
    {
        RefreshUser();
        if (reason == LogoutReason.Idle) Dialogs.Dialogs.Info("ui.autoLogout");
    }

    private void RefreshUser()
    {
        var user = _host.Session.CurrentUser;
        IsLoggedIn = user is not null;
        UserText = user is null ? Loc.T("ui.notLoggedIn") : $"{user.Username}";
        RoleText = user is null ? "" : Loc.T("value." + user.RoleCode);
        foreach (var p in Pages) p.IsEnabled = p.Permission is null || _host.Permissions.Has(p.Permission);
        Raise(nameof(CanExit));
        if (_current is { IsEnabled: false }) Navigate(Pages.First());
        // Re-evaluate the visible page for the new user (permission-bound editors, e.g. Recipe).
        else if (_current?.View.DataContext is IPageViewModel page) page.OnShown();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshAlarms()
    {
        var active = _host.Alarms.Active;
        AlarmCount = active.Count(a => !a.Acknowledged || a.IsActive);
        var top = active.FirstOrDefault(a => !a.Acknowledged) ?? active.FirstOrDefault();
        AlarmText = top is null ? "" : $"{top.ActiveUtc.ToLocalTime():HH:mm:ss}  {top.Definition.Text.Get(_host.Localizer.Language)}" + (active.Count > 1 ? $"  (+{active.Count - 1})" : "");
    }

    private void RefreshDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(Path.Combine(_host.BaseDirectory, _host.Config.Paths.Data)));
            var drive = new DriveInfo(root!);
            var free = drive.AvailableFreeSpace * 100.0 / drive.TotalSize;
            DiskText = $"{Loc.T("ui.disk")} {free:0}%";
            _host.Alarms.Set(SoftwareAlarms.DiskSpaceLow, free < _host.Settings.Current.DiskWarnFreePercent);
        }
        catch (Exception)
        {
            DiskText = "";
        }
    }

    private void LoginOrLogout()
    {
        if (_host.Session.IsLoggedIn)
        {
            // Ignore a logout request arriving right after the login dialog closed (bounced click / Enter).
            if ((DateTime.UtcNow - _loginDialogClosedUtc).TotalMilliseconds < 800) return;
            _host.Auth.Logout(LogoutReason.Manual);
            return;
        }
        new LoginDialog(_host.Auth).ShowDialog();
        _loginDialogClosedUtc = DateTime.UtcNow;
    }

    private void ChangePassword()
    {
        var user = _host.Session.CurrentUser;
        if (user is null) return;
        new LoginDialog(_host.Auth, user.Username, changePasswordOnly: true).ShowDialog();
    }

    private void ToggleLanguage() => _host.Localizer.SetLanguage(_host.Localizer.Language == "vi" ? "en" : "vi");

    private async Task ExitAsync()
    {
        if (!Dialogs.Dialogs.Confirm("ui.exitConfirm")) return;
        var outcome = await _host.Dispatcher.ExecuteAsync("APP_EXIT", _ => { });
        if (!Dialogs.Dialogs.Show(outcome)) return;
        MainWindow.AllowClose = true;
        System.Windows.Application.Current.Shutdown();
    }
}

/// <summary>Implemented by page view-models that refresh when shown.</summary>
public interface IPageViewModel
{
    void OnShown();
}
