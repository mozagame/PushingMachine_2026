using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Alarms;
using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Ports;
using CheckBarcode.Application.Security;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Plc;

/// <summary>Well-known tag ids used by the application logic (all defined in tags.json).</summary>
public static class Tags
{
    public const string MachineRunning = "MACHINE_RUNNING";
    public const string SpeedActual = "SPEED_ACTUAL";
    public const string TempActual = "TEMP_ACTUAL";
    public const string LoginLevel = "LOGIN_LEVEL";
    public const string BoxCamScanning = "BOX_CAM_SCANNING";
    public const string LeafletCamScanning = "LEAFLET_CAM_SCANNING";
    public const string SpeedSetpoint = "SPEED_SETPOINT";
    public const string TempSetpoint = "TEMP_SETPOINT";
    public const string CamGroup = "Cam";
}

/// <summary>
/// Routes tag changes by category (PLC-03): Alarm → AlarmService, Parameter → AuditTrail,
/// State → EventLog (+ audit), Process → <see cref="ProcessValueChanged"/>.
/// Also owns PC → PLC writes (login level, cam setpoints).
/// </summary>
public sealed class MachineService
{
    public const string CamEditAction = "MACHINE_CAM_EDIT";

    private readonly TagEngine _tags;
    private readonly AuditTrail _audit;
    private readonly AlarmService _alarms;
    private readonly SessionService _session;
    private readonly ActionDispatcher _dispatcher;
    private readonly IEventLog _events;
    private readonly IReadOnlyDictionary<string, int>? _loginLevels;

    public MachineService(TagEngine tags, AuditTrail audit, AlarmService alarms, SessionService session, ActionDispatcher dispatcher, IEventLog events,
        IReadOnlyDictionary<string, int>? loginLevels = null)
    {
        _loginLevels = loginLevels is null ? null : new Dictionary<string, int>(loginLevels, StringComparer.OrdinalIgnoreCase);
        _tags = tags;
        _audit = audit;
        _alarms = alarms;
        _session = session;
        _dispatcher = dispatcher;
        _events = events;

        foreach (var tag in _tags.Tags.Where(t => t.Category == TagCategory.Alarm))
            _alarms.Define(tag.Id, tag.Severity, tag.Text);
        _alarms.Define(SoftwareAlarms.PlcCommunication, AlarmSeverity.Critical, LocalizedText.Of("Mất kết nối PLC", "PLC communication lost"));

        _tags.TagChanged += OnTagChanged;
        _tags.ConnectionChanged += OnConnectionChanged;
        _tags.CommunicationError += (_, msg) => _events.Write("PLC", msg is null ? "COMM_OK" : "COMM_ERROR", msg ?? "");
        _session.Changed += (_, _) => _ = PushLoginLevelAsync();
    }

    public TagEngine Engine => _tags;
    public bool IsOnline => _tags.IsOnline;

    public event EventHandler<TagChange>? ProcessValueChanged;
    public event EventHandler<bool>? ConnectionChanged;

    public IReadOnlyList<TagDefinition> CamSetpointTags() =>
        _tags.Tags.Where(t => t.IsWrite && t.Group == Tags.CamGroup).OrderBy(t => t.Address).ToList();

    /// <summary>Current value of a cam: PLC readback if available, else last value written by the PC.</summary>
    public double? CurrentValue(TagDefinition writeTag) =>
        (writeTag.Readback is { } rb ? _tags.Value(rb) : null) ?? _tags.LastWritten(writeTag.Id);

    /// <summary>Writes cam angle setpoints entered on the PC (PLC-04). Reason required, audited per cam.</summary>
    public Task<ActionOutcome> WriteCamSetpointsAsync(IReadOnlyDictionary<string, double> values) =>
        _dispatcher.ExecuteAsync(CamEditAction, async s =>
        {
            s.Target("Machine", "Cam");
            foreach (var (id, value) in values)
            {
                var tag = _tags.Find(id) ?? throw new DomainException("msg.unknownTag", id);
                if (!tag.IsWrite || tag.Group != Tags.CamGroup) throw new DomainException("msg.unknownTag", id);
                var old = CurrentValue(tag);
                if (old is { } o && Math.Abs(o - value) < 1e-9) continue;
                await _tags.WriteAsync(id, value);
                s.Change(id, old, value);
            }
        });

    /// <summary>System write used inside other actions (setpoints, scanning flags). Not audited separately.</summary>
    public async Task WriteSystemAsync(string tagId, double value)
    {
        if (_tags.Find(tagId) is null) return;
        await _tags.WriteAsync(tagId, value);
    }

    public async Task PushLoginLevelAsync()
    {
        try
        {
            if (_tags.Find(Tags.LoginLevel) is null || !_tags.IsOnline) return;
            await _tags.WriteAsync(Tags.LoginLevel, LoginLevelFor(_session.CurrentUser?.RoleCode));
        }
        catch (Exception ex)
        {
            _events.Write("PLC", "LOGIN_LEVEL_WRITE_FAILED", ex.Message);
        }
    }

    /// <summary>Value written to LOGIN_LEVEL: 0 when nobody is logged in, else the configured map or the default.</summary>
    public int LoginLevelFor(string? roleCode)
    {
        if (string.IsNullOrEmpty(roleCode)) return 0;
        if (_loginLevels is not null) return _loginLevels.TryGetValue(roleCode, out var v) ? v : 1;
        return LoginLevel.For(roleCode);
    }

    private void OnConnectionChanged(object? sender, bool online)
    {
        _events.Write("PLC", online ? "ONLINE" : "OFFLINE", "");
        _audit.Write(AuditCodes.PlcConnection, "PLC", "", "Online", (!online).ToString().ToLowerInvariant(), online.ToString().ToLowerInvariant());
        _alarms.Set(SoftwareAlarms.PlcCommunication, !online);
        if (online) _ = PushLoginLevelAsync();
        ConnectionChanged?.Invoke(this, online);
    }

    private void OnTagChanged(object? sender, TagChange change)
    {
        var tag = change.Tag;
        switch (tag.Category)
        {
            case TagCategory.Alarm:
                _alarms.Set(tag.Id, TagEngine.IsActive(tag, change.New));
                break;

            case TagCategory.Parameter:
                if (change.IsInitial) break;
                var writer = _tags.Tags.FirstOrDefault(t => t.IsWrite && string.Equals(t.Readback, tag.Id, StringComparison.OrdinalIgnoreCase));
                var expected = writer is null ? null : _tags.LastWritten(writer.Id);
                if (expected is { } e && Math.Abs(e - change.New) < 1e-9) break; // our own write, already audited
                _audit.Write(AuditCodes.PlcParameterChangedOutside, "Tag", tag.Id, tag.Id,
                    ActionScope.Format(change.Old), ActionScope.Format(change.New),
                    args: new Dictionary<string, string> { ["tag"] = tag.Id, ["unit"] = tag.Unit });
                break;

            case TagCategory.State:
                if (change.IsInitial) break;
                _events.Write("PLC", tag.Id, $"{ActionScope.Format(change.Old)} -> {ActionScope.Format(change.New)}");
                if (tag.Audit)
                    _audit.Write(AuditCodes.PlcStateChanged, "Tag", tag.Id, tag.Id, ActionScope.Format(change.Old), ActionScope.Format(change.New),
                        args: new Dictionary<string, string> { ["tag"] = tag.Id });
                break;

            case TagCategory.Process:
                ProcessValueChanged?.Invoke(this, change);
                break;
        }
    }
}
