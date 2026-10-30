using System.Text;
using JwwToCad.Core.Jww;

namespace JwwToCad.Core.Convert;

/// <summary>
/// Lays out a Jw_cad text the way Jw_cad does, in the text's own frame (paper mm):
/// u runs along the writing direction from the start point, v is perpendicular (to the left).
/// Handles character spacing (half-width characters get half the gap), vertical writing (縦字),
/// and the control codes ^u ^d ^c ^o ^w ^b ^B ^n ^$n ^! ^/ ^_ ^- ^# ^¥ ^&amp; ^% ^* ^^.
/// </summary>
public sealed class JwTextLayout
{
    /// <summary>A piece of text drawn as one AutoCAD TEXT.</summary>
    public sealed class Run
    {
        public string Text = "";
        /// <summary>Bottom-left corner of the first character cell (u, v), before the baseline shift.</summary>
        public double U, V;
        /// <summary>Cell size of a full-width character: width along the glyph's x axis, height along its y axis.</summary>
        public double CellW, CellH;
        /// <summary>Glyph x axis turned 90° left of the writing direction (upright characters in vertical writing).</summary>
        public bool Upright;
        public int? Pen;
        public bool Bold, Italic;
        public string? Font;
    }

    public sealed class Deco { public double U1, V1, U2, V2; public int? Pen; }

    public List<Run> Runs { get; } = new();
    public List<Deco> Lines { get; } = new();
    /// <summary>Length along the writing direction, comparable to Jw_cad's stored end point.</summary>
    public double Length { get; private set; }
    public bool UsedControlCodes { get; private set; }
    public List<string> Notes { get; } = new();

