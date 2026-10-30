using System.Globalization;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using JwwToCad.Core.Jww;

/// <summary>
/// Compares a converted DWG/DXF with its JWW source, without using the converter's code:
/// every JWW object is placed on the sheet (paper mm × main scale) and looked up among the
/// drawing's objects after all blocks are expanded.
/// </summary>
static class Verify
{
    // A flat, world-space copy of what is in the drawing.
    record Seg(double X1, double Y1, double X2, double Y2, string Layer);
    record Circ(double X, double Y, double R, bool Full, string Layer);
    record Txt(double X, double Y, double H, double Rot, double Wf, string S, string Layer);
    record Sol(double X, double Y, string Layer);

    sealed class Flat
    {
        public List<Seg> Segs = new();
        public List<Circ> Circs = new();
        public List<Txt> Txts = new();
        public List<Sol> Sols = new();
        public List<(double X, double Y)> Pts = new(); // line ends and solid corners
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
        return Run(jww, doc, Path.GetFileName(jwwPath), paper, quiet);
    }

    public static int Run(JwwDocument jww, CadDocument doc, string title, bool paper, bool quiet = false)
    {

        double GS(JwwEntity e) => Math.Max(jww.Groups[e.LayerGroup & 15].Scale, 1e-9);
        double main = paper ? 1 : jww.Entities.GroupBy(GS).OrderByDescending(g => g.Count()).First().Key;

        var flat = new Flat();
        foreach (var l in doc.Layers) if (!l.IsOn) flat.LayersOff.Add(l.Name);
        foreach (var e in doc.Entities) FlattenCad(e, T.Id, flat, 0);

        var segIdx = new Grid<Seg>(s => ((s.X1 + s.X2) / 2, (s.Y1 + s.Y2) / 2), flat.Segs, main);
        var circIdx = new Grid<Circ>(c => (c.X, c.Y), flat.Circs, main);
        var txtIdx = new Grid<Txt>(t => (t.X, t.Y), flat.Txts, main);
        var solIdx = new Grid<Sol>(s => (s.X, s.Y), flat.Sols, main);
        var ptIdx = new Grid<(double X, double Y)>(q => (q.X, q.Y), flat.Pts, main);

        double tol = 0.02 * main; // 0.02 mm on paper
        var stats = new SortedDictionary<string, (int ok, int bad)>();
        var samples = new List<string>();
        void Count(string kind, bool ok, string? why = null)
        {
            var (o, b) = stats.GetValueOrDefault(kind);
            stats[kind] = ok ? (o + 1, b) : (o, b + 1);
            if (!ok && why != null && samples.Count(s => s.StartsWith(kind)) < 5) samples.Add($"{kind}: {why}");
        }
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        var blocks = jww.Blocks.ToDictionary(b => b.Number);

        void Check(JwwEntity e, T t, int depth)
        {
            switch (e)
            {
                case JwwLine l:
                {
                    if (Math.Abs(l.X1 - l.X2) < 1e-9 && Math.Abs(l.Y1 - l.Y2) < 1e-9) { Count("Đường dài 0", true); break; }
                    var (x1, y1) = t.Ap(l.X1, l.Y1); var (x2, y2) = t.Ap(l.X2, l.Y2);
                    bool ok = segIdx.Near((x1 + x2) / 2, (y1 + y2) / 2, tol).Any(s =>
                        (D(s.X1, s.Y1, x1, y1) < tol && D(s.X2, s.Y2, x2, y2) < tol) ||
                        (D(s.X1, s.Y1, x2, y2) < tol && D(s.X2, s.Y2, x1, y1) < tol));
                    Count("Đường", ok, $"({F(x1)},{F(y1)})-({F(x2)},{F(y2)}) không có trong DWG");
                    break;
                }
                case JwwArc a:
                {
                    var (cx, cy) = t.Ap(a.CX, a.CY);
                    double r = a.Radius * t.Scale;
                    if (Math.Abs(a.Flatness - 1) > 1e-9) { Count("Elip", circIdx.Near(cx, cy, tol).Any(c => Math.Abs(c.R - r) < tol), $"tâm ({F(cx)},{F(cy)})"); break; }
                    bool ok = circIdx.Near(cx, cy, tol).Any(c => Math.Abs(c.R - r) < tol && c.Full == a.IsFullCircle);
                    Count(a.IsFullCircle ? "Đường tròn" : "Cung", ok, $"tâm ({F(cx)},{F(cy)}) r={F(r)}");
                    break;
                }
                case JwwText tx: CheckText(tx, t, "Chữ"); break;
                case JwwSolid so:
                {
                    var p = so.Points;
                    double px = so.PenStyle == 101 ? p[0] : (p[0] + p[2] + p[4] + p[6]) / 4;
                    double py = so.PenStyle == 101 ? p[1] : (p[1] + p[3] + p[5] + p[7]) / 4;
                    var (x, y) = t.Ap(px, py);
                    // Solids are compared by the corner/centre the converter must reproduce.
                    bool ok = so.PenStyle == 101
                        ? flat.Sols.Count > 0 && solIdx.Near(x, y, tol).Any()
                        : solIdx.Near(x, y, tol).Any();
                    Count("Tô đặc", ok, $"tâm ({F(x)},{F(y)})");
                    break;
                }
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
                    foreach (var be in def.Entities) Check(be, bt, depth + 1);
                    Count("Block", true);
                    break;
                }
            }
        }

