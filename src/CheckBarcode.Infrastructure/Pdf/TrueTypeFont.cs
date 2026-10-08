using System.Buffers.Binary;
using System.Text;

namespace CheckBarcode.Infrastructure.Pdf;

/// <summary>
/// Minimal TrueType reader: glyph mapping (cmap 4/12), advance widths and metrics needed to embed
/// the font in a PDF as a CIDFontType2 with Identity-H encoding (full Unicode, Vietnamese included).
/// </summary>
public sealed class TrueTypeFont
{
    private readonly Dictionary<int, ushort> _cmap = new();
    private ushort[] _advances = Array.Empty<ushort>();

    public byte[] Data { get; }
    public string PostScriptName { get; private set; } = "Font";
    public int UnitsPerEm { get; private set; } = 1000;
    public short Ascent { get; private set; }
    public short Descent { get; private set; }
    public short CapHeight { get; private set; }
    public short XMin { get; private set; }
    public short YMin { get; private set; }
    public short XMax { get; private set; }
    public short YMax { get; private set; }
    public double ItalicAngle { get; private set; }
    public bool IsBold { get; private set; }

    private TrueTypeFont(byte[] data)
    {
        Data = data;
        Parse();
    }

    public static TrueTypeFont Load(string path) => new(File.ReadAllBytes(path));
    public static TrueTypeFont FromBytes(byte[] data) => new(data);

    /// <summary>First existing file of the list.</summary>
    public static TrueTypeFont? LoadFirst(IEnumerable<string> candidates)
    {
        foreach (var c in candidates)
        {
            var path = Environment.ExpandEnvironmentVariables(c);
            if (!Path.IsPathRooted(path)) path = Path.Combine(AppContext.BaseDirectory, path);
            if (File.Exists(path)) return Load(path);
        }
        return null;
    }

    public ushort GlyphId(int codePoint) => _cmap.TryGetValue(codePoint, out var g) ? g : (ushort)0;

    public int Advance(ushort glyph) =>
        _advances.Length == 0 ? 0 : glyph < _advances.Length ? _advances[glyph] : _advances[^1];

    /// <summary>Advance width in PDF text space units (1/1000 em).</summary>
    public int PdfWidth(ushort glyph) => (int)Math.Round(Advance(glyph) * 1000.0 / UnitsPerEm);

    public double MeasureWidth(string text, double size)
    {
        double units = 0;
        foreach (var cp in CodePoints(text)) units += Advance(GlyphId(cp));
        return units * size / UnitsPerEm;
    }

