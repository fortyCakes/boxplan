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
            .WithTypeDiscriminatingNodeDeserializer(o =>
            {
                o.AddKeyValueTypeDiscriminator<RawShape>(
                    "type",
                    new Dictionary<string, Type>
                    {
                        { "box", typeof(RawBoxShape) },
                    });
                o.AddKeyValueTypeDiscriminator<RawFeature>(
                    "type",
                    new Dictionary<string, Type>
                    {
                        { "cutout", typeof(RawCutoutFeature) },
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
