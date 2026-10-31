using JwwToCad.Core.Jww;

namespace JwwToCad.Core.Convert;

/// <summary>
/// Pen colour, line type and line width of Jw_cad objects, read from the settings stored in each file
/// (jwdatafmt.txt), with the Jw_cad / SXF defaults when a file has no readable settings.
/// </summary>
public sealed class JwwStyles
{
    readonly JwwHeader? _h;
    public JwwStyles(JwwHeader? header) { _h = header; }

    // ---------------- colours ----------------

    /// <summary>Jw_cad standard pens 1-9 -> AutoCAD colour index (Jw_cad's own DXF convention).</summary>
    public static readonly short[] StandardPenAci = { 7, 4, 7, 3, 2, 6, 5, 7, 1, 8 };

    /// <summary>SXF predefined colours 1-16 (Jw_cad numbers 101-116), as RGB.</summary>
    public static readonly (string Name, int R, int G, int B)[] SxfColors =
    {
        ("black", 0, 0, 0), ("red", 255, 0, 0), ("green", 0, 255, 0), ("blue", 0, 0, 255),
        ("yellow", 255, 255, 0), ("magenta", 255, 0, 255), ("cyan", 0, 255, 255), ("white", 255, 255, 255),
        ("deeppink", 192, 0, 128), ("brown", 192, 128, 64), ("orange", 255, 128, 0), ("lightgreen", 128, 192, 128),
        ("lightblue", 0, 128, 255), ("lavender", 128, 64, 255), ("lightgray", 192, 192, 192), ("darkgray", 128, 128, 128),
    };

    /// <summary>RGB of AutoCAD colour indexes 1-9 (exact values).</summary>
    public static readonly (int R, int G, int B)[] AciRgb =
    {
        (0, 0, 0), (255, 0, 0), (255, 255, 0), (0, 255, 0), (0, 255, 255), (0, 0, 255), (255, 0, 255),
        (255, 255, 255), (128, 128, 128), (192, 192, 192),
    };

    public enum ColorKind { Index, TrueColor }

    /// <summary>
    /// Colour for a Jw_cad pen number. Standard pens use colour indexes; SXF pens (100-356) use the
    /// colour stored in the file: black and white become index 7 (shown against any background),
    /// colours equal to an index colour use that index, the rest are true colours.
    /// </summary>
    public (ColorKind Kind, short Index, int R, int G, int B) PenColor(int pen, out string? problem)
    {
        problem = null;
        if (pen >= 1 && pen <= 9)
        {
            // Each file stores its own screen colour for pens 1-9 (users change them, e.g. pen 7 orange
            // or pen 5 red); the fixed table is only for files whose settings could not be read.
            if (_h == null || !_h.ScreenColor.TryGetValue(pen, out uint sc)) return (ColorKind.Index, StandardPenAci[pen], 0, 0, 0);
            return FromRgb((int)(sc & 0xFF), (int)((sc >> 8) & 0xFF), (int)((sc >> 16) & 0xFF));
        }
        if (pen >= 100 && pen <= 356)
        {
            (int r, int g, int b)? rgb = null;
            if (_h != null && _h.ScreenColor.TryGetValue(pen, out uint cr)) rgb = ((int)(cr & 0xFF), (int)((cr >> 8) & 0xFF), (int)((cr >> 16) & 0xFF));
            else if (pen >= 101 && pen <= 116) { var c = SxfColors[pen - 101]; rgb = (c.R, c.G, c.B); }
            if (rgb is not (int r, int g, int b)) { problem = $"màu SXF {pen} không có định nghĩa trong file"; return (ColorKind.Index, 7, 0, 0, 0); }
            return FromRgb(r, g, b);
        }
        if (pen == 10) return (ColorKind.Index, 7, 0, 0, 0); // solids: the RGB stored with the solid is used
        if (pen == 0) return (ColorKind.Index, 7, 0, 0, 0); // background colour pen
        problem = $"số màu {pen} không thuộc bảng màu Jw_cad";
        return (ColorKind.Index, 7, 0, 0, 0);
    }

