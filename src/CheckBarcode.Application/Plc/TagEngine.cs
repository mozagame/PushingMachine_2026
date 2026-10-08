using System.Collections.Concurrent;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Plc;

public sealed record TagChange(TagDefinition Tag, double? Old, double New, bool IsInitial, DateTime Utc);

/// <summary>
/// Generic PLC tag engine (PLC-01, PLC-03). Reads every "Read" tag of tags.json in optimized blocks,
/// decodes bits / words, applies deadband and debounce and raises <see cref="TagChanged"/>.
/// Nothing in here knows about specific alarms or parameters: those are configuration.
/// </summary>
public sealed class TagEngine : IDisposable
{
    public const string PcHeartbeatTag = "PC_HEARTBEAT";
    public const string PlcHeartbeatTag = "PLC_HEARTBEAT";

    private readonly IPlcClient _plc;
    private readonly PlcConfig _config;
    private readonly IClock _clock;
    private readonly Dictionary<string, TagDefinition> _byId;
    private readonly List<TagDefinition> _readTags;
    private readonly List<(ushort Start, ushort Count)> _blocks;
    private readonly ConcurrentDictionary<string, double> _values = new();
    private readonly ConcurrentDictionary<string, double> _written = new();
    private readonly Dictionary<string, (double Value, DateTime Since)> _pending = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private ushort _heartbeat;
    private DateTime _lastPlcHeartbeatChangeUtc;
    private bool _connected;
    private bool _alive;
    private DateTime _nextConnectAttemptUtc = DateTime.MinValue;

    public TagEngine(IPlcClient plc, IEnumerable<TagDefinition> tags, PlcConfig config, IClock clock)
    {
        _plc = plc;
        _config = config;
        _clock = clock;
        var list = tags.ToList();
        var dup = list.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidOperationException($"Duplicate tag id '{dup.Key}' in tags.json");
        _byId = list.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        _readTags = list.Where(t => !t.IsWrite).ToList();
        _blocks = BuildBlocks(_readTags);
        _lastPlcHeartbeatChangeUtc = clock.UtcNow;
    }

    public event EventHandler<TagChange>? TagChanged;
    /// <summary>Raised when the reason of a communication failure changes (null = cleared). Diagnostics only.</summary>
    public event EventHandler<string?>? CommunicationError;
    public string? LastError { get; private set; }

    private void ReportError(string? message)
    {
        if (message == LastError) return;
        LastError = message;
        CommunicationError?.Invoke(this, message);
    }
    /// <summary>Raised when the TCP connection or the PLC heartbeat state changes.</summary>
    public event EventHandler<bool>? ConnectionChanged;

    public IReadOnlyCollection<TagDefinition> Tags => _byId.Values;
    public IReadOnlyList<(ushort Start, ushort Count)> Blocks => _blocks;
    /// <summary>True when connected and the PLC heartbeat is moving.</summary>
    public bool IsOnline => _connected && _alive;

    public TagDefinition? Find(string id) => _byId.TryGetValue(id, out var t) ? t : null;
    public double? Value(string id) => _values.TryGetValue(id, out var v) ? v : null;
    public double? LastWritten(string id) => _written.TryGetValue(id, out var v) ? v : null;

    /// <summary>Groups addresses into read requests (gap ≤ 8 registers, ≤ 120 registers per request).</summary>
    public static List<(ushort Start, ushort Count)> BuildBlocks(IEnumerable<TagDefinition> tags)
    {
        var ranges = tags.Select(t => (Start: t.Address, End: t.Address + t.RegisterCount - 1))
            .Distinct().OrderBy(r => r.Start).ToList();
        var blocks = new List<(ushort, ushort)>();
        int? bs = null, be = null;
        foreach (var (s, e) in ranges)
        {
            if (bs is null) { bs = s; be = e; continue; }
            if (s - be!.Value <= 8 && Math.Max(e, be.Value) - bs.Value + 1 <= 120) { be = Math.Max(be.Value, e); continue; }
            blocks.Add(((ushort)bs.Value, (ushort)(be.Value - bs.Value + 1)));
            bs = s; be = e;
        }
        if (bs is not null) blocks.Add(((ushort)bs.Value, (ushort)(be!.Value - bs.Value + 1)));
        return blocks;
    }

    public static double Decode(TagDefinition tag, ushort[] regs, int offset)
    {
        double raw = tag.Type.ToLowerInvariant() switch
        {
            "bool" => tag.Bit is { } b ? (regs[offset] >> b) & 1 : (regs[offset] != 0 ? 1 : 0),
            "int16" => (short)regs[offset],
            "int32" => (int)((uint)regs[offset] << 16 | regs[offset + 1]),
            _ => tag.Bit is { } bit ? (regs[offset] >> bit) & 1 : regs[offset],
        };
        return tag.Bit is null && tag.Scale != 1.0 ? raw * tag.Scale : raw;
    }

    public static ushort[] Encode(TagDefinition tag, double value)
    {
        if (tag.Bit is not null) throw new InvalidOperationException($"Tag {tag.Id}: writing single bits is not supported");
        var raw = Math.Round(value / (tag.Scale == 0 ? 1 : tag.Scale));
        return tag.Type.ToLowerInvariant() switch
        {
            "int16" => new[] { unchecked((ushort)(short)raw) },
            "int32" => new[] { (ushort)(((int)raw >> 16) & 0xFFFF), (ushort)((int)raw & 0xFFFF) },
            "bool" => new[] { (ushort)(raw != 0 ? 1 : 0) },
            _ => new[] { (ushort)Math.Clamp(raw, 0, ushort.MaxValue) },
        };
    }

