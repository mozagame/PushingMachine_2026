using System.Globalization;
using CheckBarcode.Application.Alarms;
using CheckBarcode.Application.Configuration;
using CheckBarcode.Application.Localization;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Reports;

/// <summary>Builds report models from the database (RPT-01..03). Rendering is done elsewhere.</summary>
public sealed class ReportService
{
    private readonly IBatchRepository _batches;
    private readonly IRecipeRepository _recipes;
    private readonly IAuditStore _audit;
    private readonly IAlarmStore _alarms;
    private readonly AlarmService _alarmService;
    private readonly AuditFormatter _formatter;
    private readonly ILocalizer _loc;
    private readonly TimeFormat _time;
    private readonly AppConfig _config;
    private readonly IClock _clock;

    public ReportService(IBatchRepository batches, IRecipeRepository recipes, IAuditStore audit, IAlarmStore alarms,
        AlarmService alarmService, AuditFormatter formatter, ILocalizer loc, TimeFormat time, AppConfig config, IClock clock)
    {
        _batches = batches;
        _recipes = recipes;
        _audit = audit;
        _alarms = alarms;
        _alarmService = alarmService;
        _formatter = formatter;
        _loc = loc;
        _time = time;
        _config = config;
        _clock = clock;
        _formatter.AlarmText = id => _alarmService.Definition(id)?.Text.Get(_loc.Language);
    }

    private string T(string key, params object[] args) => _loc.T(key, args);

    /// <summary>Period of a batch (start → end, or now when still open) used as default report range (RPT-02).</summary>
    public (DateTime FromUtc, DateTime ToUtc) BatchPeriod(Batch batch) =>
        (batch.StartUtc ?? batch.CreatedUtc, batch.EndUtc ?? _clock.UtcNow);

