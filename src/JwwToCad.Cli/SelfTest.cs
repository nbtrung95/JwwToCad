using ACadSharp.IO;
using JwwToCad.Core.Convert;
using JwwToCad.Core.Jww;

/// <summary>
/// Builds a small drawing that holds every symbol code at several angles and scales,
/// converts it, writes and reads back the DWG, and runs the same check as after a real export.
/// </summary>
static class SelfTest
{
    public static int Run(string outDwg)
    {
        var jww = new JwwDocument();
        for (int g = 0; g < 16; g++)
        {
            jww.Groups[g] = new JwwLayerGroup { State = 2, Scale = g == 1 ? 1 : 50 };
            for (int l = 0; l < 16; l++) { jww.Groups[g].LayerState[l] = 2; jww.Groups[g].LayerNames[l] = ""; }
        }
        var codes = Enumerable.Range(1, 11).Concat(Enumerable.Range(1, 7).Select(c => -c)).ToArray();
        int row = 0;
        foreach (var code in codes)
        {
            for (int a = 0; a < 4; a++)
            {
                jww.Entities.Add(new JwwPoint
                {
                    PenStyle = 100, PenColor = (ushort)(1 + a), Layer = 0, LayerGroup = 0,
                    X = a * 20, Y = -row * 15, Code = unchecked((uint)code),
                    Angle = a * Math.PI / 3, Scale = 0.3 + 0.2 * a,
                });
                jww.Entities.Add(new JwwText
                {
                    LayerGroup = 0, PenColor = 1, StartX = -40, StartY = -row * 15, Height = 3, Width = 3,
                    Text = $"code {code}",
                });
            }
            row++;
        }
        // The same symbols in a 1/1 group, which goes into a scaled block.
        jww.Entities.Add(new JwwPoint { PenStyle = 100, LayerGroup = 1, X = 120, Y = 0, Code = 6, Angle = 1, Scale = 0.5 });
        jww.Entities.Add(new JwwPoint { PenStyle = 100, LayerGroup = 1, X = 120, Y = -20, Code = unchecked((uint)-6), Angle = 0.5, Scale = 0.5 });

        var doc = JwwToCadConverter.Convert(jww, new ConvertOptions(), out var rep);
        DwgWriter.Write(outDwg, doc);
        Console.WriteLine("Tự kiểm tra bảng ký hiệu: " + rep);
        foreach (var w in rep.Warnings) Console.WriteLine("    ! " + w);
        var back = DwgReader.Read(outDwg);
        int rc = Verify.Run(jww, back, "bản vẽ thử ký hiệu", paper: false);

        // The check itself must catch damage: drop every 7th object and expect a mismatch.
        var damaged = DwgReader.Read(outDwg);
        var victims = damaged.Entities.Where((e, i) => i % 7 == 3).ToList();
        foreach (var v in victims) damaged.Entities.Remove(v);
        Console.WriteLine($"Thử làm hỏng bản vẽ (xoá {victims.Count} đối tượng), bộ kiểm tra phải báo lệch:");
        bool caught = Verify.Run(jww, damaged, "bản vẽ đã làm hỏng", paper: false, quiet: true) != 0;
        Console.WriteLine(caught ? "  => bộ kiểm tra đã phát hiện, đúng" : "  => LỖI: bộ kiểm tra không phát hiện");
        return rc == 0 && caught ? 0 : 2;
    }
}
