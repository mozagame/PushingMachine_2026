using System.Text.Json;
using System.Text.Json.Serialization;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Configuration;

/// <summary>Text in Vietnamese and English.</summary>
public sealed class LocalizedText
{
    [JsonPropertyName("vi")] public string Vi { get; set; } = "";
    [JsonPropertyName("en")] public string En { get; set; } = "";

    public string Get(string lang) => lang == "en" ? (string.IsNullOrEmpty(En) ? Vi : En) : (string.IsNullOrEmpty(Vi) ? En : Vi);
    public static LocalizedText Of(string vi, string en) => new() { Vi = vi, En = en };
}

/// <summary>A user action that is permission-checked and audited (actions.json).</summary>
public sealed class ActionDefinition
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    /// <summary>Permission required; empty = any logged-in user; "*" = also allowed without login.</summary>
    [JsonPropertyName("permission")] public string Permission { get; set; } = "";
    [JsonPropertyName("requireReason")] public bool RequireReason { get; set; }
    [JsonPropertyName("requireSignature")] public bool RequireSignature { get; set; }
    [JsonPropertyName("signatureMeaning")] public LocalizedText? SignatureMeaning { get; set; }
    /// <summary>Message template; placeholders {objectId} {field} {old} {new} {reason} and any message argument.</summary>
    [JsonPropertyName("text")] public LocalizedText Text { get; set; } = new();
}

/// <summary>PLC tag definition (tags.json). Adding an alarm or a monitored value = adding one entry.</summary>
public sealed class TagDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("category")] public TagCategory Category { get; set; }
    /// <summary>"Read" (PLC→PC) or "Write" (PC→PLC).</summary>
    [JsonPropertyName("direction")] public string Direction { get; set; } = "Read";
    [JsonPropertyName("address")] public int Address { get; set; }
    /// <summary>Bit index 0..15 for packed booleans; null = whole register.</summary>
    [JsonPropertyName("bit")] public int? Bit { get; set; }
    /// <summary>UInt16, Int16, Int32 (two registers, high word first), Bool.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "UInt16";
    [JsonPropertyName("scale")] public double Scale { get; set; } = 1.0;
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("deadband")] public double Deadband { get; set; }
    [JsonPropertyName("debounceMs")] public int DebounceMs { get; set; }
    /// <summary>Alarm condition: "== 1", "!= 0", "> 80" … Default "!= 0".</summary>
    [JsonPropertyName("activeWhen")] public string ActiveWhen { get; set; } = "!= 0";
    [JsonPropertyName("severity")] public AlarmSeverity Severity { get; set; } = AlarmSeverity.High;
    /// <summary>Also write State changes to the audit trail (e.g. machine on/off).</summary>
    [JsonPropertyName("audit")] public bool Audit { get; set; }
    /// <summary>For Write parameters: id of the Read tag that reflects the PLC value.</summary>
    [JsonPropertyName("readback")] public string? Readback { get; set; }
    /// <summary>Grouping for UI (e.g. "Cam").</summary>
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("min")] public double? Min { get; set; }
    [JsonPropertyName("max")] public double? Max { get; set; }
    [JsonPropertyName("text")] public LocalizedText Text { get; set; } = new();

    [JsonIgnore] public bool IsWrite => string.Equals(Direction, "Write", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public int RegisterCount => Type.Equals("Int32", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
}

public sealed class CameraConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("role")] public CameraRole Role { get; set; }
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    /// <summary>"Cognex" or "Simulator".</summary>
    [JsonPropertyName("driver")] public string Driver { get; set; } = "Cognex";
    /// <summary>DMCC "DVALID.PROG-TARG" value used for match-string validation (v1 used 3).</summary>
    [JsonPropertyName("dvalidTarget")] public int DvalidTarget { get; set; } = 3;
    /// <summary>DMCC "DVALID.FAIL-ACTION" value (v1 used 2).</summary>
    [JsonPropertyName("dvalidFailAction")] public int DvalidFailAction { get; set; } = 2;
}

public sealed class PlcConfig
{
    [JsonPropertyName("ip")] public string Ip { get; set; } = "192.168.1.10";
    [JsonPropertyName("port")] public int Port { get; set; } = 502;
    [JsonPropertyName("unitId")] public byte UnitId { get; set; } = 1;
    [JsonPropertyName("pollMs")] public int PollMs { get; set; } = 250;
    [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; set; } = 1000;
    [JsonPropertyName("heartbeatTimeoutMs")] public int HeartbeatTimeoutMs { get; set; } = 5000;
    /// <summary>"Modbus" or "Simulator".</summary>
    [JsonPropertyName("driver")] public string Driver { get; set; } = "Modbus";
    /// <summary>Register map file in the config folder: "tags.json" (new PLC map) or "tags.legacy.json" (original PLC program).</summary>
    [JsonPropertyName("tagsFile")] public string TagsFile { get; set; } = "tags.json";
    /// <summary>Optional role → value written to LOGIN_LEVEL. Null = default (Operator 1, Supervisor 2, Admin 3).</summary>
    [JsonPropertyName("loginLevels")] public Dictionary<string, int>? LoginLevels { get; set; }
}

public sealed class PathsConfig
{
    [JsonPropertyName("data")] public string Data { get; set; } = "data";
    [JsonPropertyName("images")] public string Images { get; set; } = "data/images";
    [JsonPropertyName("reports")] public string Reports { get; set; } = "data/reports";
    [JsonPropertyName("backup")] public string Backup { get; set; } = "data/backup";
    [JsonPropertyName("logs")] public string Logs { get; set; } = "data/logs";
    /// <summary>TrueType fonts used for PDFs, first existing file wins.</summary>
    [JsonPropertyName("pdfFonts")] public List<string> PdfFonts { get; set; } = new() { @"C:\Windows\Fonts\arial.ttf" };
    [JsonPropertyName("pdfBoldFonts")] public List<string> PdfBoldFonts { get; set; } = new() { @"C:\Windows\Fonts\arialbd.ttf" };
}

/// <summary>Root of appsettings.json.</summary>
public sealed class AppConfig
{
    [JsonPropertyName("machineName")] public string MachineName { get; set; } = "DH-630-4";
    [JsonPropertyName("machineCode")] public string MachineCode { get; set; } = "PDP-178";
    [JsonPropertyName("customer")] public string Customer { get; set; } = "";
    [JsonPropertyName("defaultLanguage")] public string DefaultLanguage { get; set; } = "vi";
    [JsonPropertyName("plc")] public PlcConfig Plc { get; set; } = new();
    [JsonPropertyName("cameras")] public List<CameraConfig> Cameras { get; set; } = new();
    [JsonPropertyName("paths")] public PathsConfig Paths { get; set; } = new();
}

public static class ConfigLoader
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new T();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
    }

    public static List<T> LoadList<T>(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Configuration file not found", path);
        return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Options) ?? new List<T>();
    }

    /// <summary>SHA-256 of a configuration file; recorded in the audit trail at start-up (Annex 11 §10).</summary>
    public static string FileHash(string path)
    {
        if (!File.Exists(path)) return "";
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path)));
    }
}
