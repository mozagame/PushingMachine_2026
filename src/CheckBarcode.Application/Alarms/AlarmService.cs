using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Ports;
using CheckBarcode.Application.Security;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Alarms;

public sealed class AlarmDefinition
{
    public string Id { get; init; } = "";
    public AlarmSeverity Severity { get; init; }
    public LocalizedText Text { get; init; } = new();
}

public sealed class ActiveAlarm
{
    public AlarmDefinition Definition { get; init; } = new();
    public DateTime ActiveUtc { get; set; }
    public bool IsActive { get; set; }
    public bool Acknowledged { get; set; }
    public string? AckBy { get; set; }
    public DateTime? AckUtc { get; set; }
}

/// <summary>Software alarm ids (not coming from the PLC).</summary>
public static class SoftwareAlarms
{
    public const string PlcCommunication = "SW_PLC_COMM";
    public const string SpeedOutOfRange = "SW_SPEED_RANGE";
    public const string TempOutOfRange = "SW_TEMP_RANGE";
    public const string BoxCameraOffline = "SW_CAM_BOX_OFFLINE";
    public const string LeafletCameraOffline = "SW_CAM_LEAFLET_OFFLINE";
    public const string DiskSpaceLow = "SW_DISK_LOW";
    public const string AuditIntegrity = "SW_AUDIT_INTEGRITY";
}

/// <summary>
/// Alarm lifecycle Active → Acknowledged → Cleared, stored separately from the audit trail (ALM-01..03).
/// An alarm stays in the list until it is both cleared and acknowledged.
/// </summary>
public sealed class AlarmService
{
    public const string AckAction = "ALARM_ACK";

    private readonly IAlarmStore _store;
    private readonly IClock _clock;
    private readonly SessionService _session;
    private readonly ActionDispatcher _dispatcher;
    private readonly Dictionary<string, AlarmDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActiveAlarm> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public AlarmService(IAlarmStore store, IClock clock, SessionService session, ActionDispatcher dispatcher)
    {
        _store = store;
        _clock = clock;
        _session = session;
        _dispatcher = dispatcher;
    }

    public Func<long?> CurrentBatchId { get; set; } = () => null;
    public event EventHandler? Changed;

    public void Define(string id, AlarmSeverity severity, LocalizedText text)
    {
        lock (_gate) _definitions[id] = new AlarmDefinition { Id = id, Severity = severity, Text = text };
    }

    public AlarmDefinition? Definition(string id)
    {
        lock (_gate) return _definitions.TryGetValue(id, out var d) ? d : null;
    }

    public IReadOnlyList<ActiveAlarm> Active
    {
        get { lock (_gate) return _active.Values.OrderByDescending(a => a.ActiveUtc).ToList(); }
    }

    public bool HasUnacknowledged
    {
        get { lock (_gate) return _active.Values.Any(a => !a.Acknowledged); }
    }

    /// <summary>Rebuilds the list after a restart from the last stored transition per alarm.</summary>
    public void Restore()
    {
        lock (_gate)
        {
            foreach (var rec in _store.LatestPerAlarm())
            {
                if (rec.State == AlarmState.Cleared) continue;
                var def = _definitions.TryGetValue(rec.AlarmId, out var d) ? d : new AlarmDefinition { Id = rec.AlarmId, Severity = rec.Severity, Text = LocalizedText.Of(rec.AlarmId, rec.AlarmId) };
                _active[rec.AlarmId] = new ActiveAlarm
                {
                    Definition = def,
                    ActiveUtc = rec.Utc,
                    IsActive = true,
                    Acknowledged = rec.State == AlarmState.Acknowledged,
                    AckBy = rec.State == AlarmState.Acknowledged ? rec.Username : null,
                };
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the condition of an alarm; writes a record only on a state change.</summary>
    public void Set(string id, bool active)
    {
        if (active) Raise(id); else Clear(id);
    }

    public void Raise(string id)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(id, out var existing) && existing.IsActive) return;
            var def = _definitions.TryGetValue(id, out var d) ? d : throw new InvalidOperationException($"Alarm '{id}' is not defined");
            Append(id, def.Severity, AlarmState.Active);
            _active[id] = new ActiveAlarm { Definition = def, ActiveUtc = _clock.UtcNow, IsActive = true };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(string id)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var alarm) || !alarm.IsActive) return;
            Append(id, alarm.Definition.Severity, AlarmState.Cleared);
            alarm.IsActive = false;
            if (alarm.Acknowledged) _active.Remove(id);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsRaised(string id)
    {
        lock (_gate) return _active.TryGetValue(id, out var a) && a.IsActive;
    }

    /// <summary>Acknowledges one alarm (null = all unacknowledged). Requires login (ALM-02).</summary>
    public Task<ActionOutcome> AcknowledgeAsync(string? id) =>
        _dispatcher.ExecuteAsync(AckAction, s =>
        {
            List<ActiveAlarm> targets;
            lock (_gate)
            {
                targets = _active.Values.Where(a => !a.Acknowledged && (id is null || a.Definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
                foreach (var a in targets)
                {
                    Append(a.Definition.Id, a.Definition.Severity, AlarmState.Acknowledged);
                    a.Acknowledged = true;
                    a.AckBy = _session.CurrentUser?.Username;
                    a.AckUtc = _clock.UtcNow;
                    if (!a.IsActive) _active.Remove(a.Definition.Id);
                }
            }
            s.Target("Alarm", id ?? "*");
            s.Args["count"] = targets.Count.ToString();
            s.Args["alarms"] = string.Join(",", targets.Select(t => t.Definition.Id));
            Changed?.Invoke(this, EventArgs.Empty);
        });

    private void Append(string id, AlarmSeverity severity, AlarmState state)
    {
        _store.Append(new AlarmRecord
        {
            AlarmId = id,
            Severity = severity,
            State = state,
            Utc = _clock.UtcNow,
            Username = _session.Attribution.Username,
            BatchId = CurrentBatchId(),
        });
    }
}