    /// <summary>Black and white become index 7 (shown against any background), colours equal to an
    /// index colour use that index, the rest are true colours.</summary>
    static (ColorKind Kind, short Index, int R, int G, int B) FromRgb(int r, int g, int b)
    {
        if ((r, g, b) == (0, 0, 0) || (r, g, b) == (255, 255, 255)) return (ColorKind.Index, 7, r, g, b);
        for (short i = 1; i <= 9; i++)
            if (i != 7 && AciRgb[i] == (r, g, b)) return (ColorKind.Index, i, r, g, b);
        return (ColorKind.TrueColor, 0, r, g, b);
    }

    // ---------------- line types ----------------

    // Used only when a file has no readable settings: Jw_cad defaults, derived the same way.
    static readonly Dictionary<int, (uint Bits, int UnitDots, int PrintPitch)> DefaultBits = new()
    {
        [2] = (0x99999999, 4, 5), [3] = (0xC3C3C3C3, 8, 5), [4] = (0xE7E7E7E7, 8, 5),
        [5] = (0xF99FF99F, 16, 5), [6] = (0xFFF99FFF, 32, 5), [7] = (0xF24FF24F, 16, 5),
        [8] = (0xFFF24FFF, 32, 5), [9] = (0x22222222, 4, 10),
        [16] = (0xFFF99FFF, 32, 20), [17] = (0xFFF24FFF, 32, 20), [18] = (0xFFFE7FFF, 32, 20), [19] = (0xFFFE7FFF, 32, 40),
    };

    static readonly Dictionary<int, (string Name, double[] Pitch)> DefaultSxf = new()
    {
        [31] = ("continuous", Array.Empty<double>()),
        [32] = ("dashed", new[] { 6, 1.5 }),
        [33] = ("dashed spaced", new[] { 6.0, 6 }),
        [34] = ("long dashed dotted", new[] { 12, 1.5, 0.25, 1.5 }),
        [35] = ("long dashed double-dotted", new[] { 12, 1.5, 0.25, 1.5, 0.25, 1.5 }),
        [36] = ("long dashed triplicate-dotted", new[] { 12, 1.5, 0.25, 1.5, 0.25, 1.5, 0.25, 1.5 }),
        [37] = ("dotted", new[] { 0.25, 1.5 }),
        [38] = ("chain", new[] { 12, 1.5, 3.5, 1.5 }),
        [39] = ("chain double dash", new[] { 12, 1.5, 3.5, 1.5, 3.5, 1.5 }),
        [40] = ("dashed dotted", new[] { 6, 1.5, 0.25, 1.5 }),
        [41] = ("double-dashed dotted", new[] { 6, 1.5, 6, 1.5, 0.25, 1.5 }),
        [42] = ("dashed double-dotted", new[] { 6, 1.5, 0.25, 1.5, 0.25, 1.5 }),
        [43] = ("double-dashed double-dotted", new[] { 6, 1.5, 6, 1.5, 0.25, 1.5, 0.25, 1.5 }),
        [44] = ("dashed triplicate-dotted", new[] { 6, 1.5, 0.25, 1.5, 0.25, 1.5, 0.25, 1.5 }),
        [45] = ("double-dashed triplicate-dotted", new[] { 6, 1.5, 6, 1.5, 0.25, 1.5, 0.25, 1.5, 0.25, 1.5 }),
    };

    /// <summary>
    /// Length of one pattern bit at "printer pitch" 1, in paper mm. Found from the SXF line types, whose
    /// Jw_cad bit patterns and mm lengths are both stored in every file: e.g. 'dashed' 6+1.5 mm =
    /// 16 bits × pitch 15 × 1/32 mm; it holds for all 15 SXF types.
    /// </summary>
    public const double BitMm = 1.0 / 32.0;

