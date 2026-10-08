using System.Text;
using CheckBarcode.Application.Ports;
using CheckBarcode.Application.Reports;

namespace CheckBarcode.Infrastructure.Pdf;

/// <summary>Lays out a <see cref="ReportDocument"/> on A4 pages (tables with repeated headers, wrapping, page x/y).</summary>
public sealed class PdfReportRenderer : IPdfRenderer
{
    private const double Margin = 36;
    private const double FooterHeight = 24;
    private const double CellPad = 3;

    private readonly IReadOnlyList<string> _regularFonts;
    private readonly IReadOnlyList<string> _boldFonts;
    private TrueTypeFont? _regularCache;
    private TrueTypeFont? _boldCache;

    public PdfReportRenderer(IEnumerable<string> regularFonts, IEnumerable<string> boldFonts)
    {
        _regularFonts = regularFonts.ToList();
        _boldFonts = boldFonts.ToList();
    }

    public byte[] Render(ReportDocument doc)
    {
        _regularCache ??= TrueTypeFont.LoadFirst(_regularFonts) ?? throw new FileNotFoundException("No PDF font found: " + string.Join(", ", _regularFonts));
        _boldCache ??= TrueTypeFont.LoadFirst(_boldFonts) ?? _regularCache;

        var pdf = new PdfDocumentWriter { Title = doc.Title, Author = "CheckBarcode v2" };
        var regular = pdf.AddFont(_regularCache);
        var bold = ReferenceEquals(_boldCache, _regularCache) ? regular : pdf.AddFont(_boldCache);
        var layout = new Layout(pdf, regular, bold, doc);
        layout.Run();
        return pdf.Save();
    }

    private sealed class Layout
    {
        private readonly PdfDocumentWriter _pdf;
        private readonly PdfFont _regular;
        private readonly PdfFont _bold;
        private readonly ReportDocument _doc;
        private readonly double _pageW;
        private readonly double _pageH;
        private PdfPage _page = null!;
        private double _y;

        public Layout(PdfDocumentWriter pdf, PdfFont regular, PdfFont bold, ReportDocument doc)
        {
            _pdf = pdf;
            _regular = regular;
            _bold = bold;
            _doc = doc;
            (_pageW, _pageH) = doc.Landscape ? (841.89, 595.28) : (595.28, 841.89);
        }

        private double ContentWidth => _pageW - 2 * Margin;
        private double Bottom => Margin + FooterHeight;

        public void Run()
        {
            NewPage();
            _page.Text(_bold, 16, Margin, _y - 16, Normalize(_doc.Title));
            _y -= 22;
            if (_doc.Subtitle.Length > 0)
            {
                _page.Text(_regular, 9, Margin, _y - 10, Normalize(_doc.Subtitle), 0.3);
                _y -= 14;
            }
            _page.Line(Margin, _y - 2, _pageW - Margin, _y - 2, 1);
            _y -= 10;
            Fields(_doc.Header);

            foreach (var section in _doc.Sections)
            {
                if (section.Heading is { } h)
                {
                    EnsureSpace(40);
                    _y -= 6;
                    _page.Text(_bold, 11, Margin, _y - 11, Normalize(h));
                    _y -= 16;
                }
                Fields(section.Fields);
                if (section.Text is { } t) Paragraph(t, 9);
                if (section.Table is { } table) Table(table);
            }
            Footers();
        }

        private void NewPage()
        {
            _page = _pdf.AddPage(_pageW, _pageH);
            _y = _pageH - Margin;
        }

        private void EnsureSpace(double needed)
        {
            if (_y - needed < Bottom) NewPage();
        }

        private void Fields(List<KeyValuePair<string, string>> fields)
        {
            if (fields.Count == 0) return;
            const double size = 9;
            var keyWidth = Math.Min(ContentWidth * 0.35, fields.Max(f => _bold.Font.MeasureWidth(Normalize(f.Key), size)) + 12);
            foreach (var (key, value) in fields)
            {
                var lines = Wrap(Normalize(value), _regular, size, ContentWidth - keyWidth);
                EnsureSpace(lines.Count * 12 + 2);
                _page.Text(_bold, size, Margin, _y - size, Normalize(key) + ":");
                foreach (var line in lines)
                {
                    _page.Text(_regular, size, Margin + keyWidth, _y - size, line);
                    _y -= 12;
                }
            }
            _y -= 4;
        }

        private void Paragraph(string text, double size)
        {
            foreach (var line in Wrap(Normalize(text), _regular, size, ContentWidth))
            {
                EnsureSpace(size + 4);
                _page.Text(_regular, size, Margin, _y - size, line, 0.2);
                _y -= size + 3;
            }
            _y -= 4;
        }

