using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Alarms;
using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Cameras;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Plc;
using CheckBarcode.Application.Ports;
using CheckBarcode.Application.Recipes;
using CheckBarcode.Application.Security;
using CheckBarcode.Application.Settings;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Batches;

public static class BatchActions
{
    public const string Create = "BATCH_CREATE";
    public const string Start = "BATCH_START";
    public const string Pause = "BATCH_PAUSE";
    public const string Resume = "BATCH_RESUME";
    public const string End = "BATCH_END";
    public const string ResetCounters = "BATCH_RESET_COUNTERS";
}

/// <summary>Result of one inspection after evaluation, for the UI.</summary>
public sealed record InspectionEvent(CameraRole Camera, InspectionOutcome Outcome, string ReadString, byte[]? Image, DateTime Utc, string? ImagePath);

/// <summary>Snapshot of the open batch for display.</summary>
public sealed record BatchSnapshot(Batch Batch, Recipe? BoxRecipe, Recipe? LeafletRecipe, BatchCounter Box, BatchCounter Leaflet, long FinishedGoods);

/// <summary>Batch lifecycle, counting, persistence and restore (BAT-01..10).</summary>
public sealed class BatchService
{
    private readonly IBatchRepository _batches;
    private readonly IRecipeRepository _recipes;
    private readonly ActionDispatcher _dispatcher;
    private readonly AuditTrail _audit;
    private readonly SessionService _session;
    private readonly CameraService _cameras;
    private readonly MachineService? _machine;
    private readonly AlarmService _alarms;
    private readonly IImageStore _images;
    private readonly SettingsService _settings;
    private readonly ICodeMatcher _matcher;
    private readonly IClock _clock;
    private readonly object _gate = new();

    private Batch? _current;
    private Recipe? _boxRecipe;
    private Recipe? _leafletRecipe;
    private BatchCounter _boxCounter = new() { Camera = CameraRole.Box };
    private BatchCounter _leafletCounter = new() { Camera = CameraRole.Leaflet };

    public BatchService(IBatchRepository batches, IRecipeRepository recipes, ActionDispatcher dispatcher, AuditTrail audit,
        SessionService session, CameraService cameras, MachineService? machine, AlarmService alarms, IImageStore images,
        SettingsService settings, ICodeMatcher matcher, IClock clock)
    {
        _batches = batches;
        _recipes = recipes;
        _dispatcher = dispatcher;
        _audit = audit;
        _session = session;
        _cameras = cameras;
        _machine = machine;
        _alarms = alarms;
        _images = images;
        _settings = settings;
        _matcher = matcher;
        _clock = clock;

        _audit.CurrentBatchId = () => _current?.Id;
        _alarms.CurrentBatchId = () => _current?.Id;
        _alarms.Define(SoftwareAlarms.SpeedOutOfRange, AlarmSeverity.Medium, LocalizedText.Of("Tốc độ vượt ngoài giới hạn cài đặt", "Line speed outside the recipe limit"));
        _alarms.Define(SoftwareAlarms.TempOutOfRange, AlarmSeverity.Medium, LocalizedText.Of("Nhiệt độ vượt ngoài giới hạn cài đặt", "Temperature outside the recipe limit"));

        _cameras.ResultArrived += (_, e) => OnResult(e.Role, e.Result);
        _cameras.ConnectionChanged += (_, e) => { if (!e.Connected) AutoPause($"camera {e.Role} offline", e.Role); };
        _session.Changed += (_, reason) => OnSessionChanged(reason);
        if (_machine is not null)
        {
            _machine.ProcessValueChanged += (_, c) => CheckTolerance(c.Tag.Id, c.New);
            _machine.ConnectionChanged += (_, online) => { if (!online) AutoPause("PLC offline", null); };
        }
    }