    /// <summary>Detailed report of one batch (BAT-10, RPT-01).</summary>
    public ReportDocument BatchReport(long batchId, string generatedBy)
    {
        var batch = _batches.Find(batchId) ?? throw new DomainException("msg.batchNotFound", batchId);
        var box = batch.BoxRecipeId is { } b ? _recipes.Find(b) : null;
        var leaflet = batch.LeafletRecipeId is { } l ? _recipes.Find(l) : null;
        var doc = NewDocument(T("report.batch.title"), "BATCH", generatedBy);
        doc.BatchId = batch.Id;
        doc.FileTag = batch.BatchNo;
        doc.Landscape = false;

        var info = doc.AddSection(T("report.batch.info"));
        info.Fields.Add(new(T("col.product"), batch.ProductName));
        info.Fields.Add(new(T("col.batchNo"), batch.BatchNo));
        info.Fields.Add(new(T("col.start"), _time.DateTime(batch.StartUtc)));
        info.Fields.Add(new(T("col.end"), _time.DateTime(batch.EndUtc)));
        info.Fields.Add(new(T("col.status"), T("batchStatus." + batch.Status)));
        info.Fields.Add(new(T("col.createdBy"), batch.CreatedBy));
        info.Fields.Add(new(T("col.endedBy"), batch.EndedBy ?? ""));
        if (box is not null)
        {
            info.Fields.Add(new(T("col.boxRecipe"), $"{box.Name} (v{batch.BoxRecipeVersion}) — {box.Barcode} [{box.BarcodeFormat}]"));
            info.Fields.Add(new(T("col.setpoints"), $"{T("col.speed")}: {Num(box.SpeedSetpoint)} ± {Num(box.SpeedTolerance)} · {T("col.temp")}: {Num(box.TempSetpoint)} ± {Num(box.TempTolerance)}"));
        }
        if (leaflet is not null)
            info.Fields.Add(new(T("col.leafletRecipe"), $"{leaflet.Name} (v{batch.LeafletRecipeVersion}) — {leaflet.Barcode} [{leaflet.BarcodeFormat}]"));

        var counters = doc.AddSection(T("report.batch.counters"));
        var table = new ReportTable()
            .Col(T("col.camera"), 1.4).Col(T("col.total"), 1, ColumnAlign.Right).Col(T("col.pass"), 1, ColumnAlign.Right)
            .Col(T("col.fail"), 1, ColumnAlign.Right).Col(T("col.noRead"), 1, ColumnAlign.Right);
        BatchCounter? boxC = null, leafC = null;
        foreach (var role in new[] { CameraRole.Box, CameraRole.Leaflet })
        {
            if (!batch.UsesCamera(role)) continue;
            var c = _batches.GetCounter(batch.Id, role);
            if (role == CameraRole.Box) boxC = c; else leafC = c;
            table.Row(T("camera." + role), c.Scanned.ToString(), c.Pass.ToString(), c.Fail.ToString(), c.NoRead.ToString());
        }
        counters.Table = table;
        var finished = boxC is null ? leafC?.Pass ?? 0 : Math.Max(0, boxC.Pass - (leafC is null ? 0 : leafC.Fail + leafC.NoRead));
        counters.Fields.Add(new(T("col.finished"), finished.ToString()));

        var ops = doc.AddSection(T("report.batch.operators"));
        var opTable = new ReportTable().Col(T("col.user"), 1.5).Col(T("col.from"), 1.5).Col(T("col.to"), 1.5);
        foreach (var op in _batches.Operators(batch.Id)) opTable.Row(op.Username, _time.DateTime(op.FromUtc), _time.DateTime(op.ToUtc));
        ops.Table = opTable;

        var fails = doc.AddSection(T("report.batch.fails"));
        var failTable = new ReportTable().Col(T("col.time"), 1.4).Col(T("col.camera"), 0.8).Col(T("col.result"), 0.8).Col(T("col.readString"), 2);
        var results = _batches.Results(batch.Id, null, null).Where(r => r.Outcome != InspectionOutcome.Pass).ToList();
        foreach (var r in results) failTable.Row(_time.DateTime(r.Utc), T("camera." + r.Camera), T("outcome." + r.Outcome), r.ReadString.Length == 0 ? "N/A" : r.ReadString);
        if (results.Count == 0) fails.Text = T("report.none");
        else fails.Table = failTable;

        var (from, to) = BatchPeriod(batch);
        var alarms = doc.AddSection(T("report.batch.alarms"));
        var alarmTable = AlarmTable(_alarms.Query(from, to, null));
        if (alarmTable.Rows.Count == 0) alarms.Text = T("report.none");
        else alarms.Table = alarmTable;

        var sign = doc.AddSection(T("report.batch.signatures"));
        foreach (var rec in _audit.Query(new AuditQuery(BatchId: batch.Id)).Where(r => r.SignatureMeaning is not null))
            sign.Fields.Add(new(_time.DateTime(rec.Utc), $"{rec.Username} — {rec.SignatureMeaning}"));
        if (sign.Fields.Count == 0) sign.Text = T("report.none");

        AppendIntegrity(doc);
        return doc;
    }

