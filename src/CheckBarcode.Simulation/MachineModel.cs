namespace CheckBarcode.Simulator;

/// <summary>
/// Simple cartoning-machine model on top of <see cref="ModbusTcpServer"/> using the default register map
/// (PLAN.md §5.1): heartbeat, running state, speed / temperature following setpoints, cam readbacks.
/// </summary>
public sealed class MachineModel : IDisposable
{
    private readonly ModbusTcpServer _server;
    private readonly Timer _timer;
    private readonly Random _random = new(1);

    public MachineModel(ModbusTcpServer server, int cycleMs = 200)
    {
        _server = server;
        _server.Written += OnWritten;
        _timer = new Timer(_ => Cycle(), null, cycleMs, cycleMs);
        for (var i = 0; i < 30; i++) _server[10 + i] = (ushort)(i * 12 % 360);
        _server[6] = 25;
    }

    public bool Running
    {
        get => _server[1] != 0;
        set => _server[1] = (ushort)(value ? 1 : 0);
    }

    public bool HeartbeatEnabled { get; set; } = true;
    /// <summary>Offset added to the speed / temperature targets to simulate process deviations (FAT-16).</summary>
    public int SpeedOffset { get; set; }
    public int TempOffset { get; set; }

    public void SetAlarm(int bit, bool active) => _server.SetBit(3, bit, active);
    public void SetEStop(bool active) => _server[2] = (ushort)(active ? 1 : 0);

    /// <summary>Simulates a technician changing a cam on the machine itself (audited as "outside PC").</summary>
    public void ChangeCamLocally(int index, ushort value) => _server[10 + index] = value;

    private void OnWritten(ushort start, ushort[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var address = start + i;
            if (address is >= 110 and <= 139) _server[address - 100] = values[i]; // cam setpoint → readback
        }
    }

    private void Cycle()
    {
        if (HeartbeatEnabled) _server[0] = (ushort)(_server[0] + 1);
        var speedSp = _server[104];
        var tempSp = (short)_server[105];
        var speed = (int)_server[5];
        var target = Running ? Math.Max(0, speedSp + SpeedOffset) : 0;
        speed += Math.Sign(target - speed) * Math.Min(Math.Abs(target - speed), 5);
        _server[5] = (ushort)Math.Max(0, speed);
        var temp = (short)_server[6];
        if (tempSp > 0) temp += (short)Math.Sign(tempSp + TempOffset - temp);
        if (_random.NextDouble() < 0.05) temp += (short)(_random.Next(3) - 1);
        _server[6] = unchecked((ushort)temp);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _server.Written -= OnWritten;
    }
}
