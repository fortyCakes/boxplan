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
    TopCenter,
    BottomCenter,
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

public sealed class Plan
{
    public required IReadOnlyList<Shape> Shapes { get; init; }
    public required IReadOnlyDictionary<string, Shape> ShapesById { get; init; }
}

public abstract class Shape
{
    public required string Id { get; init; }
    public required Origin Origin { get; init; }
    public required Vec3 Location { get; init; }
    public required IReadOnlyList<Face> Faces { get; init; }
    public required IReadOnlyList<DividerSet> Dividers { get; init; }
    public required IReadOnlyList<Insert> Inserts { get; init; }
    public required IReadOnlyList<Feature> Features { get; init; }
    public Fit? Fit { get; init; }
}

public sealed class BoxShape : Shape
{
    public Vec3? Dimensions { get; init; }
}

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

    public sealed record Fixed(double Value) : FitDimension;
}
