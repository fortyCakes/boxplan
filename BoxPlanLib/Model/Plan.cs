namespace BoxPlanLib.Model;

public enum Origin
{
    BottomLeftFront,
    Center,
}

public enum FaceName
{
    Top,
    Bottom,
    Left,
    Right,
    Front,
    Back,
}

public enum FaceType
{
    Closed,
    Open,
}

public enum Axis
{
    X,
    Y,
    Z,
}

public enum Anchor
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
    LeftCenter,
    RightCenter,
    Center,
}

public enum CutoutShape
{
    Circle,
    Semicircle,
    Rectangle,
}

public enum FitMode
{
    Cell,
}

public sealed class BoxPlan
{
    public required IReadOnlyList<Shape> Shapes { get; init; }
    public required IReadOnlyDictionary<string, Shape> ShapesById { get; init; }
}

public abstract class Shape
{
    public required string Id { get; init; }
    public required Origin Origin { get; init; }
    public required Vec3 Location { get; init; }
    public bool Disjoint { get; init; }
    public required IReadOnlyList<Face> Faces { get; init; }
    public required IReadOnlyList<DividerSet> Dividers { get; init; }
    public required IReadOnlyList<Insert> Inserts { get; init; }
    public required IReadOnlyList<Feature> Features { get; init; }
    public Fit? Fit { get; init; }
}

public sealed class BoxShape : Shape
{
    public Vec3? Dimensions { get; init; }
    public IReadOnlyList<Scoop> Scoops { get; init; } = Array.Empty<Scoop>();
}

// An interior sloped panel inside a box. The scoop sits on a host face (e.g. bottom),
// rising to meet one of the four faces adjacent to the host (the "edge wall"). It joints
// to the host face along its toe, into the edge wall's interior at the rise line, and
// into both perpendicular caps along an oblique line from heel to toe.
public sealed record Scoop(FaceName Face, FaceName Edge, double Inset, double Rise);

public sealed record Face(FaceName Name, FaceType Type);

public sealed class DividerSet
{
    public required Axis Axis { get; init; }
    public required IReadOnlyList<double> Positions { get; init; }
    public FaceName? Facing { get; init; }
}

public sealed class Insert
{
    public (int Col, int Row, int Layer)? Cell { get; init; }
    public Shape Target { get; internal set; } = null!;
    public Vec3? ResolvedDimensions { get; internal set; }
    public Vec3? ResolvedLocation { get; internal set; }
    // Entry axis for entire-face inserts: the axis perpendicular to the open face.
    // null = default (Z, same as all-cells).  Remaps fit.Depth to the stated axis.
    public Axis? EntryAxis { get; init; }
}

public abstract class Feature
{
    public required FaceName Face { get; init; }
    public Position? Position { get; init; }
}

public sealed class CutoutFeature : Feature
{
    public required CutoutShape Shape { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public CutoutRepeat? Repeat { get; init; }
}

public sealed record CutoutRepeat(Vec2 Spacing);

public sealed class EngravingTextFeature : Feature
{
    public required string Text { get; init; }
    public required double Size { get; init; }
    public string Font { get; init; } = "sans-serif";
    public bool Bold { get; init; }
    public bool Italic { get; init; }
}

public sealed class LineEngravingFeature : Feature
{
    public required CutoutShape Shape { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
}

public enum GridCenter
{
    Space,
    Corner,
    Maximize,
}

public sealed class EngravingGridFeature : Feature
{
    public required double CellSize { get; init; }
    public GridCenter Center { get; init; } = GridCenter.Space;
}

// A non-closed cut line that splits a side panel into lid/base parts while
// remaining one physical panel in layout.
public sealed class SplitCutFeature : Feature
{
    // Height above the panel base in mm.
    public required double Height { get; init; }
    // Vertical deviation in mm applied to the normalised curve Y values.
    public double Amplitude { get; init; }
    // Polyline points with X normalised to [0,1], monotonic in X.
    public required IReadOnlyList<Vec2> NormalizedCurve { get; init; }
    public bool ValidateSeparation { get; init; } = true;
}

public sealed record Position(Anchor Anchor, Vec2 Offset);

public sealed class Fit
{
    public required FitMode Mode { get; init; }
    public required double Clearance { get; init; }
    public required FitDimension Width { get; init; }
    public required FitDimension Height { get; init; }
    public required FitDimension Depth { get; init; }
}

public abstract record FitDimension
{
    public sealed record Auto : FitDimension
    {
        public static readonly Auto Instance = new();
        private Auto() { }
    }

    public sealed record AutoOffset(double Offset) : FitDimension;

    public sealed record Fixed(double Value) : FitDimension;
}

// A single flat panel with no connected edges. Geometry comes from a PrismProfile
// (reused from prism shapes) — the panel emits exactly one cuttable face with no
// finger joints, suitable for standalone signs, labels or other 2D pieces.
public sealed class PanelShape : Shape
{
    public required PrismProfile Profile { get; init; }
    public Vec2 ProfileCentroid { get; init; } = Vec2.Zero;
}

public sealed class PrismShape : Shape
{
    public required PrismProfile Profile { get; init; }
    public double? Depth { get; init; }
    public required IReadOnlyList<PrismLateralFace> LateralFaces { get; init; }
    // Uniform scale factor for the back profile, centroid-aligned. 1.0 = symmetric (default).
    public double BackScale { get; init; } = 1.0;
    // Area centroid of the front profile (in profile X,Z coords). Used to scale back vertices.
    public Vec2 ProfileCentroid { get; init; } = Vec2.Zero;
}

public sealed class PrismProfile
{
    // Closed polygon in the X-Z plane. StartPoint is vertex 0; Segments define
    // subsequent vertices (and optional curves). The polygon is implicitly closed:
    // the last segment's endpoint should equal StartPoint.
    public required Vec2 StartPoint { get; init; }
    public required IReadOnlyList<ProfileSegment> Segments { get; init; }
}

public abstract record ProfileSegment
{
    public abstract Vec2 EndPoint { get; }

    public sealed record Line(Vec2 To) : ProfileSegment
    {
        public override Vec2 EndPoint => To;
    }

    public sealed record Bezier(Vec2 To, Vec2 Control1, Vec2 Control2) : ProfileSegment
    {
        public override Vec2 EndPoint => To;
    }

    public sealed record Arc(Vec2 To, double Radius, bool Clockwise) : ProfileSegment
    {
        public override Vec2 EndPoint => To;
    }
}

public sealed class PrismLateralFace
{
    public required int EdgeIndex { get; init; }
    public FaceType Type { get; init; } = FaceType.Closed;
    // Interior dihedral angle (degrees) between this lateral face and the NEXT one
    // at their shared vertical edge. Used to calculate adjusted slot depths.
    // 90.0 for the last face's edge back to the first (if all are right angles).
    public double DihedralAngleToNext { get; init; } = 90.0;
    // True for Arc or Bezier segments — signals the cutting pipeline to add flex cuts.
    public bool IsCurved { get; init; }
    // Pre-computed length of this edge in the X-Z plane (profile segment chord/arc length).
    public required double EdgeLength { get; init; }
}
