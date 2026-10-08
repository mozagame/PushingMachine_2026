using System.Diagnostics;
using System.Text.RegularExpressions;
using CheckBarcode.Application.Reports;
using CheckBarcode.Application.Security;
using CheckBarcode.Domain;
using CheckBarcode.Infrastructure.Pdf;
using CheckBarcode.Tests.Framework;

namespace CheckBarcode.Tests;

public sealed class ReportTests
{
    private static readonly string[] Fonts = { "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", @"C:\Windows\Fonts\arial.ttf" };
    private static readonly string[] BoldFonts = { "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", @"C:\Windows\Fonts\arialbd.ttf" };

    [Test("RPT-04")]
    public void TrueType_font_maps_Vietnamese_characters()
    {
        var font = TrueTypeFont.LoadFirst(Fonts)!;
        foreach (var ch in "Đặng Thị Hương — Trạng thái máy ° ±")
            Assert.True(ch == ' ' || font.GlyphId(ch) != 0, $"glyph for '{ch}'");
        Assert.True(font.MeasureWidth("WWW", 10) > font.MeasureWidth("iii", 10));
    }

    [Test("RPT-04")]
    public void Pdf_is_valid_and_text_extractable()
    {
        var doc = new ReportDocument { Title = "BÁO CÁO LÔ SẢN XUẤT", Subtitle = "DH-630-4 · PDP-178", Footer = "Toàn vẹn dữ liệu: OK" };
        var section = doc.AddSection("Thông tin lô");
        section.Fields.Add(new("Sản phẩm", "Partamol Codein Eff."));
        var table = new ReportTable().Col("Thời gian", 1.5).Col("Sự kiện", 4).Col("Số", 1, ColumnAlign.Right);
        for (var i = 0; i < 120; i++) table.Row($"06/10/2026 08:{i % 60:00}:00", $"Cam Gấp Tai Dưới ON thay đổi từ {i} thành {i + 1} — dòng dài để kiểm tra việc xuống dòng tự động trong ô của bảng báo cáo", i.ToString());
        doc.AddSection("Nhật ký").Table = table;
        var bytes = new PdfReportRenderer(Fonts, BoldFonts).Render(doc);

        var head = System.Text.Encoding.ASCII.GetString(bytes, 0, 8);
        Assert.Equal("%PDF-1.7", head);
        var dir = Directory.CreateTempSubdirectory("cbpdf").FullName;
        var path = Path.Combine(dir, "r.pdf");
        File.WriteAllBytes(path, bytes);
        var text = Run("pdftotext", $"-layout \"{path}\" -");
        if (text is null) return; // poppler not installed (Windows CI): structural checks above are enough
        Assert.Contains("BÁO CÁO LÔ SẢN XUẤT", text);
        Assert.Contains("Cam Gấp Tai Dưới ON", text);
        var info = Run("pdfinfo", $"\"{path}\"")!;
        var pages = int.Parse(Regex.Match(info, @"Pages:\s+(\d+)").Groups[1].Value);
        Assert.True(pages > 3, "table continues on several pages");
        Assert.Contains($"Page {pages}/{pages}", text);
    }

    [Test("RPT-01", "RPT-02", "RPT-03", "RPT-07", "BAT-07")]
    public async Task Reports_contain_required_columns_and_values()
    {
        await using var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        await Assert.Eventually(() => env.Box.IsConnected && env.Leaflet.IsConnected, "cameras");
        await env.CreateAndLoginAsync("op1", Roles.Operator);
        env.LoginAdmin();
        var (boxId, leafId) = await env.CreateRecipesAsync();
        await env.Host.Batches.CreateAsync("Partamol", "L100", boxId, leafId);
        await env.Host.Batches.StartAsync();
        env.Box.Emit("512");
        env.Box.Emit("511");
        env.Leaflet.Emit("");
        env.Host.Auth.Login("op1", "Pass@123");
        env.Prompt.Signature = new Application.Actions.SignatureInput("op1", "Pass@123");
        Assert.True((await env.Host.Batches.EndAsync()).Ok);
        var batch = env.Host.BatchesRepo.FindByNumber("L100")!;

        var list = env.Host.Reports.BatchListReport(batch.StartUtc!.Value.AddHours(-1), batch.EndUtc!.Value.AddHours(1), null, null, "test");
        var table = list.Sections[0].Table!;
        Assert.Equal("ID,Bắt đầu,Kết thúc,Số lô,Người vận hành,Recipe,Barcode,Tổng,Đạt,Sai,No read,DS barcode lỗi", string.Join(",", table.Columns.Select(c => c.Header)));
        var boxRow = table.Rows[0];
        Assert.Equal("L100", boxRow[3]);
        Assert.Contains("admin", boxRow[4]);
        Assert.Contains("op1", boxRow[4]);
        Assert.Equal("2", boxRow[7]);
        Assert.Equal("511", boxRow[11]);
        Assert.True(Regex.IsMatch(boxRow[1], @"^\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2}$"), "24h format: " + boxRow[1]);
        Assert.Equal("N/A", table.Rows[1][11], "leaflet no read listed");

        var batchDoc = env.Host.Reports.BatchReport(batch.Id, "admin");
        Assert.True(batchDoc.Sections.Any(s => s.Fields.Any(f => f.Value.Contains("Xác nhận kết thúc lô"))), "signature printed");
        Assert.Contains("Toàn vẹn dữ liệu: OK", batchDoc.Footer);

        var (from, to) = env.Host.Reports.BatchPeriod(batch);
        var audit = env.Host.Reports.AuditReport(from, to, batch.Id, "admin");
        Assert.True(audit.Sections[0].Table!.Rows.Any(r => r[4].Contains("Bắt đầu lô L100")), "audit messages rendered");

        var log = env.Host.Reports.InspectionLogReport(CameraRole.Box, from.AddMinutes(-1), to.AddMinutes(1), "admin");
        Assert.Equal(1, log.Sections[0].Table!.Rows.Count);

        env.Host.Localizer.SetLanguage("en");
        var en = env.Host.Reports.AuditReport(from, to, batch.Id, "admin");
        Assert.True(en.Sections[0].Table!.Rows.Any(r => r[4].Contains("Start batch L100")), "English rendering");
    }

