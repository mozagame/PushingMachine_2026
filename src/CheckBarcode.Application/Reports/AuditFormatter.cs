using System.Globalization;
using System.Text.Json;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Localization;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Reports;

/// <summary>Local 24-hour time formatting (RPT-07). Storage is always UTC.</summary>
public sealed class TimeFormat
{
    private readonly TimeZoneInfo _zone;

    public TimeFormat(TimeZoneInfo? zone = null) => _zone = zone ?? TimeZoneInfo.Local;

    public const string DateTimePattern = "dd/MM/yyyy HH:mm:ss";
    public const string DatePattern = "dd/MM/yyyy";

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(System.DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);
    public DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(System.DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _zone);
    public string DateTime(DateTime? utc) => utc is { } u ? ToLocal(u).ToString(DateTimePattern, CultureInfo.InvariantCulture) : "";
    public string Date(DateTime? utc) => utc is { } u ? ToLocal(u).ToString(DatePattern, CultureInfo.InvariantCulture) : "";
}

/// <summary>Turns an audit record (code + arguments) into a sentence in the selected language (AUD-06).</summary>
public sealed class AuditFormatter
{
    private readonly IReadOnlyDictionary<string, ActionDefinition> _actions;
    private readonly IReadOnlyDictionary<string, TagDefinition> _tags;
    private readonly ILocalizer _loc;

    public AuditFormatter(IEnumerable<ActionDefinition> actions, IEnumerable<TagDefinition> tags, ILocalizer loc)
    {
        _actions = actions.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
        _tags = tags.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        _loc = loc;
    }

    /// <summary>Resolves alarm ids to text (set by the composition root).</summary>
    public Func<string, string?>? AlarmText { get; set; }

    public string Message(AuditRecord r)
    {
        var args = ParseArgs(r.MessageArgs);
        if (AlarmText is not null && args.TryGetValue("alarms", out var ids) && ids.Length > 0)
            args["alarms"] = string.Join(", ", ids.Split(',').Select(id => AlarmText(id) ?? id));
        string template;
        if (_actions.TryGetValue(r.ActionCode, out var def)) template = def.Text.Get(_loc.Language);
        else template = _loc.T("audit." + r.ActionCode);

        var fieldText = FieldText(r.Field);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectType"] = r.ObjectType,
            ["objectId"] = r.ObjectId,
            ["field"] = fieldText,
            ["old"] = Value(r.OldValue),
            ["new"] = Value(r.NewValue),
            ["reason"] = r.Reason,
        };
        foreach (var kv in args) values[kv.Key] = kv.Value;
        if (args.TryGetValue("tag", out var tagId)) values["tagText"] = FieldText(tagId);
        if (args.TryGetValue("action", out var action) && _actions.TryGetValue(action, out var ad))
            values["actionText"] = Strip(ad.Text.Get(_loc.Language));

        var message = Fill(template, values);
        if (!template.Contains("{field}") && !string.IsNullOrEmpty(r.Field) && (r.OldValue.Length > 0 || r.NewValue.Length > 0))
            message += $" — {fieldText}: {Value(r.OldValue)} → {Value(r.NewValue)}";
        return message;
    }

    public string FieldText(string field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        if (_tags.TryGetValue(field, out var tag)) return tag.Text.Get(_loc.Language) + (tag.Unit.Length > 0 ? $" ({tag.Unit})" : "");
        var key = "field." + field;
        var t = _loc.T(key);
        return t == key ? field : t;
    }

    public string SessionText(AuditRecord r) => r.SessionState switch
    {
        SessionState.Expired => _loc.T("session.expired"),
        SessionState.None => _loc.T("session.none"),
        _ => "",
    };

    private string Value(string v)
    {
        if (v.Length == 0) return "—";
        var key = "value." + v;
        var t = _loc.T(key);
        return t == key ? v : t;
    }

    private static string Strip(string template)
    {
        var i = template.IndexOf('{');
        return (i > 0 ? template[..i] : template).Trim(' ', ':', '-');
    }

    private static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new System.Text.StringBuilder(template.Length + 32);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end > i)
                {
                    var key = template.Substring(i + 1, end - i - 1);
                    sb.Append(values.TryGetValue(key, out var v) ? v : "");
                    i = end;
                    continue;
                }
            }
            sb.Append(template[i]);
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseArgs(string json)
    {
        if (string.IsNullOrEmpty(json)) return new Dictionary<string, string>();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }
}
