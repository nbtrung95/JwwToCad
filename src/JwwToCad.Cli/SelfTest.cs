using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using JwwToCad.Core.Convert;
using JwwToCad.Core.Jww;

/// <summary>
/// Builds a drawing that holds every kind of Jw_cad object and setting the JWW format allows (all
/// symbol codes, all pen colours and line types, widths, text variants and control codes, circle
/// solids, images, nested scaled blocks), converts it, writes and reads back the DWG, and runs the
/// same check as after a real export. Then it damages the drawing in several ways and requires the
/// check to notice each one. This covers objects that do not occur in the sample files.
/// </summary>
static class SelfTest
{
    public static int Run(string outDwg)
    {
        var jww = Build();
        string dir = Path.GetDirectoryName(Path.GetFullPath(outDwg)) ?? ".";
        Directory.CreateDirectory(dir);
        string jwwPath = Path.Combine(dir, "selftest.jww"); // only a name: used by embedded file-name codes
        var opt = new ConvertOptions { SourcePath = jwwPath, OutputPath = outDwg };
        var doc = JwwToCadConverter.Convert(jww, opt, out var rep);
        DwgWriter.Write(outDwg, doc);
        Console.WriteLine("Tự kiểm tra với bản vẽ thử đủ mọi loại đối tượng: " + rep);
        foreach (var w in rep.Warnings) Console.WriteLine("    ! " + w);
        int rc = Verify.Run(jww, DwgReader.Read(outDwg), "bản vẽ thử", paper: false, quiet: false, jwwPath, outDwg);

        // The check itself must catch damage of every kind.
        bool allCaught = true;
        void Damage(string what, Action<CadDocument> harm)
        {
            var d = DwgReader.Read(outDwg);
            harm(d);
            Console.WriteLine($"Thử làm hỏng: {what} — bộ kiểm tra phải báo lệch:");
            bool caught = Verify.Run(jww, d, "bản vẽ đã làm hỏng", paper: false, quiet: true, jwwPath, outDwg) != 0;
            Console.WriteLine(caught ? "  => đã phát hiện, đúng" : "  => LỖI: bộ kiểm tra không phát hiện");
            allCaught &= caught;
        }
        IEnumerable<Entity> All(CadDocument d) => d.Entities.Concat(d.BlockRecords.SelectMany(b => b.Entities));
        Damage("xoá 1/7 số đối tượng", d => { foreach (var v in d.Entities.Where((e, i) => i % 7 == 3).ToList()) d.Entities.Remove(v); });
        Damage("đổi màu một số đường", d => { foreach (var l in All(d).OfType<Line>().Where((e, i) => i % 9 == 4)) l.Color = new Color(l.Color.Index == 3 ? (short)4 : (short)3); });
        Damage("bỏ kiểu nét", d => { foreach (var l in All(d).OfType<Line>()) l.LineType = d.LineTypes["Continuous"]; });
        Damage("sai độ dày nét", d => { foreach (var l in All(d).OfType<Line>().Where(l => l.LineWeight != LineWeightType.ByLayer)) l.LineWeight = LineWeightType.W100; });
        Damage("dịch chữ", d => { foreach (var t in All(d).OfType<TextEntity>().Where((e, i) => i % 5 == 0)) t.InsertPoint += new CSMath.XYZ(30, 0, 0); });
        Damage("đảo đầu cung", d => { foreach (var a in All(d).OfType<Arc>()) (a.StartAngle, a.EndAngle) = (a.EndAngle, a.StartAngle + 0.3); });
        Damage("bật lại layer ẩn", d => { foreach (var l in d.Layers) { l.IsOn = true; l.Flags &= ~ACadSharp.Tables.LayerFlags.Frozen; } });
        Damage("bỏ ảnh", d =>
        {
            foreach (var i in d.Entities.OfType<RasterImage>().ToList()) d.Entities.Remove(i);
            foreach (var br in d.BlockRecords) foreach (var i in br.Entities.OfType<RasterImage>().ToList()) br.Entities.Remove(i);
        });
        return rc == 0 && allCaught ? 0 : 2;
    }

    // ---------------------------------------------------------------------------------------------

