using System.Globalization;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using JwwToCad.Core.Convert;

// jwwtocad <input.jww|folder> [output] [--paper] [--dxf]
// jwwtocad preview <file.dwg|dxf> <out.svg>
Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0)
{
    Console.WriteLine("Cách dùng: jwwtocad <file.jww | thư mục> [đầu ra] [--paper] [--dxf]");
    return 1;
}
if (args[0] == "selftest") return SelfTest.Run(args.Length > 1 ? args[1] : "selftest.dwg");
if (args[0] == "verify")
{
    // jwwtocad verify <jww file|folder> <dwg file|folder> [--paper]
    bool vp = args.Contains("--paper");
    var va = args.Where(a => !a.StartsWith("--")).ToList();
    if (Directory.Exists(va[1]))
    {
        int rc = 0;
        foreach (var j in Directory.GetFiles(va[1 - 0], "*.jww"))
        {
            var dwgFile = Path.Combine(va[2], Path.GetFileNameWithoutExtension(j) + ".dwg");
            rc |= Verify.Run(j, dwgFile, vp);
        }
        return rc;
    }
    return Verify.Run(va[1], va[2], vp);
}
if (args[0] == "info") return Info.Run(args[1]);
if (args[0] == "preview") return Preview.Run(args[1], args[2], args.Length > 3 ? args[3] : null);

var opt = new ConvertOptions();
bool dxf = args.Contains("--dxf");
if (args.Contains("--paper")) opt.Mode = CoordinateMode.Paper;
var pos = args.Where(a => !a.StartsWith("--")).ToList();
string input = pos[0];
var files = Directory.Exists(input)
    ? Directory.GetFiles(input, "*.jww", SearchOption.TopDirectoryOnly)
    : new[] { input };
string? outArg = pos.Count > 1 ? pos[1] : null;
int failed = 0;
foreach (var f in files)
{
    string ext = dxf ? ".dxf" : ".dwg";
    string outPath = outArg == null
        ? Path.ChangeExtension(f, ext)
        : Directory.Exists(outArg) || files.Length > 1
            ? Path.Combine(outArg, Path.GetFileNameWithoutExtension(f) + ext)
            : outArg;
    try
    {
        if (outArg != null && files.Length > 1) Directory.CreateDirectory(outArg);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rep = JwwToCadConverter.ConvertFile(f, outPath, opt);
        Console.WriteLine($"OK  {Path.GetFileName(f)} -> {Path.GetFileName(outPath)} ({sw.ElapsedMilliseconds} ms)");
        Console.WriteLine("    " + rep);
        foreach (var w in rep.Warnings) Console.WriteLine("    ! " + w);
        // Every export is read back and compared with the JWW source.
        if (!args.Contains("--no-verify") && Verify.Run(f, outPath, opt.Mode == CoordinateMode.Paper, quiet: true) != 0) failed++;
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"LỖI {Path.GetFileName(f)}: {ex.Message}");
    }
}
return failed == 0 ? 0 : 2;