    /// <summary>One row per batch and camera (Batch report list, old "Hộp/Toa/BatchReport").</summary>
    public ReportDocument BatchListReport(DateTime fromUtc, DateTime toUtc, CameraRole? camera, string? batchNo, string generatedBy)
    {
        var doc = NewDocument(T("report.batchList.title"), "BATCHLIST", generatedBy);
        doc.Header.Add(new(T("col.period"), $"{_time.DateTime(fromUtc)} → {_time.DateTime(toUtc)}"));
        if (camera is { } cam) doc.Header.Add(new(T("col.camera"), T("camera." + cam)));
        var table = new ReportTable()
            .Col("ID", 0.5, ColumnAlign.Right).Col(T("col.start"), 1.3).Col(T("col.end"), 1.3).Col(T("col.batchNo"), 1)
            .Col(T("col.operators"), 1.6).Col(T("col.recipe"), 1.3).Col(T("col.barcode"), 0.9)
            .Col(T("col.total"), 0.7, ColumnAlign.Right).Col(T("col.pass"), 0.7, ColumnAlign.Right).Col(T("col.fail"), 0.7, ColumnAlign.Right)
            .Col(T("col.noRead"), 0.7, ColumnAlign.Right).Col(T("col.failList"), 1.6);
        foreach (var batch in _batches.List(fromUtc, toUtc).Where(b => batchNo is null || b.BatchNo.Equals(batchNo, StringComparison.OrdinalIgnoreCase)))
        {
            var operators = string.Join(", ", _batches.Operators(batch.Id).Select(o => o.Username).Distinct(StringComparer.OrdinalIgnoreCase));
            foreach (var role in new[] { CameraRole.Box, CameraRole.Leaflet })
            {
                if (!batch.UsesCamera(role) || camera is { } c && c != role) continue;
                var recipe = _recipes.Find(role == CameraRole.Box ? batch.BoxRecipeId!.Value : batch.LeafletRecipeId!.Value);
                var counter = _batches.GetCounter(batch.Id, role);
                var failList = string.Join(", ", _batches.Results(batch.Id, role, null).Where(r => r.Outcome != InspectionOutcome.Pass)
                    .Select(r => r.ReadString.Length == 0 ? "N/A" : r.ReadString).Distinct());
                table.Row(batch.Id.ToString(), _time.DateTime(batch.StartUtc), _time.DateTime(batch.EndUtc), batch.BatchNo, operators,
                    recipe?.Name ?? "", recipe?.Barcode ?? "", counter.Scanned.ToString(), counter.Pass.ToString(), counter.Fail.ToString(),
                    counter.NoRead.ToString(), failList);
            }
        }
        doc.AddSection().Table = table;
        AppendIntegrity(doc);
        return doc;
    }

    public ReportDocument AuditReport(DateTime fromUtc, DateTime toUtc, long? batchId, string generatedBy)
    {
        var doc = NewDocument(T("report.audit.title"), "AUDIT", generatedBy);
        doc.BatchId = batchId;
        doc.Header.Add(new(T("col.period"), $"{_time.DateTime(fromUtc)} → {_time.DateTime(toUtc)}"));
        if (batchId is { } id && _batches.Find(id) is { } b)
        {
            doc.Header.Add(new(T("col.batchNo"), b.BatchNo));
            doc.FileTag = b.BatchNo;
        }
        var table = new ReportTable()
            .Col("#", 0.5, ColumnAlign.Right).Col(T("col.time"), 1.2).Col(T("col.user"), 0.9).Col(T("col.role"), 0.8)
            .Col(T("col.event"), 3.2).Col(T("col.reason"), 1.2).Col(T("col.signature"), 1.2);
        var records = _audit.Query(new AuditQuery(fromUtc, toUtc)).ToList();
        if (batchId is { } bid)
        {
            // Records linked to the batch (creation before start, end just after the end time) are always included.
            var seen = records.Select(r => r.Seq).ToHashSet();
            records.AddRange(_audit.Query(new AuditQuery(BatchId: bid)).Where(r => !seen.Contains(r.Seq)));
            records.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        }
        foreach (var r in records)
        {
            var user = r.Username + (r.SessionState == SessionState.Expired ? "*" : "");
            table.Row(r.Seq.ToString(), _time.DateTime(r.Utc), user, r.Role, _formatter.Message(r), r.Reason, r.SignatureMeaning ?? "");
        }
        doc.AddSection().Table = table;
        doc.AddSection().Text = T("report.audit.legend");
        AppendIntegrity(doc);
        return doc;
    }

