using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using JwwToCad.Core.Convert;
using JwwToCad.Core.Jww;

/// <summary>
/// Compares a converted DWG/DXF with its JWW source. Every JWW object is placed on the sheet
/// (paper mm × main scale) and looked up among the drawing's objects after all blocks are expanded.
/// Geometry, colour, line type, line width, text and images are checked against the JWW data and the
/// settings stored in the JWW file, not against what the converter decided.
/// </summary>
static class Verify
{
    // Attributes as seen in the drawing: colour as RGB (index colours resolved), pattern in world units.
    record Attr(int Rgb, bool Index7, double[] Pattern, int Weight, bool Vis);
    record Seg(double X1, double Y1, double X2, double Y2, Attr A);
    record Curve(double X, double Y, double R1, double R2, bool Full, (double X, double Y) P1, (double X, double Y) P2, Attr A);
    record Txt(double X, double Y, double H, double Rot, double Wf, string S, Attr A, bool Bold, bool Italic, string Face);
    record Sol(double X, double Y, Attr A);
    record Img(double X, double Y, double W, double H, double Rot, string File);

    sealed class Flat
    {
        public List<Seg> Segs = new();
        public List<Curve> Curves = new();
        public List<Txt> Txts = new();
        public List<Sol> Sols = new();
        public List<(double X, double Y)> Pts = new();     // line ends and solid corners
        public List<(double X, double Y)> SolPts = new();  // solid corners only
        public List<Img> Imgs = new();
        public HashSet<string> LayersOff = new();
    }