    /// <summary>
    /// Dash pattern in paper mm (dash &gt; 0, gap &lt; 0, dot = 0), or null for a continuous line.
    /// </summary>
    public double[]? LinePattern(int style, out string name, out string? problem)
    {
        problem = null;
        name = $"JW_LINE{style}";
        if (style <= 1 || style == 100) return null;
        if (style >= 11 && style <= 15)
        {
            problem = $"nét ngẫu nhiên (nét số {style}) chưa vẽ được dạng lượn sóng, đã vẽ bằng nét liền";
            return null;
        }
        if (style >= 30 && style <= 62)
        {
            (string Name, double[] Pitch) sx = default;
            bool have = _h != null && _h.SxfLineTypes.TryGetValue(style, out sx);
            if (!have && DefaultSxf.TryGetValue(style, out var d)) { sx = d; have = true; }
            if (have && !string.IsNullOrWhiteSpace(sx.Name)) name = "SXF_" + sx.Name.Trim().Replace(' ', '_');
            if (have && sx.Pitch.Length >= 2 && sx.Pitch.Any(p => p > 0))
            {
                var pat = new double[sx.Pitch.Length];
                for (int i = 0; i < pat.Length; i++) pat[i] = i % 2 == 0 ? Math.Max(sx.Pitch[i], 0) : -Math.Abs(sx.Pitch[i]);
                // SXF dots are short dashes (0.25 mm); keep them as dashes so the length matches.
                return pat;
            }
            if (have && (sx.Name ?? "").Trim().Equals("continuous", StringComparison.OrdinalIgnoreCase)) return null;
            // User-defined SXF type without mm lengths: use its bit pattern if it has one.
        }
        uint bits; int unit, pitch;
        if (_h != null && _h.LineTypes.TryGetValue(style, out var lt) && lt.UnitDots > 0)
            (bits, unit, pitch) = (lt.Bits, lt.UnitDots, lt.PrintPitch);
        else if (DefaultBits.TryGetValue(style, out var db))
            (bits, unit, pitch) = db;
        else
        {
            if (style >= 30 && style <= 62) return null; // empty user slot: Jw_cad draws it continuous
            problem = $"nét số {style} không có định nghĩa, đã vẽ bằng nét liền";
            return null;
        }
        var fromBits = FromBits(bits, unit, Math.Max(pitch, 1) * BitMm);
        if (fromBits == null && bits == 0 && !(style >= 30 && style <= 62))
            problem = $"nét số {style} có mẫu rỗng, đã vẽ bằng nét liền";
        return fromBits;
    }

    /// <summary>Turn a Jw_cad bit pattern (1 = ink, most significant bit first) into dash/gap lengths.</summary>
    public static double[]? FromBits(uint bits, int unitDots, double bitLen)
    {
        unitDots = Math.Clamp(unitDots, 1, 32);
        var b = new bool[unitDots];
        for (int i = 0; i < unitDots; i++) b[i] = ((bits >> (31 - i)) & 1) != 0;
        if (b.All(x => x) || b.All(x => !x)) return null;
        // Start at the beginning of an ink run.
        int start = 0;
        for (int i = 0; i < unitDots; i++)
            if (b[i] && !b[(i - 1 + unitDots) % unitDots]) { start = i; break; }
        var runs = new List<double>();
        int n = 0; bool cur = true;
        for (int k = 0; k < unitDots; k++)
        {
            bool v = b[(start + k) % unitDots];
            if (v == cur) n++;
            else { runs.Add(cur ? n * bitLen : -n * bitLen); cur = v; n = 1; }
        }
        runs.Add(cur ? n * bitLen : -n * bitLen);
        return runs.ToArray();
    }

    // ---------------- line width ----------------

    static readonly int[] Weights = { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };

    /// <summary>
    /// Line width in 1/100 mm, or null when Jw_cad leaves it to the printer settings.
    /// Object widths are 1/100 mm (they are SXF values such as 13, 18, 50); a pen's print width is
    /// 1/100 mm for SXF pens and, for standard pens, only when the file says "線幅を1/100mm単位とする".
    /// </summary>
    public int? Width(JwwEntity e)
    {
        if (e is JwwText || e is JwwSolid || e is JwwBlockInsert || e is JwwDimension) return null;
        if (e.PenWidth > 0) return e.PenWidth;
        int pen = e.PenColor;
        if (_h == null || !_h.PrintWidth.TryGetValue(pen, out int w) || w <= 0) return null;
        if (pen >= 100 || _h.MaxDrawWidth == -101) return w;
        return null;
    }

    /// <summary>Nearest AutoCAD line weight (1/100 mm).</summary>
    public static int NearestWeight(int hundredths) =>
        Weights.OrderBy(w => Math.Abs(w - hundredths)).First();
}