    public static IEnumerable<int> CodePoints(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else yield return s[i];
        }
    }

    private void Parse()
    {
        var d = Data;
        var numTables = U16(4);
        var tables = new Dictionary<string, (int Offset, int Length)>();
        for (var i = 0; i < numTables; i++)
        {
            var rec = 12 + i * 16;
            var tag = Encoding.ASCII.GetString(d, rec, 4);
            tables[tag] = ((int)U32(rec + 8), (int)U32(rec + 12));
        }
        if (!tables.ContainsKey("cmap") || !tables.ContainsKey("hmtx") || !tables.ContainsKey("head"))
            throw new InvalidDataException("Not a TrueType font (cmap/hmtx/head missing). OpenType CFF fonts are not supported.");
        if (!tables.ContainsKey("glyf")) throw new InvalidDataException("Font has no 'glyf' table (CFF outlines not supported).");

        var head = tables["head"].Offset;
        UnitsPerEm = U16(head + 18);
        XMin = S16(head + 36); YMin = S16(head + 38); XMax = S16(head + 40); YMax = S16(head + 42);
        IsBold = (U16(head + 44) & 1) != 0;

        var hhea = tables["hhea"].Offset;
        Ascent = S16(hhea + 4);
        Descent = S16(hhea + 6);
        int numberOfHMetrics = U16(hhea + 34);
        var numGlyphs = tables.TryGetValue("maxp", out var maxp) ? U16(maxp.Offset + 4) : numberOfHMetrics;

        var hmtx = tables["hmtx"].Offset;
        _advances = new ushort[Math.Max(numGlyphs, numberOfHMetrics)];
        ushort last = 0;
        for (var g = 0; g < _advances.Length; g++)
        {
            if (g < numberOfHMetrics) last = U16(hmtx + g * 4);
            _advances[g] = last;
        }

        CapHeight = Ascent;
        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 90 && U16(os2.Offset) >= 2) CapHeight = S16(os2.Offset + 88);
        if (tables.TryGetValue("post", out var post)) ItalicAngle = S16(post.Offset + 4) + U16(post.Offset + 6) / 65536.0;
        if (tables.TryGetValue("name", out var name)) PostScriptName = ReadName(name.Offset) ?? PostScriptName;

        ParseCmap(tables["cmap"].Offset);
    }

    private void ParseCmap(int cmap)
    {
        var count = U16(cmap + 2);
        int best = -1, bestScore = -1;
        for (var i = 0; i < count; i++)
        {
            var rec = cmap + 4 + i * 8;
            int platform = U16(rec), encoding = U16(rec + 2);
            var offset = cmap + (int)U32(rec + 4);
            int format = U16(offset);
            var score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 4,
                (0, _, 12) => 3,
                (3, 1, 4) => 2,
                (0, _, 4) => 1,
                _ => -1,
            };
            if (score > bestScore) { bestScore = score; best = offset; }
        }
        if (best < 0) throw new InvalidDataException("No Unicode cmap subtable found");
        if (U16(best) == 12) ParseFormat12(best); else ParseFormat4(best);
    }

    private void ParseFormat4(int t)
    {
        int segX2 = U16(t + 6);
        var segCount = segX2 / 2;
        var endCodes = t + 14;
        var startCodes = endCodes + segX2 + 2;
        var idDeltas = startCodes + segX2;
        var idRangeOffsets = idDeltas + segX2;
        for (var s = 0; s < segCount; s++)
        {
            int end = U16(endCodes + s * 2), start = U16(startCodes + s * 2);
            var delta = S16(idDeltas + s * 2);
            var roPos = idRangeOffsets + s * 2;
            int ro = U16(roPos);
            if (start == 0xFFFF) continue;
            for (var c = start; c <= end; c++)
            {
                int glyph;
                if (ro == 0) glyph = (c + delta) & 0xFFFF;
                else
                {
                    var pos = roPos + ro + (c - start) * 2;
                    glyph = U16(pos);
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                if (glyph != 0) _cmap[c] = (ushort)glyph;
            }
        }
    }

    private void ParseFormat12(int t)
    {
        var groups = (int)U32(t + 12);
        for (var i = 0; i < groups; i++)
        {
            var g = t + 16 + i * 12;
            var start = (int)U32(g);
            var end = (int)U32(g + 4);
            var startGlyph = (int)U32(g + 8);
            for (var c = start; c <= end && c <= 0x10FFFF; c++) _cmap[c] = (ushort)(startGlyph + c - start);
        }
    }

    private string? ReadName(int t)
    {
        int count = U16(t + 2), strings = t + U16(t + 4);
        for (var i = 0; i < count; i++)
        {
            var rec = t + 6 + i * 12;
            int platform = U16(rec), nameId = U16(rec + 6), length = U16(rec + 8), offset = U16(rec + 10);
            if (nameId != 6) continue;
            var bytes = Data.AsSpan(strings + offset, length);
            var s = platform is 3 or 0 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
            s = new string(s.Where(ch => ch > 32 && ch < 127 && ch != '/' && ch != '[' && ch != ']' && ch != '(' && ch != ')').ToArray());
            if (s.Length > 0) return s;
        }
        return null;
    }

    private ushort U16(int o) => BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(o));
    private short S16(int o) => BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan(o));
    private uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(o));
}
