using System.Security.Cryptography;
using CheckBarcode.Application.Actions;
using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Batches;
using CheckBarcode.Application.Ports;
using CheckBarcode.Application.Security;
using CheckBarcode.Domain;

namespace CheckBarcode.Application.Reports;

public static class ReportActions
{
    public const string Export = "REPORT_EXPORT";
    public const string Print = "REPORT_PRINT";
    public const string AuditReview = "AUDIT_REVIEW";
}

/// <summary>Saves reports as PDF with SHA-256, exports to USB, records everything (RPT-04, RPT-05).</summary>
public sealed class ExportService
{
    private readonly IPdfRenderer _pdf;
    private readonly IReportFileRepository _files;
    private readonly IRemovableDriveProvider _drives;
    private readonly ActionDispatcher _dispatcher;
    private readonly AuditTrail _audit;
    private readonly IClock _clock;
    private readonly SessionService _session;
    private readonly string _reportFolder;

    public ExportService(IPdfRenderer pdf, IReportFileRepository files, IRemovableDriveProvider drives, ActionDispatcher dispatcher,
        AuditTrail audit, IClock clock, SessionService session, string reportFolder)
    {
        _pdf = pdf;
        _files = files;
        _drives = drives;
        _dispatcher = dispatcher;
        _audit = audit;
        _clock = clock;
        _session = session;
        _reportFolder = reportFolder;
    }

    public IReadOnlyList<string> UsbDrives() => _drives.RemovableRoots();

    /// <summary>Renders and stores a report locally (used by auto-generation at batch end).</summary>
    public string SaveLocal(ReportDocument doc)
    {
        var bytes = _pdf.Render(doc);
        var folder = Path.Combine(_reportFolder, doc.Kind);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName(doc));
        var hash = Write(path, bytes);
        _files.Insert(new ReportFileRecord { Kind = doc.Kind, BatchId = doc.BatchId, Path = path, Sha256 = hash, CreatedUtc = _clock.UtcNow, CreatedBy = _session.Attribution.Username });
        return path;
    }

    /// <summary>User export: local copy + optional USB copy; audited with file name and hash.</summary>
    public async Task<(ActionOutcome Outcome, string? Path)> ExportAsync(ReportDocument doc, bool toUsb)
    {
        string? target = null;
        var outcome = await _dispatcher.ExecuteAsync(ReportActions.Export, s =>
        {
            string? usb = null;
            if (toUsb)
            {
                usb = _drives.RemovableRoots().FirstOrDefault() ?? throw new DomainException("msg.noUsb");
            }
            var local = SaveLocal(doc);
            target = local;
            if (usb is not null)
            {
                var folder = Path.Combine(usb, "CheckBarcode", string.IsNullOrEmpty(doc.FileTag) ? _clock.UtcNow.ToString("yyyyMMdd") : Sanitize(doc.FileTag));
                Directory.CreateDirectory(folder);
                target = Path.Combine(folder, Path.GetFileName(local));
                File.Copy(local, target, overwrite: true);
                File.Copy(local + ".sha256", target + ".sha256", overwrite: true);
            }
            s.Target("Report", doc.Kind);
            s.BatchId = doc.BatchId;
            s.Args["file"] = Path.GetFileName(target);
            s.Args["sha256"] = File.ReadAllText(local + ".sha256").Split(' ')[0];
            s.Args["destination"] = usb is null ? "local" : "USB";
        });
        return (outcome, outcome.Ok ? target : null);
    }

    public Task<ActionOutcome> RecordPrintAsync(ReportDocument doc) =>
        _dispatcher.ExecuteAsync(ReportActions.Print, s =>
        {
            s.Target("Report", doc.Kind);
            s.BatchId = doc.BatchId;
            s.Args["title"] = doc.Title;
        });

    private static string Write(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllText(path + ".sha256", $"{hash}  {Path.GetFileName(path)}\n");
        return hash;
    }

    private string FileName(ReportDocument doc)
    {
        var tag = string.IsNullOrEmpty(doc.FileTag) ? "" : "_" + Sanitize(doc.FileTag);
        return $"{_clock.UtcNow.ToLocalTime():yyyyMMdd_HHmmss}_{doc.Kind}{tag}.pdf";
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':' })) s = s.Replace(c, '_');
        return s.Trim();
    }
}

/// <summary>Generates batch, audit and alarm PDFs automatically when a batch ends (BAT-10, BTĐ item 8).</summary>
public sealed class ReportArchiver
{
    private readonly ReportService _reports;
    private readonly ExportService _export;
    private readonly AuditTrail _audit;
    private readonly IEventLog _events;

    public ReportArchiver(BatchService batches, ReportService reports, ExportService export, AuditTrail audit, IEventLog events)
    {
        _reports = reports;
        _export = export;
        _audit = audit;
        _events = events;
        batches.Completed += (_, batch) => Archive(batch);
    }

    public IReadOnlyList<string> Archive(Batch batch)
    {
        var paths = new List<string>();
        var by = batch.EndedBy ?? "SYSTEM";
        try
        {
            var (from, to) = _reports.BatchPeriod(batch);
            paths.Add(_export.SaveLocal(_reports.BatchReport(batch.Id, by)));
            paths.Add(_export.SaveLocal(_reports.AuditReport(from, to, batch.Id, by)));
            paths.Add(_export.SaveLocal(_reports.AlarmReport(from, to, batch.Id, by)));
            _audit.Write(AuditCodes.ReportGenerated, "Batch", batch.BatchNo, batchId: batch.Id,
                args: new Dictionary<string, string> { ["files"] = string.Join(", ", paths.Select(Path.GetFileName)), ["batchNo"] = batch.BatchNo });
        }
        catch (Exception ex)
        {
            _events.Write("Report", "AUTO_REPORT_FAILED", ex.ToString());
        }
        return paths;
    }
}

/// <summary>Periodic audit trail review with e-signature (AUD-05, Annex 11 §9).</summary>
public sealed class AuditReviewService
{
    private readonly ActionDispatcher _dispatcher;
    private readonly IAuditStore _audit;
    private readonly IAlarmStore _alarms;
    private readonly TimeFormat _time;

    public AuditReviewService(ActionDispatcher dispatcher, IAuditStore audit, IAlarmStore alarms, TimeFormat time)
    {
        _dispatcher = dispatcher;
        _audit = audit;
        _alarms = alarms;
        _time = time;
    }

    public Task<ActionOutcome> ReviewAsync(DateTime fromUtc, DateTime toUtc, long? batchId) =>
        _dispatcher.ExecuteAsync(ReportActions.AuditReview, s =>
        {
            var audit = _audit.Verify();
            var alarms = _alarms.Verify();
            s.Target("AuditTrail", batchId?.ToString() ?? "*");
            s.BatchId = batchId;
            s.Args["from"] = _time.DateTime(fromUtc);
            s.Args["to"] = _time.DateTime(toUtc);
            s.Args["records"] = _audit.Query(new AuditQuery(fromUtc, toUtc)).Count.ToString();
            s.Args["integrity"] = audit.Ok && alarms.Ok ? "OK" : "BROKEN";
        });
}
