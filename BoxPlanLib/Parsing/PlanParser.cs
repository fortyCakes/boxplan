using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.BufferedDeserialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.NodeDeserializers;

namespace BoxPlanLib.Parsing;

public sealed class PlanParser
{
    private readonly IDeserializer _deserializer;

    public PlanParser()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .WithTypeConverter(new RawFitDimensionConverter())
            .WithTypeConverter(new RawScoopEdgeConverter())
            .WithTypeDiscriminatingNodeDeserializer(o =>
            {
                o.AddKeyValueTypeDiscriminator<RawShape>(
                    "type",
                    new Dictionary<string, Type>
                    {
                        { "box", typeof(RawBoxShape) },
                        { "panel", typeof(RawPanelShape) },
                        { "prism", typeof(RawPrismShape) },
                        { "triangle", typeof(RawNamedPolygonShape) },
                        { "pentagon", typeof(RawNamedPolygonShape) },
                        { "hexagon", typeof(RawNamedPolygonShape) },
                        { "regular-polygon", typeof(RawRegularPolygonShape) },
                        { "rectangle", typeof(RawRectangleShape) },
                        { "circle", typeof(RawCircleShape) },
                        { "semicircle", typeof(RawSemicircleShape) },
                        { "quarter-circle", typeof(RawQuarterCircleShape) },
                    });
                // The same map applies when the prism shape appears nested as a
                // panel's 'profile:' block (declared type is RawPrismShapeBase).
                o.AddKeyValueTypeDiscriminator<RawPrismShapeBase>(
                    "type",
                    new Dictionary<string, Type>
                    {
                        { "prism", typeof(RawPrismShape) },
                        { "triangle", typeof(RawNamedPolygonShape) },
                        { "pentagon", typeof(RawNamedPolygonShape) },
                        { "hexagon", typeof(RawNamedPolygonShape) },
                        { "regular-polygon", typeof(RawRegularPolygonShape) },
                        { "rectangle", typeof(RawRectangleShape) },
                        { "circle", typeof(RawCircleShape) },
                        { "semicircle", typeof(RawSemicircleShape) },
                        { "quarter-circle", typeof(RawQuarterCircleShape) },
                    });
                o.AddKeyValueTypeDiscriminator<RawFeature>(
                    "type",
                    new Dictionary<string, Type>
                    {
                        { "cutout", typeof(RawCutoutFeature) },
                        { "engraving", typeof(RawEngravingFeature) },
                        { "line-engraving", typeof(RawLineEngravingFeature) },
                        { "engraving-grid", typeof(RawEngravingGridFeature) },
                        { "split-cut", typeof(RawSplitCutFeature) },
                    });
            })
            .WithNodeDeserializer(
                inner => new LocationCapturingDiscriminatingDeserializer(inner),
                w => w.InsteadOf<TypeDiscriminatingNodeDeserializer>())
            .WithNodeDeserializer(
                inner => new LocationCapturingNodeDeserializer(inner),
                w => w.InsteadOf<ObjectNodeDeserializer>())
            .Build();
    }

    public ParseResult<RawPlan> Parse(string yaml)
    {
        try
        {
            var raw = _deserializer.Deserialize<RawPlan>(yaml);
            if (raw is null)
            {
                return ParseResult<RawPlan>.Fail(new PlanError(
                    Severity.Error,
                    "YAML document is empty.",
                    Line: 1,
                    Column: 1));
            }
            raw.Shapes ??= new List<RawShape>();
            return ParseResult<RawPlan>.Ok(raw);
        }
        catch (YamlException ex)
        {
            int? line = ex.Start.Line == 0 ? null : (int)ex.Start.Line;
            int? column = ex.Start.Column == 0 ? null : (int)ex.Start.Column;
            return ParseResult<RawPlan>.Fail(new PlanError(
                Severity.Error,
                ex.InnerException?.Message ?? ex.Message,
                Line: line,
                Column: column));
        }
    }
}
