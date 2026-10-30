using System.Globalization;

namespace JwwToCad.Core.Convert;

/// <summary>
/// Jw_cad "文字の埋め込み" codes (file name, memo, scale, dates), which Jw_cad replaces when it draws
/// the text. AutoCAD has no such codes, so they are replaced at conversion time. Jw_cad accepts one
/// code per text.
/// </summary>
public sealed class JwEmbeddedText
{
    public string? FilePath;
    public string Memo = "";
    public double Scale = 1;
    public DateTime Saved = DateTime.Now;
    public DateTime Now = DateTime.Now;

    static readonly CultureInfo Jp = CreateJp();
    static CultureInfo CreateJp()
    {
        var c = new CultureInfo("ja-JP");
        try { c.DateTimeFormat.Calendar = new JapaneseCalendar(); } catch (Exception) { }
        return c;
    }

    /// <summary>Returns the text with its embedded code replaced, or the text itself when it has none.</summary>
    public string Apply(string s, out string? used)
    {
        used = null;
        foreach (var (code, value) in Codes())
        {
            int i = Find(s, code);
            if (i < 0) continue;
            used = code;
            return s[..i] + value() + s[(i + code.Length)..];
        }
        return s;
    }

    // '=' and '&' codes look like ordinary text ("W=1,650", "梁成=H900", "A&B"), so they count only at
    // the start of the text. No code counts when an ASCII letter follows it.
    static int Find(string s, string code)
    {
        for (int i = s.IndexOf(code, StringComparison.Ordinal); i >= 0; i = s.IndexOf(code, i + 1, StringComparison.Ordinal))
        {
            int after = i + code.Length;
            if (after < s.Length && char.IsAsciiLetter(s[after])) continue;
            if (code[0] == '%' || i == 0) return i;
        }
        return -1;
    }

    IEnumerable<(string, Func<string>)> Codes()
    {
        string path = FilePath ?? "";
        string file = Path.GetFileName(path), stem = Path.GetFileNameWithoutExtension(path);
        string scale = Scale >= 1 ? $"1/{Scale:0.###}" : $"{1 / Scale:0.###}/1";
        string[] memo = Memo.Replace("\r", "").Split('\n');
        // Longest codes first.
        for (int n = 9; n >= 1; n--)
        {
            int k = n;
            yield return ($"&F{k}", () => Folder(path, k));
            yield return ($"%f{k}", () => file.Length > k ? file[..k] : file);
            yield return ($"&fs{k}", () => { var p = stem.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries); return k <= p.Length ? p[k - 1] : ""; });
        }
        yield return ("&F", () => path);
        yield return ("&f", () => stem);
        yield return ("%f", () => file);
        yield return ("%mm", () => Memo.Replace("\r", "").Replace("\n", " "));
        yield return ("%m1", () => memo.Length > 0 ? memo[0] : "");
        yield return ("%m2", () => memo.Length > 1 ? memo[1] : "");
        yield return ("%SS", () => scale);
        yield return ("%SP", () => scale);
        yield return ("%ss", () => $"{Scale:0.###}");
        yield return ("%sp", () => $"{Scale:0.###}");
        foreach (var (p, t) in new[] { ("=", Saved), ("&", Now) })
        {
            var d = t;
            yield return ($"{p}GEE", () => d.ToString("gg", Jp) + d.ToString("yy", Jp) + "年");
            yield return ($"{p}GE", () => d.ToString("gg", Jp) + d.ToString("%y", Jp));
            yield return ($"{p}ma", () => d.ToString("MMM", CultureInfo.InvariantCulture));
            yield return ($"{p}wa", () => d.ToString("ddd", Jp));
            yield return ($"{p}J", () => d.ToString("yyyy年M月d日", CultureInfo.InvariantCulture));
            yield return ($"{p}F", () => d.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture));
            yield return ($"{p}f", () => d.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
            yield return ($"{p}Y", () => d.ToString("yyyy", CultureInfo.InvariantCulture));
            yield return ($"{p}y", () => d.ToString("yy", CultureInfo.InvariantCulture));
            yield return ($"{p}E", () => d.ToString("gg", Jp));
            yield return ($"{p}G", () => d.ToString("%y", Jp));
            yield return ($"{p}m", () => d.Month.ToString(CultureInfo.InvariantCulture));
            yield return ($"{p}d", () => d.Day.ToString(CultureInfo.InvariantCulture));
            yield return ($"{p}H", () => d.ToString("HH", CultureInfo.InvariantCulture));
            yield return ($"{p}h", () => d.ToString("hh", CultureInfo.InvariantCulture));
            yield return ($"{p}M", () => d.ToString("mm", CultureInfo.InvariantCulture));
            yield return ($"{p}S", () => d.ToString("ss", CultureInfo.InvariantCulture));
            yield return ($"{p}n", () => d.Hour < 12 ? "AM" : "PM");
            yield return ($"{p}N", () => d.Hour < 12 ? "午前" : "午後");
        }
        yield return ("%y", () => Now.ToString("yyyy", CultureInfo.InvariantCulture));
    }

    static string Folder(string path, int up)
    {
        var dir = Path.GetDirectoryName(path);
        for (int i = 1; i < up && dir != null; i++) dir = Path.GetDirectoryName(dir);
        return dir == null ? "" : Path.GetFileName(dir);
    }
}
