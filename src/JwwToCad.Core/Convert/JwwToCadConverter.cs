using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.XData;
using CSMath;
using JwwToCad.Core.Jww;

namespace JwwToCad.Core.Convert;

/// <summary>Builds an AutoCAD document from a Jw_cad document.</summary>
public sealed class JwwToCadConverter
{
    readonly JwwDocument _jww;
    readonly ConvertOptions _opt;
    readonly CadDocument _doc = new();
    readonly Layer[,] _layers = new Layer[16, 16];
    readonly Dictionary<int, LineType> _lineTypes = new();
    readonly Dictionary<string, TextStyle> _styles = new();
    readonly Dictionary<(uint num, double scale), BlockRecord> _blocks = new();
    readonly Dictionary<uint, JwwBlockDef> _blockDefs = new();
    public ConvertReport Report { get; } = new();
    readonly HashSet<int> _unknownCodes = new();

    // Jw_cad pen colours 1-9 -> AutoCAD colour index.
    static readonly short[] JwColorToAci = { 7, 4, 7, 3, 2, 6, 5, 7, 1, 8 };

    // Line type patterns in paper millimetres (dash > 0, gap < 0, dot = 0).
    static readonly Dictionary<int, double[]> Patterns = new()
    {
        [2] = new[] { 3.0, -1.0 },
        [3] = new[] { 1.5, -1.5 },
        [4] = new[] { 0.0, -1.0 },
        [5] = new[] { 6.0, -1.0, 0.0, -1.0 },
        [6] = new[] { 12.0, -1.5, 0.0, -1.5 },
        [7] = new[] { 6.0, -1.0, 0.0, -1.0, 0.0, -1.0 },
        [8] = new[] { 12.0, -1.5, 0.0, -1.5, 0.0, -1.5 },
        [9] = new[] { 1.0, -1.0 },
    };

    JwwToCadConverter(JwwDocument jww, ConvertOptions opt) { _jww = jww; _opt = opt; }

    public static ConvertReport ConvertFile(string jwwPath, string outPath, ConvertOptions? opt = null)
    {
        opt ??= new ConvertOptions();
        var c = new JwwToCadConverter(JwwReader.Read(jwwPath), opt);
        var doc = c.Build();
        if (Path.GetExtension(outPath).Equals(".dxf", StringComparison.OrdinalIgnoreCase))
            DxfWriter.Write(outPath, doc, false);
        else
            DwgWriter.Write(outPath, doc);
        return c.Report;
    }

    public static CadDocument Convert(JwwDocument jww, ConvertOptions? opt, out ConvertReport report)
    {
        var c = new JwwToCadConverter(jww, opt ?? new ConvertOptions());
        var doc = c.Build();
        report = c.Report;
        return doc;
    }

    CadDocument Build()
    {
        _doc.Header.Version = _opt.Version;
        _doc.Header.CodePage = "ANSI_932"; // Japanese, for programs that still read the code page
        _doc.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
        CreateLayers();
        foreach (var b in _jww.Blocks) _blockDefs[b.Number] = b;
        if (_opt.Mode == CoordinateMode.Paper || !_opt.KeepLayoutForOtherScales)
        {
            foreach (var e in _jww.Entities)
                foreach (var ce in ToCad(e, Scale(e), 0))
                    _doc.Entities.Add(ce);
        }
        else
            BuildRealSizeWithLayout();
        if (_jww.ImageCount > 0)
            Report.Warnings.Add($"File có {_jww.ImageCount} ảnh chèn, bản này chưa chuyển ảnh.");
        return _doc;
    }