/// <summary>Reads a DWG/DXF back and draws it as SVG, to check the written file by eye.</summary>
static class Preview
{
    public static int Run(string path, string svgPath, string? box)
    {
        CadDocument doc = path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)
            ? DxfReader.Read(path) : DwgReader.Read(path);
        var ents = doc.Entities.ToList();
        Console.WriteLine($"Đọc lại {ents.Count} đối tượng, {doc.Layers.Count} layer, {doc.BlockRecords.Count} block");
        var sb = new StringBuilder();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var xs = new List<double>(); var ys = new List<double>();
        void Ext(double x, double y) { xs.Add(x); ys.Add(y); }
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        string Col(Entity e)
        {
            var c = e.Color.IsByLayer ? e.Layer.Color : e.Color;
            if (c.Index == 7 || c.Index == 0) return "#000";
            var rgb = c.GetRgb();
            return $"rgb({rgb[0]},{rgb[1]},{rgb[2]})";
        }
        void Draw(Entity e, Func<double, double, (double, double)> tf, double sc)
        {
            if (!e.Layer.IsOn) return;
            switch (e)
            {
                case Line l:
                    var (a1, b1) = tf(l.StartPoint.X, l.StartPoint.Y); var (a2, b2) = tf(l.EndPoint.X, l.EndPoint.Y);
                    Ext(a1, b1); Ext(a2, b2);
                    sb.Append($"<line x1='{F(a1)}' y1='{F(-b1)}' x2='{F(a2)}' y2='{F(-b2)}' stroke='{Col(e)}'/>");
                    break;
                case Arc ar:
                {
                    var (cx, cy) = tf(ar.Center.X, ar.Center.Y); double r = ar.Radius * sc;
                    double sa = ar.StartAngle, ea = ar.EndAngle; if (ea < sa) ea += 2 * Math.PI;
                    var (x1, y1) = (cx + r * Math.Cos(sa), cy + r * Math.Sin(sa));
                    var (x2, y2) = (cx + r * Math.Cos(ea), cy + r * Math.Sin(ea));
                    Ext(x1, y1); Ext(x2, y2);
                    sb.Append($"<path d='M{F(x1)} {F(-y1)} A{F(r)} {F(r)} 0 {(ea - sa > Math.PI ? 1 : 0)} 0 {F(x2)} {F(-y2)}' stroke='{Col(e)}' fill='none'/>");
                    break;
                }
                case Circle ci:
                {
                    var (cx, cy) = tf(ci.Center.X, ci.Center.Y); Ext(cx, cy);
                    sb.Append($"<circle cx='{F(cx)}' cy='{F(-cy)}' r='{F(ci.Radius * sc)}' stroke='{Col(e)}' fill='none'/>");
                    break;
                }
                case Ellipse el:
                {
                    var (cx, cy) = tf(el.Center.X, el.Center.Y); Ext(cx, cy);
                    double rx = Math.Sqrt(el.MajorAxisEndPoint.X * el.MajorAxisEndPoint.X + el.MajorAxisEndPoint.Y * el.MajorAxisEndPoint.Y) * sc, ry = rx * el.RadiusRatio;
                    double rot = Math.Atan2(el.MajorAxisEndPoint.Y, el.MajorAxisEndPoint.X) * 180 / Math.PI;
                    sb.Append($"<ellipse cx='{F(cx)}' cy='{F(-cy)}' rx='{F(rx)}' ry='{F(ry)}' transform='rotate({F(-rot)} {F(cx)} {F(-cy)})' stroke='{Col(e)}' fill='none'/>");
                    break;
                }
                case Solid so:
                {
                    var pts = new[] { so.FirstCorner, so.SecondCorner, so.FourthCorner, so.ThirdCorner }
                        .Select(p => tf(p.X, p.Y)).ToList();
                    foreach (var p in pts) Ext(p.Item1, p.Item2);
                    sb.Append($"<polygon points='{string.Join(" ", pts.Select(p => F(p.Item1) + "," + F(-p.Item2)))}' fill='{Col(e)}' fill-opacity='0.5'/>");
                    break;
                }
                case TextEntity t:
                {
                    var (x, y) = tf(t.InsertPoint.X, t.InsertPoint.Y); Ext(x, y);
                    double h = t.Height * sc, deg = -t.Rotation * 180 / Math.PI;
                    var v = System.Security.SecurityElement.Escape(t.Value);
                    sb.Append($"<text x='{F(x)}' y='{F(-y)}' font-size='{F(h * 1.15)}' transform='rotate({F(deg)} {F(x)} {F(-y)})' fill='{Col(e)}' stroke='none' font-family='Noto Sans CJK JP, sans-serif'>{v}</text>");
                    break;
                }
                case Insert ins:
                {
                    double cs = Math.Cos(ins.Rotation), sn = Math.Sin(ins.Rotation);
                    (double, double) tf2(double x, double y)
                    {
                        double px = x * ins.XScale, py = y * ins.YScale;
                        return tf(ins.InsertPoint.X + px * cs - py * sn, ins.InsertPoint.Y + px * sn + py * cs);
                    }
                    foreach (var be in ins.Block.Entities) Draw(be, tf2, sc * Math.Abs(ins.XScale));
                    break;
                }
            }
        }
        foreach (var e in ents) Draw(e, (x, y) => (x, y), 1);
        xs.Sort(); ys.Sort();
        double Pct(List<double> v, double q) => v[(int)Math.Clamp(q * (v.Count - 1), 0, v.Count - 1)];
        minX = Pct(xs, 0.002); maxX = Pct(xs, 0.998); minY = Pct(ys, 0.002); maxY = Pct(ys, 0.998);
        Console.WriteLine($"Toàn bộ: X {xs[0]:0} .. {xs[^1]:0}, Y {ys[0]:0} .. {ys[^1]:0}; vùng chính X {minX:0} .. {maxX:0}, Y {minY:0} .. {maxY:0}");
        if (box != null)
        {
            var b = box.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            (minX, minY, maxX, maxY) = (b[0], b[1], b[2], b[3]);
        }
        double w = maxX - minX, h2 = maxY - minY, sw = Math.Max(w, h2) / 2500;
        File.WriteAllText(svgPath,
            $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='{F(minX - w * 0.02)} {F(-maxY - h2 * 0.02)} {F(w * 1.04)} {F(h2 * 1.04)}' width='{F(w > h2 ? 2400 : 2400 * w / h2)}' height='{F(w > h2 ? 2400 * h2 / w : 2400)}' stroke-width='{F(sw)}'><rect x='{F(minX - w)}' y='{F(-maxY - h2)}' width='{F(w * 3)}' height='{F(h2 * 3)}' fill='#fff'/>{sb}</svg>");
        return 0;
    }
}

