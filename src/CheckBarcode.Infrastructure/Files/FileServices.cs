using System.Text;
using CheckBarcode.Application.Audit;
using CheckBarcode.Application.Ports;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure.Sqlite;

namespace CheckBarcode.Infrastructure.Files;

/// <summary>Stores inspection images as PNG: images/yyyy-MM-dd/{batch}/{camera}_{time}_{outcome}_{code}.png (CAM-06).</summary>
public sealed class FileImageStore : IImageStore
{
    private readonly string _root;

    public FileImageStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public string Save(string batchNo, CameraRole camera, DateTime utc, InspectionOutcome outcome, string readString, byte[] png)
    {
        var day = utc.ToLocalTime().ToString("yyyy-MM-dd");
        var folder = Path.Combine(_root, day, Safe(batchNo));
        Directory.CreateDirectory(folder);
        var code = string.IsNullOrEmpty(readString) ? "NA" : Safe(readString);
        if (code.Length > 40) code = code[..40];
        var name = $"{camera}_{utc.ToLocalTime():HHmmss_fff}_{outcome}_{code}.png";
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, png);
        return path;
    }

    public byte[]? Load(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    public int DeleteOlderThan(DateTime utc)
    {
        var count = 0;
        if (!Directory.Exists(_root)) return 0;
        foreach (var dir in Directory.GetDirectories(_root))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day)) continue;
            if (day.ToUniversalTime() >= utc) continue;
            count += Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
            Directory.Delete(dir, recursive: true);
        }
        return count;
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_');
        return sb.ToString();
    }
}

/// <summary>Finds USB sticks (RPT-05).</summary>
public sealed class RemovableDriveProvider : IRemovableDriveProvider
{
    public IReadOnlyList<string> RemovableRoots() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
}

/// <summary>Fixed list of folders treated as removable (tests / simulator).</summary>
public sealed class FixedDriveProvider : IRemovableDriveProvider
{
    private readonly List<string> _roots;
    public FixedDriveProvider(params string[] roots) => _roots = roots.ToList();
    public IReadOnlyList<string> RemovableRoots() => _roots.Where(Directory.Exists).ToList();
}

/// <summary>Daily online backup of the database with retention (SYS-03).</summary>
public sealed class BackupService
{
    private readonly SqliteDatabase _db;
    private readonly string _folder;
    private readonly AuditTrail _audit;
    private readonly IClock _clock;

    public BackupService(SqliteDatabase db, string folder, AuditTrail audit, IClock clock)
    {
        _db = db;
        _folder = folder;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>Creates today's backup if missing; deletes backups older than the retention.</summary>
    public string? RunDaily(int retentionDays)
    {
        Directory.CreateDirectory(_folder);
        var name = $"checkbarcode_{_clock.UtcNow.ToLocalTime():yyyyMMdd}.db";
        var path = Path.Combine(_folder, name);
        string? created = null;
        if (!File.Exists(path))
        {
            _db.BackupTo(path);
            created = path;
            _audit.Write(AuditCodes.BackupCreated, "Database", name, attribution: CheckBarcode.Application.Security.Attribution.System,
                args: new Dictionary<string, string> { ["file"] = name });
        }
        foreach (var file in Directory.GetFiles(_folder, "checkbarcode_*.db"))
        {
            if (File.GetLastWriteTimeUtc(file) < _clock.UtcNow.AddDays(-retentionDays)) File.Delete(file);
        }
        return created;
    }
}

/// <summary>Technical log (not GMP data): daily rolling text file.</summary>
public sealed class FileLogger
{
    private readonly string _folder;
    private readonly object _gate = new();

    public FileLogger(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(Path.Combine(_folder, $"app_{now:yyyyMMdd}.log"), line); }
            catch (IOException) { /* logging must never crash the app */ }
        }
    }
}
