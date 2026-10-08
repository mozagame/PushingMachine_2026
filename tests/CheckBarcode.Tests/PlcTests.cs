using CheckBarcode.Application.Alarms;
using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Plc;
using CheckBarcode.Application.Security;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure.Plc;
using CheckBarcode.Simulator;
using CheckBarcode.Tests.Framework;

namespace CheckBarcode.Tests;

public sealed class PlcTests
{
    [Test("PLC-01")]
    public async Task Modbus_client_reads_and_writes_registers()
    {
        using var server = new ModbusTcpServer();
        server.Start();
        server[5] = 1234;
        server[6] = 0xFFFF;
        using var client = new ModbusTcpClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);
        var values = await client.ReadHoldingRegistersAsync(5, 2, CancellationToken.None);
        Assert.Equal((ushort)1234, values[0]);
        Assert.Equal((ushort)0xFFFF, values[1]);
        await client.WriteMultipleRegistersAsync(110, new ushort[] { 45, 90 }, CancellationToken.None);
        Assert.Equal((ushort)90, server[111]);
    }

    [Test("PLC-01")]
    public async Task Modbus_exception_and_timeout_are_reported()
    {
        using var server = new ModbusTcpServer();
        server.Start();
        using var client = new ModbusTcpClient("127.0.0.1", server.Port, timeoutMs: 300);
        await client.ConnectAsync(CancellationToken.None);
        var ex = await ThrowsAsync<ModbusException>(() => client.ReadHoldingRegistersAsync(65530, 100, CancellationToken.None));
        Assert.Equal((byte)2, ex.ExceptionCode);
        server.Mute = true;
        await ThrowsAsync<TimeoutException>(() => client.ReadHoldingRegistersAsync(0, 1, CancellationToken.None));
        Assert.False(client.IsConnected, "connection dropped after timeout");
    }

    [Test("PLC-03")]
    public void Blocks_are_merged_and_bits_decoded()
    {
        var tags = new[]
        {
            new TagDefinition { Id = "A", Address = 0 },
            new TagDefinition { Id = "B", Address = 3, Bit = 2, Type = "Bool" },
            new TagDefinition { Id = "C", Address = 6, Type = "Int16" },
            new TagDefinition { Id = "D", Address = 40, Type = "Int32" },
        };
        var blocks = TagEngine.BuildBlocks(tags);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(((ushort)0, (ushort)7), blocks[0]);
        Assert.Equal(((ushort)40, (ushort)2), blocks[1]);
        Assert.Equal(1.0, TagEngine.Decode(tags[1], new ushort[] { 0b100 }, 0));
        Assert.Equal(0.0, TagEngine.Decode(tags[1], new ushort[] { 0b011 }, 0));
        Assert.Equal(-5.0, TagEngine.Decode(tags[2], new ushort[] { unchecked((ushort)-5) }, 0));
        Assert.Equal(70000.0, TagEngine.Decode(tags[3], new ushort[] { 1, 4464 }, 0));
        Assert.True(TagEngine.IsActive(new TagDefinition { ActiveWhen = "> 80" }, 81));
        Assert.False(TagEngine.IsActive(new TagDefinition { ActiveWhen = "== 1" }, 0));
    }

    [Test("PLC-03", "ALM-01", "ALM-03")]
    public async Task Alarm_bits_create_alarm_records_with_lifecycle()
    {
        await using var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        env.Machine.SetAlarm(2, true); // air pressure
        await Assert.Eventually(() => env.Host.Alarms.IsRaised("ALM_AIR_PRESSURE"), "alarm raised");
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        Assert.True((await env.Host.Alarms.AcknowledgeAsync("ALM_AIR_PRESSURE")).Ok);
        env.Machine.SetAlarm(2, false);
        await Assert.Eventually(() => !env.Host.Alarms.IsRaised("ALM_AIR_PRESSURE"), "alarm cleared");
        Assert.False(env.Host.Alarms.Active.Any(a => a.Definition.Id == "ALM_AIR_PRESSURE"), "acked + cleared leaves the list");

        var states = env.Host.AlarmStore.Query(null, null, null).Where(r => r.AlarmId == "ALM_AIR_PRESSURE").Select(r => r.State).ToList();
        Assert.Equal("Active,Acknowledged,Cleared", string.Join(",", states));
        Assert.Equal("op1", env.Host.AlarmStore.Query(null, null, null).Single(r => r.AlarmId == "ALM_AIR_PRESSURE" && r.State == AlarmState.Acknowledged).Username);
        Assert.Equal(0, env.Audit().Count(a => a.ActionCode.StartsWith("ALM_")), "alarms not mixed into the audit trail");
    }

    [Test("ALM-02")]
    public async Task Alarm_ack_requires_login()
    {
        await using var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        env.Machine.SetEStop(true);
        await Assert.Eventually(() => env.Host.Alarms.IsRaised("ESTOP_ACTIVE"), "e-stop alarm");
        Assert.Equal("msg.loginRequired", (await env.Host.Alarms.AcknowledgeAsync(null)).MessageKey);
        Assert.True(env.Host.Alarms.HasUnacknowledged);
    }

    [Test("PLC-04", "AUD-02")]
    public async Task Cam_setpoints_written_from_PC_are_audited_with_reason()
    {
        await using var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        env.LoginAdmin();
        env.Prompt.Reason = "Chỉnh cam theo khuôn mới";
        var outcome = await env.Host.Machine.WriteCamSetpointsAsync(new Dictionary<string, double> { ["CAM_HUT_HOP_ON_SP"] = 123, ["CAM_HUT_HOP_OFF_SP"] = 200 });
        Assert.True(outcome.Ok, outcome.MessageKey);
        Assert.Equal((ushort)123, env.Plc[110]);
        Assert.Equal((ushort)200, env.Plc[111]);
        var audit = env.Audit("MACHINE_CAM_EDIT");
        Assert.Equal(2, audit.Count);
        Assert.Equal("Chỉnh cam theo khuôn mới", audit[0].Reason);
        Assert.Equal("123", audit.Single(a => a.Field == "CAM_HUT_HOP_ON_SP").NewValue);
        await Task.Delay(300);
        Assert.Equal(0, env.Audit(AuditCodes.PlcParameterChangedOutside).Count, "own write is not reported as outside change");
    }

    [Test("PLC-04", "USR-09")]
    public async Task Cam_edit_needs_permission_and_range()
    {
        await using var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        Assert.Equal("msg.permissionDenied", (await env.Host.Machine.WriteCamSetpointsAsync(new Dictionary<string, double> { ["CAM_HUT_HOP_ON_SP"] = 1 })).MessageKey);
        env.LoginAdmin();
        Assert.Equal("msg.valueOutOfRange", (await env.Host.Machine.WriteCamSetpointsAsync(new Dictionary<string, double> { ["CAM_HUT_HOP_ON_SP"] = 400 })).MessageKey);
    }

    [Test("PLC-06")]
    public async Task Changes_made_on_the_machine_are_audited_as_outside_PC()
    {
        await using var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        await Task.Delay(200);
        env.Machine.ChangeCamLocally(0, 77);
        await Assert.Eventually(() => env.Audit(AuditCodes.PlcParameterChangedOutside).Count == 1, "outside change audited");
        var rec = env.Audit(AuditCodes.PlcParameterChangedOutside).Single();
        Assert.Equal("CAM_HUT_HOP_ON_ACT", rec.Field);
        Assert.Equal("77", rec.NewValue);
        Assert.Equal("SYSTEM", rec.Username);
        Assert.Contains("Cam Hút hộp ON", env.Host.Formatter.Message(rec));
    }

    [Test("PLC-05", "USR-09")]
    public async Task Login_level_is_written_to_PLC()
    {
        await using var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        await env.CreateAndLoginAsync("sup1", Roles.Supervisor);
        await Assert.Eventually(() => env.Plc[101] == 2, "supervisor level 2");
        env.Host.Auth.Logout(LogoutReason.Manual);
        await Assert.Eventually(() => env.Plc[101] == 0, "level 0 after logout");
    }

    [Test("PLC-01", "AUD-07")]
    public async Task Heartbeat_loss_raises_communication_alarm()
    {
        await using var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        Assert.True(env.Plc[100] > 0, "PC heartbeat written");
        env.Machine.HeartbeatEnabled = false;
        await Assert.Eventually(() => env.Host.Alarms.IsRaised(SoftwareAlarms.PlcCommunication), "comm alarm", 6000);
        env.Machine.HeartbeatEnabled = true;
        await Assert.Eventually(() => !env.Host.Alarms.IsRaised(SoftwareAlarms.PlcCommunication), "comm alarm cleared", 6000);
        Assert.True(env.Audit(AuditCodes.PlcConnection).Count >= 2);
    }

    [Test("PLC-01")]
    public async Task Reconnects_after_connection_drop()
    {
        await using var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        env.Plc.DropClients();
        await Assert.Eventually(() => env.Audit(AuditCodes.PlcConnection).Any(a => a.NewValue == "false"), "offline detected and audited", 6000);
        await Assert.Eventually(() => env.Host.Machine.IsOnline, "back online", 8000);
    }

    [Test("AUD-07")]
    public async Task Machine_on_off_is_audited()
    {
        await using var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        await Task.Delay(200);
        env.Machine.Running = true;
        await Assert.Eventually(() => env.Audit(AuditCodes.PlcStateChanged).Any(a => a.ObjectId == "MACHINE_RUNNING" && a.NewValue == "1"), "machine on audited");
        var rec = env.Audit(AuditCodes.PlcStateChanged).First(a => a.ObjectId == "MACHINE_RUNNING");
        Assert.Contains("Trạng thái máy chạy", env.Host.Formatter.Message(rec));
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { throw new AssertionException($"expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertionException($"expected {typeof(T).Name}");
    }
}
