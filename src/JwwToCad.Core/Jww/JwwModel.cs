namespace JwwToCad.Core.Jww;

/// <summary>Attributes shared by every Jw_cad entity (CData).</summary>
public abstract class JwwEntity
{
    public uint Group;        // curve attribute / grouping id
    public byte PenStyle;     // line type 1-9 (100+ = special)
    public ushort PenColor;   // 1-9 standard, 100+ = SXF color
    public ushort PenWidth;
    public ushort Layer;      // 0-15
    public ushort LayerGroup; // 0-15
    public ushort Flag;
}

public sealed class JwwLine : JwwEntity { public double X1, Y1, X2, Y2; }

/// <summary>Circle, arc, ellipse or elliptic arc (CDataEnko). Angles in radians.</summary>
public sealed class JwwArc : JwwEntity
{
    public double CX, CY, Radius, StartAngle, SweepAngle, Tilt, Flatness;
    public bool IsFullCircle;
}

public sealed class JwwPoint : JwwEntity
{
    public double X, Y;
    public bool IsTemporary;
    public uint Code; public double Angle, Scale; // only when PenStyle == 100 (marker)
}

public sealed class JwwText : JwwEntity
{
    public double StartX, StartY, EndX, EndY;
    public uint TextType;
    public double Width, Height, Spacing, AngleDeg;
    public string Font = "";
    public string Text = "";
}

/// <summary>Filled quad (CDataSolid). Points in the order they are stored.</summary>
public sealed class JwwSolid : JwwEntity
{
    public double[] Points = new double[8];
    public uint? Rgb; // when PenColor == 10
}

public sealed class JwwDimension : JwwEntity
{
    public JwwLine Line = new();
    public JwwText Text = new();
    public ushort SxfMode;
    public JwwLine? Ext1, Ext2;
    public List<JwwPoint> Points = new();
}

public sealed class JwwBlockInsert : JwwEntity
{
    public double X, Y, ScaleX, ScaleY, AngleRad;
    public uint BlockNumber;
}

public sealed class JwwBlockDef : JwwEntity
{
    public uint Number;
    public bool Referenced;
    public string Name = "";
    public List<JwwEntity> Entities = new();
}

public sealed class JwwLayerGroup
{
    public uint State;      // 0 hidden, 1 display only, 2 editable, 3 current
    public double Scale;    // drawing scale denominator, e.g. 50 for 1/50
    public string Name = "";
    public uint[] LayerState = new uint[16];
    public string[] LayerNames = new string[16];
}

public sealed class JwwDocument
{
    public uint Version;
    public string Memo = "";
    public uint PaperSize;  // 0=A0 ... 4=A4, 8=2A ...
    public JwwLayerGroup[] Groups = new JwwLayerGroup[16];
    public List<JwwEntity> Entities = new();
    public List<JwwBlockDef> Blocks = new();
    public int ImageCount;
}
