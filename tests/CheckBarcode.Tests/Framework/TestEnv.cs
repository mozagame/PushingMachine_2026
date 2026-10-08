using System.Text.Json;
using System.Text.Json.Nodes;
using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Ports;
using CheckBarcode.Application.Security;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure;
using CheckBarcode.Simulator;

namespace CheckBarcode.Tests.Framework;

/// <summary>Real-time clock that tests can move forward (password expiry, idle logout).</summary>
public sealed class FakeClock : IClock
{
    private TimeSpan _offset;
    public DateTime UtcNow => DateTime.UtcNow + _offset;
    public void Advance(TimeSpan by) => _offset += by;
}

/// <summary>Scripted answers for reason / e-signature dialogs.</summary>
public sealed class TestPrompt : IActionPrompt
{
    public string? Reason { get; set; } = "test reason";
    public SignatureInput? Signature { get; set; }
    public int ReasonAsked { get; private set; }
    public int SignatureAsked { get; private set; }

    public Task<string?> AskReasonAsync(ActionDefinition action)
    {
        ReasonAsked++;
        return Task.FromResult(Reason);
    }

    public Task<SignatureInput?> AskSignatureAsync(ActionDefinition action, string meaning)
    {
        SignatureAsked++;
        return Task.FromResult(Signature);
    }
}

/// <summary>Complete application on a temp folder: real SQLite, real Modbus TCP to the simulator, simulated cameras.</summary>
public sealed class TestEnv : IAsyncDisposable
{
    public const string DefaultPassword = "Abc@1234";
    public const string AdminPassword = "Admin@123";

    public string Dir { get; }
    public string UsbDir { get; }
    public FakeClock Clock { get; } = new();
    public ModbusTcpServer Plc { get; }
    public MachineModel Machine { get; }
    public SimulatedCodeReader Box { get; private set; } = null!;
    public SimulatedCodeReader Leaflet { get; private set; } = null!;
    public TestPrompt Prompt { get; } = new();
    public AppHost Host { get; private set; } = null!;

    private TestEnv(string dir)
    {
        Dir = dir;
        UsbDir = Path.Combine(dir, "usb");
        Plc = new ModbusTcpServer();
        Plc.Start();
        Machine = new MachineModel(Plc, 50);
    }

    public static async Task<TestEnv> CreateAsync(bool startDevices = true, Action<JsonObject>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cbtest_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "config"), Path.Combine(dir, "config"));
        var env = new TestEnv(dir);

        var settingsPath = Path.Combine(dir, "config", "appsettings.json");
        var json = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        json["plc"]!["ip"] = "127.0.0.1";
        json["plc"]!["port"] = env.Plc.Port;
        json["plc"]!["pollMs"] = 40;
        json["plc"]!["heartbeatTimeoutMs"] = 1500;
        // Tests run against the simulator, which implements the new register map.
        json["plc"]!["tagsFile"] = "tags.json";
        json["plc"]!.AsObject().Remove("loginLevels");
        foreach (var cam in json["cameras"]!.AsArray()) cam!["driver"] = "Simulator";
        configure?.Invoke(json);
        File.WriteAllText(settingsPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        env.Build();
        await env.Host.StartAsync(startDevices);
        return env;
    }

    /// <summary>Simulates a restart of the PC application on the same data folder.</summary>
    public async Task RestartAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
        Build();
        await Host.StartAsync(true);
    }

    private void Build()
    {
        Directory.CreateDirectory(UsbDir);
        Host = new AppHost(new AppHostOptions
        {
            BaseDirectory = Dir,
            Clock = Clock,
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"),
            Workstation = "TEST-PC",
            PasswordHasher = new Pbkdf2PasswordHasher(1000),
            DriveProvider = new Infrastructure.Files.FixedDriveProvider(UsbDir),
            ReaderFactory = cfg =>
            {
                var reader = new SimulatedCodeReader(cfg.Name, cfg.Role, cfg.Ip);
                if (cfg.Role == CameraRole.Box) Box = reader; else Leaflet = reader;
                return reader;
            },
        });
        Host.Dispatcher.Prompt = Prompt;
    }

    public async Task WaitPlcOnlineAsync() => await Assert.Eventually(() => Host.Machine.IsOnline, "PLC online");

    /// <summary>Completes the first-login password change of the seeded admin and logs in.</summary>
    public void LoginAdmin()
    {
        var first = Host.Auth.Login("admin", DefaultPassword);
        if (first.Status == LoginStatus.MustChangePassword)
        {
            var changed = Host.Auth.ChangePassword("admin", DefaultPassword, AdminPassword, AdminPassword);
            Assert.True(changed.Ok, "admin password change: " + changed.MessageKey);
        }
        var login = Host.Auth.Login("admin", AdminPassword);
        Assert.Equal(LoginStatus.Success, login.Status, "admin login");
        Prompt.Signature = new SignatureInput("admin", AdminPassword);
    }

    /// <summary>Creates a user (as admin), sets its password and logs it in.</summary>
    public async Task<string> CreateAndLoginAsync(string username, string role, string password = "Pass@123")
    {
        LoginAdmin();
        var created = await Host.Users.CreateAsync(username, username.ToUpperInvariant(), role);
        Assert.True(created.Ok, "create user: " + created.MessageKey);
        Host.Auth.Logout(LogoutReason.Manual);
        Assert.Equal(LoginStatus.MustChangePassword, Host.Auth.Login(username, DefaultPassword).Status);
        Assert.True(Host.Auth.ChangePassword(username, DefaultPassword, password, password).Ok);
        Assert.Equal(LoginStatus.Success, Host.Auth.Login(username, password).Status);
        Prompt.Signature = new SignatureInput(username, password);
        return password;
    }

    public async Task<(long Box, long Leaflet)> CreateRecipesAsync(string boxCode = "512", string leafletCode = "767")
    {
        var box = await Host.Recipes.CreateAsync(new Application.Recipes.RecipeInput("Partamol eff", CameraRole.Box, boxCode, BarcodeFormat.Pharmacode, 50, 5, 110, 3));
        Assert.True(box.Ok, "box recipe: " + box.MessageKey);
        var leaf = await Host.Recipes.CreateAsync(new Application.Recipes.RecipeInput("Partamol eff toa", CameraRole.Leaflet, leafletCode, BarcodeFormat.Pharmacode, null, null, null, null));
        Assert.True(leaf.Ok, "leaflet recipe: " + leaf.MessageKey);
        return (Host.Recipes.List(CameraRole.Box).Single(r => r.Barcode == boxCode).Id, Host.Recipes.List(CameraRole.Leaflet).Single(r => r.Barcode == leafletCode).Id);
    }

    public IReadOnlyList<AuditRecord> Audit(string? actionPrefix = null) =>
        Host.AuditStore.Query(new AuditQuery(ActionPrefix: actionPrefix));

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    public async ValueTask DisposeAsync()
    {
        try { await Host.StopAsync(); } catch { /* ignore */ }
        Host.Dispose();
        Machine.Dispose();
        Plc.Dispose();
        try { Directory.Delete(Dir, true); } catch { /* temp */ }
    }
}
