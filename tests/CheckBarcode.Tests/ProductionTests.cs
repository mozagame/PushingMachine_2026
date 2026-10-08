using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Alarms;
using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Recipes;
using CheckBarcode.Application.Security;
using CheckBarcode.Application.Settings;
using CheckBarcode.Domain;
using CheckBarcode.Tests.Framework;

namespace CheckBarcode.Tests;

public sealed class ProductionTests
{
    private static async Task<TestEnv> ReadyAsync()
    {
        var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        await Assert.Eventually(() => env.Box.IsConnected && env.Leaflet.IsConnected, "cameras connected");
        return env;
    }

    [Test("REC-01", "REC-04")]
    public async Task Recipe_edit_increments_version_and_audits_each_field()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("sup1", Roles.Supervisor);
        var (boxId, _) = await env.CreateRecipesAsync();
        var r = env.Host.Recipes.Find(boxId)!;
        Assert.Equal(1, r.Version);
        var outcome = await env.Host.Recipes.EditAsync(boxId, new RecipeInput(r.Name, CameraRole.Box, "513", BarcodeFormat.Pharmacode, 55, 5, 110, 3));
        Assert.True(outcome.Ok, outcome.MessageKey);
        Assert.Equal(2, env.Host.Recipes.Find(boxId)!.Version);
        var edits = env.Audit("RECIPE_EDIT");
        Assert.Equal("Barcode,SpeedSetpoint,Version", string.Join(",", edits.Select(e => e.Field)));
        Assert.Equal("512", edits[0].OldValue);
        Assert.Equal("513", edits[0].NewValue);
        Assert.True(edits.All(e => e.SignatureMeaning is not null), "edit is e-signed");
        Assert.Equal(1, env.Prompt.ReasonAsked);
        Assert.Contains("Sửa recipe Partamol eff: Barcode từ 512 thành 513", env.Host.Formatter.Message(edits[0]));
    }

    [Test("REC-03")]
    public async Task Deleted_recipe_name_can_be_reused()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("sup1", Roles.Supervisor);
        var (_, leafId) = await env.CreateRecipesAsync();
        var dup = await env.Host.Recipes.CreateAsync(new RecipeInput("PARTAMOL EFF TOA", CameraRole.Leaflet, "1", BarcodeFormat.Pharmacode, null, null, null, null));
        Assert.Equal("msg.recipeNameExists", dup.MessageKey);
        Assert.True((await env.Host.Recipes.DeleteAsync(leafId)).Ok);
        Assert.True((await env.Host.Recipes.CreateAsync(new RecipeInput("Partamol eff toa", CameraRole.Leaflet, "768", BarcodeFormat.Pharmacode, null, null, null, null))).Ok);
        Assert.Equal(RecordStatus.Deleted, env.Host.RecipesRepo.Find(leafId)!.Status);
        Assert.True(env.Audit("RECIPE_DELETE").Single().SignatureMeaning is not null, "delete is e-signed");
    }

    [Test("REC-02")]
    public async Task Box_recipe_requires_setpoints()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        env.LoginAdmin();
        var r = await env.Host.Recipes.CreateAsync(new RecipeInput("B", CameraRole.Box, "1", BarcodeFormat.Pharmacode, null, null, 100, 2));
        Assert.Equal("msg.recipeSetpointsRequired", r.MessageKey);
        Assert.Equal(1, env.Audit(AuditCodes.ActionFailed).Count, "failed action audited");
    }

    [Test("BAT-01", "BAT-02", "BAT-04", "BAT-05", "CAM-04", "PLC-05")]
    public async Task Full_batch_counts_and_writes_setpoints()
    {
        await using var env = await ReadyAsync();
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        env.LoginAdmin();
        var (boxId, leafId) = await env.CreateRecipesAsync();
        await env.CreateAndLoginAsync("op2", Roles.Operator);

        Assert.True((await env.Host.Batches.CreateAsync("Partamol", "L001", boxId, leafId)).Ok);
        Assert.Equal("msg.batchAlreadyOpen", (await env.Host.Batches.CreateAsync("Partamol", "L002", boxId, leafId)).MessageKey);

        env.Box.Emit("512");
        Assert.Equal(0L, env.Host.Batches.Snapshot()!.Box.Scanned, "no counting before start");

        Assert.True((await env.Host.Batches.StartAsync()).Ok);
        Assert.Equal("512", env.Box.MatchString);
        Assert.Equal("767", env.Leaflet.MatchString);
        Assert.Equal((ushort)50, env.Plc[104]);
        Assert.Equal((ushort)110, env.Plc[105]);
        Assert.Equal((ushort)1, env.Plc[102]);
        Assert.Equal((ushort)1, env.Plc[103]);

        for (var i = 0; i < 10; i++) env.Box.Emit("512");
        env.Box.Emit("513");
        env.Box.Emit("");
        for (var i = 0; i < 8; i++) env.Leaflet.Emit("767");
        env.Leaflet.Emit("");
        env.Leaflet.Emit("999");

        var snap = env.Host.Batches.Snapshot()!;
        Assert.Equal(12L, snap.Box.Scanned);
        Assert.Equal(10L, snap.Box.Pass);
        Assert.Equal(1L, snap.Box.Fail);
        Assert.Equal(1L, snap.Box.NoRead);
        Assert.Equal(10L, snap.Leaflet.Scanned);
        Assert.Equal(8L, snap.FinishedGoods, "10 box pass - 1 leaflet fail - 1 no read");
        Assert.Equal(4, env.Host.Batches.FailResults().Count);
    }

    [Test("BAT-01")]
    public async Task Batch_number_must_be_unique()
    {
        await using var env = await ReadyAsync();
        env.LoginAdmin();
        var (boxId, _) = await env.CreateRecipesAsync();
        Assert.True((await env.Host.Batches.CreateAsync("P", "L-1", boxId, null)).Ok);
        Assert.True((await env.Host.Batches.StartAsync()).Ok);
        Assert.True((await env.Host.Batches.EndAsync()).Ok);
        Assert.Equal("msg.batchNoExists", (await env.Host.Batches.CreateAsync("P", " l-1 ", boxId, null)).MessageKey);
    }

    [Test("BAT-02", "CAM-01")]
    public async Task Start_requires_connected_cameras_and_plc()
    {
        await using var env = await ReadyAsync();
        env.LoginAdmin();
        var (boxId, leafId) = await env.CreateRecipesAsync();
        await env.Host.Batches.CreateAsync("P", "L1", boxId, leafId);
        env.Leaflet.Drop();
        var r = await env.Host.Batches.StartAsync();
        Assert.Equal("msg.cameraNotConnected", r.MessageKey);
        Assert.True(env.Host.Alarms.IsRaised(SoftwareAlarms.LeafletCameraOffline));
        Assert.True((await env.Host.Cameras.ConnectAsync(CameraRole.Leaflet)).Ok);
        Assert.False(env.Host.Alarms.IsRaised(SoftwareAlarms.LeafletCameraOffline));
        Assert.True((await env.Host.Batches.StartAsync()).Ok);
    }

    [Test("BAT-03")]
    public async Task Camera_loss_auto_pauses_running_batch()
    {
        await using var env = await ReadyAsync();
        env.LoginAdmin();
        var (boxId, leafId) = await env.CreateRecipesAsync();
        await env.Host.Batches.CreateAsync("P", "L1", boxId, leafId);
        await env.Host.Batches.StartAsync();
        env.Box.Drop();
        Assert.Equal(BatchStatus.Paused, env.Host.Batches.Current!.Status);
        Assert.Equal(1, env.Audit(AuditCodes.BatchAutoPaused).Count);
        await Assert.Eventually(() => env.Plc[102] == 0, "PLC told to stop scanning");
        env.Box.Emit("512");
        Assert.Equal(0L, env.Host.Batches.Snapshot()!.Box.Scanned, "no counting while paused");
    }

    [Test("USR-09", "CAM-01")]
    public async Task Camera_disconnect_requires_login_and_no_running_batch()
    {
        await using var env = await ReadyAsync();
        Assert.Equal("msg.loginRequired", (await env.Host.Cameras.DisconnectAsync(CameraRole.Box, () => env.Host.Batches.IsRunning)).MessageKey);
        env.LoginAdmin();
        var (boxId, _) = await env.CreateRecipesAsync();
        await env.Host.Batches.CreateAsync("P", "L1", boxId, null);
        await env.Host.Batches.StartAsync();
        Assert.Equal("msg.cannotDisconnectWhileRunning", (await env.Host.Cameras.DisconnectAsync(CameraRole.Box, () => env.Host.Batches.IsRunning)).MessageKey);
        await env.Host.Batches.PauseAsync();
        Assert.True((await env.Host.Cameras.DisconnectAsync(CameraRole.Box, () => env.Host.Batches.IsRunning)).Ok);
        Assert.False(env.Host.Alarms.IsRaised(SoftwareAlarms.BoxCameraOffline), "manual disconnect is not an alarm");
    }

    [Test("BAT-06", "BAT-07")]
    public async Task Batch_survives_restart_with_counters_and_fail_list()
    {
        await using var env = await ReadyAsync();
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        env.LoginAdmin();
        var (boxId, leafId) = await env.CreateRecipesAsync();
        Assert.Equal(LoginStatus.Success, env.Host.Auth.Login("op1", "Pass@123").Status);
        await env.Host.Batches.CreateAsync("P", "L9", boxId, leafId);
        await env.Host.Batches.StartAsync();
        env.Box.Emit("512");
        env.Box.Emit("000");
        env.Leaflet.Emit("");

        await env.RestartAsync();
        await env.WaitPlcOnlineAsync();
        var batch = env.Host.Batches.Current!;
        Assert.Equal("L9", batch.BatchNo);
        Assert.Equal(BatchStatus.Paused, batch.Status, "running batch comes back paused");
        var snap = env.Host.Batches.Snapshot()!;
        Assert.Equal(2L, snap.Box.Scanned);
        Assert.Equal(1L, snap.Box.Fail);
        Assert.Equal(2, env.Host.Batches.FailResults().Count, "fail list restored");
        Assert.True(env.Host.Batches.FailResults().All(f => f.ImagePath is not null && File.Exists(f.ImagePath)), "fail images restored");
        Assert.Equal(1, env.Audit(AuditCodes.BatchRestored).Count);

        await env.CreateAndLoginAsync("op2", Roles.Operator);
        Assert.True((await env.Host.Batches.ResumeAsync()).Ok);
        env.Prompt.Signature = new Application.Actions.SignatureInput("op2", "Pass@123");
        Assert.True((await env.Host.Batches.EndAsync()).Ok);
        var operators = env.Host.BatchesRepo.Operators(batch.Id).Select(o => o.Username).Distinct().ToList();
        Assert.True(operators.Contains("op1") && operators.Contains("op2"), "all operators recorded: " + string.Join(",", operators));
        Assert.True(env.Host.BatchesRepo.Operators(batch.Id).All(o => o.ToUtc is not null), "sessions closed");
    }

    [Test("BAT-10", "RPT-04")]
    public async Task Ending_a_batch_requires_signature_and_creates_three_pdfs()
    {
        await using var env = await ReadyAsync();
        env.LoginAdmin();
        var (boxId, leafId) = await env.CreateRecipesAsync();
        await env.Host.Batches.CreateAsync("Partamol", "L777", boxId, leafId);
        await env.Host.Batches.StartAsync();
        env.Box.Emit("512");
        env.Prompt.Signature = null;
        Assert.Equal(ActionStatus.Cancelled, (await env.Host.Batches.EndAsync()).Status);
        Assert.NotNull(env.Host.Batches.Current);
        env.Prompt.Signature = new Application.Actions.SignatureInput("admin", TestEnv.AdminPassword);
        Assert.True((await env.Host.Batches.EndAsync()).Ok);
        Assert.Null(env.Host.Batches.Current);
        var batch = env.Host.BatchesRepo.FindByNumber("L777")!;
        Assert.Equal(BatchStatus.Completed, batch.Status);
        Assert.True(batch.EndUtc > batch.StartUtc, "actual start → end");
        var reports = new Infrastructure.Sqlite.SqliteReportFileRepository(env.Host.Db).ForBatch(batch.Id);
        Assert.Equal("BATCH,AUDIT,ALARM", string.Join(",", reports.Select(r => r.Kind)));
        foreach (var r in reports)
        {
            Assert.True(File.Exists(r.Path) && File.Exists(r.Path + ".sha256"), "pdf + hash file");
            Assert.Equal(r.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(r.Path))).ToLowerInvariant());
        }
        Assert.Equal(1, env.Audit(AuditCodes.ReportGenerated).Count);
        Assert.Equal(0L, (long)env.Plc[102], "scanning stopped");
    }

    [Test("CAM-06")]
    public async Task Image_mode_controls_which_images_are_saved()
    {
        await using var env = await ReadyAsync();
        env.LoginAdmin();
        var (boxId, _) = await env.CreateRecipesAsync();
        await env.Host.Batches.CreateAsync("P", "L1", boxId, null);
        await env.Host.Batches.StartAsync();
        env.Box.Emit("512");
        env.Box.Emit("1");
        var images = Path.Combine(env.Dir, "data", "images");
        Assert.Equal(1, Directory.GetFiles(images, "*.png", SearchOption.AllDirectories).Length, "fail only by default");

        var s = env.Host.Settings.Current;
        Assert.True((await env.Host.Settings.SaveAsync(new OperationSettings { ImageMode = SaveImageMode.All, ImageRetentionDays = s.ImageRetentionDays, BackupRetentionDays = s.BackupRetentionDays, DiskWarnFreePercent = s.DiskWarnFreePercent })).Ok);
        env.Box.Emit("512");
        Assert.Equal(2, Directory.GetFiles(images, "*.png", SearchOption.AllDirectories).Length, "pass images when mode = All");
        Assert.Equal(1, env.Audit("SETTINGS_CHANGE").Count);
    }

    [Test("BAT-09")]
    public async Task Speed_out_of_tolerance_raises_warning_alarm()
    {
        await using var env = await ReadyAsync();
        env.LoginAdmin();
        var (boxId, leafId) = await env.CreateRecipesAsync();
        await env.Host.Batches.CreateAsync("P", "L1", boxId, leafId);
        await env.Host.Batches.StartAsync();
        env.Machine.Running = true;
        await Assert.Eventually(() => env.Plc[5] >= 48, "machine reaches speed", 8000);
        await Task.Delay(200);
        Assert.False(env.Host.Alarms.IsRaised(SoftwareAlarms.SpeedOutOfRange), "within ±5");
        env.Machine.Running = false;
        await Assert.Eventually(() => env.Host.Alarms.IsRaised(SoftwareAlarms.SpeedOutOfRange), "speed dropped → warning", 8000);
    }

    [Test("BAT-04")]
    public async Task Counter_reset_needs_pause_and_is_audited()
    {
        await using var env = await ReadyAsync();
        env.LoginAdmin();
        var (boxId, _) = await env.CreateRecipesAsync();
        await env.Host.Batches.CreateAsync("P", "L1", boxId, null);
        await env.Host.Batches.StartAsync();
        env.Box.Emit("512");
        Assert.Equal("msg.pauseBeforeReset", (await env.Host.Batches.ResetCountersAsync(CameraRole.Box)).MessageKey);
        await env.Host.Batches.PauseAsync();
        Assert.True((await env.Host.Batches.ResetCountersAsync(CameraRole.Box)).Ok);
        Assert.Equal(0L, env.Host.Batches.Snapshot()!.Box.Scanned);
        Assert.True(env.Audit("BATCH_RESET_COUNTERS").Any(a => a.Field == "Scanned" && a.OldValue == "1"));
    }

    [Test("REC-06")]
    public async Task Recipe_in_use_cannot_be_changed()
    {
        await using var env = await ReadyAsync();
        env.LoginAdmin();
        var (boxId, _) = await env.CreateRecipesAsync();
        await env.Host.Batches.CreateAsync("P", "L1", boxId, null);
        Assert.Equal("msg.recipeInUse", (await env.Host.Recipes.DeleteAsync(boxId)).MessageKey);
    }

    [Test("AUD-02", "BAT-02")]
    public async Task Audit_records_are_linked_to_the_batch()
    {
        await using var env = await ReadyAsync();
        env.LoginAdmin();
        var (boxId, _) = await env.CreateRecipesAsync();
        await env.Host.Batches.CreateAsync("P", "L1", boxId, null);
        await env.Host.Batches.StartAsync();
        await env.Host.Batches.PauseAsync();
        var id = env.Host.Batches.Current!.Id;
        var linked = env.Host.AuditStore.Query(new Application.Ports.AuditQuery(BatchId: id));
        Assert.True(linked.Any(a => a.ActionCode == "BATCH_START") && linked.Any(a => a.ActionCode == "BATCH_PAUSE"));
        Assert.True(linked.All(a => a.Workstation == "TEST-PC"));
    }
}
