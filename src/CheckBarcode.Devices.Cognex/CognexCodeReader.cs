using System.Drawing;
using System.Globalization;
using System.Net;
using System.Xml;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;
using Cognex.DataMan.SDK;
using Cognex.DataMan.SDK.Utils;
using DmImageFormat = Cognex.DataMan.SDK.ImageFormat;

namespace CheckBarcode.Devices.Cognex;

/// <summary>
/// ICodeReader over the Cognex DataMan SDK (DM262). DMCC commands are the same ones v1 used
/// (README_DECODE.md §3), so camera-side behaviour is unchanged.
/// </summary>
public sealed class CognexCodeReader : ICodeReader
{
    private readonly CameraConfig _config;
    private readonly object _gate = new();
    private DataManSystem? _system;
    private ResultCollector? _results;
    private volatile bool _live;

    public CognexCodeReader(CameraConfig config)
    {
        _config = config;
    }

    public string Name => _config.Name;
    public CameraRole Role => _config.Role;
    public string Address => _config.Ip;
    public bool IsConnected => _system?.State == ConnectionState.Connected;

    public event EventHandler<CodeReadResult>? ResultArrived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<byte[]>? LiveImageArrived;

    public Task ConnectAsync(CancellationToken ct) => Task.Run(() =>
    {
        lock (_gate)
        {
            if (IsConnected) return;
            Teardown();
            var connector = new EthSystemConnector(IPAddress.Parse(_config.Ip), 23) { UserName = "admin", Password = "" };
            var system = new DataManSystem(connector) { DefaultTimeout = 5000 };
            system.SystemConnected += (_, _) => ConnectionChanged?.Invoke(this, true);
            system.SystemDisconnected += (_, _) => ConnectionChanged?.Invoke(this, false);
            var types = ResultTypes.ReadXml | ResultTypes.Image | ResultTypes.ImageGraphics;
            _results = new ResultCollector(system, types);
            _results.ComplexResultCompleted += OnComplexResult;
            system.Connect();
            system.SetKeepAliveOptions(true, 3000, 1000);
            system.SetResultTypes(types);
            _system = system;
        }
    }, ct);

    public Task DisconnectAsync() => Task.Run(() =>
    {
        lock (_gate) Teardown();
    });

    public Task ApplyMatchStringAsync(string matchString, BarcodeFormat format) => Send(
        $"SET DVALID.PROG-TARG {_config.DvalidTarget}",
        "SET DVALID.TYPE 4",
        $"SET DVALID.FAIL-ACTION {_config.DvalidFailAction}",
        $"SET DVALID.MATCH-STRING \"{matchString.Replace("\"", "")}\"");

    public Task ClearMatchStringAsync() => Send("SET DVALID.TYPE 0");

    public Task StartLiveAsync()
    {
        if (_live || _system is null) return Task.CompletedTask;
        _live = true;
        Send("SET LIVEIMG.MODE 2");
        _system.BeginGetLiveImage(DmImageFormat.jpeg, ImageSize.Quarter, ImageQuality.Medium, OnLiveImage, null);
        return Task.CompletedTask;
    }

    public Task StopLiveAsync()
    {
        _live = false;
        return Send("SET LIVEIMG.MODE 0");
    }

    public Task TriggerAsync() => Send("TRIGGER ON");

    public Task<CameraSettings> GetSettingsAsync() => Task.Run(() => new CameraSettings(
        Int(Get("GET CAMERA.EXPOSURE")),
        Dbl(Get("GET CAMERA.GAIN")),
        Int(Get("GET TRIGGER.DELAY-TIME")),
        Int(Get("GET CAMERA.INTERVAL")),
        Int(Get("GET DECODER.TIMEOUT")),
        Int(Get("GET CAMERA.BURST-LENGTH"))));

    public Task SetSettingsAsync(CameraSettings s) => Send(
        $"SET CAMERA.EXPOSURE {s.ExposureUs}",
        $"SET CAMERA.GAIN {s.Gain.ToString(CultureInfo.InvariantCulture)}",
        "SET TRIGGER.DELAY-TYPE 1",
        $"SET TRIGGER.DELAY-TIME {s.TriggerDelayMs}",
        $"SET CAMERA.INTERVAL {s.IntervalMs}",
        $"SET DECODER.TIMEOUT {s.DecoderTimeoutMs}",
        $"SET CAMERA.BURST-LENGTH {s.BurstLength}",
        "CONFIG.SAVE");

    public Task<string> GetSerialAsync() => Task.Run(() => Get("GET DEVICE.MAC-ADDRESS"));

    private Task Send(params string[] commands) => Task.Run(() =>
    {
        var system = _system ?? throw new DomainException("msg.cameraNotConnected", Role);
        foreach (var c in commands) system.SendCommand(c);
    });

    private string Get(string command)
    {
        var system = _system ?? throw new DomainException("msg.cameraNotConnected", Role);
        using var response = system.SendCommand(command);
        return (response.PayLoad ?? "").Trim();
    }

    private static int Int(string s) => int.TryParse(s.Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static double Dbl(string s) => double.TryParse(s.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private void OnComplexResult(object sender, ComplexResult result)
    {
        string? read = null;
        byte[]? png = null;
        var utc = DateTime.UtcNow;
        foreach (var simple in result.SimpleResults)
        {
            switch (simple.Id.Type)
            {
                case ResultTypes.ReadXml:
                    read = ReadStringFromXml(simple.GetDataAsString());
                    utc = simple.ArrivedAtUtc == DateTime.MinValue ? utc : simple.ArrivedAtUtc;
                    break;
                case ResultTypes.ReadString:
                    read = simple.GetDataAsString();
                    break;
                case ResultTypes.Image:
                    png = ToPng(simple.Data);
                    break;
            }
        }
        if (read is null) return;
        ResultArrived?.Invoke(this, new CodeReadResult(read, png, utc));
    }

    private void OnLiveImage(IAsyncResult ar)
    {
        try
        {
            var system = _system;
            if (system is null) return;
            using var image = system.EndGetLiveImage(ar);
            if (image is not null) LiveImageArrived?.Invoke(this, Encode(image));
            if (_live) system.BeginGetLiveImage(DmImageFormat.jpeg, ImageSize.Quarter, ImageQuality.Medium, OnLiveImage, null);
        }
        catch
        {
            _live = false;
        }
    }

    /// <summary>Extracts result/general/full_string (base64 aware) like v1 did.</summary>
    private string ReadStringFromXml(string xml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var node = doc.SelectSingleNode("result/general/full_string");
            if (node is null) return "";
            if (node.Attributes?["encoding"]?.InnerText == "base64")
            {
                var bytes = Convert.FromBase64String(node.InnerText);
                return (_system?.Encoding ?? System.Text.Encoding.UTF8).GetString(bytes);
            }
            return node.InnerText;
        }
        catch (XmlException)
        {
            return "";
        }
    }

    private static byte[]? ToPng(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        using var image = ImageArrivedEventArgs.GetImageFromImageBytes(data);
        return image is null ? null : Encode(image);
    }

    private static byte[] Encode(Image image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private void Teardown()
    {
        _live = false;
        if (_results is not null) _results.ComplexResultCompleted -= OnComplexResult;
        _results = null;
        if (_system is null) return;
        try { _system.Disconnect(); } catch { /* already gone */ }
        _system.Dispose();
        _system = null;
    }

    public void Dispose()
    {
        lock (_gate) Teardown();
    }
}