    [Test("RPT-05", "RPT-04")]
    public async Task Export_to_usb_is_audited_with_hash()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        env.LoginAdmin();
        var doc = env.Host.Reports.AuditReport(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(1), null, "admin");
        var (outcome, path) = await env.Host.Export.ExportAsync(doc, toUsb: true);
        Assert.True(outcome.Ok, outcome.MessageKey);
        Assert.True(path!.StartsWith(env.UsbDir), "written to USB: " + path);
        Assert.True(File.Exists(path) && File.Exists(path + ".sha256"));
        var rec = env.Audit("REPORT_EXPORT").Single();
        Assert.Contains("USB", rec.MessageArgs);
        Assert.Contains(File.ReadAllText(path + ".sha256").Split(' ')[0], rec.MessageArgs);
        var again = await env.Host.Export.ExportAsync(doc, toUsb: true);
        Assert.True(again.Outcome.Ok, "export can be repeated");

        Directory.Delete(env.UsbDir, true);
        Assert.Equal("msg.noUsb", (await env.Host.Export.ExportAsync(doc, toUsb: true)).Outcome.MessageKey);
    }

    [Test("AUD-05")]
    public async Task Audit_review_is_signed_and_recorded()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        await env.CreateAndLoginAsync("sup1", Roles.Supervisor);
        var r = await env.Host.Review.ReviewAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, null);
        Assert.True(r.Ok, r.MessageKey);
        var rec = env.Audit("AUDIT_REVIEW").Single();
        Assert.Equal("Đã xem xét audit trail", rec.SignatureMeaning);
        Assert.Contains("\"integrity\":\"OK\"", rec.MessageArgs);
    }

    [Test("AUD-03", "AUD-08")]
    public async Task Startup_records_integrity_config_hashes_and_clock_rollback()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        Assert.Equal(3, env.Audit("CONFIG_LOADED").Count);
        Assert.Contains("\"result\":\"OK\"", env.Audit("INTEGRITY_CHECK").Single().MessageArgs);
        env.Clock.Advance(TimeSpan.FromHours(-1));
        await env.RestartAsync();
        Assert.Equal(1, env.Audit("CLOCK_ROLLBACK").Count);
    }

    [Test("AUD-03")]
    public async Task Tampered_database_raises_alarm_at_startup()
    {
        await using var env = await TestEnv.CreateAsync(startDevices: false);
        env.Host.Db.Execute("DROP TRIGGER TR_AuditTrail_NoUpdate");
        env.Host.Db.Execute("UPDATE AuditTrail SET Username = 'x' WHERE Seq = 2");
        await env.RestartAsync();
        Assert.True(env.Host.Alarms.IsRaised(Application.Alarms.SoftwareAlarms.AuditIntegrity));
        var doc = env.Host.Reports.AuditReport(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(5), null, "x");
        Assert.Contains("CẢNH BÁO", doc.Footer);
    }

    /// <summary>Writes sample PDFs to $CB_SAMPLE_DIR for visual review (skipped when the variable is not set).</summary>
    [Test("RPT-01")]
    public async Task Sample_reports_for_review()
    {
        var target = Environment.GetEnvironmentVariable("CB_SAMPLE_DIR");
        if (string.IsNullOrEmpty(target)) return;
        await using var env = await TestEnv.CreateAsync();
        await env.WaitPlcOnlineAsync();
        await Assert.Eventually(() => env.Box.IsConnected && env.Leaflet.IsConnected, "cameras");
        await env.CreateAndLoginAsync("nguyenvana", Roles.Operator);
        env.LoginAdmin();
        var (boxId, leafId) = await env.CreateRecipesAsync();
        env.Host.Auth.Login("nguyenvana", "Pass@123");
        env.Prompt.Signature = new Application.Actions.SignatureInput("nguyenvana", "Pass@123");
        await env.Host.Batches.CreateAsync("Partamol Codein Eff.", "031225", boxId, leafId);
        await env.Host.Batches.StartAsync();
        env.Machine.SetAlarm(2, true);
        for (var i = 0; i < 40; i++) env.Box.Emit(i % 13 == 0 ? "511" : "512");
        for (var i = 0; i < 38; i++) env.Leaflet.Emit(i % 17 == 0 ? "" : "767");
        await Task.Delay(300);
        env.Machine.SetAlarm(2, false);
        await env.Host.Alarms.AcknowledgeAsync(null);
        await Task.Delay(300);
        Assert.True((await env.Host.Batches.EndAsync()).Ok);
        Directory.CreateDirectory(target);
        foreach (var f in Directory.GetFiles(Path.Combine(env.Dir, "data", "reports"), "*.pdf", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
    }

    private static string? Run(string exe, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
