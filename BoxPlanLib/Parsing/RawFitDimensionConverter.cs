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
        var raw = scalar.Value.Trim();

        if (string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return RawFitDimension.Auto;
        }

        if (raw.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
        {
            var rest = raw[4..].Trim();
            if (rest.Length > 0 && (rest[0] == '+' || rest[0] == '-'))
            {
                var sign = rest[0] == '+' ? 1 : -1;
                var numPart = rest[1..].Trim();
                if (double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var offset))
                {
                    return RawFitDimension.AutoWithOffset(sign * offset);
                }
            }

            throw new YamlException(
                scalar.Start,
                scalar.End,
                $"Expected 'auto', 'auto + N', or 'auto - N', got '{raw}'.");
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
        string text;
        if (!dim.IsAuto)
        {
            text = dim.Value.ToString(CultureInfo.InvariantCulture);
        }
        else if (dim.Offset == 0)
        {
            text = "auto";
        }
        else
        {
            var sign = dim.Offset >= 0 ? "+" : "-";
            text = $"auto {sign} {Math.Abs(dim.Offset).ToString(CultureInfo.InvariantCulture)}";
        }
        emitter.Emit(new Scalar(text));
    }
}