    static readonly Encoding Sjis = CreateSjis();
    static Encoding CreateSjis() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(932); }

    /// <summary>Half-width (1 byte in Shift-JIS) characters take half a cell.</summary>
    public static bool IsHalf(char c) => c < 0x80 || (c >= 0xFF61 && c <= 0xFF9F) || (c != '\0' && Sjis.GetByteCount(c.ToString()) == 1);

    // Full-width characters that Jw_cad turns sideways in vertical writing (long marks, brackets, dashes).
    const string SidewaysInVertical = "ー－―‐～〜（）「」『』【】〔〕［］｛｝＜＞〈〉《》＝…‥→←";

    public static JwTextLayout Build(JwwText t, string text)
    {
        var L = new JwTextLayout();
        L.Lay(t, text);
        return L;
    }

    struct State { public int? Pen; public bool Bold, Italic, Under, Strike; public string? Font; }

    void Lay(JwwText t, string s)
    {
        bool vertical = (t.Flag & 0x20) != 0;
        double w = t.Width > 0 ? t.Width : t.Height, h = t.Height, sp = t.Spacing;
        var st = new State();
        bool codesOn = true;
        double u = 0;
        // Previous character cell, for overlays.
        double prevU = 0, prevAdv = 0; bool havePrev = false;
        Run? cur = null;
        double lastGap = 0;

        void Flush() { if (cur != null && cur.Text.Length > 0) Runs.Add(cur); cur = null; }

        // Places one character (or overlay) and returns its advance.
        void Put(char c, double scale, double vOff, double uAt, bool advance)
        {
            bool half = IsHalf(c);
            double adv, cellW, cellH, uCell, vCell = vOff; bool upright;
            if (!vertical)
            {
                cellW = w * scale; cellH = h * scale; upright = false;
                adv = (half ? cellW / 2 : cellW);
                uCell = uAt;
            }
            else if (!half && SidewaysInVertical.IndexOf(c) < 0)
            {
                // Upright glyph: its x axis runs across the column, its y axis against the writing direction.
                cellW = w * scale; cellH = h * scale; upright = true;
                adv = cellH;
                uCell = uAt + cellH; // glyph's bottom edge is the far end of its cell
            }
            else
            {
                // Sideways glyph lying along the column: the column width is its height.
                cellH = w * scale; cellW = h * scale; upright = false;
                adv = half ? cellW / 2 : cellW;
                uCell = uAt;
            }
            bool merge = cur != null && advance && sp == 0 && !vertical
                && cur.Upright == upright && cur.CellW == cellW && cur.CellH == cellH && cur.V == vCell
                && cur.Pen == st.Pen && cur.Bold == st.Bold && cur.Italic == st.Italic && cur.Font == st.Font;
            if (merge) cur!.Text += c;
            else
            {
                Flush();
                cur = new Run { Text = c.ToString(), U = uCell, V = vCell, CellW = cellW, CellH = cellH, Upright = upright, Pen = st.Pen, Bold = st.Bold, Italic = st.Italic, Font = st.Font };
                if (!advance || vertical || sp != 0) Flush();
            }
            double across = vertical && upright ? cellW : (vertical ? cellH : cellH);
            if (st.Under) Lines.Add(new Deco { U1 = uAt, V1 = vOff - 0.1 * across, U2 = uAt + adv, V2 = vOff - 0.1 * across, Pen = st.Pen });
            if (st.Strike) Lines.Add(new Deco { U1 = uAt, V1 = vOff + 0.45 * across, U2 = uAt + adv, V2 = vOff + 0.45 * across, Pen = st.Pen });
            if (advance)
            {
                prevU = uAt; prevAdv = adv; havePrev = true;
                double gap = half ? sp / 2 : sp;
                u = uAt + adv + gap; lastGap = gap;
            }
        }

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '^' && i + 1 < s.Length)
            {
                char k = s[i + 1];
                if (!codesOn)
                {
                    if (k == '^') { codesOn = true; i++; UsedControlCodes = true; continue; }
                    Put(c, 1, 0, u, true); continue;
                }
                bool handled = true;
                switch (k)
                {
                    case 'u': case 'd': case 'c':
                        if (i + 2 < s.Length)
                        {
                            double v = k == 'u' ? h / 2 : k == 'c' ? h / 4 : 0;
                            Put(s[i + 2], 0.5, v, u, true);
                            i += 2;
                        }
                        else i++;
                        break;
                    case 'o': case 'w':
                        {
                            int n = k == 'o' ? 1 : (i + 3 < s.Length && IsHalf(s[i + 2]) && IsHalf(s[i + 3]) ? 2 : 1);
                            string ov = s.Substring(i + 2, Math.Min(n, s.Length - i - 2));
                            if (ov.Length > 0 && havePrev)
                            {
                                const double k2 = 0.7;
                                double ovLen = ov.Sum(ch => (IsHalf(ch) ? 0.5 : 1.0) * (vertical ? h : w) * k2);
                                double u0 = prevU + (prevAdv - ovLen) / 2;
                                double v0 = (vertical ? 0 : h * (1 - k2) / 2);
                                foreach (var ch in ov) { Put(ch, k2, v0, u0, false); u0 += (IsHalf(ch) ? 0.5 : 1.0) * (vertical ? h : w) * k2; }
                            }
                            else foreach (var ch in ov) Put(ch, 1, 0, u, true);
                            i += 1 + ov.Length;
                        }
                        break;
                    case 'b': case 'B': case 'n':
                        if (i + 2 < s.Length)
                        {
                            double back = k == 'B' ? 0.75 : k == 'b' ? 0.5 : 0.25;
                            double uAt = havePrev ? prevU + prevAdv * (1 - back) : u;
                            Put(s[i + 2], 1, 0, uAt, true);
                            i += 2;
                        }
                        else i++;
                        break;
                    case '$':
                        if (i + 2 < s.Length && s[i + 2] >= '1' && s[i + 2] <= '9') { Flush(); st.Pen = s[i + 2] - '0'; i += 2; }
                        else handled = false;
                        break;
                    case '!': Flush(); st.Bold = !st.Bold; i++; break;
                    case '/': Flush(); st.Italic = !st.Italic; i++; break;
                    case '_': Flush(); st.Under = !st.Under; i++; break;
                    case '-': Flush(); st.Strike = !st.Strike; i++; break;
                    case '#': Flush(); st = new State { Pen = st.Pen }; i++; break;
                    case '\\': case '¥': Flush(); st.Font = "ＭＳ ゴシック"; i++; break;
                    case '&': Flush(); st.Font = null; i++; break;
                    case '%': Flush(); st = new State(); i++; break;
                    case '*': codesOn = false; i++; break;
                    case '^': i++; break;
                    case >= '1' and <= '9':
                        Notes.Add($"mã ^{k} (thu hẹp chữ) chưa hỗ trợ, đã bỏ qua");
                        i++;
                        break;
                    default: handled = false; break;
                }
                if (handled) { UsedControlCodes = true; continue; }
            }
            Put(c, 1, 0, u, true);
        }
        Flush();
        Length = Math.Max(0, u - lastGap);
    }
}