    /// <summary>Raised after every counted inspection.</summary>
    public event EventHandler<InspectionEvent>? Inspected;
    /// <summary>Raised when the open batch or its state changes.</summary>
    public event EventHandler? Changed;
    /// <summary>Raised after BATCH_END succeeded (report generation hooks here).</summary>
    public event EventHandler<Batch>? Completed;

    public Batch? Current { get { lock (_gate) return _current; } }
    public bool IsRunning { get { lock (_gate) return _current?.Status == BatchStatus.Running; } }

    public BatchSnapshot? Snapshot()
    {
        lock (_gate)
        {
            if (_current is null) return null;
            return new BatchSnapshot(_current, _boxRecipe, _leafletRecipe, Copy(_boxCounter), Copy(_leafletCounter), FinishedGoods());
        }
    }

    /// <summary>Finished goods = box pass − leaflet fail − leaflet no-read, never negative (BAT-05).</summary>
    public long FinishedGoods()
    {
        lock (_gate)
        {
            if (_current is null) return 0;
            if (_current.BoxRecipeId is null) return _leafletCounter.Pass;
            var leafletLoss = _current.LeafletRecipeId is null ? 0 : _leafletCounter.Fail + _leafletCounter.NoRead;
            return Math.Max(0, _boxCounter.Pass - leafletLoss);
        }
    }

