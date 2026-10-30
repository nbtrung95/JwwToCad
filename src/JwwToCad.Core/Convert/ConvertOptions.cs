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

    /// <summary>
    /// AutoCAD sizes TrueType text by roughly the cap height, while Jw_cad's text height is the full
    /// character cell. Measured in AutoCAD 2026 with ＭＳ ゴシック: the cell is about 1.24 × the AutoCAD
    /// height, so the AutoCAD height is the Jw height × 0.8.
    /// </summary>
    public double TextHeightFactor { get; set; } = 0.8;

    /// <summary>Raise the baseline by this part of the cell, because Jw_cad places the bottom of the cell
    /// (below the descenders) where AutoCAD places the baseline.</summary>
    public double TextBaselineShift { get; set; } = 0.12;
}

public sealed class ConvertReport
{
    public Dictionary<string, int> Written { get; } = new();
    public Dictionary<string, int> Skipped { get; } = new();
    public List<string> Warnings { get; } = new();
    public double MainScale { get; set; }

    internal void Add(string kind, int n = 1) => Written[kind] = Written.GetValueOrDefault(kind) + n;
    internal void Skip(string kind) => Skipped[kind] = Skipped.GetValueOrDefault(kind) + 1;

    public override string ToString() =>
        "Đã ghi: " + string.Join(", ", Written.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")) +
        (Skipped.Count > 0 ? " | Bỏ qua: " + string.Join(", ", Skipped.Select(k => $"{k.Key} {k.Value}")) : "");
}
