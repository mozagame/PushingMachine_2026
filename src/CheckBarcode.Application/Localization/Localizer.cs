using System.Text.Json;

namespace CheckBarcode.Application.Localization;

/// <summary>Runtime Vietnamese / English text lookup (SYS-01).</summary>
public interface ILocalizer
{
    string Language { get; }
    event EventHandler? LanguageChanged;
    void SetLanguage(string language);
    /// <summary>Text for <paramref name="key"/>, formatted with {0}.. arguments. Missing key returns the key.</summary>
    string T(string key, params object[] args);
}

public sealed class Localizer : ILocalizer
{
    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private string _language;

    public Localizer(string language = "vi") => _language = language;

    public string Language => _language;
    public event EventHandler? LanguageChanged;

    /// <summary>Loads every lang/*.json file of the folder (file name = language code).</summary>
    public static Localizer FromFolder(string folder, string language)
    {
        var loc = new Localizer(language);
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new();
                loc.Add(Path.GetFileNameWithoutExtension(file), map);
            }
        }
        return loc;
    }

    public void Add(string language, IDictionary<string, string> entries)
    {
        if (!_tables.TryGetValue(language, out var table))
            _tables[language] = table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in entries) table[kv.Key] = kv.Value;
    }

    public void SetLanguage(string language)
    {
        if (language == _language) return;
        _language = language;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string T(string key, params object[] args)
    {
        string? text = null;
        if (_tables.TryGetValue(_language, out var table)) table.TryGetValue(key, out text);
        if (text is null && _tables.TryGetValue("vi", out var vi)) vi.TryGetValue(key, out text);
        text ??= key;
        if (args.Length == 0) return text;
        try { return string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args); }
        catch (FormatException) { return text; }
    }
}