    /// <summary>Loads an open batch after a restart (BAT-06). Running batches come back Paused.</summary>
    public void Restore()
    {
        var open = _batches.FindOpen();
        if (open is null) return;
        lock (_gate) Load(open);
        var oldStatus = open.Status;
        if (open.Status == BatchStatus.Running)
        {
            open.Status = BatchStatus.Paused;
            _batches.Update(open);
        }
        _batches.CloseOperator(open.Id, "*", _clock.UtcNow);
        _audit.Write(AuditCodes.BatchRestored, "Batch", open.BatchNo, "Status", oldStatus.ToString(), open.Status.ToString(),
            attribution: Attribution.System, batchId: open.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<ActionOutcome> CreateAsync(string productName, string batchNo, long? boxRecipeId, long? leafletRecipeId) =>
        Notify(_dispatcher.ExecuteAsync(BatchActions.Create, s =>
        {
            productName = (productName ?? "").Trim();
            batchNo = (batchNo ?? "").Trim();
            if (productName.Length == 0) throw new DomainException("msg.productNameRequired");
            if (batchNo.Length == 0) throw new DomainException("msg.batchNoRequired");
            if (boxRecipeId is null && leafletRecipeId is null) throw new DomainException("msg.recipeRequired");
            if (Current is not null) throw new DomainException("msg.batchAlreadyOpen", Current.BatchNo);
            if (_batches.FindByNumber(batchNo) is not null) throw new DomainException("msg.batchNoExists", batchNo);
            var box = boxRecipeId is { } b ? ActiveRecipe(b, CameraRole.Box) : null;
            var leaflet = leafletRecipeId is { } l ? ActiveRecipe(l, CameraRole.Leaflet) : null;

            var batch = new Batch
            {
                BatchNo = batchNo,
                ProductName = productName,
                BoxRecipeId = box?.Id,
                BoxRecipeVersion = box?.Version,
                LeafletRecipeId = leaflet?.Id,
                LeafletRecipeVersion = leaflet?.Version,
                Status = BatchStatus.Created,
                CreatedUtc = _clock.UtcNow,
                CreatedBy = _session.CurrentUser?.Username ?? "",
            };
            batch.Id = _batches.Insert(batch);
            lock (_gate) Load(batch);
            s.Target("Batch", batch.BatchNo);
            s.BatchId = batch.Id;
            s.Args["batchNo"] = batch.BatchNo;
            s.Change("ProductName", null, batch.ProductName);
            s.Change("BoxRecipe", null, box is null ? "" : $"{box.Name} v{box.Version}");
            s.Change("LeafletRecipe", null, leaflet is null ? "" : $"{leaflet.Name} v{leaflet.Version}");
        }));

    public Task<ActionOutcome> StartAsync() =>
        Notify(_dispatcher.ExecuteAsync(BatchActions.Start, async s =>
        {
            var batch = RequireOpen();
            if (batch.Status != BatchStatus.Created) throw new DomainException("msg.batchNotCreated");
            await PrepareDevicesAsync(batch);
            batch.Status = BatchStatus.Running;
            batch.StartUtc = _clock.UtcNow;
            _batches.Update(batch);
            OpenOperatorSession(batch);
            Describe(s, batch, BatchStatus.Created);
        }));

    public Task<ActionOutcome> PauseAsync() =>
        Notify(_dispatcher.ExecuteAsync(BatchActions.Pause, async s =>
        {
            var batch = RequireOpen();
            if (batch.Status != BatchStatus.Running) throw new DomainException("msg.batchNotRunning");
            batch.Status = BatchStatus.Paused;
            _batches.Update(batch);
            await SetScanningAsync(batch, false);
            Describe(s, batch, BatchStatus.Running);
        }));

    public Task<ActionOutcome> ResumeAsync() =>
        Notify(_dispatcher.ExecuteAsync(BatchActions.Resume, async s =>
        {
            var batch = RequireOpen();
            if (batch.Status != BatchStatus.Paused) throw new DomainException("msg.batchNotPaused");
            await PrepareDevicesAsync(batch);
            batch.Status = BatchStatus.Running;
            _batches.Update(batch);
            OpenOperatorSession(batch);
            Describe(s, batch, BatchStatus.Paused);
        }));

    /// <summary>Ends the batch (e-signature in actions.json); reports are generated by the Completed handler.</summary>
    public async Task<ActionOutcome> EndAsync()
    {
        Batch? ended = null;
        var outcome = await Notify(_dispatcher.ExecuteAsync(BatchActions.End, async s =>
        {
            var batch = RequireOpen();
            var old = batch.Status;
            if (old == BatchStatus.Running || old == BatchStatus.Paused) await SetScanningAsync(batch, false);
            foreach (var role in new[] { CameraRole.Box, CameraRole.Leaflet })
            {
                if (!batch.UsesCamera(role) || _cameras.Reader(role) is not { IsConnected: true } reader) continue;
                try { await reader.ClearMatchStringAsync(); } catch { /* camera keeps last setting, not critical */ }
            }
            batch.Status = BatchStatus.Completed;
            batch.StartUtc ??= _clock.UtcNow;
            batch.EndUtc = _clock.UtcNow;
            batch.EndedBy = _session.CurrentUser?.Username;
            _batches.Update(batch);
            _batches.CloseOperator(batch.Id, "*", batch.EndUtc.Value);
            Describe(s, batch, old);
            s.Args["finished"] = FinishedGoods().ToString();
            ended = batch;
        }));
        if (outcome.Ok && ended is not null)
        {
            Completed?.Invoke(this, ended);
            lock (_gate) Unload();
            _alarms.Clear(SoftwareAlarms.SpeedOutOfRange);
            _alarms.Clear(SoftwareAlarms.TempOutOfRange);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return outcome;
    }

    public Task<ActionOutcome> ResetCountersAsync(CameraRole camera) =>
        Notify(_dispatcher.ExecuteAsync(BatchActions.ResetCounters, s =>
        {
            var batch = RequireOpen();
            if (batch.Status == BatchStatus.Running) throw new DomainException("msg.pauseBeforeReset");
            BatchCounter counter;
            lock (_gate) counter = camera == CameraRole.Box ? _boxCounter : _leafletCounter;
            s.Target("BatchCounter", $"{batch.BatchNo}/{camera}");
            s.BatchId = batch.Id;
            s.Change("Scanned", counter.Scanned, 0);
            s.Change("Pass", counter.Pass, 0);
            s.Change("Fail", counter.Fail, 0);
            s.Change("NoRead", counter.NoRead, 0);
            lock (_gate) counter.Reset();
            _batches.SaveCounter(counter);
        }));

    public IReadOnlyList<InspectionResult> FailResults(CameraRole? camera = null)
    {
        var batch = Current;
        if (batch is null) return Array.Empty<InspectionResult>();
        return _batches.Results(batch.Id, camera, null).Where(r => r.Outcome != InspectionOutcome.Pass).ToList();
    }

    /// <summary>Counts one camera result (BAT-02, BAT-04, BAT-06).</summary>
    public void OnResult(CameraRole camera, CodeReadResult result)
    {
        Batch batch;
        Recipe recipe;
        BatchCounter counter;
        lock (_gate)
        {
            if (_current is not { Status: BatchStatus.Running } b) return;
            var r = camera == CameraRole.Box ? _boxRecipe : _leafletRecipe;
            if (r is null) return;
            batch = b;
            recipe = r;
            counter = camera == CameraRole.Box ? _boxCounter : _leafletCounter;
        }

        var outcome = _matcher.Evaluate(recipe, result.ReadString);
        lock (_gate) counter.Add(outcome);
        _batches.SaveCounter(counter);

        string? path = null;
        var mode = _settings.Current.ImageMode;
        var saveImage = result.PngImage is { Length: > 0 } && (mode == SaveImageMode.All || (mode == SaveImageMode.FailOnly && outcome != InspectionOutcome.Pass));
        if (saveImage) path = _images.Save(batch.BatchNo, camera, result.Utc, outcome, result.ReadString, result.PngImage!);
        if (outcome != InspectionOutcome.Pass || path is not null)
        {
            _batches.AddResult(new InspectionResult
            {
                BatchId = batch.Id,
                Camera = camera,
                Utc = result.Utc,
                ReadString = result.ReadString ?? "",
                Outcome = outcome,
                ImagePath = path,
            });
        }
        Inspected?.Invoke(this, new InspectionEvent(camera, outcome, result.ReadString ?? "", result.PngImage, result.Utc, path));
    }

    private void CheckTolerance(string tagId, double value)
    {
        Recipe? box;
        bool running;
        lock (_gate) { box = _boxRecipe; running = _current?.Status == BatchStatus.Running; }
        if (tagId == Tags.SpeedActual)
            _alarms.Set(SoftwareAlarms.SpeedOutOfRange, running && box is { SpeedSetpoint: { } sp } && Math.Abs(value - sp) > (box.SpeedTolerance ?? 0));
        else if (tagId == Tags.TempActual)
            _alarms.Set(SoftwareAlarms.TempOutOfRange, running && box is { TempSetpoint: { } tp } && Math.Abs(value - tp) > (box.TempTolerance ?? 0));
    }

    private void AutoPause(string cause, CameraRole? camera)
    {
        Batch? batch;
        lock (_gate) batch = _current;
        if (batch is not { Status: BatchStatus.Running }) return;
        if (camera is { } role && !batch.UsesCamera(role)) return;
        batch.Status = BatchStatus.Paused;
        _batches.Update(batch);
        _audit.Write(AuditCodes.BatchAutoPaused, "Batch", batch.BatchNo, "Status", BatchStatus.Running.ToString(), BatchStatus.Paused.ToString(),
            args: new Dictionary<string, string> { ["cause"] = cause }, batchId: batch.Id);
        _ = SetScanningAsync(batch, false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionChanged(LogoutReason? reason)
    {
        var batch = Current;
        if (batch is null || batch.Status == BatchStatus.Completed) return;
        if (reason is null)
        {
            if (batch.Status is BatchStatus.Running or BatchStatus.Paused) OpenOperatorSession(batch);
        }
        else if (_session.LastUser is { } last)
        {
            _batches.CloseOperator(batch.Id, last.Username, _clock.UtcNow);
        }
    }

    private void OpenOperatorSession(Batch batch)
    {
        var user = _session.CurrentUser;
        if (user is null) return;
        var open = _batches.Operators(batch.Id).Any(o => o.ToUtc is null && o.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase));
        if (!open) _batches.AddOperator(new BatchOperator { BatchId = batch.Id, Username = user.Username, FromUtc = _clock.UtcNow });
    }

    private async Task PrepareDevicesAsync(Batch batch)
    {
        foreach (var role in new[] { CameraRole.Box, CameraRole.Leaflet })
        {
            if (!batch.UsesCamera(role)) continue;
            var reader = _cameras.Reader(role);
            if (reader is not { IsConnected: true }) throw new DomainException("msg.cameraNotConnected", role);
            var recipe = role == CameraRole.Box ? _boxRecipe! : _leafletRecipe!;
            await reader.ApplyMatchStringAsync(recipe.Barcode, recipe.BarcodeFormat);
        }
        if (_machine is not null)
        {
            if (!_machine.IsOnline) throw new DomainException("msg.plcNotConnected");
            if (_boxRecipe is { } box)
            {
                if (box.SpeedSetpoint is { } sp) await _machine.WriteSystemAsync(Tags.SpeedSetpoint, sp);
                if (box.TempSetpoint is { } tp) await _machine.WriteSystemAsync(Tags.TempSetpoint, tp);
            }
            await SetScanningAsync(batch, true);
        }
    }

    private async Task SetScanningAsync(Batch batch, bool on)
    {
        if (_machine is null || !_machine.IsOnline) return;
        try
        {
            if (batch.UsesCamera(CameraRole.Box)) await _machine.WriteSystemAsync(Tags.BoxCamScanning, on ? 1 : 0);
            if (batch.UsesCamera(CameraRole.Leaflet)) await _machine.WriteSystemAsync(Tags.LeafletCamScanning, on ? 1 : 0);
        }
        catch (DomainException) when (!on)
        {
            // PLC offline while stopping: the PLC side stops scanning on heartbeat loss.
        }
    }

    private void Describe(ActionScope s, Batch batch, BatchStatus old)
    {
        s.Target("Batch", batch.BatchNo);
        s.BatchId = batch.Id;
        s.Args["batchNo"] = batch.BatchNo;
        s.Change("Status", old, batch.Status);
    }

    private Batch RequireOpen() => Current ?? throw new DomainException("msg.noOpenBatch");

    private Recipe ActiveRecipe(long id, CameraRole role)
    {
        var r = _recipes.Find(id);
        if (r is null || r.Status != RecordStatus.Active || r.Type != role) throw new DomainException("msg.recipeNotFound");
        return r;
    }

    private void Load(Batch batch)
    {
        _current = batch;
        _boxRecipe = batch.BoxRecipeId is { } b ? _recipes.Find(b) : null;
        _leafletRecipe = batch.LeafletRecipeId is { } l ? _recipes.Find(l) : null;
        _boxCounter = _batches.GetCounter(batch.Id, CameraRole.Box);
        _leafletCounter = _batches.GetCounter(batch.Id, CameraRole.Leaflet);
    }

    private void Unload()
    {
        _current = null;
        _boxRecipe = _leafletRecipe = null;
        _boxCounter = new BatchCounter { Camera = CameraRole.Box };
        _leafletCounter = new BatchCounter { Camera = CameraRole.Leaflet };
    }

    private static BatchCounter Copy(BatchCounter c) => new() { BatchId = c.BatchId, Camera = c.Camera, Scanned = c.Scanned, Pass = c.Pass, Fail = c.Fail, NoRead = c.NoRead };

    private async Task<ActionOutcome> Notify(Task<ActionOutcome> action)
    {
        var outcome = await action;
        if (outcome.Ok) Changed?.Invoke(this, EventArgs.Empty);
        return outcome;
    }
}
