using ACadSharp;

namespace JwwToCad.Core.Convert;

public enum CoordinateMode
{
    /// <summary>Real size: paper coordinates × layer group scale (what AutoCAD users usually expect).</summary>
    RealSize,
    /// <summary>Paper size 1:1, exactly as the sheet is printed.</summary>
    Paper,
}

public sealed class ConvertOptions
{
    public CoordinateMode Mode { get; set; } = CoordinateMode.RealSize;
    public ACadVersion Version { get; set; } = ACadVersion.AC1027; // AutoCAD 2013
    public bool SkipTemporaryPoints { get; set; } = true;

    /// <summary>Real size mode: keep groups at other scales (title block, details) where they sit on the
    /// sheet, by putting each in a block scaled to the main scale.</summary>
    public bool KeepLayoutForOtherScales { get; set; } = true;

    /// <summary>Fixed AutoCAD text height as a part of the Jw_cad height. Leave null to use the measured
    /// value of each font (see JwwToCadConverter.FontMetrics).</summary>
    public double? TextHeightFactor { get; set; }

    /// <summary>Fixed baseline raise as a part of the Jw_cad height. Leave null to use each font's descent.</summary>
    public double? TextBaselineShift { get; set; }

    /// <summary>The JWW file (for embedded file-name codes and linked images).</summary>
    public string? SourcePath { get; set; }

    /// <summary>The drawing being written: images from inside the JWW file are saved next to it.</summary>
    public string? OutputPath { get; set; }
}

public sealed class ConvertReport
{
    public Dictionary<string, int> Written { get; } = new();
    public Dictionary<string, int> Skipped { get; } = new();
    public List<string> Warnings { get; } = new();
    public double MainScale { get; set; }
    /// <summary>Image files written next to the drawing.</summary>
    public List<string> ImageFiles { get; } = new();

    internal void Add(string kind, int n = 1) => Written[kind] = Written.GetValueOrDefault(kind) + n;
    internal void Skip(string kind) => Skipped[kind] = Skipped.GetValueOrDefault(kind) + 1;

    public override string ToString() =>
        "Đã ghi: " + string.Join(", ", Written.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")) +
        (Skipped.Count > 0 ? " | Bỏ qua: " + string.Join(", ", Skipped.Select(k => $"{k.Key} {k.Value}")) : "");
}
