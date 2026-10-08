using System.Text.RegularExpressions;
using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Localization;
using CheckBarcode.Application.Plc;
using CheckBarcode.Domain;
using CheckBarcode.Tests.Framework;

namespace CheckBarcode.Tests;

/// <summary>Validates the shipped configuration files so a wrong edit is caught before deployment (Annex 11 §10).</summary>
public sealed class ConfigTests
{
    private static string Config(string file) => Path.Combine(AppContext.BaseDirectory, "config", file);
    private static List<TagDefinition> LoadTags() => ConfigLoader.LoadList<TagDefinition>(Config("tags.json"));
    private static List<ActionDefinition> Actions() => ConfigLoader.LoadList<ActionDefinition>(Config("actions.json"));

    [Test("PLC-03", "ALM-03")]
    public void Tags_have_unique_ids_and_non_overlapping_registers()
    {
        var tags = LoadTags();
        Assert.Equal(tags.Count, tags.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "unique ids");
        foreach (var group in tags.GroupBy(t => (t.Address, t.IsWrite)))
        {
            var whole = group.Where(t => t.Bit is null).ToList();
            Assert.True(whole.Count <= 1 && (whole.Count == 0 || group.Count() == 1), $"register {group.Key.Address} used twice");
            Assert.Equal(group.Count(), group.Select(t => t.Bit).Distinct().Count(), $"bit used twice at {group.Key.Address}");
        }
        foreach (var t in tags)
        {
            Assert.True(t.Text.Vi.Length > 0 && t.Text.En.Length > 0, $"{t.Id} needs vi/en text");
            if (t.Readback is { } rb) Assert.True(tags.Any(x => x.Id == rb && !x.IsWrite), $"{t.Id} readback {rb} missing");
            if (t.Category == TagCategory.Alarm) TagEngine.IsActive(t, 0);
        }
    }

    [Test("PLC-03", "PLC-04")]
    public void Legacy_map_matches_original_PLC_program()
    {
        var tags = ConfigLoader.LoadList<TagDefinition>(Config("tags.legacy.json"));
        Assert.Equal(tags.Count, tags.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "unique ids");
        int A(string id) => tags.Single(t => t.Id == id).Address;
        Assert.Equal(0, A(Tags.MachineRunning), "machine running");
        Assert.Equal(19, A(Tags.SpeedActual), "speed");
        Assert.Equal(20, A(Tags.TempActual), "temp");
        Assert.Equal(19, A(Tags.SpeedSetpoint), "speed setpoint written to the same register as v1");
        Assert.Equal(20, A(Tags.TempSetpoint), "temp setpoint written to the same register as v1");
        Assert.Equal(59, A(Tags.BoxCamScanning), "box scanning");
        Assert.Equal(60, A(Tags.LeafletCamScanning), "leaflet scanning");
        Assert.Equal(61, A(Tags.LoginLevel), "login level");
        var cams = tags.Where(t => t.IsWrite && t.Group == Tags.CamGroup).ToList();
        Assert.Equal(30, cams.Count);
        foreach (var c in cams)
        {
            var rb = tags.Single(t => t.Id == c.Readback && !t.IsWrite);
            Assert.Equal(rb.Address, c.Address, $"{c.Id} writes the register it reads back");
            Assert.True(c.Address is >= 21 and <= 50, $"{c.Id} in 21..50");
        }
        foreach (var t in tags) Assert.True(t.Text.Vi.Length > 0 && t.Text.En.Length > 0, $"{t.Id} needs vi/en text");
    }

    [Test("PLC-04")]
    public void Fifteen_cams_with_on_off_setpoints_and_readbacks()
    {
        var tags = LoadTags();
        var cams = tags.Where(t => t.IsWrite && t.Group == Tags.CamGroup).ToList();
        Assert.Equal(30, cams.Count);
        Assert.True(cams.All(c => c.Min == 0 && c.Max == 359), "cam range 0..359");
        foreach (var id in new[] { Tags.MachineRunning, Tags.SpeedActual, Tags.TempActual, Tags.LoginLevel, Tags.BoxCamScanning, Tags.LeafletCamScanning, Tags.SpeedSetpoint, Tags.TempSetpoint, TagEngine.PcHeartbeatTag, TagEngine.PlcHeartbeatTag })
            Assert.True(tags.Any(t => t.Id == id), $"required tag {id}");
    }

