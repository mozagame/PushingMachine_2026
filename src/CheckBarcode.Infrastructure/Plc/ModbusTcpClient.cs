using System.Buffers.Binary;
using System.Net.Sockets;
using CheckBarcode.Application.Ports;

namespace CheckBarcode.Infrastructure.Plc;

public sealed class ModbusException : Exception
{
    public byte FunctionCode { get; }
    public byte ExceptionCode { get; }

    public ModbusException(byte function, byte code) : base($"Modbus exception {code} ({Describe(code)}) on function {function}")
    {
        FunctionCode = function;
        ExceptionCode = code;
    }

    public static string Describe(byte code) => code switch
    {
        1 => "illegal function",
        2 => "illegal data address",
        3 => "illegal data value",
        4 => "slave device failure",
        5 => "acknowledge",
        6 => "slave device busy",
        10 => "gateway path unavailable",
        11 => "gateway target failed to respond",
        _ => "unknown",
    };
}

/// <summary>Modbus TCP client (FC03 read holding registers, FC16 write multiple registers). No external library.</summary>
public sealed class ModbusTcpClient : IPlcClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly int _timeoutMs;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private ushort _transactionId;

    public ModbusTcpClient(string host, int port = 502, byte unitId = 1, int timeoutMs = 1000)
    {
        _host = host;
        _port = port;
        _unitId = unitId;
        _timeoutMs = timeoutMs;
    }

    public bool IsConnected => _client?.Connected == true && _stream is not null;

    public async Task ConnectAsync(CancellationToken ct)
    {
        Disconnect();
        var client = new TcpClient { NoDelay = true, ReceiveTimeout = _timeoutMs, SendTimeout = _timeoutMs };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeoutMs * 3);
        try
        {
            await client.ConnectAsync(_host, _port, timeout.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        _stream = client.GetStream();
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort start, ushort count, CancellationToken ct)
    {
        if (count is 0 or > 125) throw new ArgumentOutOfRangeException(nameof(count));
        var pdu = new byte[5];
        pdu[0] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);
        var resp = await TransactAsync(pdu, ct);
        if (resp.Length < 2 || resp[1] != count * 2) throw new IOException("Modbus: unexpected response length");
        var values = new ushort[count];
        for (var i = 0; i < count; i++) values[i] = BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(2 + i * 2));
        return values;
    }

    public async Task WriteMultipleRegistersAsync(ushort start, ushort[] values, CancellationToken ct)
    {
        if (values.Length is 0 or > 123) throw new ArgumentOutOfRangeException(nameof(values));
        var pdu = new byte[6 + values.Length * 2];
        pdu[0] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2), values[i]);
        var resp = await TransactAsync(pdu, ct);
        if (resp.Length < 5) throw new IOException("Modbus: unexpected write response");
    }

    private async Task<byte[]> TransactAsync(byte[] pdu, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var stream = _stream ?? throw new IOException("Modbus: not connected");
            var tid = ++_transactionId;
            var frame = new byte[7 + pdu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), tid);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
            frame[6] = _unitId;
            pdu.CopyTo(frame, 7);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeoutMs);
            await stream.WriteAsync(frame, timeout.Token);

            var header = new byte[7];
            await ReadExactAsync(stream, header, timeout.Token);
            var rtid = BinaryPrimitives.ReadUInt16BigEndian(header);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (length is < 2 or > 260) throw new IOException("Modbus: invalid frame length");
            var body = new byte[length - 1];
            await ReadExactAsync(stream, body, timeout.Token);
            if (rtid != tid) throw new IOException("Modbus: transaction id mismatch");
            if ((body[0] & 0x80) != 0) throw new ModbusException((byte)(body[0] & 0x7F), body.Length > 1 ? body[1] : (byte)0);
            if (body[0] != pdu[0]) throw new IOException("Modbus: function code mismatch");
            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Disconnect();
            throw new TimeoutException("Modbus: response timeout");
        }
        catch (IOException)
        {
            Disconnect();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new IOException("Modbus: connection closed");
            read += n;
        }
    }

    public void Dispose()
    {
        Disconnect();
        _lock.Dispose();
    }
}
