using YamlDotNet.Serialization;

namespace BoxPlanLib.Parsing;

public sealed class RawPlan
{
    public List<RawShape> Shapes { get; set; } = new();
}

public abstract class RawShape
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
}

public sealed class RawBoxShape : RawShape
{
    public double[]? Dimensions { get; set; }
}

public sealed class RawFace
{
    public string? Name { get; set; }
    public string? Type { get; set; }
}

public sealed class RawSplit
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Z { get; set; }
}

public sealed class RawDividerSet
{
    public RawSplit? Split { get; set; }
    public string? Axis { get; set; }
    public double[]? Positions { get; set; }
    public string? Facing { get; set; }
}

public sealed class RawInsert
{
    public string? Fill { get; set; }

    [YamlMember(Alias = "cell")]
    public int[]? Cell { get; set; }

    [YamlMember(Alias = "ref")]
    public string? Ref { get; set; }

    public RawShape? Inline { get; set; }
}

public abstract class RawFeature
{
    public string? Face { get; set; }

    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    public RawPosition? Position { get; set; }
}

public sealed class RawCutoutFeature : RawFeature
{
    public string? Shape { get; set; }
    public double? Diameter { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}

public sealed class RawPosition
{
    public string? Anchor { get; set; }
    public double[]? Offset { get; set; }
}

public sealed class RawFit
{
    public string? Mode { get; set; }
    public double? Clearance { get; set; }
    public RawFitDimension? Width { get; set; }
    public RawFitDimension? Height { get; set; }
    public RawFitDimension? Depth { get; set; }
}

public readonly record struct RawFitDimension(bool IsAuto, double Value)
{
    public static RawFitDimension Auto => new(true, 0);
    public static RawFitDimension Fixed(double value) => new(false, value);
}