    /// <summary>Evaluates "== 1", "!= 0", "> 80", ">= 5", "&lt; 3" … against a value.</summary>
    public static bool IsActive(TagDefinition tag, double value)
    {
        var expr = (tag.ActiveWhen ?? "!= 0").Trim();
        string[] ops = { "==", "!=", ">=", "<=", ">", "<" };
        foreach (var op in ops)
        {
            if (!expr.StartsWith(op, StringComparison.Ordinal)) continue;
            if (!double.TryParse(expr[op.Length..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var operand))
                break;
            return op switch
            {
                "==" => Math.Abs(value - operand) < 1e-9,
                "!=" => Math.Abs(value - operand) >= 1e-9,
                ">=" => value >= operand,
                "<=" => value <= operand,
                ">" => value > operand,
                _ => value < operand,
            };
        }
        throw new InvalidOperationException($"Tag {tag.Id}: invalid activeWhen '{tag.ActiveWhen}'");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct);
            try { await Task.Delay(Math.Max(20, _config.PollMs), ct); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>One read cycle (also used by tests).</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            if (!_plc.IsConnected)
            {
                if (_clock.UtcNow < _nextConnectAttemptUtc) return;
                try
                {
                    await _plc.ConnectAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    ReportError($"Connect {ex.GetType().Name}: {ex.Message}");
                    _nextConnectAttemptUtc = _clock.UtcNow.AddSeconds(2);
                    SetConnected(false);
                    return;
                }
                _lastPlcHeartbeatChangeUtc = _clock.UtcNow;
            }

            var now = _clock.UtcNow;
            foreach (var (start, count) in _blocks)
            {
                var regs = await _plc.ReadHoldingRegistersAsync(start, count, ct);
                foreach (var tag in _readTags.Where(t => t.Address >= start && t.Address + t.RegisterCount <= start + count))
                    Apply(tag, Decode(tag, regs, tag.Address - start), now);
            }

            if (_byId.TryGetValue(PcHeartbeatTag, out var hb) && hb.IsWrite)
            {
                _heartbeat++;
                await _plc.WriteMultipleRegistersAsync((ushort)hb.Address, new[] { _heartbeat }, ct);
            }
            SetConnected(true);
            UpdateAlive(now);
            ReportError(null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ReportError($"Poll {ex.GetType().Name}: {ex.Message}");
            _plc.Disconnect();
            SetConnected(false);
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task WriteAsync(string tagId, double value, CancellationToken ct = default)
    {
        var tag = Find(tagId) ?? throw new InvalidOperationException($"Unknown tag '{tagId}'");
        if (!tag.IsWrite) throw new InvalidOperationException($"Tag '{tagId}' is read-only");
        if (tag.Min is { } min && value < min || tag.Max is { } max && value > max)
            throw new DomainException("msg.valueOutOfRange", tag.Id, value, tag.Min ?? double.MinValue, tag.Max ?? double.MaxValue);
        await _io.WaitAsync(ct);
        try
        {
            if (!_plc.IsConnected) throw new DomainException("msg.plcNotConnected");
            try
            {
                await _plc.WriteMultipleRegistersAsync((ushort)tag.Address, Encode(tag, value), ct);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                // A failed write means the link is down: report it like a failed poll so the outage is visible and audited.
                _plc.Disconnect();
                SetConnected(false);
                throw new DomainException("msg.plcNotConnected");
            }
            _written[tag.Id] = value;
        }
        finally
        {
            _io.Release();
        }
    }

    private void Apply(TagDefinition tag, double value, DateTime now)
    {
        var had = _values.TryGetValue(tag.Id, out var old);
        if (!had)
        {
            _values[tag.Id] = value;
            if (tag.Id.Equals(PlcHeartbeatTag, StringComparison.OrdinalIgnoreCase)) _lastPlcHeartbeatChangeUtc = now;
            TagChanged?.Invoke(this, new TagChange(tag, null, value, true, now));
            return;
        }
        if (tag.Id.Equals(PlcHeartbeatTag, StringComparison.OrdinalIgnoreCase))
        {
            if (value != old) { _values[tag.Id] = value; _lastPlcHeartbeatChangeUtc = now; }
            return;
        }

        var significant = tag.Type.Equals("bool", StringComparison.OrdinalIgnoreCase) || tag.Bit is not null
            ? value != old
            : Math.Abs(value - old) > Math.Max(tag.Deadband, 1e-9);
        if (!significant)
        {
            _pending.Remove(tag.Id);
            return;
        }
        if (tag.DebounceMs > 0)
        {
            if (!_pending.TryGetValue(tag.Id, out var p) || p.Value != value)
            {
                _pending[tag.Id] = (value, now);
                return;
            }
            if ((now - p.Since).TotalMilliseconds < tag.DebounceMs) return;
            _pending.Remove(tag.Id);
        }
        _values[tag.Id] = value;
        TagChanged?.Invoke(this, new TagChange(tag, old, value, false, now));
    }

    private void UpdateAlive(DateTime now)
    {
        _alive = !_byId.ContainsKey(PlcHeartbeatTag) || (now - _lastPlcHeartbeatChangeUtc).TotalMilliseconds <= _config.HeartbeatTimeoutMs;
        Notify();
    }

    private void SetConnected(bool connected)
    {
        _connected = connected;
        if (!connected) { _alive = false; Notify(); }
    }

    private bool? _reported;

    private void Notify()
    {
        var online = IsOnline;
        if (_reported == online) return;
        _reported = online;
        ConnectionChanged?.Invoke(this, online);
    }

    public void Dispose()
    {
        _plc.Dispose();
        _io.Dispose();
    }
}