        void CheckText(JwwText tx, T t, string kind)
        {
            if (string.IsNullOrEmpty(tx.Text) || tx.Height <= 0) return;
            var (x, y) = t.Ap(tx.StartX, tx.StartY);
            double cell = tx.Height * t.Scale;
            double rot = tx.AngleDeg * Math.PI / 180 + t.Rot;
            var cands = txtIdx.Near(x, y, cell * 0.3 + tol).Where(c => c.S == tx.Text).ToList();
            if (cands.Count == 0) { Count(kind, false, $"'{tx.Text}' tại ({F(x)},{F(y)}) không có trong DWG"); return; }
            var c = cands.OrderBy(c => D(c.X, c.Y, x, y)).First();
            double hr = c.H / cell;
            double dr = Math.Abs(Math.IEEERemainder(c.Rot - rot, 2 * Math.PI));
            double wf = tx.Width > 0 ? tx.Width / tx.Height : 1;
            string? why = null;
            if (hr < 0.78 || hr > 0.82) why = $"'{tx.Text}' cao {F(c.H)}, cần {F(cell * 0.8)}";
            else if (dr > 1e-3) why = $"'{tx.Text}' góc lệch {F(dr * 180 / Math.PI)}°";
            else if (Math.Abs(c.Wf - wf) > 1e-3) why = $"'{tx.Text}' hệ số rộng {F(c.Wf)}, cần {F(wf)}";
            Count(kind, why == null, why);
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
            if (why == null && circle > 0 && !circIdx.Near(ox, oy, tol).Any(c => Math.Abs(c.R - circle) < tol))
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
                if (layer != null && layer.IsOn != shouldOn) { visBad++; if (samples.Count < 40) samples.Add($"Layer {layer.Name}: bật/tắt khác Jw_cad"); }
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

    static void FlattenCad(Entity e, T t, Flat f, int depth)
    {
        string layer = e.Layer?.Name ?? "0";
        switch (e)
        {
            case Line l:
            {
                var (x1, y1) = t.Ap(l.StartPoint.X, l.StartPoint.Y); var (x2, y2) = t.Ap(l.EndPoint.X, l.EndPoint.Y);
                f.Segs.Add(new Seg(x1, y1, x2, y2, layer)); f.Pts.Add((x1, y1)); f.Pts.Add((x2, y2)); break;
            }
            case Arc a:
            {
                var (x, y) = t.Ap(a.Center.X, a.Center.Y); f.Circs.Add(new Circ(x, y, a.Radius * t.Scale, false, layer)); break;
            }
            case Circle c:
            {
                var (x, y) = t.Ap(c.Center.X, c.Center.Y); f.Circs.Add(new Circ(x, y, c.Radius * t.Scale, true, layer)); break;
            }
            case Ellipse el:
            {
                var (x, y) = t.Ap(el.Center.X, el.Center.Y);
                double maj = Math.Sqrt(el.MajorAxisEndPoint.X * el.MajorAxisEndPoint.X + el.MajorAxisEndPoint.Y * el.MajorAxisEndPoint.Y);
                f.Circs.Add(new Circ(x, y, maj * t.Scale, false, layer));
                f.Circs.Add(new Circ(x, y, maj * el.RadiusRatio * t.Scale, false, layer));
                break;
            }
            case TextEntity tx:
            {
                var (x, y) = t.Ap(tx.InsertPoint.X, tx.InsertPoint.Y);
                f.Txts.Add(new Txt(x, y, tx.Height * t.Scale, tx.Rotation + t.Rot, tx.WidthFactor, tx.Value, layer)); break;
            }
            case Solid so:
            {
                var pts = new[] { so.FirstCorner, so.SecondCorner, so.ThirdCorner, so.FourthCorner };
                double cx = 0, cy = 0;
                foreach (var p in pts) { var (x, y) = t.Ap(p.X, p.Y); cx += x / 4; cy += y / 4; f.Pts.Add((x, y)); }
                f.Sols.Add(new Sol(cx, cy, layer));
                // A fan triangle from a filled circle starts at the centre.
                var (fx, fy) = t.Ap(so.FirstCorner.X, so.FirstCorner.Y);
                f.Sols.Add(new Sol(fx, fy, layer));
                break;
            }
            case Insert ins when depth < 20:
            {
                var bt = T.Place(ins.InsertPoint.X, ins.InsertPoint.Y, ins.XScale, ins.YScale, ins.Rotation).Then(t);
                foreach (var be in ins.Block.Entities) FlattenCad(be, bt, f, depth + 1);
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
