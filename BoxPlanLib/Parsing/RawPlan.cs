using YamlDotNet.Serialization;

namespace BoxPlanLib.Parsing;

public readonly record struct RawLocation(int Line, int Column);

public interface IRawLocated
{
    [YamlIgnore] RawLocation? SourceLocation { get; set; }
}

public sealed class RawPlan : IRawLocated
{
    public List<RawShape> Shapes { get; set; } = new();

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

public abstract class RawShape : IRawLocated
{
    public string? Id { get; set; }

    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    public string? Origin { get; set; }
    public double[]? Location { get; set; }
    public bool? Disjoint { get; set; }
    public List<RawFace>? Faces { get; set; }
    public List<RawDividerSet>? Dividers { get; set; }
    public List<RawInsert>? Inserts { get; set; }
    public List<RawFeature>? Features { get; set; }
    public RawFit? Fit { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

public sealed class RawBoxShape : RawShape
{
    public double[]? Dimensions { get; set; }
    public List<RawScoop>? Scoops { get; set; }
}

public sealed class RawScoop : IRawLocated
{
    public string? Face { get; set; }
    public RawScoopEdge? Edge { get; set; }
    public double? Inset { get; set; }
    public double? Rise { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

// Holds the raw YAML payload for the scoop 'edge' field, which accepts either
// a scalar edge name, a YAML list of edge names, or the keyword 'all-edges'.
public sealed record RawScoopEdge(IReadOnlyList<string> Names, bool IsAllEdges)
{
    public static RawScoopEdge Single(string name) => new(new[] { name }, IsAllEdges: false);
    public static RawScoopEdge List(IReadOnlyList<string> names) => new(names, IsAllEdges: false);
    public static RawScoopEdge AllEdges { get; } = new(Array.Empty<string>(), IsAllEdges: true);
}

public sealed class RawFace : IRawLocated
{
    public string? Name { get; set; }
    public string? Type { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

public sealed class RawSplit : IRawLocated
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Z { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

public sealed class RawDividerSet : IRawLocated
{
    public RawSplit? Split { get; set; }
    public string? Axis { get; set; }
    public double[]? Positions { get; set; }
    public string? Facing { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

public sealed class RawInsert : IRawLocated
{
    public string? Fill { get; set; }

    [YamlMember(Alias = "cell")]
    public int[]? Cell { get; set; }

    [YamlMember(Alias = "ref")]
    public string? Ref { get; set; }

    public RawShape? Inline { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

public abstract class RawFeature : IRawLocated
{
    public string? Face { get; set; }

    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    public RawPosition? Position { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

public sealed class RawCutoutFeature : RawFeature
{
    public string? Shape { get; set; }
    public double? Diameter { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public RawCutoutRepeat? Repeat { get; set; }
}

public sealed class RawCutoutRepeat : IRawLocated
{
    public double[]? Spacing { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

public sealed class RawEngravingFeature : RawFeature
{
    public string? Text { get; set; }
    public double? Size { get; set; }
    public string? Font { get; set; }
    public string? Style { get; set; }
}

public sealed class RawLineEngravingFeature : RawFeature
{
    public string? Shape { get; set; }
    public double? Diameter { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}

public sealed class RawEngravingGridFeature : RawFeature
{
    public double? CellSize { get; set; }
    public string? Center { get; set; }
}

public sealed class RawPosition : IRawLocated
{
    public string? Anchor { get; set; }
    public double[]? Offset { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

public sealed class RawFit : IRawLocated
{
    public string? Mode { get; set; }
    public double? Clearance { get; set; }
    public RawFitDimension? Width { get; set; }
    public RawFitDimension? Height { get; set; }
    public RawFitDimension? Depth { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

public readonly record struct RawFitDimension(bool IsAuto, double Value, double Offset = 0)
{
    public static RawFitDimension Auto => new(true, 0, 0);
    public static RawFitDimension AutoWithOffset(double offset) => new(true, 0, offset);
    public static RawFitDimension Fixed(double value) => new(false, value, 0);
}

// ── Panel shape ─────────────────────────────────────────────────────────────

// A flat single-face shape. The 'profile' block uses the same discriminator and
// fields as prism shapes (prism / circle / hexagon / regular-polygon / etc.).
public sealed class RawPanelShape : RawShape
{
    public RawPrismShapeBase? Profile { get; set; }
}

// ── Prism shapes ────────────────────────────────────────────────────────────

// Abstract base shared by all prism-like shape types.
public abstract class RawPrismShapeBase : RawShape
{
    public double? Depth { get; set; }

    [YamlMember(Alias = "lateral-faces")]
    public List<RawLateralFace>? LateralFaces { get; set; }

    // Optional uniform scale factor for the back profile (centroid-aligned).
    // 1.0 (or omitted) = symmetric prism; <1 = back smaller than front (frustum-like);
    // >1 = back larger than front (inverted frustum).
    [YamlMember(Alias = "back-size")]
    public double? BackSize { get; set; }
}

// type: "prism" — explicit polygon via points list OR segment list.
public sealed class RawPrismShape : RawPrismShapeBase
{
    // Simple polygon: list of [x, z] pairs.
    public List<double[]>? Points { get; set; }

    // Segment-based polygon (supports arcs and bezier curves).
    public List<RawProfileSegment>? Segments { get; set; }
}

// type: "triangle" | "pentagon" | "hexagon" — regular polygon fitted to a bounding box.
public sealed class RawNamedPolygonShape : RawPrismShapeBase
{
    public double? Width { get; set; }
    public double? Height { get; set; }
}

// type: "regular-polygon" — N-sided regular polygon fitted to a bounding box.
public sealed class RawRegularPolygonShape : RawPrismShapeBase
{
    public int? Sides { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}

// type: "rectangle" — axis-aligned rectangular profile.
public sealed class RawRectangleShape : RawPrismShapeBase
{
    public double? Width { get; set; }
    public double? Height { get; set; }
}

// type: "circle"
public sealed class RawCircleShape : RawPrismShapeBase
{
    public double? Diameter { get; set; }
}

// type: "semicircle"
public sealed class RawSemicircleShape : RawPrismShapeBase
{
    public double? Diameter { get; set; }
}

// type: "quarter-circle"
public sealed class RawQuarterCircleShape : RawPrismShapeBase
{
    public double? Radius { get; set; }
}

// One entry in a segments list. Exactly one of LineTo / ArcTo / BezierTo will be set.
public sealed class RawProfileSegment : IRawLocated
{
    [YamlMember(Alias = "line-to")]
    public double[]? LineTo { get; set; }

    [YamlMember(Alias = "arc-to")]
    public double[]? ArcTo { get; set; }

    [YamlMember(Alias = "bezier-to")]
    public double[]? BezierTo { get; set; }

    public double? Radius { get; set; }
    public bool? Clockwise { get; set; }

    [YamlMember(Alias = "control-1")]
    public double[]? Control1 { get; set; }

    [YamlMember(Alias = "control-2")]
    public double[]? Control2 { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}

// Lateral face override (open/closed) for prism shapes.
public sealed class RawLateralFace : IRawLocated
{
    public int? Index { get; set; }
    public string? Type { get; set; }

    [YamlIgnore] public RawLocation? SourceLocation { get; set; }
}
