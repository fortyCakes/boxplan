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