static class Info
{
    public static int Run(string path)
    {
        var d = JwwToCad.Core.Jww.JwwReader.Read(path);
        for (int g = 0; g < 16; g++)
        {
            var es = d.Entities.Where(e => e.LayerGroup == g).ToList();
            if (es.Count == 0) continue;
            double x0 = 1e18, y0 = 1e18, x1 = -1e18, y1 = -1e18;
            void P(double x, double y) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            foreach (var e in es)
                switch (e) { case JwwToCad.Core.Jww.JwwLine l: P(l.X1, l.Y1); P(l.X2, l.Y2); break;
                             case JwwToCad.Core.Jww.JwwText t: P(t.StartX, t.StartY); break;
                             case JwwToCad.Core.Jww.JwwBlockInsert b: P(b.X, b.Y); break; }
            var types = string.Join(",", es.GroupBy(e => e.GetType().Name).Select(k => $"{k.Key[3..]}={k.Count()}"));
            var layers = string.Join(",", es.Select(e => e.Layer).Distinct().OrderBy(x => x).Select(x => x.ToString("X")));
            Console.WriteLine($"G{g:X} '{d.Groups[g].Name}' scale={d.Groups[g].Scale} state={d.Groups[g].State} layers[{layers}] paper=({x0:F0},{y0:F0})-({x1:F0},{y1:F0}) {types}");
        }
        foreach (var b in d.Blocks) Console.WriteLine($"Block {b.Number} '{b.Name}' n={b.Entities.Count}");
        foreach (var bi in d.Entities.OfType<JwwToCad.Core.Jww.JwwBlockInsert>().Take(20)) Console.WriteLine($"Insert #{bi.BlockNumber} g{bi.LayerGroup:X} at ({bi.X:F1},{bi.Y:F1}) s=({bi.ScaleX},{bi.ScaleY})");
        return 0;
    }
}
