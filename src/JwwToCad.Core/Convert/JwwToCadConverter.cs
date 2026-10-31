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
    readonly Dictionary<uint, JwwBlockDef> _blockDefs = new();
    public ConvertReport Report { get; } = new();
    readonly HashSet<int> _unknownCodes = new();

    readonly JwwStyles _st;
    readonly JwEmbeddedText _embed;
    readonly HashSet<string> _warned = new();
    readonly Dictionary<string, ACadSharp.Objects.ImageDefinition> _images = new(StringComparer.OrdinalIgnoreCase);

    void WarnOnce(string msg) { if (_warned.Add(msg)) Report.Warnings.Add(msg); }

    JwwToCadConverter(JwwDocument jww, ConvertOptions opt)
    {
        _jww = jww; _opt = opt;
        _st = new JwwStyles(jww.Header);
        _embed = new JwEmbeddedText { FilePath = opt.SourcePath, Memo = jww.Memo ?? "" };
        if (opt.SourcePath != null && File.Exists(opt.SourcePath)) _embed.Saved = File.GetLastWriteTime(opt.SourcePath);
        if (jww.Header == null)
            Report.Warnings.Add("Không đọc được phần cài đặt của file (màu SXF, kiểu nét): đã dùng cài đặt mặc định của Jw_cad.");
    }

    public static ConvertReport ConvertFile(string jwwPath, string outPath, ConvertOptions? opt = null)
    {
        opt ??= new ConvertOptions();
        opt.SourcePath = jwwPath;
        opt.OutputPath = outPath;
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
            var all = new List<Entity>();
            foreach (var e in _jww.Entities) all.AddRange(ToCad(e, Scale(e), 0));
            AddInDrawOrder(_doc.Entities, all);
        }
        else
            BuildRealSizeWithLayout();
        AddTextGroups();
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
        var top = new List<Entity>();
        var groups = new SortedDictionary<int, (double S, List<Entity> List)>();
        foreach (var e in _jww.Entities)
        {
            double s = Scale(e);
            if (Math.Abs(s - main) < 1e-9) { top.AddRange(ToCad(e, s, 0)); continue; }
            int g = e.LayerGroup & 15;
            if (!groups.TryGetValue(g, out var gl)) groups[g] = gl = (s, new List<Entity>());
            gl.List.AddRange(ToCad(e, s, 0));
        }
        foreach (var (g, (s, list)) in groups)
        {
            string name = $"JW_NHOM_{g:X}_TL_1-{s.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";
            double k = main / s;
            Insert Place(BlockRecord b) => new Insert(b) { InsertPoint = XYZ.Zero, XScale = k, YScale = k, ZScale = 1, Layer = _doc.Layers["0"] };
            // Fills of the group go into their own block so they can sit under every other object of the sheet.
            var fills = list.Where(x => DrawRank(x) == 0).ToList();
            if (fills.Count > 0)
            {
                var fb = new BlockRecord(name + "_TO_DAC");
                _doc.BlockRecords.Add(fb);
                AddInDrawOrder(fb.Entities, fills);
                top.Add(Place(fb));
            }
            var br = new BlockRecord(name);
            _doc.BlockRecords.Add(br);
            AddInDrawOrder(br.Entities, list.Where(x => DrawRank(x) != 0).ToList());
            top.Add(Place(br));
            Report.Warnings.Add($"Nhóm lớp {g:X} (tỉ lệ 1/{s:0.###}) khác tỉ lệ chính 1/{main:0.###}: để trong block {name} để giữ bố cục bản vẽ"
                + (fills.Count > 0 ? $", phần tô đặc trong block {name}_TO_DAC nằm dưới cùng." : "."));
        }
        AddInDrawOrder(_doc.Entities, top);
    }

    // ---------- draw order ----------
    // Fills (solids, images) are drawn first so that lines and text are never hidden under them,
    // then blocks that contain fills, then everything else, keeping the Jw_cad order within each rank.

    readonly Dictionary<BlockRecord, bool> _hasFill = new();

    bool HasFill(BlockRecord b)
    {
        if (_hasFill.TryGetValue(b, out bool v)) return v;
        _hasFill[b] = false;
        v = b.Entities.Any(x => x is Solid || x is RasterImage || (x is Insert i && i.Block != null && HasFill(i.Block)));
        return _hasFill[b] = v;
    }

    int DrawRank(Entity x) => x switch
    {
        Solid or RasterImage => 0,
        Insert i when i.Block != null && i.Block.Entities.All(y => y is Solid || y is RasterImage) && i.Block.Entities.Count > 0 => 0,
        Insert i when i.Block != null && HasFill(i.Block) => 1,
        _ => 2,
    };

    void AddInDrawOrder(CadObjectCollection<Entity> target, List<Entity> list)
    {
        foreach (var x in list.Select((x, n) => (x, n)).OrderBy(p => DrawRank(p.x)).ThenBy(p => p.n)) target.Add(x.x);
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
                bool on = grp.State != 0 && grp.LayerState[l] != 0;
                var layer = new Layer(name) { IsOn = on };
                // A hidden Jw_cad layer hides everything on it, blocks included. AutoCAD hides a block
                // on an "off" layer only partly (just its layer-0 parts), so hidden layers are also frozen.
                if (!on) layer.Flags |= LayerFlags.Frozen;
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
        if (_lineTypes.TryGetValue(style, out var lt)) return lt;
        var pat = _st.LinePattern(style, out string name, out string? problem);
        if (problem != null) WarnOnce($"Nét: {problem}.");
        if (pat == null) { _lineTypes[style] = null!; return null; }
        name = Clean(name);
        string unique = name; int k = 1;
        while (_doc.LineTypes.Contains(unique)) unique = $"{name}_{++k}";
        lt = new LineType(unique) { Description = $"Jw_cad nét {style}" };
        foreach (var len in pat) lt.AddSegment(new LineType.Segment { Length = len });
        _doc.LineTypes.Add(lt);
        _lineTypes[style] = lt;
        return lt;
    }

    // Font files of the usual Japanese fonts (looked up with full-width letters folded).
    static readonly (string Key, string File, bool Proportional)[] KnownFonts =
    {
        ("MS Pゴシック", "msgothic.ttc", true), ("MS UI Gothic", "msgothic.ttc", true), ("MS ゴシック", "msgothic.ttc", false),
        ("MS P明朝", "msmincho.ttc", true), ("MS 明朝", "msmincho.ttc", false),
        ("Meiryo UI", "meiryo.ttc", true), ("メイリオ", "meiryo.ttc", true),
        ("游ゴシック", "YuGothR.ttc", true), ("Yu Gothic", "YuGothR.ttc", true), ("游明朝", "yumin.ttf", true), ("Yu Mincho", "yumin.ttf", true),
        ("BIZ UDPゴシック", "BIZ-UDGothicR.ttc", true), ("BIZ UDゴシック", "BIZ-UDGothicR.ttc", false),
        ("BIZ UDP明朝", "BIZ-UDMinchoM.ttc", true), ("BIZ UD明朝", "BIZ-UDMinchoM.ttc", false),
        ("HG丸ゴシックM-PRO", "HGRSMP.TTF", true), ("HGゴシックE", "HGRGE.TTC", false), ("HGPゴシックE", "HGRGE.TTC", true),
        ("HGゴシックM", "HGRGM.TTC", false), ("HGPゴシックM", "HGRGM.TTC", true), ("HG明朝E", "HGRME.TTC", false), ("HGP明朝E", "HGRME.TTC", true),
        ("HG明朝B", "HGRMB.TTC", false), ("HGP明朝B", "HGRMB.TTC", true), ("HG創英角ゴシックUB", "HGRSGU.TTC", false),
        ("HG正楷書体-PRO", "HGRKK.TTC", true), ("HG教科書体", "HGRKK.TTC", false), ("HG行書体", "HGRGY.TTC", false),
        ("HG創英角ポップ体", "HGRPP1.TTC", false), ("HG平成角ゴシック体W9", "HGRHG9.TTC", false),
    };

    TextStyle StyleOf(string font, bool bold = false, bool italic = false)
    {
        font = string.IsNullOrWhiteSpace(font) ? "ＭＳ ゴシック" : font.Trim();
        string key = $"{font}|{bold}|{italic}";
        if (_styles.TryGetValue(key, out var st)) return st;
        string folded = font.Normalize(System.Text.NormalizationForm.FormKC);
        var known = KnownFonts.FirstOrDefault(k => folded.Equals(k.Key.Normalize(System.Text.NormalizationForm.FormKC), StringComparison.OrdinalIgnoreCase));
        string face = font, file;
        bool proportional;
        if (known.Key != null) { file = known.File; proportional = known.Proportional; }
        else if (folded.Length <= 2 || folded.All(ch => char.IsDigit(ch) || char.IsPunctuation(ch)))
        {
            // Not a font name (e.g. "3"): Jw_cad shows such text in its standard font.
            WarnOnce($"Phông chữ \"{font}\" không phải tên phông hợp lệ: đã dùng ＭＳ ゴシック như Jw_cad.");
            face = "ＭＳ ゴシック"; file = "msgothic.ttc"; proportional = false;
        }
        else
        {
            bool mincho = folded.Contains("明朝");
            proportional = folded.Contains("P") || folded.Contains("PRO");
            file = mincho ? "msmincho.ttc" : "msgothic.ttc"; // AutoCAD uses the face name below; the file is a fallback
            WarnOnce($"Phông chữ \"{font}\": AutoCAD dùng phông này nếu máy có cài, nếu không sẽ thay bằng phông khác.");
        }
        string name = Clean(face) + (bold ? "_B" : "") + (italic ? "_I" : "");
        if (_doc.TextStyles.TryGetValue(name, out var same)) { _styles[key] = same; return same; }
        st = new TextStyle(name) { Filename = file };
        _doc.TextStyles.Add(st);
        // AutoCAD picks a TrueType face (and its character set) from this ACAD xdata, not from the file
        // name alone: charset 128 = Shift-JIS, pitch/family, then the italic (0x02000000) and bold (0x01000000) flags.
        int pitchFamily = proportional ? 0x32 : 0x31;
        int flags = (italic ? 0x02000000 : 0) | (bold ? 0x01000000 : 0) | (128 << 8) | pitchFamily;
        st.ExtendedData.Add(AcadAppId(), new ExtendedData(new ExtendedDataRecord[]
        {
            new ExtendedDataString(face),
            new ExtendedDataInteger32(flags),
        }));
        _styles[key] = st;
        return st;
    }

    AppId AcadAppId()
    {
        if (_doc.AppIds.TryGetValue("ACAD", out var app)) return app;
        app = new AppId("ACAD");
        _doc.AppIds.Add(app);
        return app;
    }

    // Line patterns are paper lengths in Jw_cad, also inside scaled blocks: _ltK is the scale of the
    // blocks being built, which AutoCAD applies to the pattern too, so it is divided out.
    double _ltK = 1;

    void Apply(Entity ce, JwwEntity e, double s, int? penOverride = null)
    {
        ce.Layer = LayerOf(e);
        ce.Color = ColorOf(penOverride ?? e.PenColor);
        var lt = e is JwwSolid || e is JwwText ? null : LineTypeOf(e);
        if (lt != null) { ce.LineType = lt; ce.LineTypeScale = s / _ltK; }
        if (_st.Width(e) is int w) ce.LineWeight = (LineWeightType)JwwStyles.NearestWeight(w);
    }

    Color ColorOf(int pen)
    {
        var c = _st.PenColor(pen, out string? problem);
        if (problem != null) WarnOnce($"Màu: {problem}, đã dùng màu trắng/đen (số 7).");
        return c.Kind == JwwStyles.ColorKind.Index ? new Color(c.Index) : new Color((byte)c.R, (byte)c.G, (byte)c.B);
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
                foreach (var x in TextToCad(t, s)) yield return x;
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

    /// <summary>The text after embedded codes are replaced, as the verifier must expect it too.</summary>
    public static string VisibleText(JwwText t, JwEmbeddedText embed, out string? code) => embed.Apply(t.Text, out code);

    IEnumerable<Entity> TextToCad(JwwText t, double s)
    {
        if (string.IsNullOrEmpty(t.Text) || t.Height <= 0) { Report.Skip("Chữ rỗng"); yield break; }
        if (t.Text.StartsWith("^@BM", StringComparison.Ordinal))
        {
            var img = ImageToCad(t, s);
            if (img != null) { Report.Add("Ảnh"); yield return img; }
            yield break;
        }
        _embed.Scale = Scale(t);
        string text = VisibleText(t, _embed, out string? code);
        if (code != null) WarnOnce($"Chữ có mã nhúng (ví dụ {code}): đã thay bằng giá trị lúc chuyển đổi.");
        var lay = JwTextLayout.Build(t, text);
        foreach (var n in lay.Notes) WarnOnce($"Chữ: {n}.");
        double rot = t.AngleDeg * Math.PI / 180.0;
        double dx = Math.Cos(rot), dy = Math.Sin(rot), nx = -dy, ny = dx;
        XYZ At(double u, double v) => new((t.StartX + u * dx + v * nx) * s, (t.StartY + u * dy + v * ny) * s, 0);
        int type = (int)(t.TextType / 10000);
        bool italic = (type & 1) != 0, bold = (type & 2) != 0;

        // Vertical text made of upright characters only: one MTEXT, one character per line, so it stays
        // one object as in Jw_cad. AutoCAD puts the first baseline one text height below the top and the
        // next ones 5/3 × height × spacing factor apart (measured in AutoCAD 2026).
        var r0 = lay.Runs.FirstOrDefault();
        if (lay.Runs.Count > 1 && lay.Lines.Count == 0 && r0 != null && lay.Runs.All(r => r.Upright && r.V == 0 && r.CellW == r0.CellW && r.CellH == r0.CellH
                && r.Pen == r0.Pen && r.Bold == r0.Bold && r.Italic == r0.Italic && r.Font == r0.Font && r.Text.Length == 1))
        {
            double pitch = lay.Runs.Count > 1 ? lay.Runs[1].U - lay.Runs[0].U : r0.CellH;
            bool even = lay.Runs.Zip(lay.Runs.Skip(1)).All(z => Math.Abs(z.Second.U - z.First.U - pitch) < 1e-9);
            double H = r0.CellH * _opt.TextHeightFactor;
            double factor = pitch / (5.0 / 3.0 * H);
            if (even && factor >= 0.25 && factor <= 4)
            {
                double topU = r0.U - r0.CellH * _opt.TextBaselineShift - H; // first baseline minus one height
                var mt = new MText
                {
                    Value = MTextValue(lay.Runs.Select(r => r.Text), r0.CellW / r0.CellH),
                    InsertPoint = At(topU, 0),
                    Height = H * s,
                    AttachmentPoint = AttachmentPointType.TopLeft,
                    LineSpacingStyle = LineSpacingStyleType.Exact,
                    LineSpacing = factor,
                    AlignmentPoint = new XYZ(Math.Cos(rot + Math.PI / 2), Math.Sin(rot + Math.PI / 2), 0), // x axis direction
                    Style = StyleOf(r0.Font ?? t.Font, bold ^ r0.Bold, italic ^ r0.Italic),
                };
                Apply(mt, t, s, r0.Pen);
                Report.Add("Chữ");
                yield return mt;
                yield break;
            }
        }

        var parts = new List<Entity>();
        foreach (var r in lay.Runs)
        {
            double shift = r.CellH * _opt.TextBaselineShift;
            // Glyph "up" in the text frame: the column direction for normal glyphs, back along u for upright ones.
            var p = r.Upright ? At(r.U - shift, r.V) : At(r.U, r.V + shift);
            var txt = new TextEntity
            {
                Value = Escape(r.Text),
                InsertPoint = p,
                Height = r.CellH * s * _opt.TextHeightFactor,
                Rotation = Norm(rot + (r.Upright ? Math.PI / 2 : 0)),
                WidthFactor = r.CellW / r.CellH,
                Style = StyleOf(r.Font ?? t.Font, bold ^ r.Bold, italic ^ r.Italic),
            };
            Apply(txt, t, s, r.Pen);
            parts.Add(txt);
            yield return txt;
        }
        foreach (var d in lay.Lines)
        {
            var line = new Line(At(d.U1, d.V1), At(d.U2, d.V2));
            Apply(line, t, s, d.Pen);
            parts.Add(line);
            yield return line;
        }
        // One Jw_cad text drawn as several pieces (spacing, mixed sizes or colours): keep them together
        // as an AutoCAD group so they are picked as one.
        if (parts.Count > 1) _textGroups.Add(parts);
        Report.Add("Chữ");
    }

    readonly List<List<Entity>> _textGroups = new();

    void AddTextGroups()
    {
        int n = 0;
        foreach (var parts in _textGroups)
        {
            // Groups can only hold objects of the drawing itself, not of blocks.
            if (!parts.All(e => e.Owner is BlockRecord br && br.Name == BlockRecord.ModelSpaceName)) continue;
            var g = new ACadSharp.Objects.Group($"JW_CHU_{++n}") { Description = "Jw_cad text", Selectable = true };
            _doc.Groups.Add(g);
            g.AddRange(parts);
        }
    }

    /// <summary>MTEXT content: one entry per line, AutoCAD's \, {, } escaped, width factor in front.</summary>
    public static string MTextValue(IEnumerable<string> lines, double widthFactor)
    {
        static string Esc(string v) => v.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
        string w = Math.Abs(widthFactor - 1) < 1e-9 ? "" : $"\\W{widthFactor.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)};";
        return w + string.Join("\\P", lines.Select(l => Esc(Escape(l))));
    }

    /// <summary>AutoCAD reads %%c, %%d, %%p, %%u, %%o and %%nnn in TEXT; "%%%" is a literal %.</summary>
    public static string Escape(string v) => v.Contains("%%") ? v.Replace("%", "%%%") : v;

    /// <summary>
    /// Jw_cad images are texts "^@BM&lt;file&gt;,width,height[,...]" (paper mm; start point = lower left).
    /// "%temp%name" means the image is stored inside the JWW file.
    /// </summary>
    Entity? ImageToCad(JwwText t, double s)
    {
        var parts = t.Text[4..].Split(',');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length < 3 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out double w) ||
            !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out double h) || w <= 0 || h <= 0)
        {
            WarnOnce($"Ảnh \"{t.Text}\": không đọc được kích thước, đã bỏ qua.");
            return null;
        }
        string src = parts[0];
        byte[]? data = null;
        string fileName;
        if (src.StartsWith("%temp%", StringComparison.OrdinalIgnoreCase))
        {
            fileName = src[6..];
            _jww.Images.TryGetValue(fileName, out data);
            if (data == null) { WarnOnce($"Ảnh {fileName}: không thấy dữ liệu ảnh trong file JWW."); }
        }
        else
        {
            fileName = Path.GetFileName(src.Replace('\\', '/'));
            string? near = _opt.SourcePath == null ? null : Path.Combine(Path.GetDirectoryName(_opt.SourcePath) ?? "", fileName);
            if (File.Exists(src)) data = File.ReadAllBytes(src);
            else if (near != null && File.Exists(near)) data = File.ReadAllBytes(near);
            else WarnOnce($"Ảnh liên kết \"{src}\" không có trên máy này: AutoCAD sẽ báo thiếu ảnh cho đến khi chép ảnh vào cạnh file DWG.");
        }
        if (parts.Length > 6 && (parts[3] != "0" || parts[4] != "0" || parts[5] != "1" || parts[6] != "0"))
            WarnOnce($"Ảnh {fileName}: có cắt xén/xoay trong Jw_cad, bản DWG hiện cả ảnh.");
        if (!_images.TryGetValue(fileName, out var def))
        {
            string path = fileName;
            if (data != null && _opt.OutputPath != null)
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(_opt.OutputPath)) ?? ".";
                string folder = Path.GetFileNameWithoutExtension(_opt.OutputPath) + "_anh";
                Directory.CreateDirectory(Path.Combine(dir, folder));
                path = Path.Combine(folder, fileName);
                File.WriteAllBytes(Path.Combine(dir, path), data);
                Report.ImageFiles.Add(Path.Combine(dir, path));
            }
            else if (data == null) path = src;
            var (pw, ph) = data != null ? ImageSize(data) : (1000, (int)Math.Max(1, Math.Round(1000 * h / w)));
            def = new ACadSharp.Objects.ImageDefinition { Name = Clean(Path.GetFileNameWithoutExtension(fileName)), FileName = path, Size = new XY(pw, ph), IsLoaded = data != null };
            _doc.ImageDefinitions.Add(def);
            _images[fileName] = def;
        }
        double rot = t.AngleDeg * Math.PI / 180.0;
        var img = new RasterImage(def)
        {
            InsertPoint = new XYZ(t.StartX * s, t.StartY * s, 0),
            Size = def.Size,
            // One pixel's size and direction in drawing units.
            UVector = new XYZ(Math.Cos(rot) * w * s / def.Size.X, Math.Sin(rot) * w * s / def.Size.X, 0),
            VVector = new XYZ(-Math.Sin(rot) * h * s / def.Size.Y, Math.Cos(rot) * h * s / def.Size.Y, 0),
        };
        img.Flags |= ImageDisplayFlags.ShowImage;
        // AutoCAD needs a clip boundary even when the whole image is shown (pixel corners).
        img.ClipBoundaryVertices.Add(new XY(-0.5, -0.5));
        img.ClipBoundaryVertices.Add(new XY(def.Size.X - 0.5, def.Size.Y - 0.5));
        img.Layer = LayerOf(t);
        return img;
    }

    /// <summary>Pixel size of a BMP, PNG, GIF or JPEG image.</summary>
    public static (int W, int H) ImageSize(byte[] b)
    {
        try
        {
            if (b.Length > 26 && b[0] == 'B' && b[1] == 'M') return (BitConverter.ToInt32(b, 18), Math.Abs(BitConverter.ToInt32(b, 22)));
            if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P') return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
            if (b.Length > 10 && b[0] == 'G' && b[1] == 'I') return (BitConverter.ToUInt16(b, 6), BitConverter.ToUInt16(b, 8));
            if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
                for (int i = 2; i + 9 < b.Length;)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    int m = b[i + 1], len = (b[i + 2] << 8) | b[i + 3];
                    if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC) return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                    i += 2 + len;
                }
        }
        catch (Exception) { }
        return (1000, 1000);
    }

    IEnumerable<Entity> SolidToCad(JwwSolid so, double s)
    {
        var p = so.Points;
        if (so.PenStyle <= 100)
        {
            // Corners are stored in polygon order; AutoCAD SOLID wants 1, 2, 4, 3.
            XYZ P(int i) => new(p[i * 2] * s, p[i * 2 + 1] * s, 0);
            return new[] { new Solid { FirstCorner = P(0), SecondCorner = P(1), ThirdCorner = P(3), FourthCorner = P(2) } };
        }
        return CircleSolid(so, s);
    }

    /// <summary>
    /// Circle solids (CDataSolid with pen style 101 and up): centre, radius, flatness, tilt, start, sweep,
    /// then a shape flag (101: -1 outside of an arc, 0 sector, 5 segment, 100 full circle) or, for rings
    /// (105/106), the inner radius. Style 111 is a solid along the circumference.
    /// </summary>
    public static List<(XYZ A, XYZ B, XYZ C, XYZ D)> CircleSolidQuads(double[] p, int style, double s)
    {
        double cx = p[0], cy = p[1], r = p[2], flat = p[3] > 0 ? p[3] : 1, tilt = p[4], start = p[5], sweep = p[6], extra = p[7];
        bool full = Math.Abs(sweep) < 1e-12 || Math.Abs(Math.Abs(sweep) - 2 * Math.PI) < 1e-9 || (style == 101 && extra == 100);
        if (full) { start = 0; sweep = 2 * Math.PI; }
        int n = Math.Max(8, (int)Math.Ceiling(Math.Abs(sweep) / (2 * Math.PI) * 64));
        XYZ E(double a, double b, double t)
        {
            double x = a * Math.Cos(t), y = b * Math.Sin(t);
            return new XYZ((cx + x * Math.Cos(tilt) - y * Math.Sin(tilt)) * s, (cy + x * Math.Sin(tilt) + y * Math.Cos(tilt)) * s, 0);
        }
        XYZ Unit(double x, double y)
        {
            x *= r; y *= r * flat;
            return new XYZ((cx + x * Math.Cos(tilt) - y * Math.Sin(tilt)) * s, (cy + x * Math.Sin(tilt) + y * Math.Cos(tilt)) * s, 0);
        }
        var q = new List<(XYZ, XYZ, XYZ, XYZ)>();
        double T(int i) => start + sweep * i / n;
        if (style == 105 || style == 106)
        {
            double ri = extra, bi = style == 105 ? ri * flat : r * flat - (r - ri);
            for (int i = 0; i < n; i++) q.Add((E(r, r * flat, T(i)), E(r, r * flat, T(i + 1)), E(ri, Math.Max(bi, 0), T(i + 1)), E(ri, Math.Max(bi, 0), T(i))));
            return q;
        }
        XYZ hub;
        if (style == 101 && extra == 5 && !full) hub = E(r, r * flat, start);                        // segment: fan from the chord end
        else if (style == 101 && extra == -1 && !full)
        {
            // Outside of an arc (fillet corner): fan from where the end tangents meet.
            double m = start + sweep / 2, k = 1 / Math.Cos(sweep / 2);
            hub = Unit(Math.Cos(m) * k, Math.Sin(m) * k);
        }
        else hub = new XYZ(cx * s, cy * s, 0);                                                       // sector / full circle
        for (int i = 0; i < n; i++) { var a = E(r, r * flat, T(i)); var b = E(r, r * flat, T(i + 1)); q.Add((hub, a, b, b)); }
        return q;
    }

    IEnumerable<Entity> CircleSolid(JwwSolid so, double s)
    {
        int style = so.PenStyle;
        if (style == 111)
        {
            // Solid along the circumference: drawn as the arc itself.
            var p = so.Points;
            WarnOnce("Tô đặc theo đường tròn (kiểu 111): đã vẽ bằng cung/đường tròn.");
            var arc = new JwwArc { CX = p[0], CY = p[1], Radius = p[2], Flatness = p[3] > 0 ? p[3] : 1, Tilt = p[4], StartAngle = p[5], SweepAngle = p[6], IsFullCircle = p[7] == 100 || Math.Abs(p[6]) < 1e-12 };
            return new[] { ArcToCad(arc, s) };
        }
        if (style != 101 && style != 105 && style != 106)
            WarnOnce($"Tô đặc kiểu {style} chưa có trong đặc tả Jw_cad: đã tô hình quạt.");
        else if (style == 101 && so.Points[7] is not (-1 or 0 or 5 or 100))
            WarnOnce($"Tô đặc tròn có cờ {so.Points[7]} lạ: đã tô hình quạt.");
        return CircleSolidQuads(so.Points, style, s)
            .Select(t => (Entity)new Solid { FirstCorner = t.A, SecondCorner = t.B, ThirdCorner = t.D, FourthCorner = t.C }).ToList();
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
        foreach (var x in TextToCad(d.Text, s)) yield return x;
        foreach (var p in d.Points)
            if (p.PenStyle == 100) foreach (var m in MarkerToCad(p, s)) yield return m;
    }

    /// <summary>
    /// Point marks (CDataTen with pen style 100). Jw_cad stores SXF symbols here:
    /// positive codes are SXF arrow codes 1-11 (dimension and leader ends), negative codes are
    /// SXF point markers 1-7. Sizes follow the SXF shapes: base size 1.25 × Scale paper mm,
    /// arrows 7.5 × Scale long with the tip on the point and the body along the rotation angle.
    /// Checked against Jw_cad screens: code 7 dot and code -2 circle both have radius 1.25 × Scale.
    /// </summary>
    IEnumerable<Entity> MarkerToCad(JwwPoint p, double s)
    {
        int code = (int)p.Code;
        double k = (p.Scale > 0 ? p.Scale : 1.0) * s; // 1 SXF unit in drawing units
        double cx = p.X * s, cy = p.Y * s;
        XYZ At(double u, double v, double rotExtra = 0)
        {
            double c2 = Math.Cos(p.Angle + rotExtra), s2 = Math.Sin(p.Angle + rotExtra);
            return new XYZ(cx + (u * c2 - v * s2) * k, cy + (u * s2 + v * c2) * k, 0);
        }
        var centre = new XYZ(cx, cy, 0);
        var list = new List<Entity>();
        void L(XYZ a, XYZ b) => list.Add(new Line(a, b));
        void Poly(params XYZ[] pts) { for (int i = 0; i < pts.Length; i++) L(pts[i], pts[(i + 1) % pts.Length]); }
        void Tri(XYZ a, XYZ b, XYZ c) => list.Add(new Solid { FirstCorner = a, SecondCorner = b, ThirdCorner = c, FourthCorner = c });
        void Quad(XYZ a, XYZ b, XYZ c, XYZ d) => list.Add(new Solid { FirstCorner = a, SecondCorner = b, ThirdCorner = d, FourthCorner = c });
        XYZ A0 = At(0, 0), W1 = At(7.5, 1.25), W2 = At(7.5, -1.25);
        XYZ B1 = At(-1.25, -1.25), B2 = At(1.25, -1.25), B3 = At(1.25, 1.25), B4 = At(-1.25, 1.25);
        string name;
        switch (code)
        {
            // ----- SXF arrow codes -----
            case 1: name = "mũi tên rỗng"; Poly(A0, W1, W2); break;
            case 2: name = "ô vuông rỗng"; Poly(B1, B2, B3, B4); break;
            case 3: name = "chấm rỗng"; list.Add(new Circle { Center = centre, Radius = 1.25 * k }); break;
            case 4: name = "gốc kích thước"; list.Add(new Circle { Center = centre, Radius = 1.25 * k }); break;
            case 5: name = "ô vuông đặc"; Quad(B1, B2, B3, B4); break;
            case 6: name = "mũi tên đặc"; Tri(A0, W1, W2); break;
            case 7: name = "chấm đặc"; list.AddRange(FilledCircle(centre, 1.25 * k)); break;
            case 8:
                name = "dấu tích phân";
                {
                    double h = 1.25; // two quarter arcs meeting at the point: (-h,-h) → (0,0) → (h,h)
                    var pts = new List<XYZ>();
                    for (int i = 0; i <= 12; i++) { double t = -Math.PI / 2 + Math.PI / 2 * i / 12; pts.Add(At(-h + h * Math.Cos(t), h * Math.Sin(t))); }
                    for (int i = 1; i <= 12; i++) { double t = Math.PI - Math.PI / 2 * i / 12; pts.Add(At(h + h * Math.Cos(t), h * Math.Sin(t))); }
                    for (int i = 0; i + 1 < pts.Count; i++) L(pts[i], pts[i + 1]);
                }
                break;
            case 9: name = "mũi tên mở"; L(A0, W1); L(A0, W2); break;
            case 10: name = "gạch chéo"; L(At(-1.25, -1.25), At(1.25, 1.25)); break;
            case 11: name = "mũi tên viền"; Poly(A0, W1, W2); break;
            // ----- SXF point markers (stored negative) -----
            case -1:
                name = "dấu sao";
                for (int i = 0; i < 3; i++) L(At(0, -1.25, i * Math.PI / 3), At(0, 1.25, i * Math.PI / 3));
                break;
            case -2: name = "vòng tròn"; list.Add(new Circle { Center = centre, Radius = 1.25 * k }); break;
            case -3: name = "chấm"; list.AddRange(FilledCircle(centre, 0.25 * s)); break; // fixed small dot
            case -4: name = "dấu cộng"; L(At(-1.25, 0), At(1.25, 0)); L(At(0, -1.25), At(0, 1.25)); break;
            case -5: name = "ô vuông"; Poly(B1, B2, B3, B4); break;
            case -6:
                name = "tam giác";
                {
                    double R = 5.0 / 3.0;
                    XYZ V(double deg) => At(R * Math.Cos(deg * Math.PI / 180), R * Math.Sin(deg * Math.PI / 180));
                    Poly(V(90), V(210), V(330));
                }
                break;
            case -7: name = "dấu nhân"; L(At(-1.25, -1.25), At(1.25, 1.25)); L(At(-1.25, 1.25), At(1.25, -1.25)); break;
            default:
                // Not an SXF code: still mark the spot so nothing disappears, and say so.
                name = $"mã {code} (vẽ tạm)";
                list.Add(new Circle { Center = centre, Radius = 1.25 * k });
                if (_unknownCodes.Add(code))
                    Report.Warnings.Add($"Ký hiệu điểm mã {code} không có trong bảng SXF: đã vẽ tạm bằng vòng tròn nhỏ, cần kiểm tra lại.");
                break;
        }
        Report.Add($"Ký hiệu: {name}");
        foreach (var e in list) { Apply(e, p, s); yield return e; }
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
        double k = _ltK * Math.Sqrt(Math.Abs(bi.ScaleX * bi.ScaleY));
        if (k < 1e-12) k = 1;
        var block = BlockFor(bi.BlockNumber, s, depth, k);
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

    // One AutoCAD block per Jw_cad block, drawing scale and pattern scale (see _ltK).
    readonly Dictionary<(uint num, double scale, double k), BlockRecord> _blocksK = new();

    BlockRecord? BlockFor(uint num, double s, int depth, double ltK)
    {
        ltK = Math.Round(ltK, 9);
        if (_blocksK.TryGetValue((num, s, ltK), out var br)) return br;
        if (!_blockDefs.TryGetValue(num, out var def) || depth > 16) return null;
        string baseName = Clean(System.Text.RegularExpressions.Regex.Replace(def.Name, "@@SfigorgFlag@@\\d*", ""));
        if (baseName.Length == 0) baseName = $"JW_BLOCK_{num}";
        string name = baseName;
        int n = 1;
        while (_doc.BlockRecords.Contains(name)) name = $"{baseName}_{++n}";
        br = new BlockRecord(name);
        _blocksK[(num, s, ltK)] = br;
        _doc.BlockRecords.Add(br);
        double saved = _ltK;
        _ltK = ltK;
        try
        {
            var list = new List<Entity>();
            foreach (var e in def.Entities) list.AddRange(ToCad(e, s, depth + 1));
            AddInDrawOrder(br.Entities, list);
        }
        finally { _ltK = saved; }
        return br;
    }
}
