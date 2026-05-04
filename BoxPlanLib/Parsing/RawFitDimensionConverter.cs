using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace BoxPlanLib.Parsing;

internal sealed class RawFitDimensionConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) =>
        type == typeof(RawFitDimension) || type == typeof(RawFitDimension?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        var raw = scalar.Value;

        if (string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return RawFitDimension.Auto;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return RawFitDimension.Fixed(value);
        }

        throw new YamlException(
            scalar.Start,
            scalar.End,
            $"Expected a number or the literal 'auto', got '{raw}'.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var dim = (RawFitDimension)(value ?? RawFitDimension.Auto);
        var text = dim.IsAuto ? "auto" : dim.Value.ToString(CultureInfo.InvariantCulture);
        emitter.Emit(new Scalar(text));
    }
}
