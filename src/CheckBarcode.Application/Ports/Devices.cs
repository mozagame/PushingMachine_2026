using CheckBarcode.Domain;

namespace CheckBarcode.Application.Ports;

/// <summary>Minimal Modbus-style register access to the PLC (holding registers, 16-bit).</summary>
public interface IPlcClient : IDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    void Disconnect();
    Task<ushort[]> ReadHoldingRegistersAsync(ushort start, ushort count, CancellationToken ct);
    Task WriteMultipleRegistersAsync(ushort start, ushort[] values, CancellationToken ct);
}

/// <summary>Result coming from a code reader (one trigger).</summary>
public sealed record CodeReadResult(string ReadString, byte[]? PngImage, DateTime Utc)
{
    public bool IsNoRead => string.IsNullOrEmpty(ReadString);
}

/// <summary>Camera parameters editable from the PC (CAM-05).</summary>
public sealed record CameraSettings(int ExposureUs, double Gain, int TriggerDelayMs, int IntervalMs, int DecoderTimeoutMs, int BurstLength)
{
    public IEnumerable<(string Field, string Value)> Fields()
    {
        yield return (nameof(ExposureUs), ExposureUs.ToString());
        yield return (nameof(Gain), Gain.ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return (nameof(TriggerDelayMs), TriggerDelayMs.ToString());
        yield return (nameof(IntervalMs), IntervalMs.ToString());
        yield return (nameof(DecoderTimeoutMs), DecoderTimeoutMs.ToString());
        yield return (nameof(BurstLength), BurstLength.ToString());
    }
}

/// <summary>Port to a code reader (Cognex DataMan or simulator).</summary>
public interface ICodeReader : IDisposable
{
    string Name { get; }
    CameraRole Role { get; }
    string Address { get; }
    bool IsConnected { get; }

    event EventHandler<CodeReadResult>? ResultArrived;
    event EventHandler<bool>? ConnectionChanged;
    event EventHandler<byte[]>? LiveImageArrived;

    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
    /// <summary>Configures on-camera match validation for the batch (CAM-04).</summary>
    Task ApplyMatchStringAsync(string matchString, BarcodeFormat format);
    Task ClearMatchStringAsync();
    Task StartLiveAsync();
    Task StopLiveAsync();
    /// <summary>Software trigger; the result arrives through <see cref="ResultArrived"/>.</summary>
    Task TriggerAsync();
    Task<CameraSettings> GetSettingsAsync();
    Task SetSettingsAsync(CameraSettings settings);
    Task<string> GetSerialAsync();
}

/// <summary>Stores inspection images on disk (CAM-06).</summary>
public interface IImageStore
{
    string Save(string batchNo, CameraRole camera, DateTime utc, InspectionOutcome outcome, string readString, byte[] png);
    byte[]? Load(string path);
    int DeleteOlderThan(DateTime utc);
}

public interface IRemovableDriveProvider
{
    /// <summary>Root paths of removable drives (e.g. "E:\").</summary>
    IReadOnlyList<string> RemovableRoots();
}

/// <summary>Renders a report model to a PDF byte array.</summary>
public interface IPdfRenderer
{
    byte[] Render(Reports.ReportDocument document);
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