    static JwwHeader Header()
    {
        var h = new JwwHeader { MaxDrawWidth = 100 };
        uint Cr(int r, int g, int b) => (uint)(r | (g << 8) | (b << 16));
        int[][] std = { new[] { 0, 0, 0 }, new[] { 0, 255, 255 }, new[] { 255, 255, 255 }, new[] { 0, 255, 0 }, new[] { 255, 255, 0 },
                        new[] { 255, 0, 0 }, new[] { 0, 0, 255 }, new[] { 255, 128, 0 }, new[] { 128, 0, 255 }, new[] { 192, 192, 192 } };
        for (int n = 0; n <= 9; n++) { h.ScreenColor[n] = Cr(std[n][0], std[n][1], std[n][2]); h.PrintColor[n] = 0; h.PrintWidth[n] = 1 + n % 3; h.ScreenWidth[n] = 1; }
        var rnd = new Random(7);
        for (int n = 100; n <= 356; n++)
        {
            uint c;
            if (n >= 101 && n <= 116) { var s = JwwStyles.SxfColors[n - 101]; c = Cr(s.R, s.G, s.B); h.ColorName[n] = s.Name; }
            else { c = Cr(rnd.Next(256), rnd.Next(256), rnd.Next(256)); h.ColorName[n] = n == 100 ? "" : $"user{n}"; }
            h.ScreenColor[n] = c; h.PrintColor[n] = c; h.ScreenWidth[n] = 18; h.PrintWidth[n] = n % 4 == 0 ? 35 : 18; h.PointRadius[n] = 0.5;
        }
        (uint, int, int, int)[] lt =
        {
            (0x99999999, 4, 1, 5), (0xC3C3C3C3, 8, 1, 5), (0xE7E7E7E7, 8, 1, 5), (0xF99FF99F, 16, 1, 5),
            (0xFFF99FFF, 32, 1, 5), (0xF24FF24F, 16, 1, 5), (0xFFF24FFF, 32, 1, 5), (0x22222222, 4, 1, 10),
        };
        for (int n = 2; n <= 9; n++) h.LineTypes[n] = lt[n - 2];
        h.LineTypes[16] = (0xFFF99FFF, 32, 2, 20); h.LineTypes[17] = (0xFFF24FFF, 32, 2, 20);
        h.LineTypes[18] = (0xFFFE7FFF, 32, 2, 20); h.LineTypes[19] = (0xFFFE7FFF, 32, 4, 40);
        string[] names = { "", "continuous", "dashed", "dashed spaced", "long dashed dotted", "long dashed double-dotted",
            "long dashed triplicate-dotted", "dotted", "chain", "chain double dash", "dashed dotted", "double-dashed dotted",
            "dashed double-dotted", "double-dashed double-dotted", "dashed triplicate-dotted", "double-dashed triplicate-dotted", "undefined" };
        double[][] pitch =
        {
            Array.Empty<double>(), Array.Empty<double>(), new[] { 6, 1.5 }, new[] { 6.0, 6 }, new[] { 12, 1.5, 0.25, 1.5 },
            new[] { 12, 1.5, 0.25, 1.5, 0.25, 1.5 }, new[] { 12, 1.5, 0.25, 1.5, 0.25, 1.5, 0.25, 1.5 }, new[] { 0.25, 1.5 },
            new[] { 12, 1.5, 3.5, 1.5 }, new[] { 12, 1.5, 3.5, 1.5, 3.5, 1.5 }, new[] { 6, 1.5, 0.25, 1.5 }, new[] { 6, 1.5, 6, 1.5, 0.25, 1.5 },
            new[] { 6, 1.5, 0.25, 1.5, 0.25, 1.5 }, new[] { 6, 1.5, 6, 1.5, 0.25, 1.5, 0.25, 1.5 }, new[] { 6, 1.5, 0.25, 1.5, 0.25, 1.5, 0.25, 1.5 },
            new[] { 6, 1.5, 6, 1.5, 0.25, 1.5, 0.25, 1.5, 0.25, 1.5 }, Array.Empty<double>(),
        };
        for (int n = 30; n <= 62; n++)
        {
            int i = n - 30;
            if (i < names.Length) { h.SxfLineTypes[n] = (names[i], pitch[i]); h.LineTypes[n] = (i == 0 ? 0u : 0xFFFFFFFF, 32, 1, 10); }
            else h.SxfLineTypes[n] = ("", Array.Empty<double>());
            if (i >= names.Length) h.LineTypes[n] = (0, 0, 0, 0);
        }
        // User-defined SXF types: one with mm lengths, one with only a bit pattern.
        h.SxfLineTypes[47] = ("user dash", new[] { 3.0, 1, 1, 1 }); h.LineTypes[47] = (0xFF0F0F0F, 32, 1, 8);
        h.SxfLineTypes[48] = ("", Array.Empty<double>()); h.LineTypes[48] = (0xF0F0F0F0, 8, 1, 12);
        return h;
    }

