using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Alarms;
using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Cameras;

public sealed record CameraResultEvent(CameraRole Role, CodeReadResult Result);

/// <summary>Owns the code readers: connect / disconnect, live view, settings (CAM-01..05).</summary>
public sealed class CameraService
{
    public const string ConnectAction = "CAMERA_CONNECT";
    public const string DisconnectAction = "CAMERA_DISCONNECT";
    public const string SettingsAction = "CAMERA_SETTINGS";

    private readonly Dictionary<CameraRole, ICodeReader> _readers;
    private readonly ActionDispatcher _dispatcher;
    private readonly AuditTrail _audit;
    private readonly AlarmService _alarms;
    private readonly IEventLog _events;
    private readonly HashSet<CameraRole> _manuallyDisconnected = new();

    public CameraService(IEnumerable<ICodeReader> readers, ActionDispatcher dispatcher, AuditTrail audit, AlarmService alarms, IEventLog events)
    {
        _readers = readers.ToDictionary(r => r.Role);
        _dispatcher = dispatcher;
        _audit = audit;
        _alarms = alarms;
        _events = events;

        _alarms.Define(SoftwareAlarms.BoxCameraOffline, AlarmSeverity.High, LocalizedText.Of("Mất kết nối camera Hộp", "Box camera disconnected"));
        _alarms.Define(SoftwareAlarms.LeafletCameraOffline, AlarmSeverity.High, LocalizedText.Of("Mất kết nối camera Toa", "Leaflet camera disconnected"));

        foreach (var reader in _readers.Values)
        {
            var role = reader.Role;
            reader.ResultArrived += (_, r) => ResultArrived?.Invoke(this, new CameraResultEvent(role, r));
            reader.LiveImageArrived += (_, img) => LiveImageArrived?.Invoke(this, (role, img));
            reader.ConnectionChanged += (_, connected) => OnConnectionChanged(role, connected);
        }
    }

    public event EventHandler<CameraResultEvent>? ResultArrived;
    public event EventHandler<(CameraRole Role, byte[] Image)>? LiveImageArrived;
    public event EventHandler<(CameraRole Role, bool Connected)>? ConnectionChanged;

    public IReadOnlyCollection<ICodeReader> Readers => _readers.Values;
    public ICodeReader? Reader(CameraRole role) => _readers.TryGetValue(role, out var r) ? r : null;
    public bool IsConnected(CameraRole role) => Reader(role)?.IsConnected == true;

    /// <summary>Automatic connection at start-up (system, not a user action).</summary>
    public async Task ConnectAllAsync(CancellationToken ct)
    {
        foreach (var reader in _readers.Values)
        {
            try { await reader.ConnectAsync(ct); }
            catch (Exception ex) { _events.Write("Camera", "CONNECT_FAILED", $"{reader.Role}: {ex.Message}"); }
        }
    }

    private int _reconnecting;

    /// <summary>Retries readers that dropped without a manual disconnect (called periodically by the host).</summary>
    public async Task ReconnectLostAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _reconnecting, 1) == 1) return;
        try
        {
            foreach (var reader in _readers.Values.Where(r => !r.IsConnected && !_manuallyDisconnected.Contains(r.Role)))
            {
                try { await reader.ConnectAsync(ct); }
                catch (Exception) { /* next attempt later */ }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    public Task<ActionOutcome> ConnectAsync(CameraRole role) =>
        _dispatcher.ExecuteAsync(ConnectAction, async s =>
        {
            var reader = Reader(role) ?? throw new DomainException("msg.cameraNotConfigured", role);
            s.Target("Camera", role.ToString());
            _manuallyDisconnected.Remove(role);
            await reader.ConnectAsync(CancellationToken.None);
            if (!reader.IsConnected) throw new DomainException("msg.cameraConnectFailed", reader.Address);
        });

    /// <summary>Disconnect requires a logged-in user (USR-09).</summary>
    public Task<ActionOutcome> DisconnectAsync(CameraRole role, Func<bool> batchRunning) =>
        _dispatcher.ExecuteAsync(DisconnectAction, async s =>
        {
            var reader = Reader(role) ?? throw new DomainException("msg.cameraNotConfigured", role);
            if (batchRunning()) throw new DomainException("msg.cannotDisconnectWhileRunning");
            s.Target("Camera", role.ToString());
            _manuallyDisconnected.Add(role);
            await reader.DisconnectAsync();
        });

    public Task<ActionOutcome> SaveSettingsAsync(CameraRole role, CameraSettings settings) =>
        _dispatcher.ExecuteAsync(SettingsAction, async s =>
        {
            var reader = Reader(role) ?? throw new DomainException("msg.cameraNotConfigured", role);
            if (!reader.IsConnected) throw new DomainException("msg.cameraNotConnected", role);
            var before = (await reader.GetSettingsAsync()).Fields().ToDictionary(f => f.Field, f => f.Value);
            await reader.SetSettingsAsync(settings);
            s.Target("Camera", role.ToString());
            foreach (var (field, value) in settings.Fields()) s.Change(field, before[field], value);
        });

    public Task StartLiveAsync(CameraRole role) => Reader(role)?.StartLiveAsync() ?? Task.CompletedTask;
    public Task StopLiveAsync(CameraRole role) => Reader(role)?.StopLiveAsync() ?? Task.CompletedTask;
    public Task TriggerAsync(CameraRole role) => Reader(role)?.TriggerAsync() ?? Task.CompletedTask;

    private void OnConnectionChanged(CameraRole role, bool connected)
    {
        var reader = _readers[role];
        _events.Write("Camera", connected ? "CONNECTED" : "DISCONNECTED", $"{role} {reader.Address}");
        _audit.Write(AuditCodes.CameraConnection, "Camera", role.ToString(), "Connected",
            (!connected).ToString().ToLowerInvariant(), connected.ToString().ToLowerInvariant(),
            args: new Dictionary<string, string> { ["camera"] = role.ToString(), ["ip"] = reader.Address });
        var alarmId = role == CameraRole.Box ? SoftwareAlarms.BoxCameraOffline : SoftwareAlarms.LeafletCameraOffline;
        _alarms.Set(alarmId, !connected && !_manuallyDisconnected.Contains(role));
        ConnectionChanged?.Invoke(this, (role, connected));
    }
}
