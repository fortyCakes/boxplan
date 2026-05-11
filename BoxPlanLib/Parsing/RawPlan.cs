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

public readonly record struct RawFitDimension(bool IsAuto, double Value)
{
    public static RawFitDimension Auto => new(true, 0);
    public static RawFitDimension Fixed(double value) => new(false, value);
}
