using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace CheckBarcode.Simulator;

/// <summary>In-memory Modbus TCP server (FC03 / FC06 / FC16) used as a PLC simulator for tests and FAT.</summary>
public sealed class ModbusTcpServer : IDisposable
{
    private readonly ushort[] _registers = new ushort[65536];
    private readonly object _gate = new();
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = new();

    public ModbusTcpServer(int port = 0, IPAddress? address = null)
    {
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int RequestCount { get; private set; }
    /// <summary>Simulates a dead PLC: requests are not answered.</summary>
    public bool Mute { get; set; }

    public event Action<ushort, ushort[]>? Written;

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoop();
    }

    public ushort this[int address]
    {
        get { lock (_gate) return _registers[address]; }
        set { lock (_gate) _registers[address] = value; }
    }

    public void SetBit(int address, int bit, bool on)
    {
        lock (_gate)
        {
            if (on) _registers[address] |= (ushort)(1 << bit);
            else _registers[address] &= (ushort)~(1 << bit);
        }
    }

    /// <summary>Closes all client connections (simulates a cable pull).</summary>
    public void DropClients()
    {
        lock (_clients)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            lock (_clients) _clients.Add(client);
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var header = new byte[7];
                while (!_cts.IsCancellationRequested)
                {
                    if (!await ReadExact(stream, header)) return;
                    var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                    var pdu = new byte[length - 1];
                    if (!await ReadExact(stream, pdu)) return;
                    RequestCount++;
                    if (Mute) continue;
                    var response = Handle(pdu);
                    var frame = new byte[7 + response.Length];
                    Array.Copy(header, frame, 4);
                    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(response.Length + 1));
                    frame[6] = header[6];
                    response.CopyTo(frame, 7);
                    await stream.WriteAsync(frame, _cts.Token);
                }
            }
        }
        catch
        {
            // client gone
        }
    }

    private byte[] Handle(byte[] pdu)
    {
        var fc = pdu[0];
        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        switch (fc)
        {
            case 0x03:
            {
                var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                if (count is 0 or > 125 || start + count > 65536) return new byte[] { 0x83, 0x02 };
                var resp = new byte[2 + count * 2];
                resp[0] = 0x03;
                resp[1] = (byte)(count * 2);
                lock (_gate)
                {
                    for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2 + i * 2), _registers[start + i]);
                }
                return resp;
            }
            case 0x06:
            {
                var value = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                this[start] = value;
                Written?.Invoke(start, new[] { value });
                return pdu[..5];
            }
            case 0x10:
            {
                var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                var values = new ushort[count];
                lock (_gate)
                {
                    for (var i = 0; i < count; i++)
                    {
                        values[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2));
                        _registers[start + i] = values[i];
                    }
                }
                Written?.Invoke(start, values);
                return pdu[..5];
            }
            default:
                return new byte[] { (byte)(fc | 0x80), 0x01 };
        }
    }

    private async Task<bool> ReadExact(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), _cts.Token);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        DropClients();
    }
}
