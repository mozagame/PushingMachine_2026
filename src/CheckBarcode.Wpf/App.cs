using System.Net;
using System.Windows;
using System.Windows.Threading;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Ports;
using CheckBarcode.Devices.Cognex;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure;
using CheckBarcode.Infrastructure.Plc;
using CheckBarcode.Simulator;
using CheckBarcode.Wpf.Dialogs;
using CheckBarcode.Wpf.Pages.Alarms;
using CheckBarcode.Wpf.Pages.Audit;
using CheckBarcode.Wpf.Pages.Images;
using CheckBarcode.Wpf.Pages.Machine;
using CheckBarcode.Wpf.Pages.Operation;
using CheckBarcode.Wpf.Pages.Recipes;
using CheckBarcode.Wpf.Pages.Reports;
using CheckBarcode.Wpf.Pages.Settings;
using CheckBarcode.Wpf.Pages.Users;
using CheckBarcode.Wpf.Shell;
using CheckBarcode.Wpf.Ui;

namespace CheckBarcode.Wpf;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var mutex = new Mutex(true, "Apillis.CheckBarcode.v2", out var first);
        if (!first)
        {
            MessageBox.Show("CheckBarcode is already running / Phần mềm đang chạy.", "CheckBarcode", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        var app = new App();
        return app.Run();
    }
}

/// <summary>Application object: composition root for the UI (devices, pages, shell).</summary>
public sealed class App : System.Windows.Application
{
    private AppHost? _host;
    private ModbusTcpServer? _plcSimulator;
    private MachineModel? _machineSimulator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
        try
        {
            _host = new AppHost(new AppHostOptions
            {
                BaseDirectory = AppContext.BaseDirectory,
                ReaderFactory = CreateReader,
                PlcFactory = CreatePlc,
            });
            Loc.Instance.Attach(_host.Localizer);
            _host.Dispatcher.Prompt = new WpfActionPrompt(() => _host.Session.CurrentUser?.Username, () => _host.Localizer.Language);
            await _host.StartAsync();

            var host = _host;
            var pages = new List<NavItem>
            {
                new("ui.nav.operation", "▶", null, () => new OperationView(new OperationViewModel(host))),
                new("ui.nav.recipes", "☰", null, () => new RecipesView(new RecipesViewModel(host))),
                new("ui.nav.alarms", "⚠", null, () => new AlarmsView(new AlarmsViewModel(host))),
                new("ui.nav.audit", "✎", Permissions.AuditView, () => new AuditView(new AuditViewModel(host))),
                new("ui.nav.reports", "▤", Permissions.ReportView, () => new ReportsView(new ReportsViewModel(host))),
                new("ui.nav.images", "▣", Permissions.ImageView, () => new ImagesView(new ImagesViewModel(host))),
                new("ui.nav.machine", "⚙", Permissions.MachineCamEdit, () => new MachineView(new MachineViewModel(host))),
                new("ui.nav.users", "☺", Permissions.UserManage, () => new UsersView(new UsersViewModel(host))),
                new("ui.nav.settings", "⚒", Permissions.MachineSettings, () => new SettingsView(new SettingsViewModel(host))),
            };
            var shell = new ShellViewModel(host, pages);
            // Any input in any window (dialogs included) counts as activity for the idle logout (USR-07).
            System.Windows.Input.InputManager.Current.PreProcessInput += (_, _) => host.Session.Touch();
            MainWindow = new Shell.MainWindow(shell);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "CheckBarcode — start-up error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try { _host.StopAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
            _host.Dispose();
        }
        _machineSimulator?.Dispose();
        _plcSimulator?.Dispose();
        base.OnExit(e);
    }

    private static ICodeReader CreateReader(CameraConfig config)
    {
        if (!string.Equals(config.Driver, "Simulator", StringComparison.OrdinalIgnoreCase)) return new CognexCodeReader(config);
        var sim = new SimulatedCodeReader(config.Name, config.Role, config.Ip);
        sim.StartAuto();
        return sim;
    }

    /// <summary>"Simulator" driver: an in-process PLC simulator for demos and FAT without the machine.</summary>
    private IPlcClient CreatePlc(PlcConfig config)
    {
        if (!string.Equals(config.Driver, "Simulator", StringComparison.OrdinalIgnoreCase))
            return new ModbusTcpClient(config.Ip, config.Port, config.UnitId, config.TimeoutMs);
        _plcSimulator = new ModbusTcpServer(0, IPAddress.Loopback);
        _plcSimulator.Start();
        _machineSimulator = new MachineModel(_plcSimulator) { Running = true };
        return new ModbusTcpClient("127.0.0.1", _plcSimulator.Port, config.UnitId, config.TimeoutMs);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Logger.Error("Unhandled UI exception", e.Exception);
        Dialogs.Dialogs.Error(e.Exception);
        e.Handled = true;
    }
}