    // Real size for the main scale. Groups drawn at another scale (title block at 1/1, details at 1/10...)
    // go into one block per group, drawn in that group's own real size and inserted at main/own scale,
    // so the sheet keeps the same layout as in Jw_cad.
    void BuildRealSizeWithLayout()
    {
        double main = _jww.Entities
            .GroupBy(e => Scale(e))
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key).FirstOrDefault(1.0);
        Report.MainScale = main;
        var groupBlocks = new Dictionary<int, BlockRecord>();
        foreach (var e in _jww.Entities)
        {
            double s = Scale(e);
            if (Math.Abs(s - main) < 1e-9)
            {
                foreach (var ce in ToCad(e, s, 0)) _doc.Entities.Add(ce);
                continue;
            }
            int g = e.LayerGroup & 15;
            if (!groupBlocks.TryGetValue(g, out var br))
            {
                string name = $"JW_NHOM_{g:X}_TL_1-{s.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";
                br = new BlockRecord(name);
                _doc.BlockRecords.Add(br);
                groupBlocks[g] = br;
                double k = main / s;
                _doc.Entities.Add(new Insert(br)
                {
                    InsertPoint = XYZ.Zero, XScale = k, YScale = k, ZScale = 1,
                    Layer = _doc.Layers["0"],
                });
                Report.Warnings.Add($"Nhóm lớp {g:X} (tỉ lệ 1/{s:0.###}) khác tỉ lệ chính 1/{main:0.###}: để trong block {name} để giữ bố cục bản vẽ.");
            }
            foreach (var ce in ToCad(e, s, 0)) br.Entities.Add(ce);
        }
    }

    double Scale(JwwEntity e) =>
        _opt.Mode == CoordinateMode.Paper ? 1.0 : Math.Max(_jww.Groups[e.LayerGroup & 15].Scale, 1e-9);

    // ---------- tables ----------

    void CreateLayers()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int g = 0; g < 16; g++)
        {
            var grp = _jww.Groups[g];
            for (int l = 0; l < 16; l++)
            {
                string name = $"{g:X}-{l:X}";
                string jwName = Clean(grp.LayerNames[l]);
                if (jwName.Length > 0) name += " " + jwName;
                while (!used.Add(name)) name += "_";
                var layer = new Layer(name)
                {
                    IsOn = grp.State != 0 && grp.LayerState[l] != 0,
                };
                _layers[g, l] = layer;
            }
        }
    }

    Layer LayerOf(JwwEntity e)
    {
        var layer = _layers[e.LayerGroup & 15, e.Layer & 15];
        if (layer.Document == null) _doc.Layers.Add(layer);
        return layer;
    }

    static string Clean(string s)
    {
        var bad = "<>/\\\":;?*|=`,".ToCharArray();
        var chars = s.Trim().Select(ch => bad.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    LineType? LineTypeOf(JwwEntity e)
    {
        int style = e.PenStyle;
        if (!Patterns.TryGetValue(style, out var pat)) return null;
        if (!_lineTypes.TryGetValue(style, out var lt))
        {
            lt = new LineType($"JW_LINE{style}") { Description = $"Jw_cad nét {style}" };
            foreach (var len in pat) lt.AddSegment(new LineType.Segment { Length = len });
            _doc.LineTypes.Add(lt);
            _lineTypes[style] = lt;
        }
        return lt;
    }

    TextStyle StyleOf(string font)
    {
        font = string.IsNullOrWhiteSpace(font) ? "ＭＳ ゴシック" : font.Trim();
        if (_styles.TryGetValue(font, out var st)) return st;
        bool mincho = font.Contains("明朝");
        bool proportional = font.Contains("Ｐ") || font.Contains("P明朝") || font.Contains("Pゴシック");
        string face = mincho ? (proportional ? "ＭＳ Ｐ明朝" : "ＭＳ 明朝") : (proportional ? "ＭＳ Ｐゴシック" : "ＭＳ ゴシック");
        string file = mincho ? "msmincho.ttc" : "msgothic.ttc";
        st = new TextStyle(Clean(font)) { Filename = file };
        _doc.TextStyles.Add(st);
        // AutoCAD picks a TrueType face (and its character set) from this ACAD xdata,
        // not from the file name alone: charset 128 = Shift-JIS, 0x31 = fixed pitch, modern family.
        int pitchFamily = proportional ? 0x32 : 0x31;
        st.ExtendedData.Add(AcadAppId(), new ExtendedData(new ExtendedDataRecord[]
        {
            new ExtendedDataString(face),
            new ExtendedDataInteger32((128 << 8) | pitchFamily),
        }));
        _styles[font] = st;
        return st;
    }

    AppId AcadAppId()
    {
        if (_doc.AppIds.TryGetValue("ACAD", out var app)) return app;
        app = new AppId("ACAD");
        _doc.AppIds.Add(app);
        return app;
    }

    void Apply(Entity ce, JwwEntity e, double s)
    {
        ce.Layer = LayerOf(e);
        int c = e.PenColor;
        ce.Color = c >= 1 && c <= 9 ? new Color(JwColorToAci[c]) : new Color(7);
        var lt = LineTypeOf(e);
        if (lt != null) { ce.LineType = lt; ce.LineTypeScale = s; }
    }

    // ---------- entities ----------

    IEnumerable<Entity> ToCad(JwwEntity e, double s, int depth)
    {
        switch (e)
        {
            case JwwLine l:
                var line = new Line(new XYZ(l.X1 * s, l.Y1 * s, 0), new XYZ(l.X2 * s, l.Y2 * s, 0));
                Apply(line, e, s); Report.Add("Đường"); yield return line;
                break;
            case JwwArc a:
                var arc = ArcToCad(a, s);
                Apply(arc, e, s); Report.Add("Cung/tròn"); yield return arc;
                break;
            case JwwPoint p:
                if (p.PenStyle == 100) { foreach (var m in MarkerToCad(p, s)) yield return m; break; }
                if (p.IsTemporary && _opt.SkipTemporaryPoints) { Report.Skip("Điểm tạm"); break; }
                var pt = new Point(new XYZ(p.X * s, p.Y * s, 0));
                Apply(pt, e, s); Report.Add("Điểm"); yield return pt;
                break;
            case JwwText t:
                var txt = TextToCad(t, s);
                if (txt != null) { Report.Add("Chữ"); yield return txt; }
                break;
            case JwwSolid so:
                foreach (var sol in SolidToCad(so, s))
                {
                    Apply(sol, e, s);
                    if (so.Rgb is uint rgb) sol.Color = new Color((byte)(rgb & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)((rgb >> 16) & 0xFF));
                    yield return sol;
                }
                Report.Add("Tô đặc");
                break;
            case JwwDimension d:
                foreach (var x in DimensionToCad(d, s)) yield return x;
                Report.Add("Kích thước");
                break;
            case JwwBlockInsert bi:
                var ins = InsertToCad(bi, s, depth);
                if (ins != null) { Report.Add("Block"); yield return ins; }
                break;
            default:
                Report.Skip(e.GetType().Name);
                break;
        }
    }

    Entity ArcToCad(JwwArc a, double s)
    {
        var c = new XYZ(a.CX * s, a.CY * s, 0);
        double r = a.Radius * s;
        if (Math.Abs(a.Flatness - 1.0) < 1e-9)
        {
            if (a.IsFullCircle) return new Circle { Center = c, Radius = r };
            double start = a.StartAngle + a.Tilt;
            return new Arc { Center = c, Radius = r, StartAngle = Norm(start), EndAngle = Norm(start + a.SweepAngle) };
        }
        // Ellipse: Radius is the major semi-axis along Tilt, Flatness = minor/major.
        double major = r, ratio = a.Flatness;
        var axis = new XYZ(Math.Cos(a.Tilt) * major, Math.Sin(a.Tilt) * major, 0);
        if (ratio > 1)
        {
            // AutoCAD requires ratio <= 1: swap axes.
            axis = new XYZ(-Math.Sin(a.Tilt) * major * ratio, Math.Cos(a.Tilt) * major * ratio, 0);
            ratio = 1 / ratio;
            var el2 = new Ellipse { Center = c, MajorAxisEndPoint = axis, RadiusRatio = ratio };
            if (!a.IsFullCircle) { el2.StartParameter = Norm(a.StartAngle - Math.PI / 2); el2.EndParameter = Norm(a.StartAngle + a.SweepAngle - Math.PI / 2); }
            return el2;
        }
        var el = new Ellipse { Center = c, MajorAxisEndPoint = axis, RadiusRatio = ratio };
        if (!a.IsFullCircle) { el.StartParameter = Norm(a.StartAngle); el.EndParameter = Norm(a.StartAngle + a.SweepAngle); }
        else { el.StartParameter = 0; el.EndParameter = 2 * Math.PI; }
        return el;
    }

    static double Norm(double a)
    {
        a %= 2 * Math.PI;
        return a < 0 ? a + 2 * Math.PI : a;
    }

    TextEntity? TextToCad(JwwText t, double s)
    {
        if (string.IsNullOrEmpty(t.Text) || t.Height <= 0) { Report.Skip("Chữ rỗng"); return null; }
        double rot = t.AngleDeg * Math.PI / 180.0;
        double cell = t.Height * s;
        double shift = cell * _opt.TextBaselineShift;
        var txt = new TextEntity
        {
            Value = t.Text,
            InsertPoint = new XYZ(t.StartX * s - Math.Sin(rot) * shift, t.StartY * s + Math.Cos(rot) * shift, 0),
            Height = cell * _opt.TextHeightFactor,
            Rotation = rot,
            WidthFactor = t.Width > 0 ? t.Width / t.Height : 1.0,
            Style = StyleOf(t.Font),
        };
        Apply(txt, t, s);
        return txt;
    }

    static IEnumerable<Solid> SolidToCad(JwwSolid so, double s)
    {
        var p = so.Points;
        if (so.PenStyle == 101)
        {
            // Filled circle / arc / ellipse: centre, radius, flatness, tilt, start, sweep.
            double cx = p[0], cy = p[1], r = p[2], flat = p[3], tilt = p[4], start = p[5], sweep = p[6];
            if (Math.Abs(sweep) < 1e-9) sweep = 2 * Math.PI;
            int n = Math.Max(8, (int)Math.Ceiling(Math.Abs(sweep) / (2 * Math.PI) * 48));
            XYZ At(double t)
            {
                double x = r * Math.Cos(t), y = r * flat * Math.Sin(t);
                return new XYZ((cx + x * Math.Cos(tilt) - y * Math.Sin(tilt)) * s, (cy + x * Math.Sin(tilt) + y * Math.Cos(tilt)) * s, 0);
            }
            var c = new XYZ(cx * s, cy * s, 0);
            for (int i = 0; i < n; i++)
            {
                var a = At(start + sweep * i / n);
                var b = At(start + sweep * (i + 1) / n);
                yield return new Solid { FirstCorner = c, SecondCorner = a, ThirdCorner = b, FourthCorner = b };
            }
            yield break;
        }
        // Points are stored in polygon order; AutoCAD SOLID wants 1, 2, 4, 3.
        XYZ P(int i) => new(p[i * 2] * s, p[i * 2 + 1] * s, 0);
        yield return new Solid { FirstCorner = P(0), SecondCorner = P(1), ThirdCorner = P(3), FourthCorner = P(2) };
    }

    IEnumerable<Entity> DimensionToCad(JwwDimension d, double s)
    {
        foreach (var l in new[] { d.Line, d.Ext1, d.Ext2 })
        {
            if (l == null) continue;
            if (Math.Abs(l.X1 - l.X2) < 1e-9 && Math.Abs(l.Y1 - l.Y2) < 1e-9) continue;
            var line = new Line(new XYZ(l.X1 * s, l.Y1 * s, 0), new XYZ(l.X2 * s, l.Y2 * s, 0));
            Apply(line, l, s); yield return line;
        }
        var txt = TextToCad(d.Text, s);
        if (txt != null) yield return txt;
        foreach (var p in d.Points)
            if (p.PenStyle == 100) foreach (var m in MarkerToCad(p, s)) yield return m;
    }

    /// <summary>Point marks (dimension ends and standalone marks) drawn as small geometry.</summary>
    IEnumerable<Entity> MarkerToCad(JwwPoint p, double s)
    {
        // Sizes measured on Jw_cad screenshots (施-2 at 1/50, 施-8 at 1/40): the drawn radius is
        // 1.25 × Scale in paper mm. Code 7 = filled dot (dimension ends), code -2 = small open circle
        // (leader ends).
        double r = (p.Scale > 0 ? p.Scale : 0.32) * 1.25 * s;
        var c = new XYZ(p.X * s, p.Y * s, 0);
        switch ((int)p.Code)
        {
            case 7:
                foreach (var so in FilledCircle(c, r)) { Apply(so, p, s); yield return so; }
                Report.Add("Chấm đầu mút");
                yield break;
            case -2:
                var ring = new Circle { Center = c, Radius = r };
                Apply(ring, p, s);
                Report.Add("Vòng tròn đầu mút");
                yield return ring;
                yield break;
            case 3 or 4:
                var m = new Circle { Center = c, Radius = r * 0.3 };
                Apply(m, p, s);
                yield return m;
                yield break;
            default:
                // Unknown code: still mark the spot with a small circle so nothing disappears,
                // and tell the user which code it was.
                var mark = new Circle { Center = c, Radius = r };
                Apply(mark, p, s);
                Report.Add($"Dấu điểm mã {(int)p.Code} (vẽ tạm)");
                if (_unknownCodes.Add((int)p.Code))
                    Report.Warnings.Add($"Dấu điểm mã {(int)p.Code} chưa có trong bảng ký hiệu: đã vẽ tạm bằng vòng tròn nhỏ, cần kiểm tra lại hình dạng.");
                yield return mark;
                yield break;
        }
    }

    static IEnumerable<Solid> FilledCircle(XYZ c, double r, int n = 16)
    {
        for (int i = 0; i < n; i++)
        {
            double a0 = 2 * Math.PI * i / n, a1 = 2 * Math.PI * (i + 1) / n;
            var a = new XYZ(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0), 0);
            var b = new XYZ(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1), 0);
            yield return new Solid { FirstCorner = c, SecondCorner = a, ThirdCorner = b, FourthCorner = b };
        }
    }

    Insert? InsertToCad(JwwBlockInsert bi, double s, int depth)
    {
        var block = BlockFor(bi.BlockNumber, s, depth);
        if (block == null) { Report.Skip("Block thiếu định nghĩa"); return null; }
        var ins = new Insert(block)
        {
            InsertPoint = new XYZ(bi.X * s, bi.Y * s, 0),
            XScale = bi.ScaleX, YScale = bi.ScaleY, ZScale = 1,
            Rotation = bi.AngleRad,
        };
        ins.Layer = LayerOf(bi);
        ins.Color = Color.ByLayer;
        return ins;
    }

    BlockRecord? BlockFor(uint num, double s, int depth)
    {
        if (_blocks.TryGetValue((num, s), out var br)) return br;
        if (!_blockDefs.TryGetValue(num, out var def) || depth > 16) return null;
        string baseName = Clean(def.Name.Replace("@@SfigorgFlag@@4", "").Replace("@@SfigorgFlag@@", ""));
        if (baseName.Length == 0) baseName = $"JW_BLOCK_{num}";
        string name = baseName;
        int k = 1;
        while (_doc.BlockRecords.Contains(name) || _blocks.Values.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName}_{++k}";
        br = new BlockRecord(name);
        _blocks[(num, s)] = br;
        _doc.BlockRecords.Add(br);
        foreach (var e in def.Entities)
            foreach (var ce in ToCad(e, s, depth + 1))
                br.Entities.Add(ce);
        return br;
    }
}