    // 2D affine transform: x' = a*x + b*y + e, y' = c*x + d*y + f
    readonly record struct T(double A, double B, double C, double D, double E, double F)
    {
        public static readonly T Id = new(1, 0, 0, 1, 0, 0);
        public (double, double) Ap(double x, double y) => (A * x + B * y + E, C * x + D * y + F);
        public T Then(T o) => new(o.A * A + o.B * C, o.A * B + o.B * D, o.C * A + o.D * C, o.C * B + o.D * D,
                                  o.A * E + o.B * F + o.E, o.C * E + o.D * F + o.F);
        public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));
        public double Rot => Math.Atan2(C, A);
        public static T Place(double x, double y, double sx, double sy, double rot)
        {
            double c = Math.Cos(rot), s = Math.Sin(rot);
            return new(c * sx, -s * sy, s * sx, c * sy, x, y);
        }
    }

    public static int Run(string jwwPath, string dwgPath, bool paper, bool quiet = false)
    {
        var jww = JwwReader.Read(jwwPath);
        CadDocument doc = dwgPath.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)
            ? DxfReader.Read(dwgPath) : DwgReader.Read(dwgPath);
        return Run(jww, doc, Path.GetFileName(jwwPath), paper, quiet, jwwPath, dwgPath);
    }

    static readonly (int R, int G, int B)[] Aci = JwwStyles.AciRgb;
    static int Rgb(int r, int g, int b) => (r << 16) | (g << 8) | b;

    public static int Run(JwwDocument jww, CadDocument doc, string title, bool paper, bool quiet = false,
                          string? jwwPath = null, string? dwgPath = null)
    {
        double GS(JwwEntity e) => Math.Max(jww.Groups[e.LayerGroup & 15].Scale, 1e-9);
        double main = paper ? 1 : jww.Entities.GroupBy(GS).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(1.0);
        var h = jww.Header;

        var flat = new Flat();
        foreach (var l in doc.Layers) if (!l.IsOn) flat.LayersOff.Add(l.Name);
        foreach (var e in doc.Entities) FlattenCad(e, T.Id, flat, 0, null, null, true);

        var segIdx = new Grid<Seg>(s => ((s.X1 + s.X2) / 2, (s.Y1 + s.Y2) / 2), flat.Segs, main);
        var curIdx = new Grid<Curve>(c => (c.X, c.Y), flat.Curves, main);
        var txtIdx = new Grid<Txt>(t => (t.X, t.Y), flat.Txts, main);
        var solIdx = new Grid<Sol>(s => (s.X, s.Y), flat.Sols, main);
        var ptIdx = new Grid<(double X, double Y)>(q => (q.X, q.Y), flat.Pts, main);
        var solPtIdx = new Grid<(double X, double Y)>(q => (q.X, q.Y), flat.SolPts, main);

        double tol = 0.02 * main; // 0.02 mm on paper
        var stats = new SortedDictionary<string, (int ok, int bad)>();
        var samples = new List<string>();
        void Count(string kind, bool ok, string? why = null)
        {
            var (o, b) = stats.GetValueOrDefault(kind);
            stats[kind] = ok ? (o + 1, b) : (o, b + 1);
            if (!ok && why != null && samples.Count(s => s.StartsWith(kind + ":")) < 5) samples.Add($"{kind}: {why}");
        }
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        var embed = new JwEmbeddedText { FilePath = jwwPath, Memo = jww.Memo ?? "" };
        if (jwwPath != null && File.Exists(jwwPath)) embed.Saved = File.GetLastWriteTime(jwwPath);

        // ---- expected attributes, straight from the JWW data and its stored settings ----

        // Colour: standard pens 1-9 use Jw_cad's colour numbers (1 cyan, 2 white/black, 3 green, 4 yellow,
        // 5 magenta, 6 blue, 7 white/black, 8 red, 9 grey); SXF pens use the RGB stored in the file.
        bool ColorOk(int pen, Attr a, out string want)
        {
            if (pen >= 1 && pen <= 9)
            {
                int[] want7 = { 0, 4, 7, 3, 2, 6, 5, 7, 1, 8 };
                var c = Aci[want7[pen]];
                want = $"màu Jw số {pen}";
                return want7[pen] == 7 ? a.Index7 : a.Rgb == Rgb(c.R, c.G, c.B) && !a.Index7;
            }
            if (pen >= 100 && pen <= 356 && h != null && h.ScreenColor.TryGetValue(pen, out uint cr))
            {
                int r = (int)(cr & 0xFF), g = (int)((cr >> 8) & 0xFF), b = (int)((cr >> 16) & 0xFF);
                want = $"RGB {r},{g},{b} (màu SXF {pen})";
                if ((r, g, b) == (0, 0, 0) || (r, g, b) == (255, 255, 255)) return a.Index7;
                return a.Rgb == Rgb(r, g, b);
            }
            want = "màu số 7";
            return a.Index7;
        }

        // Line type, in paper mm: SXF types from their stored mm lengths; Jw_cad types from their bit
        // pattern (period = unit dots × printer pitch × 1/32 mm, ink = share of 1 bits).
        bool PatternOk(int style, Attr a, out string want)
        {
            double[] got = a.Pattern.Select(x => x / main).ToArray();
            double period = got.Sum(Math.Abs), ink = got.Where(x => x > 0).Sum();
            want = "nét liền";
            bool continuous = got.Length == 0;
            if (style <= 1 || style == 100 || (style >= 11 && style <= 15)) return continuous;
            if (h == null) return true; // no stored settings: nothing independent to compare with
            if (style >= 30 && style <= 62 && h.SxfLineTypes.TryGetValue(style, out var sx) && sx.Pitch.Length >= 2 && sx.Pitch.Any(p => p > 0))
            {
                want = $"SXF '{sx.Name}' [{string.Join(",", sx.Pitch.Select(F))}] mm";
                if (got.Length != sx.Pitch.Length) return false;
                for (int i = 0; i < got.Length; i++) if (Math.Abs(Math.Abs(got[i]) - sx.Pitch[i]) > 1e-6) return false;
                return true;
            }
            if (style >= 30 && style <= 62 && h.SxfLineTypes.TryGetValue(style, out var sx2) && sx2.Name.Trim() == "continuous") return continuous;
            if (!h.LineTypes.TryGetValue(style, out var lt) || lt.UnitDots <= 0) return continuous;
            int unit = Math.Clamp(lt.UnitDots, 1, 32), ones = 0;
            for (int i = 0; i < unit; i++) if (((lt.Bits >> (31 - i)) & 1) != 0) ones++;
            if (ones == 0 || ones == unit) return continuous;
            double bit = Math.Max(lt.PrintPitch, 1) / 32.0;
            want = $"chu kỳ {F(unit * bit)} mm, nét mực {F(ones * bit)} mm";
            return Math.Abs(period - unit * bit) < 1e-6 && Math.Abs(ink - ones * bit) < 1e-6;
        }

        // Line width: an object's own width (1/100 mm), else the SXF pen's print width, else none.
        bool WeightOk(JwwEntity e, Attr a, out string want)
        {
            int? w = e.PenWidth > 0 ? e.PenWidth : null;
            if (w == null && h != null && h.PrintWidth.TryGetValue(e.PenColor, out int pw) && pw > 0 && (e.PenColor >= 100 || h.MaxDrawWidth == -101)) w = pw;
            if (w == null) { want = "mặc định"; return a.Weight < 0; }
            int[] std = { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };
            int near = std.OrderBy(x => Math.Abs(x - w.Value)).First();
            want = $"{F(near / 100.0)} mm";
            return a.Weight == near;
        }

        // Pick the candidate whose attributes match; report which attribute is off when none does.
        void CheckAttrs<TItem>(string kind, JwwEntity e, List<TItem> cands, Func<TItem, Attr> attr, string where, bool lineLike)
        {
            if (cands.Count == 0) return;
            bool all(TItem c) { var a = attr(c); return ColorOk(e.PenColor, a, out _) && (!lineLike || (PatternOk(e.PenStyle, a, out _) && WeightOk(e, a, out _))); }
            if (cands.Any(all)) { Count("Màu", true); if (lineLike) { Count("Kiểu nét", true); Count("Độ dày nét", true); } return; }
            var a0 = attr(cands[0]);
            Count("Màu", ColorOk(e.PenColor, a0, out var wc), $"{kind} {where}: cần {wc}");
            if (lineLike)
            {
                Count("Kiểu nét", PatternOk(e.PenStyle, a0, out var wp), $"{kind} {where} (nét số {e.PenStyle}): cần {wp}, có [{string.Join(",", a0.Pattern.Select(x => F(x / main)))}]");
                Count("Độ dày nét", WeightOk(e, a0, out var ww), $"{kind} {where}: cần {ww}, có {(a0.Weight < 0 ? "mặc định" : F(a0.Weight / 100.0) + " mm")}");
            }
        }

        var blocks = jww.Blocks.ToDictionary(b => b.Number);

        (double, double) ArcPoint(JwwArc a, double t)
        {
            if (Math.Abs(a.Flatness - 1) < 1e-9) return (a.CX + a.Radius * Math.Cos(t + a.Tilt), a.CY + a.Radius * Math.Sin(t + a.Tilt));
            double x = a.Radius * Math.Cos(t), y = a.Radius * a.Flatness * Math.Sin(t);
            return (a.CX + x * Math.Cos(a.Tilt) - y * Math.Sin(a.Tilt), a.CY + x * Math.Sin(a.Tilt) + y * Math.Cos(a.Tilt));
        }

        // Jw_cad shows an object when its layer and every enclosing block's layer are shown.
        bool On(JwwEntity x) => jww.Groups[x.LayerGroup & 15].State != 0 && jww.Groups[x.LayerGroup & 15].LayerState[x.Layer & 15] != 0;
        bool chainVis = true;
        void VisCheck(JwwEntity e, IEnumerable<Attr> found, string where)
        {
            var l = found.ToList();
            if (l.Count == 0) return;
            bool want = chainVis && On(e);
            Count("Ẩn/hiện", l.Any(a => a.Vis == want), $"{where}: Jw_cad {(want ? "hiện" : "ẩn")}, AutoCAD {(want ? "ẩn" : "hiện")}");
        }

        void Check(JwwEntity e, T t, int depth)
        {
            switch (e)
            {
                case JwwLine l:
                {
                    if (Math.Abs(l.X1 - l.X2) < 1e-9 && Math.Abs(l.Y1 - l.Y2) < 1e-9) { Count("Đường dài 0", true); break; }
                    var (x1, y1) = t.Ap(l.X1, l.Y1); var (x2, y2) = t.Ap(l.X2, l.Y2);
                    var c = segIdx.Near((x1 + x2) / 2, (y1 + y2) / 2, tol).Where(s =>
                        (D(s.X1, s.Y1, x1, y1) < tol && D(s.X2, s.Y2, x2, y2) < tol) ||
                        (D(s.X1, s.Y1, x2, y2) < tol && D(s.X2, s.Y2, x1, y1) < tol)).ToList();
                    Count("Đường", c.Count > 0, $"({F(x1)},{F(y1)})-({F(x2)},{F(y2)}) không có trong DWG");
                    CheckAttrs("đường", e, c, s => s.A, $"({F(x1)},{F(y1)})", true);
                    VisCheck(e, c.Select(k => k.A), $"đường ({F(x1)},{F(y1)})");
                    break;
                }
                case JwwArc a:
                {
                    var (cx, cy) = t.Ap(a.CX, a.CY);
                    bool ell = Math.Abs(a.Flatness - 1) > 1e-9;
                    double r1 = a.Radius * t.Scale, r2 = a.Radius * a.Flatness * t.Scale;
                    var p1 = t.Ap(ArcPoint(a, a.StartAngle).Item1, ArcPoint(a, a.StartAngle).Item2);
                    var p2 = t.Ap(ArcPoint(a, a.StartAngle + a.SweepAngle).Item1, ArcPoint(a, a.StartAngle + a.SweepAngle).Item2);
                    var c = curIdx.Near(cx, cy, tol).Where(k =>
                    {
                        bool radii = (Math.Abs(k.R1 - Math.Max(r1, r2)) < tol && Math.Abs(k.R2 - Math.Min(r1, r2)) < tol);
                        if (!radii || k.Full != a.IsFullCircle) return false;
                        if (a.IsFullCircle) return true;
                        return (D(k.P1.X, k.P1.Y, p1.Item1, p1.Item2) < tol && D(k.P2.X, k.P2.Y, p2.Item1, p2.Item2) < tol) ||
                               (D(k.P1.X, k.P1.Y, p2.Item1, p2.Item2) < tol && D(k.P2.X, k.P2.Y, p1.Item1, p1.Item2) < tol);
                    }).ToList();
                    string kind = ell ? (a.IsFullCircle ? "Elip" : "Cung elip") : (a.IsFullCircle ? "Đường tròn" : "Cung");
                    Count(kind, c.Count > 0, $"tâm ({F(cx)},{F(cy)}) r={F(r1)}" + (a.IsFullCircle ? "" : $", đầu ({F(p1.Item1)},{F(p1.Item2)}) cuối ({F(p2.Item1)},{F(p2.Item2)})"));
                    CheckAttrs(kind.ToLowerInvariant(), e, c, k => k.A, $"tâm ({F(cx)},{F(cy)})", true);
                    VisCheck(e, c.Select(k => k.A), $"{kind} tâm ({F(cx)},{F(cy)})");
                    break;
                }
                case JwwText tx: CheckText(tx, t, "Chữ"); break;
                case JwwSolid so: CheckSolid(so, t); break;
                case JwwDimension d:
                {
                    Check(d.Line, t, depth);
                    if (d.Ext1 != null) Check(d.Ext1, t, depth);
                    if (d.Ext2 != null) Check(d.Ext2, t, depth);
                    if (!string.IsNullOrEmpty(d.Text.Text)) CheckText(d.Text, t, "Chữ kích thước");
                    foreach (var p in d.Points.Where(p => p.PenStyle == 100)) CheckMarker(p, t);
                    break;
                }
                case JwwPoint pt:
                    if (pt.PenStyle == 100) CheckMarker(pt, t);
                    else if (!pt.IsTemporary) Count("Điểm", true);
                    break;
                case JwwBlockInsert bi:
                {
                    if (!blocks.TryGetValue(bi.BlockNumber, out var def) || depth > 16) { Count("Block", false, $"thiếu định nghĩa #{bi.BlockNumber}"); break; }
                    var bt = T.Place(bi.X, bi.Y, bi.ScaleX, bi.ScaleY, bi.AngleRad).Then(t);
                    bool savedVis = chainVis;
                    chainVis = chainVis && On(bi);
                    foreach (var be in def.Entities) Check(be, bt, depth + 1);
                    chainVis = savedVis;
                    Count("Block", true);
                    break;
                }
            }
        }

        void CheckSolid(JwwSolid so, T t)
        {
            var p = so.Points;
            if (so.PenStyle <= 100)
            {
                var (x, y) = t.Ap((p[0] + p[2] + p[4] + p[6]) / 4, (p[1] + p[3] + p[5] + p[7]) / 4);
                bool corners = Enumerable.Range(0, 4).All(i => { var (a, b) = t.Ap(p[2 * i], p[2 * i + 1]); return solPtIdx.Near(a, b, tol).Any(); });
                var c = solIdx.Near(x, y, tol).ToList();
                Count("Tô đặc", c.Count > 0 && corners, $"tâm ({F(x)},{F(y)})");
                VisCheck(so, c.Select(k => k.A), $"tô đặc ({F(x)},{F(y)})");
                CheckSolidColor(so, c, $"({F(x)},{F(y)})");
                return;
            }
            // Circle solids: points on the arc must be solid corners, and the fill's hub must be right:
            // centre for a sector or full circle, the chord end for a segment, the tangent corner for
            // the outside of an arc, no centre at all for a ring.
            double cx = p[0], cy = p[1], r = p[2], fl = p[3] > 0 ? p[3] : 1, tilt = p[4], st = p[5], sw = p[6], ex = p[7];
            bool full = Math.Abs(sw) < 1e-12 || Math.Abs(Math.Abs(sw) - 2 * Math.PI) < 1e-9 || (so.PenStyle == 101 && ex == 100);
            if (full) { st = 0; sw = 2 * Math.PI; }
            (double, double) E(double a, double b, double tt)
            {
                double x = a * Math.Cos(tt), y = b * Math.Sin(tt);
                return t.Ap(cx + x * Math.Cos(tilt) - y * Math.Sin(tilt), cy + x * Math.Sin(tilt) + y * Math.Cos(tilt));
            }
            if (so.PenStyle == 111) { Count("Tô đặc theo đường tròn", curIdx.Near(t.Ap(cx, cy).Item1, t.Ap(cx, cy).Item2, tol).Any(), $"tâm ({F(cx)},{F(cy)})"); return; }
            var must = new List<(double, double)> { E(r, r * fl, st), E(r, r * fl, st + sw) };
            var mustNot = new List<(double, double)>();
            var centre = t.Ap(cx, cy);
            string shape;
            if (so.PenStyle is 105 or 106)
            {
                double bi = so.PenStyle == 105 ? ex * fl : r * fl - (r - ex);
                must.Add(E(ex, bi, st)); must.Add(E(ex, bi, st + sw));
                mustNot.Add(centre); shape = "vành khuyên";
            }
            else if (full || ex == 0) { must.Add(centre); shape = full ? "tròn đặc" : "hình quạt"; }
            else if (ex == 5) { mustNot.Add(centre); shape = "viên phân"; }
            else if (ex == -1)
            {
                double m = st + sw / 2, k = 1 / Math.Cos(sw / 2);
                must.Add(E(r * k, r * fl * k, m)); mustNot.Add(centre); shape = "phần ngoài cung";
            }
            else { must.Add(centre); shape = $"cờ {ex}"; }
            string? why = null;
            foreach (var (x, y) in must) if (!solPtIdx.Near(x, y, tol).Any()) { why = $"{shape} tâm ({F(centre.Item1)},{F(centre.Item2)}): thiếu điểm ({F(x)},{F(y)})"; break; }
            if (why == null)
                foreach (var (x, y) in mustNot)
                    if (D(x, y, must[0].Item1, must[0].Item2) > tol && solPtIdx.Near(x, y, tol).Any()) { why = $"{shape} tâm ({F(centre.Item1)},{F(centre.Item2)}): tô sai hình (có tô vào tâm)"; break; }
            Count($"Tô đặc {shape}", why == null, why);
            CheckSolidColor(so, solIdx.Near(must[0].Item1, must[0].Item2, r * t.Scale * 2 + tol).ToList(), $"({F(centre.Item1)},{F(centre.Item2)})");
        }

        void CheckSolidColor(JwwSolid so, List<Sol> c, string where)
        {
            if (c.Count == 0) return;
            if (so.Rgb is uint rgb)
            {
                int want = Rgb((int)(rgb & 0xFF), (int)((rgb >> 8) & 0xFF), (int)((rgb >> 16) & 0xFF));
                Count("Màu", c.Any(k => k.A.Rgb == want && !k.A.Index7), $"tô đặc {where}: cần RGB {want:X6}");
            }
            else CheckAttrs("tô đặc", so, c, k => k.A, where, false);
        }

        void CheckText(JwwText tx, T t, string kind)
        {
            if (string.IsNullOrEmpty(tx.Text) || tx.Height <= 0) return;
            if (tx.Text.StartsWith("^@BM", StringComparison.Ordinal)) { CheckImage(tx, t); return; }
            embed.Scale = GS(tx);
            string text = embed.Apply(tx.Text, out string? code);
            var lay = JwTextLayout.Build(tx, text);
            // Independent check of the character layout: Jw_cad stores where the text ends.
            double stored = D(tx.StartX, tx.StartY, tx.EndX, tx.EndY);
            if (stored > 1e-6 && code == null && !lay.UsedControlCodes)
            {
                bool okLen = Math.Abs(lay.Length - stored) <= Math.Max(0.1, stored * 0.003); // 0.1 mm on paper: below what a print shows
                Count("Độ dài dòng chữ", okLen, $"'{tx.Text}' dài {F(lay.Length)} mm, Jw_cad {F(stored)} mm");
            }
            double rot = tx.AngleDeg * Math.PI / 180;
            double dx = Math.Cos(rot), dy = Math.Sin(rot);
            int type = (int)(tx.TextType / 10000);
            foreach (var r in lay.Runs)
            {
                double shift = r.CellH * 0.12;
                double u = r.Upright ? r.U - shift : r.U, v = r.Upright ? r.V : r.V + shift;
                var (x, y) = t.Ap(tx.StartX + u * dx - v * dy, tx.StartY + u * dy + v * dx);
                double cell = r.CellH * t.Scale;
                double grot = rot + (r.Upright ? Math.PI / 2 : 0) + t.Rot;
                string want = JwwToCadConverter.Escape(r.Text);
                var cands = txtIdx.Near(x, y, cell * 0.05 + tol).Where(c => c.S == want).ToList();
                if (cands.Count == 0) { Count(kind, false, $"'{r.Text}' (của '{tx.Text}') tại ({F(x)},{F(y)}) không có trong DWG"); continue; }
                var c = cands.OrderBy(c => D(c.X, c.Y, x, y)).First();
                VisCheck(tx, cands.Select(k => k.A), $"chữ '{r.Text}'");
                double hr = c.H / cell;
                double dr = Math.Abs(Math.IEEERemainder(c.Rot - grot, 2 * Math.PI));
                double wf = r.CellW / r.CellH;
                string? why = null;
                if (hr < 0.78 || hr > 0.82) why = $"'{r.Text}' cao {F(c.H)}, cần {F(cell * 0.8)}";
                else if (dr > 1e-3) why = $"'{r.Text}' góc lệch {F(dr * 180 / Math.PI)}°";
                else if (Math.Abs(c.Wf - wf) > 1e-3) why = $"'{r.Text}' hệ số rộng {F(c.Wf)}, cần {F(wf)}";
                Count(kind, why == null, why);
                bool italic = ((type & 1) != 0) ^ r.Italic, bold = ((type & 2) != 0) ^ r.Bold;
                Count("Chữ đậm/nghiêng", c.Bold == bold && c.Italic == italic, $"'{r.Text}': cần {(bold ? "đậm" : "thường")}/{(italic ? "nghiêng" : "đứng")}");
                if (!ColorOk(r.Pen ?? tx.PenColor, c.A, out var wc)) Count("Màu", false, $"chữ '{r.Text}': cần {wc}"); else Count("Màu", true);
            }
        }

        void CheckImage(JwwText tx, T t)
        {
            var parts = tx.Text[4..].Split(',');
            if (parts.Length < 3 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double hh)) { Count("Ảnh", false, $"không đọc được '{tx.Text}'"); return; }
            var (x, y) = t.Ap(tx.StartX, tx.StartY);
            var img = flat.Imgs.FirstOrDefault(i => D(i.X, i.Y, x, y) < tol && Math.Abs(i.W - w * t.Scale) < tol && Math.Abs(i.H - hh * t.Scale) < tol);
            if (img == null) { Count("Ảnh", false, $"không có ảnh {F(w)}×{F(hh)} mm tại ({F(x)},{F(y)})"); return; }
            bool embedded = parts[0].StartsWith("%temp%", StringComparison.OrdinalIgnoreCase);
            if (embedded && dwgPath != null)
            {
                string file = Path.IsPathRooted(img.File) ? img.File : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dwgPath)) ?? "", img.File);
                Count("Ảnh", File.Exists(file), $"thiếu file ảnh {img.File} cạnh DWG");
            }
            else Count("Ảnh", true);
        }

        // SXF symbol table (base size 1.25 × Scale paper mm; arrows 7.5 long, tip on the point):
        // the corners each symbol must have, in symbol units, plus circles (radius) and fills.
        void CheckMarker(JwwPoint p, T t)
        {
            int code = (int)p.Code;
            double k = (p.Scale > 0 ? p.Scale : 1.0) * t.Scale;
            double ang = p.Angle + t.Rot;
            var (ox, oy) = t.Ap(p.X, p.Y);
            (double, double) At(double u, double v, double extra = 0)
            {
                double c = Math.Cos(ang + extra), s = Math.Sin(ang + extra);
                return (ox + (u * c - v * s) * k, oy + (u * s + v * c) * k);
            }
            var corners = new List<(double, double)>();
            double circle = 0; bool filled = false;
            var arrow = new[] { At(0, 0), At(7.5, 1.25), At(7.5, -1.25) };
            var box = new[] { At(-1.25, -1.25), At(1.25, -1.25), At(1.25, 1.25), At(-1.25, 1.25) };
            switch (code)
            {
                case 1 or 6 or 11: corners.AddRange(arrow); filled = code == 6; break;
                case 9: corners.AddRange(arrow); break;
                case 2 or 5 or -5: corners.AddRange(box); filled = code == 5; break;
                case 3 or 4 or -2: circle = 1.25 * k; break;
                case 7: filled = true; corners.Add(At(1.25, 0, -ang)); break; // dot: a point on its rim
                case 8: corners.Add(At(-1.25, -1.25)); corners.Add(At(0, 0)); corners.Add(At(1.25, 1.25)); break;
                case 10: corners.Add(At(-1.25, -1.25)); corners.Add(At(1.25, 1.25)); break;
                case -1: for (int i = 0; i < 3; i++) { corners.Add(At(0, 1.25, i * Math.PI / 3)); corners.Add(At(0, -1.25, i * Math.PI / 3)); } break;
                case -3: filled = true; break;
                case -4: corners.AddRange(new[] { At(-1.25, 0), At(1.25, 0), At(0, -1.25), At(0, 1.25) }); break;
                case -6: foreach (var d in new[] { 90.0, 210, 330 }) corners.Add(At(5.0 / 3 * Math.Cos(d * Math.PI / 180), 5.0 / 3 * Math.Sin(d * Math.PI / 180))); break;
                case -7: corners.AddRange(box); break;
                default:
                    Count($"Ký hiệu điểm mã {code}", false, $"mã không có trong bảng SXF, đã vẽ tạm tại ({F(ox)},{F(oy)})");
                    return;
            }
            string? why = null;
            foreach (var (x, y) in corners)
                if (!ptIdx.Near(x, y, tol).Any()) { why = $"thiếu góc ({F(x)},{F(y)}) của ký hiệu tại ({F(ox)},{F(oy)})"; break; }
            if (why == null && circle > 0 && !curIdx.Near(ox, oy, tol).Any(c => c.Full && Math.Abs(c.R1 - circle) < tol))
                why = $"thiếu vòng tròn r={F(circle)} tại ({F(ox)},{F(oy)})";
            if (why == null && filled && !solIdx.Near(ox, oy, 2 * k + tol).Any())
                why = $"ký hiệu tại ({F(ox)},{F(oy)}) chưa được tô đặc";
            Count($"Ký hiệu điểm mã {code}", why == null, why);
        }

        foreach (var e in jww.Entities)
        {
            double s = paper ? 1 : main; // every group sits on the sheet at the main scale
            Check(e, T.Place(0, 0, s, s, 0), 0);
        }

        // Visibility: a hidden JWW group/layer must be off in the drawing and vice versa.
        int visBad = 0;
        for (int g = 0; g < 16; g++)
            for (int l = 0; l < 16; l++)
            {
                bool shouldOn = jww.Groups[g].State != 0 && jww.Groups[g].LayerState[l] != 0;
                var layer = doc.Layers.FirstOrDefault(x => x.Name.StartsWith($"{g:X}-{l:X} ") || x.Name == $"{g:X}-{l:X}");
                if (layer != null && layer.IsOn != shouldOn) { visBad++; if (samples.Count < 60) samples.Add($"Layer {layer.Name}: bật/tắt khác Jw_cad"); }
            }

        int bad = stats.Values.Sum(v => v.bad) + visBad;
        if (!quiet || bad > 0)
        {
            Console.WriteLine($"Kiểm tra {title} (tỉ lệ chính 1/{F(main)})");
            foreach (var (k, (o, b)) in stats) Console.WriteLine($"  {(b == 0 ? "khớp" : "LỆCH")}  {k}: {o}/{o + b}");
            if (visBad > 0) Console.WriteLine($"  LỆCH  Bật/tắt layer: {visBad}");
            foreach (var s in samples) Console.WriteLine($"     - {s}");
        }
        Console.WriteLine(bad == 0 ? "  => KHỚP" : $"  => CÒN {bad} CHỖ LỆCH");
        return bad == 0 ? 0 : 2;
    }

    static double D(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    // ---- reading the drawing ----

    static Attr AttrOf(Entity e, T t, Insert? parent, bool vis)
    {
        var col = e.Color;
        if (col.IsByLayer) col = e.Layer?.Color ?? new Color(7);
        else if (col.IsByBlock) col = parent?.Color ?? new Color(7);
        int rgb; bool i7 = false;
        if (col.IsTrueColor) rgb = Rgb(col.R, col.G, col.B);
        else
        {
            int idx = col.Index;
            i7 = idx == 7;
            rgb = idx >= 0 && idx <= 9 ? Rgb(Aci[idx].R, Aci[idx].G, Aci[idx].B) : -idx;
        }
        var lt = e.LineType;
        if (lt == null || lt.Name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase)) lt = e.Layer?.LineType;
        double k = e.LineTypeScale * t.Scale;
        var pat = lt == null ? Array.Empty<double>() : lt.Segments.Select(s => s.Length * k).ToArray();
        int w = e.LineWeight switch { LineWeightType.ByLayer or LineWeightType.ByBlock or LineWeightType.Default => -1, var x => (int)x };
        return new Attr(rgb, i7, pat, w, vis);
    }

    // Shown in AutoCAD: the object's layer (layer 0 inside a block takes the insert's) is on and not
    // frozen, and no enclosing insert sits on a frozen layer.
    static bool LayerShown(Layer? l) => l == null || (l.IsOn && !l.Flags.HasFlag(LayerFlags.Frozen));

    static (bool Bold, bool Italic, string Face) StyleFlags(TextStyle? st)
    {
        bool bold = false, italic = false; string face = "";
        if (st != null)
            foreach (var (app, xd) in st.ExtendedData)
                foreach (var rec in xd.Records)
                {
                    if (rec is ACadSharp.XData.ExtendedDataString s) face = s.Value;
                    if (rec is ACadSharp.XData.ExtendedDataInteger32 n) { bold = (n.Value & 0x01000000) != 0; italic = (n.Value & 0x02000000) != 0; }
                }
        return (bold, italic, face);
    }

    static void FlattenCad(Entity e, T t, Flat f, int depth, Insert? parent, Layer? parentLayer, bool chainShown)
    {
        var own = e.Layer;
        var eff = own != null && own.Name == "0" && parentLayer != null ? parentLayer : own;
        bool shown = chainShown && LayerShown(eff);
        var a = AttrOf(e, t, parent, shown);
        switch (e)
        {
            case Line l:
            {
                var (x1, y1) = t.Ap(l.StartPoint.X, l.StartPoint.Y); var (x2, y2) = t.Ap(l.EndPoint.X, l.EndPoint.Y);
                f.Segs.Add(new Seg(x1, y1, x2, y2, a)); f.Pts.Add((x1, y1)); f.Pts.Add((x2, y2)); break;
            }
            case Arc ar:
            {
                var c = t.Ap(ar.Center.X, ar.Center.Y);
                var p1 = t.Ap(ar.Center.X + ar.Radius * Math.Cos(ar.StartAngle), ar.Center.Y + ar.Radius * Math.Sin(ar.StartAngle));
                var p2 = t.Ap(ar.Center.X + ar.Radius * Math.Cos(ar.EndAngle), ar.Center.Y + ar.Radius * Math.Sin(ar.EndAngle));
                f.Curves.Add(new Curve(c.Item1, c.Item2, ar.Radius * t.Scale, ar.Radius * t.Scale, false, p1, p2, a)); break;
            }
            case Circle ci:
            {
                var c = t.Ap(ci.Center.X, ci.Center.Y);
                f.Curves.Add(new Curve(c.Item1, c.Item2, ci.Radius * t.Scale, ci.Radius * t.Scale, true, c, c, a)); break;
            }
            case Ellipse el:
            {
                var c = t.Ap(el.Center.X, el.Center.Y);
                double mx = el.MajorAxisEndPoint.X, my = el.MajorAxisEndPoint.Y;
                double maj = Math.Sqrt(mx * mx + my * my);
                (double, double) P(double tt) => t.Ap(el.Center.X + mx * Math.Cos(tt) - my * el.RadiusRatio * Math.Sin(tt),
                                                      el.Center.Y + my * Math.Cos(tt) + mx * el.RadiusRatio * Math.Sin(tt));
                bool full = Math.Abs(Math.Abs(el.EndParameter - el.StartParameter) - 2 * Math.PI) < 1e-9 || Math.Abs(el.EndParameter - el.StartParameter) < 1e-12;
                f.Curves.Add(new Curve(c.Item1, c.Item2, maj * t.Scale, maj * el.RadiusRatio * t.Scale, full, P(el.StartParameter), P(el.EndParameter), a));
                break;
            }
            case TextEntity tx:
            {
                var (x, y) = t.Ap(tx.InsertPoint.X, tx.InsertPoint.Y);
                var (bold, italic, face) = StyleFlags(tx.Style);
                f.Txts.Add(new Txt(x, y, tx.Height * t.Scale, tx.Rotation + t.Rot, tx.WidthFactor, tx.Value, a, bold, italic, face)); break;
            }
            case MText mt:
            {
                // One line per character of a vertical text: first baseline one height below the top,
                // then 5/3 × height × spacing factor apart (AutoCAD's rule for "exact" spacing).
                double H = mt.Height, rot = Math.Atan2(mt.AlignmentPoint.Y, mt.AlignmentPoint.X);
                if (mt.AlignmentPoint.X == 0 && mt.AlignmentPoint.Y == 0) rot = 0;
                double pitch = 5.0 / 3.0 * H * (mt.LineSpacing > 0 ? mt.LineSpacing : 1);
                string v = mt.Value; double wf = 1;
                var m = System.Text.RegularExpressions.Regex.Match(v, @"^\\W([0-9.]+);");
                if (m.Success) { wf = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); v = v[m.Length..]; }
                var lines = v.Split("\\P");
                var (bold, italic, face) = StyleFlags(mt.Style);
                for (int k = 0; k < lines.Length; k++)
                {
                    double d = H + k * pitch;
                    double bx = mt.InsertPoint.X + Math.Sin(rot) * d, by = mt.InsertPoint.Y - Math.Cos(rot) * d;
                    var (x, y) = t.Ap(bx, by);
                    string line = lines[k].Replace("\\{", "{").Replace("\\}", "}").Replace("\\\\", "\\");
                    f.Txts.Add(new Txt(x, y, H * t.Scale, rot + t.Rot, wf, line, a, bold, italic, face));
                }
                break;
            }
            case Solid so:
            {
                var pts = new[] { so.FirstCorner, so.SecondCorner, so.ThirdCorner, so.FourthCorner };
                double cx = 0, cy = 0;
                foreach (var p in pts) { var (x, y) = t.Ap(p.X, p.Y); cx += x / 4; cy += y / 4; f.Pts.Add((x, y)); f.SolPts.Add((x, y)); }
                f.Sols.Add(new Sol(cx, cy, a));
                var (fx, fy) = t.Ap(so.FirstCorner.X, so.FirstCorner.Y);
                f.Sols.Add(new Sol(fx, fy, a));
                break;
            }
            case RasterImage img:
            {
                var (x, y) = t.Ap(img.InsertPoint.X, img.InsertPoint.Y);
                var (ux, uy) = t.Ap(img.InsertPoint.X + img.UVector.X * img.Size.X, img.InsertPoint.Y + img.UVector.Y * img.Size.X);
                var (vx, vy) = t.Ap(img.InsertPoint.X + img.VVector.X * img.Size.Y, img.InsertPoint.Y + img.VVector.Y * img.Size.Y);
                f.Imgs.Add(new Img(x, y, D(x, y, ux, uy), D(x, y, vx, vy), Math.Atan2(uy - y, ux - x), img.Definition?.FileName ?? ""));
                break;
            }
            case Insert ins when depth < 20:
            {
                var bt = T.Place(ins.InsertPoint.X, ins.InsertPoint.Y, ins.XScale, ins.YScale, ins.Rotation).Then(t);
                bool frozen = ins.Layer != null && ins.Layer.Flags.HasFlag(LayerFlags.Frozen);
                foreach (var be in ins.Block.Entities) FlattenCad(be, bt, f, depth + 1, ins, eff, chainShown && !frozen);
                break;
            }
        }
    }

    sealed class Grid<TItem>
    {
        readonly Dictionary<(long, long), List<TItem>> _cells = new();
        readonly double _size;
        readonly Func<TItem, (double, double)> _pos;
        public Grid(Func<TItem, (double, double)> pos, IEnumerable<TItem> items, double main)
        {
            _pos = pos; _size = 5 * main;
            foreach (var it in items) { var (x, y) = pos(it); Cell(x, y).Add(it); }
        }
        List<TItem> Cell(double x, double y)
        {
            var k = ((long)Math.Floor(x / _size), (long)Math.Floor(y / _size));
            if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new();
            return l;
        }
        public IEnumerable<TItem> Near(double x, double y, double r)
        {
            long x0 = (long)Math.Floor((x - r) / _size), x1 = (long)Math.Floor((x + r) / _size);
            long y0 = (long)Math.Floor((y - r) / _size), y1 = (long)Math.Floor((y + r) / _size);
            for (long i = x0; i <= x1; i++)
                for (long j = y0; j <= y1; j++)
                    if (_cells.TryGetValue((i, j), out var l))
                        foreach (var it in l) { var (px, py) = _pos(it); if (D(px, py, x, y) <= r) yield return it; }
        }
    }
}
