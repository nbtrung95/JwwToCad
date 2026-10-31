using JwwToCad.Core.Jww;

/// <summary>Lists the JWW objects (top level) within r paper-mm of every text equal to s. Diagnostic tool.</summary>
static class NearTool
{
    public static int Run(string path, string s, double r)
    {
        var d = JwwReader.Read(path);
        string Desc(JwwEntity e) => e switch
        {
            JwwLine l => $"Line ({l.X1:F2},{l.Y1:F2})-({l.X2:F2},{l.Y2:F2})",
            JwwArc a => $"Arc c=({a.CX:F2},{a.CY:F2}) r={a.Radius:F2} st={a.StartAngle:F2} sw={a.SweepAngle:F2} full={a.IsFullCircle} fl={a.Flatness}",
            JwwText t => $"Text '{t.Text}' ({t.StartX:F2},{t.StartY:F2})-({t.EndX:F2},{t.EndY:F2}) {t.Width}x{t.Height} ang={t.AngleDeg} type={t.TextType}",
            JwwBlockInsert b => $"Insert #{b.BlockNumber} ({b.X:F2},{b.Y:F2}) s=({b.ScaleX},{b.ScaleY}) a={b.AngleRad}",
            JwwSolid so => $"Solid {string.Join(",", so.Points.Select(v => v.ToString("F2")))}",
            JwwPoint p => $"Point ({p.X:F2},{p.Y:F2}) tmp={p.IsTemporary}",
            _ => e.GetType().Name,
        };
        foreach (var t in d.Entities.OfType<JwwText>().Where(t => t.Text == s))
        {
            Console.WriteLine($"== '{s}' at ({t.StartX:F2},{t.StartY:F2}) g{t.LayerGroup:X} l{t.Layer:X}");
            foreach (var e in d.Entities)
            {
                double x, y;
                switch (e)
                {
                    case JwwLine l: x = (l.X1 + l.X2) / 2; y = (l.Y1 + l.Y2) / 2; break;
                    case JwwArc a: x = a.CX; y = a.CY; break;
                    case JwwText tt: x = tt.StartX; y = tt.StartY; break;
                    case JwwBlockInsert b: x = b.X; y = b.Y; break;
                    case JwwPoint p: x = p.X; y = p.Y; break;
                    case JwwSolid so: x = so.Points[0]; y = so.Points[1]; break;
                    default: continue;
                }
                if (Math.Abs(x - t.StartX) < r && Math.Abs(y - t.StartY) < r)
                    Console.WriteLine($"  g{e.LayerGroup:X} l{e.Layer:X} pen{e.PenColor} st{e.PenStyle} w{e.PenWidth} f{e.Flag:X} {Desc(e)}");
            }
        }
        return 0;
    }
}

static class BlockDump
{
    public static int Run(string path, uint num)
    {
        var d = JwwReader.Read(path);
        var b = d.Blocks.First(x => x.Number == num);
        Console.WriteLine($"Block {b.Number} '{b.Name}' g{b.LayerGroup:X} l{b.Layer:X} n={b.Entities.Count}");
        foreach (var e in b.Entities)
            Console.WriteLine($"  {e.GetType().Name[3..]} g{e.LayerGroup:X} l{e.Layer:X} pen{e.PenColor} st{e.PenStyle} f{e.Flag:X} " + e switch
            {
                JwwLine l => $"({l.X1:F2},{l.Y1:F2})-({l.X2:F2},{l.Y2:F2})",
                JwwArc a => $"c=({a.CX:F2},{a.CY:F2}) r={a.Radius:F2} st={a.StartAngle:F2} sw={a.SweepAngle:F2}",
                _ => "",
            });
        for (int g = 0; g < 16; g++) Console.WriteLine($"G{g:X} st={d.Groups[g].State} layers=" + string.Join("", d.Groups[g].LayerState));
        return 0;
    }
}