    public ReportDocument AlarmReport(DateTime fromUtc, DateTime toUtc, long? batchId, string generatedBy)
    {
        var doc = NewDocument(T("report.alarm.title"), "ALARM", generatedBy);
        doc.BatchId = batchId;
        doc.Header.Add(new(T("col.period"), $"{_time.DateTime(fromUtc)} → {_time.DateTime(toUtc)}"));
        if (batchId is { } id && _batches.Find(id) is { } b)
        {
            doc.Header.Add(new(T("col.batchNo"), b.BatchNo));
            doc.FileTag = b.BatchNo;
        }
        doc.AddSection().Table = AlarmTable(_alarms.Query(fromUtc, toUtc, null));
        AppendIntegrity(doc);
        return doc;
    }

    /// <summary>Inspection log of one camera (Log report Toa / Hộp, RPT-03).</summary>
    public ReportDocument InspectionLogReport(CameraRole camera, DateTime fromUtc, DateTime toUtc, string generatedBy)
    {
        var doc = NewDocument(T("report.log.title", T("camera." + camera)), "LOG", generatedBy);
        doc.Header.Add(new(T("col.period"), $"{_time.DateTime(fromUtc)} → {_time.DateTime(toUtc)}"));
        doc.FileTag = camera.ToString();
        var table = new ReportTable()
            .Col("ID", 0.5, ColumnAlign.Right).Col(T("col.time"), 1.3).Col(T("col.batchNo"), 1).Col(T("col.recipe"), 1.4)
            .Col(T("col.barcode"), 1).Col(T("col.result"), 0.8).Col(T("col.readString"), 1.6);
        var batches = new Dictionary<long, Batch?>();
        foreach (var r in _batches.SearchResults(fromUtc, toUtc, null, null, 50_000).Where(x => x.Camera == camera))
        {
            if (!batches.TryGetValue(r.BatchId, out var batch)) batches[r.BatchId] = batch = _batches.Find(r.BatchId);
            var recipeId = camera == CameraRole.Box ? batch?.BoxRecipeId : batch?.LeafletRecipeId;
            var recipe = recipeId is { } rid ? _recipes.Find(rid) : null;
            table.Row(r.Id.ToString(), _time.DateTime(r.Utc), batch?.BatchNo ?? "", recipe?.Name ?? "", recipe?.Barcode ?? "",
                T("outcome." + r.Outcome), r.ReadString.Length == 0 ? "N/A" : r.ReadString);
        }
        doc.AddSection().Table = table;
        AppendIntegrity(doc);
        return doc;
    }

    private ReportTable AlarmTable(IEnumerable<AlarmRecord> records)
    {
        var table = new ReportTable().Col(T("col.time"), 1.3).Col(T("col.alarm"), 2.6).Col(T("col.severity"), 0.8).Col(T("col.state"), 1).Col(T("col.user"), 1);
        foreach (var r in records)
        {
            var text = _alarmService.Definition(r.AlarmId)?.Text.Get(_loc.Language) ?? r.AlarmId;
            table.Row(_time.DateTime(r.Utc), text, T("severity." + r.Severity), T("alarmState." + r.State), r.Username);
        }
        return table;
    }

    private ReportDocument NewDocument(string title, string kind, string generatedBy)
    {
        var doc = new ReportDocument { Title = title, Kind = kind };
        doc.Subtitle = $"{_config.MachineName} · {_config.MachineCode}" + (string.IsNullOrEmpty(_config.Customer) ? "" : $" · {_config.Customer}");
        doc.PageFormat = T("report.page");
        doc.Footer = T("report.generated", generatedBy, _time.DateTime(_clock.UtcNow));
        return doc;
    }

    private void AppendIntegrity(ReportDocument doc)
    {
        var audit = _audit.Verify();
        var alarm = _alarms.Verify();
        var ok = audit.Ok && alarm.Ok;
        doc.Footer += " · " + (ok
            ? T("report.integrityOk", audit.Checked + alarm.Checked)
            : T("report.integrityBroken", audit.FirstBrokenSeq ?? alarm.FirstBrokenSeq ?? 0));
    }

    private static string Num(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—";
}
