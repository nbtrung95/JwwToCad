using System.Text;

namespace JwwToCad.Core.Jww;

/// <summary>
/// Reads Jw_cad .jww files (MFC CArchive serialization, version 7.x/8.x).
/// Drawing coordinates are stored in paper millimetres; real size = paper × layer group scale.
/// </summary>
public sealed class JwwReader
{
    static readonly Encoding ShiftJis = CreateShiftJis();

    static Encoding CreateShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    readonly byte[] _b;
    int _p;
    uint _ver;
    // MFC archive object map: index -> class name (null for plain objects).
    readonly Dictionary<int, string> _classes = new();
    int _mapCount;

    JwwReader(byte[] data) { _b = data; }

    public static JwwDocument Read(string path) => Read(File.ReadAllBytes(path));

    public static JwwDocument Read(byte[] data)
    {
        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 7) != "JwwData")
            throw new InvalidDataException("Không phải file JWW (thiếu chữ ký JwwData).");
        return new JwwReader(data).ReadDocument();
    }

    JwwDocument ReadDocument()
    {
        var doc = new JwwDocument();
        _p = 8;
        _ver = doc.Version = U32();
        if (_ver < 600)
            throw new NotSupportedException($"Phiên bản JWW {_ver} quá cũ (cần Jw_cad 6 trở lên).");
        doc.Memo = CStr();
        doc.PaperSize = U32();
        U32(); // current write layer group
        for (int g = 0; g < 16; g++)
        {
            var grp = doc.Groups[g] = new JwwLayerGroup();
            grp.State = U32(); U32(); // write layer
            grp.Scale = F64(); U32(); // protect
            for (int l = 0; l < 16; l++) { grp.LayerState[l] = U32(); U32(); }
        }
        // Fixed-size settings between layer table and layer names.
        _p += 4 * 14 + 4 * 5 + 4 + 4 + 8 * 2 + 8 + 4 + 4 + 8 + 8 * 2 + 8 * 2;
        for (int g = 0; g < 16; g++)
            for (int l = 0; l < 16; l++) doc.Groups[g].LayerNames[l] = CStr();
        for (int g = 0; g < 16; g++) doc.Groups[g].Name = CStr();

        // The remaining settings block is long and version dependent; jump to the entity list,
        // which is the first MFC object list after it.
        _p = FindEntityList();
        doc.Entities = ReadObList();
        foreach (var e in ReadObList())
            if (e is JwwBlockDef bd) doc.Blocks.Add(bd);
        if (_p + 4 <= _b.Length) doc.ImageCount = (int)U32();
        return doc;
    }

    int FindEntityList()
    {
        // First new-class tag: FFFF, schema (== version), name length, "CData..."
        for (int i = _p; i < _b.Length - 12; i++)
        {
            if (_b[i] != 0xFF || _b[i + 1] != 0xFF) continue;
            if (BitConverter.ToUInt16(_b, i + 2) != (ushort)_ver) continue;
            int n = BitConverter.ToUInt16(_b, i + 4);
            if (n < 5 || n > 32 || i + 6 + n > _b.Length) continue;
            if (Encoding.ASCII.GetString(_b, i + 6, 5) != "CData") continue;
            // Count precedes the tag: WORD, or FFFF + DWORD when >= 0xFFFF.
            if (i >= 6 && _b[i - 6] == 0xFF && _b[i - 5] == 0xFF) return i - 6;
            return i - 2;
        }
        throw new InvalidDataException("Không tìm thấy dữ liệu hình trong file JWW.");
    }

    List<JwwEntity> ReadObList()
    {
        uint n = U16();
        if (n == 0xFFFF) n = U32();
        var list = new List<JwwEntity>((int)Math.Min(n, 1_000_000));
        for (uint i = 0; i < n; i++) list.Add(ReadObject());
        return list;
    }

    JwwEntity ReadObject()
    {
        int start = _p;
        uint tag = U16();
        string cls;
        if (tag == 0xFFFF)
        {
            U16(); // schema
            int n = U16();
            cls = Encoding.ASCII.GetString(_b, _p, n); _p += n;
            _classes[++_mapCount] = cls;
        }
        else
        {
            bool isClass; int idx;
            if (tag == 0x7FFF) { uint t = U32(); isClass = (t & 0x80000000) != 0; idx = (int)(t & 0x7FFFFFFF); }
            else { isClass = (tag & 0x8000) != 0; idx = (int)(tag & 0x7FFF); }
            if (!isClass || !_classes.TryGetValue(idx, out cls!))
                throw new InvalidDataException($"Tham chiếu đối tượng không hỗ trợ tại byte {start}.");
        }
        _mapCount++; // every object also takes a map slot
        return cls switch
        {
            "CDataSen" => Line(),
            "CDataEnko" => Arc(),
            "CDataTen" => Point(),
            "CDataMoji" => Text(),
            "CDataSolid" => Solid(),
            "CDataSunpou" => Dimension(),
            "CDataBlock" => Insert(),
            "CDataList" => BlockDef(),
            _ => throw new NotSupportedException($"Loại đối tượng JWW chưa hỗ trợ: {cls} (byte {start}).")
        };
    }

    void Base(JwwEntity e)
    {
        e.Group = U32(); e.PenStyle = _b[_p++]; e.PenColor = U16();
        e.PenWidth = _ver >= 351 ? U16() : (ushort)0;
        e.Layer = U16(); e.LayerGroup = U16(); e.Flag = U16();
    }

    JwwLine Line() { var e = new JwwLine(); Base(e); e.X1 = F64(); e.Y1 = F64(); e.X2 = F64(); e.Y2 = F64(); return e; }

    JwwArc Arc()
    {
        var e = new JwwArc(); Base(e);
        e.CX = F64(); e.CY = F64(); e.Radius = F64(); e.StartAngle = F64(); e.SweepAngle = F64();
        e.Tilt = F64(); e.Flatness = F64(); e.IsFullCircle = U32() != 0; return e;
    }

    JwwPoint Point()
    {
        var e = new JwwPoint(); Base(e);
        e.X = F64(); e.Y = F64(); e.IsTemporary = U32() != 0;
        if (e.PenStyle == 100) { e.Code = U32(); e.Angle = F64(); e.Scale = F64(); }
        return e;
    }

    JwwText Text()
    {
        var e = new JwwText(); Base(e);
        e.StartX = F64(); e.StartY = F64(); e.EndX = F64(); e.EndY = F64();
        e.TextType = U32(); e.Width = F64(); e.Height = F64(); e.Spacing = F64(); e.AngleDeg = F64();
        e.Font = CStr(); e.Text = CStr(); return e;
    }

    JwwSolid Solid()
    {
        var e = new JwwSolid(); Base(e);
        for (int i = 0; i < 8; i++) e.Points[i] = F64();
        if (e.PenColor == 10) e.Rgb = U32();
        return e;
    }

    JwwDimension Dimension()
    {
        var e = new JwwDimension(); Base(e);
        e.Line = Line(); e.Text = Text();
        if (_ver >= 420)
        {
            e.SxfMode = U16(); e.Ext1 = Line(); e.Ext2 = Line();
            for (int i = 0; i < 4; i++) e.Points.Add(Point());
        }
        return e;
    }

    JwwBlockInsert Insert()
    {
        var e = new JwwBlockInsert(); Base(e);
        e.X = F64(); e.Y = F64(); e.ScaleX = F64(); e.ScaleY = F64(); e.AngleRad = F64(); e.BlockNumber = U32();
        return e;
    }

    JwwBlockDef BlockDef()
    {
        var e = new JwwBlockDef(); Base(e);
        e.Number = U32(); e.Referenced = U32() != 0; U32(); // CTime
        e.Name = CStr(); e.Entities = ReadObList(); return e;
    }

    // --- primitives ---
    ushort U16() { var v = BitConverter.ToUInt16(_b, _p); _p += 2; return v; }
    uint U32() { var v = BitConverter.ToUInt32(_b, _p); _p += 4; return v; }
    double F64() { var v = BitConverter.ToDouble(_b, _p); _p += 8; return v; }

    /// <summary>MFC CString: ANSI (Shift-JIS) or Unicode (prefixed FF FE FF).</summary>
    string CStr()
    {
        bool unicode = false;
        long n = _b[_p++];
        if (n == 0xFF)
        {
            n = U16();
            if (n == 0xFFFE)
            {
                unicode = true;
                n = _b[_p++];
                if (n == 0xFF) { n = U16(); if (n == 0xFFFF) n = U32(); }
            }
            else if (n == 0xFFFF) n = U32();
        }
        string s;
        if (unicode) { s = Encoding.Unicode.GetString(_b, _p, (int)n * 2); _p += (int)n * 2; }
        else { s = ShiftJis.GetString(_b, _p, (int)n); _p += (int)n; }
        return s;
    }
}
