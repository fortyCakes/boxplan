using BoxPlanLib.Model;

namespace BoxPlanLib;

public sealed class BoxPlanCuttableShape
{
    public required string Id { get; init; }
    public required Vec2 BoundingBoxMin { get; init; }
    public required Vec2 BoundingBoxMax { get; init; }
    public required CuttablePath Outline { get; init; }
    public required IReadOnlyList<CuttablePath> InteriorCuts { get; init; }
    public required IReadOnlyList<CuttablePath> Engravings { get; init; }
    public required IReadOnlyList<TextEngraving> TextEngravings { get; init; }
    public IReadOnlyList<RasterEngraving> RasterEngravings { get; init; } = Array.Empty<RasterEngraving>();
    public IReadOnlyList<SvgEngraving> SvgEngravings { get; init; } = Array.Empty<SvgEngraving>();
}

public sealed class TextEngraving
{
    public required string Text { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public Anchor Anchor { get; init; } = Anchor.Center;
    public required double Size { get; init; }
    public string Font { get; init; } = "sans-serif";
    public bool Bold { get; init; }
    public bool Italic { get; init; }
}

public sealed class RasterEngraving
{
    public required string Href { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public Anchor Anchor { get; init; } = Anchor.Center;
    public required double Width { get; init; }
    public required double Height { get; init; }
}

public sealed class SvgEngraving
{
    public required string Href { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public Anchor Anchor { get; init; } = Anchor.Center;
    public double? Width { get; init; }
    public double? Height { get; init; }
    public string? InlinedContent { get; init; }
    public double InlinedViewBoxWidth { get; init; }
    public double InlinedViewBoxHeight { get; init; }
}

public sealed class CuttablePath
{
    public required Vec2 Start { get; init; }
    public required IReadOnlyList<PathSegment> Segments { get; init; }
    public required bool Closed { get; init; }
}

public abstract record PathSegment;
public sealed record LineSegment(Vec2 To) : PathSegment;
public sealed record ArcSegment(Vec2 To, double Radius, bool Clockwise, bool LargeArc) : PathSegment;