        private void Table(ReportTable table)
        {
            if (table.Columns.Count == 0) return;
            const double size = 8;
            const double lineH = 10;
            var total = table.Columns.Sum(c => c.Weight);
            var widths = table.Columns.Select(c => ContentWidth * c.Weight / total).ToArray();

            double RowHeight(IReadOnlyList<List<string>> cells) => cells.Max(c => Math.Max(1, c.Count)) * lineH + 2 * CellPad;

            List<List<string>> WrapRow(IReadOnlyList<string> row, PdfFont font) =>
                widths.Select((w, i) => Wrap(Normalize(i < row.Count ? row[i] ?? "" : ""), font, size, w - 2 * CellPad)).ToList();

            var header = WrapRow(table.Columns.Select(c => c.Header).ToList(), _bold);
            var headerH = RowHeight(header);

            void DrawRow(List<List<string>> cells, double h, bool isHeader)
            {
                if (isHeader) _page.FillRect(Margin, _y - h, ContentWidth, h, 0.88);
                var x = Margin;
                for (var i = 0; i < widths.Length; i++)
                {
                    var col = table.Columns[i];
                    var ty = _y - CellPad - size;
                    foreach (var line in cells[i])
                    {
                        var font = isHeader ? _bold : _regular;
                        var tw = font.Font.MeasureWidth(line, size);
                        var tx = (isHeader ? ColumnAlign.Left : col.Align) switch
                        {
                            ColumnAlign.Right => x + widths[i] - CellPad - tw,
                            ColumnAlign.Center => x + (widths[i] - tw) / 2,
                            _ => x + CellPad,
                        };
                        _page.Text(font, size, tx, ty, line);
                        ty -= lineH;
                    }
                    x += widths[i];
                }
                _page.Line(Margin, _y - h, Margin + ContentWidth, _y - h, 0.3, 0.6);
                _y -= h;
            }

            EnsureSpace(headerH + 20);
            var top = _y;
            DrawRow(header, headerH, true);
            foreach (var row in table.Rows)
            {
                var cells = WrapRow(row, _regular);
                var h = RowHeight(cells);
                if (_y - h < Bottom)
                {
                    Frame(top, widths);
                    NewPage();
                    top = _y;
                    DrawRow(header, headerH, true);
                }
                DrawRow(cells, h, false);
            }
            Frame(top, widths);
            _y -= 8;
        }

        private void Frame(double top, double[] widths)
        {
            _page.StrokeRect(Margin, _y, ContentWidth, top - _y, 0.5, 0.4);
            var x = Margin;
            for (var i = 0; i < widths.Length - 1; i++)
            {
                x += widths[i];
                _page.Line(x, top, x, _y, 0.3, 0.6);
            }
        }

        private void Footers()
        {
            var count = _pdf.Pages.Count;
            for (var i = 0; i < count; i++)
            {
                var page = _pdf.Pages[i];
                page.Line(Margin, Margin + 14, _pageW - Margin, Margin + 14, 0.5, 0.5);
                var pageText = string.Format(_doc.PageFormat, i + 1, count);
                var pw = _regular.Font.MeasureWidth(pageText, 8);
                var footer = Wrap(Normalize(_doc.Footer), _regular, 7.5, ContentWidth - pw - 20).FirstOrDefault() ?? "";
                page.Text(_regular, 7.5, Margin, Margin + 4, footer, 0.35);
                page.Text(_regular, 8, _pageW - Margin - pw, Margin + 4, pageText, 0.35);
            }
        }

        private static string Normalize(string s) => (s ?? "").Normalize(NormalizationForm.FormC).Replace("\r", "").Replace("\t", " ");

        /// <summary>Greedy word wrap; words longer than the width are split.</summary>
        private static List<string> Wrap(string text, PdfFont font, double size, double width)
        {
            var result = new List<string>();
            foreach (var paragraph in text.Split('\n'))
            {
                var line = new StringBuilder();
                foreach (var word in paragraph.Split(' '))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (font.Font.MeasureWidth(candidate, size) <= width)
                    {
                        line.Clear().Append(candidate);
                        continue;
                    }
                    if (line.Length > 0) { result.Add(line.ToString()); line.Clear(); }
                    var w = word;
                    while (font.Font.MeasureWidth(w, size) > width && w.Length > 1)
                    {
                        var n = w.Length - 1;
                        while (n > 1 && font.Font.MeasureWidth(w[..n], size) > width) n--;
                        result.Add(w[..n]);
                        w = w[n..];
                    }
                    line.Append(w);
                }
                result.Add(line.ToString());
            }
            return result;
        }
    }
}
