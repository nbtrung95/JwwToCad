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
}

public sealed class ConvertReport
{
    public Dictionary<string, int> Written { get; } = new();
    public Dictionary<string, int> Skipped { get; } = new();
    public List<string> Warnings { get; } = new();

    internal void Add(string kind, int n = 1) => Written[kind] = Written.GetValueOrDefault(kind) + n;
    internal void Skip(string kind) => Skipped[kind] = Skipped.GetValueOrDefault(kind) + 1;

    public override string ToString() =>
        "Đã ghi: " + string.Join(", ", Written.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")) +
        (Skipped.Count > 0 ? " | Bỏ qua: " + string.Join(", ", Skipped.Select(k => $"{k.Key} {k.Value}")) : "");
}
