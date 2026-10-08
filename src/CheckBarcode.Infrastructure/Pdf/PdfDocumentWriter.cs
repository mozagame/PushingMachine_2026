using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace CheckBarcode.Infrastructure.Pdf;

/// <summary>Embedded TrueType font registered in a PDF; tracks used glyphs for widths and ToUnicode.</summary>
public sealed class PdfFont
{
    internal readonly SortedDictionary<ushort, int> Used = new();
    public TrueTypeFont Font { get; }
    public string ResourceName { get; }

    internal PdfFont(TrueTypeFont font, string resourceName)
    {
        Font = font;
        ResourceName = resourceName;
    }

    /// <summary>Hex string of glyph ids for the Tj operator.</summary>
    internal string Encode(string text)
    {
        var sb = new StringBuilder(text.Length * 4 + 2);
        sb.Append('<');
        foreach (var cp in TrueTypeFont.CodePoints(text))
        {
            var gid = Font.GlyphId(cp);
            if (!Used.ContainsKey(gid)) Used[gid] = cp;
            sb.Append(gid.ToString("X4"));
        }
        sb.Append('>');
        return sb.ToString();
    }
}

public sealed class PdfPage
{
    internal readonly StringBuilder Content = new();
    public double Width { get; }
    public double Height { get; }

    internal PdfPage(double width, double height)
    {
        Width = width;
        Height = height;
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Draws text with its baseline at (x, y); origin is the bottom-left corner.</summary>
    public void Text(PdfFont font, double size, double x, double y, string text, double gray = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        Content.Append($"BT {N(gray)} g /{font.ResourceName} {N(size)} Tf {N(x)} {N(y)} Td {font.Encode(text)} Tj ET\n");
    }

    public void Line(double x1, double y1, double x2, double y2, double width = 0.5, double gray = 0)
    {
        Content.Append($"{N(gray)} G {N(width)} w {N(x1)} {N(y1)} m {N(x2)} {N(y2)} l S\n");
    }

    public void FillRect(double x, double y, double w, double h, double gray)
    {
        Content.Append($"{N(gray)} g {N(x)} {N(y)} {N(w)} {N(h)} re f\n");
    }

    public void StrokeRect(double x, double y, double w, double h, double width = 0.5, double gray = 0)
    {
        Content.Append($"{N(gray)} G {N(width)} w {N(x)} {N(y)} {N(w)} {N(h)} re S\n");
    }
}

/// <summary>Small PDF 1.7 writer: pages, lines, rectangles and Unicode text with embedded TrueType fonts.</summary>
public sealed class PdfDocumentWriter
{
    private readonly List<PdfFont> _fonts = new();
    private readonly List<PdfPage> _pages = new();

    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Creator { get; set; } = "CheckBarcode v2";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public IReadOnlyList<PdfPage> Pages => _pages;

    public PdfFont AddFont(TrueTypeFont font)
    {
        var f = new PdfFont(font, "F" + (_fonts.Count + 1));
        _fonts.Add(f);
        return f;
    }

    public PdfPage AddPage(double width, double height)
    {
        var p = new PdfPage(width, height);
        _pages.Add(p);
        return p;
    }

    public byte[] Save()
    {
        var objects = new List<byte[]>();
        int Reserve() { objects.Add(Array.Empty<byte>()); return objects.Count; }
        void Set(int id, string body) => objects[id - 1] = Encoding.ASCII.GetBytes(body);
        void SetStream(int id, string dict, byte[] data)
        {
            var head = Encoding.ASCII.GetBytes($"<< {dict} /Length {data.Length} >>\nstream\n");
            var tail = Encoding.ASCII.GetBytes("\nendstream");
            objects[id - 1] = head.Concat(data).Concat(tail).ToArray();
        }

        var catalogId = Reserve();
        var pagesId = Reserve();
        var infoId = Reserve();

        var fontIds = new List<int>();
        foreach (var font in _fonts)
        {
            var type0 = Reserve();
            var cid = Reserve();
            var descriptor = Reserve();
            var file = Reserve();
            var toUnicode = Reserve();
            fontIds.Add(type0);
            var ttf = font.Font;
            double S(double v) => Math.Round(v * 1000.0 / ttf.UnitsPerEm);
            var name = ttf.PostScriptName;

            Set(type0, $"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
            Set(cid, $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                     $"/FontDescriptor {descriptor} 0 R /DW 1000 /W [{Widths(font)}] /CIDToGIDMap /Identity >>");
            var flags = 32 | (ttf.ItalicAngle != 0 ? 64 : 0);
            Set(descriptor, string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /FontDescriptor /FontName /{name} /Flags {flags} /FontBBox [{S(ttf.XMin)} {S(ttf.YMin)} {S(ttf.XMax)} {S(ttf.YMax)}] " +
                $"/ItalicAngle {ttf.ItalicAngle:0.##} /Ascent {S(ttf.Ascent)} /Descent {S(ttf.Descent)} /CapHeight {S(ttf.CapHeight)} " +
                $"/StemV {(ttf.IsBold ? 120 : 80)} /FontFile2 {file} 0 R >>"));
            SetStream(file, $"/Filter /FlateDecode /Length1 {ttf.Data.Length}", Deflate(ttf.Data));
            SetStream(toUnicode, "/Filter /FlateDecode", Deflate(Encoding.ASCII.GetBytes(ToUnicodeCMap(font))));
        }

        var fontResources = string.Join(" ", _fonts.Select((f, i) => $"/{f.ResourceName} {fontIds[i]} 0 R"));
        var pageIds = new List<int>();
        foreach (var page in _pages)
        {
            var pageId = Reserve();
            var contentId = Reserve();
            pageIds.Add(pageId);
            Set(pageId, string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {page.Width:0.##} {page.Height:0.##}] " +
                $"/Resources << /Font << {fontResources} >> >> /Contents {contentId} 0 R >>"));
            SetStream(contentId, "/Filter /FlateDecode", Deflate(Encoding.ASCII.GetBytes(page.Content.ToString())));
        }

        Set(catalogId, $"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        Set(pagesId, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>");
        var date = CreatedUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        Set(infoId, $"<< /Title {PdfText(Title)} /Author {PdfText(Author)} /Creator {PdfText(Creator)} /Producer (CheckBarcode PdfDocumentWriter) /CreationDate (D:{date}Z) >>");

        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.7\n");
        ms.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = ms.Position;
            W($"{i + 1} 0 obj\n");
            ms.Write(objects[i]);
            W("\nendobj\n");
        }
        var xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root {catalogId} 0 R /Info {infoId} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string Widths(PdfFont font)
    {
        var sb = new StringBuilder();
        foreach (var gid in font.Used.Keys) sb.Append(gid).Append(" [").Append(font.Font.PdfWidth(gid)).Append("] ");
        return sb.ToString().TrimEnd();
    }

    private static string ToUnicodeCMap(PdfFont font)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = font.Used.Where(kv => kv.Key != 0).ToList();
        for (var i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (gid, cp) in chunk)
            {
                var utf16 = Encoding.BigEndianUnicode.GetBytes(char.ConvertFromUtf32(cp));
                sb.Append('<').Append(gid.ToString("X4")).Append("> <").Append(Convert.ToHexString(utf16)).Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    /// <summary>PDF text string in UTF-16BE hex (any language).</summary>
    private static string PdfText(string s) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s ?? "")) + ">";

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
        return ms.ToArray();
    }
}