    static JwwDocument Build()
    {
        var jww = new JwwDocument { Header = Header(), Memo = "Bản vẽ thử\nDòng 2", Version = 700 };
        for (int g = 0; g < 16; g++)
        {
            jww.Groups[g] = new JwwLayerGroup { State = 2, Scale = g == 1 ? 1 : g == 2 ? 20 : 50, Name = $"G{g}" };
            for (int l = 0; l < 16; l++) { jww.Groups[g].LayerState[l] = (ushort)(g == 3 && l == 5 ? 0 : 2); jww.Groups[g].LayerNames[l] = l == 2 ? "壁" : ""; }
        }
        var E = jww.Entities;
        T Add<T>(T e) where T : JwwEntity { E.Add(e); return e; }

        // 1) Symbols: every SXF code at four angles and scales, in two groups.
        var codes = Enumerable.Range(1, 11).Concat(Enumerable.Range(1, 7).Select(c => -c)).ToArray();
        for (int row = 0; row < codes.Length; row++)
            for (int a = 0; a < 4; a++)
                Add(new JwwPoint { PenStyle = 100, PenColor = (ushort)(1 + a), X = a * 20, Y = -row * 15, Code = unchecked((uint)codes[row]), Angle = a * Math.PI / 3, Scale = 0.3 + 0.2 * a });
        Add(new JwwPoint { PenStyle = 100, LayerGroup = 1, X = 120, Y = 0, Code = 6, Angle = 1, Scale = 0.5 });
        Add(new JwwPoint { PenStyle = 100, LayerGroup = 1, X = 120, Y = -20, Code = unchecked((uint)-6), Angle = 0.5, Scale = 0.5 });

        // 2) Lines in every line type and every pen colour, with and without own widths.
        int[] styles = Enumerable.Range(1, 9).Concat(Enumerable.Range(11, 9)).Concat(Enumerable.Range(30, 33)).ToArray();
        int[] pens = Enumerable.Range(0, 10).Concat(new[] { 100, 101, 102, 108, 115, 116, 117, 200, 255, 356 }).ToArray();
        for (int i = 0; i < styles.Length; i++)
        {
            int pen = pens[i % pens.Length];
            ushort width = (ushort)(i % 5 == 0 ? 0 : new[] { 5, 13, 18, 25, 35, 50, 70, 140, 211, 7, 33 }[i % 11]);
            Add(new JwwLine { PenStyle = (byte)styles[i], PenColor = (ushort)pen, PenWidth = width, Layer = (ushort)(i % 16), X1 = 200, Y1 = -i * 4, X2 = 280, Y2 = -i * 4 - 2 });
            Add(new JwwArc { PenStyle = (byte)styles[i], PenColor = (ushort)pens[(i + 3) % pens.Length], CX = 320, CY = -i * 4, Radius = 1.5, StartAngle = 0.3 * i, SweepAngle = 1.2, Flatness = 1, Tilt = 0.1 * i });
        }

        // 3) Arcs and ellipses: full, partial, tilted, flat > 1, negative sweep.
        double y0 = -300;
        foreach (var (flat, tilt, st, sw, full) in new[] { (1.0, 0.0, 0.0, 0.0, true), (1.0, 0.7, 0.4, 2.0, false), (0.5, 0.3, 0.0, 0.0, true),
                     (0.5, 0.3, 1.0, 3.5, false), (1.8, 1.1, 0.2, 1.4, false), (0.3, -0.6, 5.0, 1.0, false), (1.0, 0.0, 1.0, -1.5, false) })
        {
            Add(new JwwArc { PenColor = 3, CX = 10, CY = y0, Radius = 6, Flatness = flat, Tilt = tilt, StartAngle = st, SweepAngle = sw, IsFullCircle = full });
            Add(new JwwArc { PenColor = 2, LayerGroup = 2, CX = 40, CY = y0 * 2.5, Radius = 4, Flatness = flat, Tilt = tilt, StartAngle = st, SweepAngle = sw, IsFullCircle = full });
            y0 -= 20;
        }

        // 4) Texts: spacing, vertical, half/full width, bold/italic, fonts, control and embedded codes, %%.
        double ty = -500;
        JwwText Txt(string s, double w = 3, double hgt = 3, double sp = 0, bool vertical = false, uint type = 0, string font = "ＭＳ ゴシック", double ang = 0, ushort pen = 2, int group = 0)
        {
            var t = new JwwText { PenColor = pen, LayerGroup = (ushort)group, StartX = 10, StartY = ty, Width = w, Height = hgt, Spacing = sp, TextType = type, Font = font, AngleDeg = ang, Text = s, Flag = (ushort)(vertical ? 0x20 : 0) };
            // End point by the rule Jw_cad files show: a half-width character takes half a cell and half a gap.
            double len = 0, last = 0;
            foreach (var c in s)
            {
                bool half = JwTextLayout.IsHalf(c);
                double cell = vertical ? hgt : w;
                len += (half ? cell / 2 : cell) + (half ? sp / 2 : sp); last = half ? sp / 2 : sp;
            }
            len -= last;
            double r = ang * Math.PI / 180;
            t.EndX = t.StartX + len * Math.Cos(r); t.EndY = t.StartY + len * Math.Sin(r);
            ty -= vertical ? len + 8 : hgt + 4;
            return Add(t);
        }
        Txt("通り芯 X1-Y2");
        Txt("避難ハッチ", 1.5, 1.53, 0.5);
        Txt("Scale 1/50", 4, 4, 0.69);
        Txt("回転した文字 abc", 3, 3, 0.2, ang: 30);
        Txt("縦書き（ＡＢ）ー12", 3, 3, 0, vertical: true, ang: -90);
        Txt("縦FIX", 2, 2.5, 0.3, vertical: true, ang: -90);
        Txt("斜体 italic", type: 10000);
        Txt("太字 bold", type: 20000);
        Txt("太字斜体", type: 30000);
        Txt("ＭＳ 明朝の文字", font: "ＭＳ 明朝");
        Txt("丸ゴシック", font: "HG丸ｺﾞｼｯｸM-PRO");
        Txt("phông lạ", font: "Some Unknown Font");
        Txt("tên phông sai", font: "3");
        Txt("50%% và %%c %%d", pen: 5);
        Txt("m^u2 H^d2O B^cY", pen: 1);
        Txt("○^oA □^w12 E^bX", pen: 4);
        Txt("D13^$2@200^$6-2列", pen: 1);
        Txt("^!đậm^! ^/nghiêng^/ ^_gạch dưới^_ ^-gạch giữa^-", pen: 3);
        Txt("^*m^u2 không đổi^^ m^u3", pen: 6);
        Txt("Tệp: %f — tỉ lệ %SS — %m1", pen: 7);
        Txt("文字 SXF色", pen: 117);
        Txt("1/1 group text", group: 1);

        // 5) Solids: quads in several colours, custom RGB, circle solids of every shape.
        double sy = -800;
        for (int i = 0; i < 6; i++)
        {
            var s = Add(new JwwSolid { PenColor = (ushort)(i < 5 ? 1 + i : 10), PenStyle = 1 });
            s.Points = new[] { 10.0 + i * 15, sy, 20 + i * 15, sy, 22 + i * 15, sy + 8, 8 + i * 15, sy + 6 };
            if (i == 5) s.Rgb = 0x3080C0;
        }
        sy -= 30;
        int k = 0;
        foreach (var (style, flag, sw) in new[] { (101, 100.0, 0.0), (101, 0, 2.0), (101, 5, 2.5), (101, -1, 1.2), (105, 2.0, 0.0), (106, 2.5, 3.0), (101, 0, -1.0), (111, 0, 1.5) })
        {
            foreach (double flat in new[] { 1.0, 0.6 })
            {
                var s = Add(new JwwSolid { PenStyle = (byte)style, PenColor = 10, Rgb = (uint)(0x102030 * (k + 1) & 0xFFFFFF) });
                s.Points = new[] { 15.0 + k * 20, sy, 6, flat, 0.2 * k, 0.4, sw, flag };
                k++;
            }
        }

        // 6) Images: one stored in the file, one linked to a file that is not there.
        jww.Images["test_logo.bmp"] = Bmp(40, 10);
        jww.ImageCount = 1;
        Add(new JwwText { StartX = 200, StartY = -800, Height = 3, Width = 3, AngleDeg = 0, Text = "^@BM%temp%test_logo.bmp,60,15,0,0,1,0,255,255,255" });
        Add(new JwwText { StartX = 200, StartY = -830, Height = 3, Width = 3, AngleDeg = 15, Text = "^@BMC:\\khong_co\\anh.bmp,40,20" });

        // 7) Blocks: nested, scaled, rotated, mirrored, with line types inside.
        var inner = new JwwBlockDef { Number = 2, Name = "inner@@SfigorgFlag@@3" };
        inner.Entities.Add(new JwwLine { PenStyle = 5, PenColor = 6, X1 = 0, Y1 = 0, X2 = 10, Y2 = 0 });
        inner.Entities.Add(new JwwArc { PenStyle = 2, PenColor = 8, CX = 5, CY = 5, Radius = 3, Flatness = 1, StartAngle = 0.5, SweepAngle = 2 });
        var outer = new JwwBlockDef { Number = 1, Name = "outer" };
        outer.Entities.Add(new JwwLine { PenStyle = 34, PenColor = 102, PenWidth = 18, X1 = 0, Y1 = 0, X2 = 20, Y2 = 5 });
        outer.Entities.Add(new JwwBlockInsert { BlockNumber = 2, X = 3, Y = 3, ScaleX = 0.5, ScaleY = 0.5, AngleRad = 0.3 });
        outer.Entities.Add(new JwwText { PenColor = 2, StartX = 0, StartY = -5, EndX = 4.5, EndY = -5, Height = 3, Width = 3, Text = "BLK" });
        jww.Blocks.Add(outer); jww.Blocks.Add(inner);
        Add(new JwwBlockInsert { BlockNumber = 1, X = 400, Y = -100, ScaleX = 1, ScaleY = 1 });
        Add(new JwwBlockInsert { BlockNumber = 1, X = 400, Y = -150, ScaleX = 2, ScaleY = 2, AngleRad = 0.8 });
        Add(new JwwBlockInsert { BlockNumber = 1, X = 400, Y = -200, ScaleX = -1.5, ScaleY = 1.5, AngleRad = 0 });
        Add(new JwwBlockInsert { BlockNumber = 1, LayerGroup = 1, X = 400, Y = -250, ScaleX = 1, ScaleY = 1 });

        // 8) A dimension with extension lines and arrow ends.
        var dim = new JwwDimension { PenColor = 1 };
        dim.Line = new JwwLine { PenColor = 1, X1 = 400, Y1 = -400, X2 = 460, Y2 = -400 };
        dim.Text = new JwwText { PenColor = 2, StartX = 425, StartY = -399, EndX = 430, EndY = -399, Width = 2.5, Height = 2.5, Text = "3000" };
        dim.Ext1 = new JwwLine { PenColor = 1, X1 = 400, Y1 = -410, X2 = 400, Y2 = -398 };
        dim.Ext2 = new JwwLine { PenColor = 1, X1 = 460, Y1 = -410, X2 = 460, Y2 = -398 };
        dim.Points.Add(new JwwPoint { PenStyle = 100, PenColor = 1, X = 400, Y = -400, Code = 6, Angle = 0, Scale = 1 });
        dim.Points.Add(new JwwPoint { PenStyle = 100, PenColor = 1, X = 460, Y = -400, Code = 6, Angle = Math.PI, Scale = 1 });
        Add(dim);

        // 9) A block placed on a hidden layer: Jw_cad hides all of it, whatever layers its parts are on.
        Add(new JwwBlockInsert { BlockNumber = 1, LayerGroup = 3, Layer = 5, X = 500, Y = -100, ScaleX = 1, ScaleY = 1 });

        // 10) A hidden layer.
        Add(new JwwLine { LayerGroup = 3, Layer = 5, PenColor = 2, X1 = 0, Y1 = 50, X2 = 50, Y2 = 50 });
        return jww;
    }


    /// <summary>A small 24-bit BMP.</summary>
    static byte[] Bmp(int w, int h)
    {
        int row = (w * 3 + 3) & ~3, size = 54 + row * h;
        var b = new byte[size];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(size).CopyTo(b, 2); BitConverter.GetBytes(54).CopyTo(b, 10); BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(w).CopyTo(b, 18); BitConverter.GetBytes(h).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26); BitConverter.GetBytes((short)24).CopyTo(b, 28);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int o = 54 + y * row + x * 3; b[o] = (byte)(x * 6); b[o + 1] = (byte)(y * 25); b[o + 2] = 200; }
        return b;
    }
}