    [Test("AUD-04", "AUD-06")]
    public void Every_action_used_in_code_is_defined_with_texts()
    {
        var actions = Actions().ToDictionary(a => a.Code);
        var codes = new[]
        {
            "USER_CREATE", "USER_UPDATE", "USER_RESET_PASSWORD", "USER_LOCK", "USER_UNLOCK", "USER_DEACTIVATE", "USER_REACTIVATE",
            "ROLE_PERMISSIONS_CHANGE", "SECURITY_POLICY_CHANGE", "RECIPE_CREATE", "RECIPE_EDIT", "RECIPE_DELETE", "BATCH_CREATE",
            "BATCH_START", "BATCH_PAUSE", "BATCH_RESUME", "BATCH_END", "BATCH_RESET_COUNTERS", "ALARM_ACK", "MACHINE_CAM_EDIT",
            "CAMERA_CONNECT", "CAMERA_DISCONNECT", "CAMERA_SETTINGS", "SETTINGS_CHANGE", "REPORT_EXPORT", "REPORT_PRINT", "AUDIT_REVIEW", "APP_EXIT",
        };
        foreach (var c in codes)
        {
            Assert.True(actions.ContainsKey(c), $"action {c} missing in actions.json");
            Assert.True(actions[c].Text.Vi.Length > 0 && actions[c].Text.En.Length > 0, $"{c} text");
            Assert.True(actions[c].Permission == "*" || Permissions.All.Contains(actions[c].Permission), $"{c} permission {actions[c].Permission}");
        }
        foreach (var signed in new[] { "BATCH_END", "RECIPE_EDIT", "RECIPE_DELETE", "AUDIT_REVIEW" })
            Assert.True(actions[signed].RequireSignature, $"{signed} requires e-signature (user decision 2026-10-06)");
    }

    [Test("SYS-01", "AUD-06")]
    public void Every_language_key_exists_in_vi_and_en()
    {
        var vi = Localizer.FromFolder(Path.Combine(AppContext.BaseDirectory, "config", "lang"), "vi");
        var en = Localizer.FromFolder(Path.Combine(AppContext.BaseDirectory, "config", "lang"), "en");
        var keys = new HashSet<string>();
        foreach (var f in typeof(AuditCodes).GetFields()) keys.Add("audit." + f.GetValue(null));
        var root = FindRepoRoot();
        if (root is not null)
        {
            foreach (var file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
            {
                var code = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(code, "\"((?:msg|ui|report|col|camera|outcome|batchStatus|severity|alarmState|field|value|session|perm)\\.[A-Za-z0-9_.]+)\""))
                    keys.Add(m.Groups[1].Value);
            }
        }
        foreach (var p in Permissions.All) keys.Add("perm." + p);
        foreach (var r in new[] { CameraRole.Box, CameraRole.Leaflet }) { keys.Add("camera.title." + r); keys.Add("camera." + r); }
        foreach (var k in new[] { "Batch", "BatchList", "Audit", "Alarm", "LogBox", "LogLeaflet" }) keys.Add("ui.report." + k);
        foreach (var m in Enum.GetNames<SaveImageMode>()) keys.Add("ui.imageMode." + m);
        foreach (var l in new[] { "WrongPassword", "LockedByAdmin" }) keys.Add("ui.lockReason." + l);
        foreach (var v in Enum.GetNames<UserStatus>().Concat(new[] { Roles.Operator, Roles.Supervisor, Roles.Admin })) keys.Add("value." + v);
        keys.RemoveWhere(k => k.EndsWith('.'));
        var missing = keys.Where(k => vi.T(k) == k || en.T(k) == k).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "missing language keys: " + string.Join(", ", missing));
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PLAN.md"))) dir = dir.Parent;
        return dir?.FullName;
    }
}
