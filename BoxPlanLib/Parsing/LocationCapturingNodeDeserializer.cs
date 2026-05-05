using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace BoxPlanLib.Parsing;

internal sealed class LocationCapturingNodeDeserializer : INodeDeserializer
{
    private readonly INodeDeserializer _inner;

    public LocationCapturingNodeDeserializer(INodeDeserializer inner)
    {
        _inner = inner;
    }

    public bool Deserialize(
        IParser reader,
        Type expectedType,
        Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value,
        ObjectDeserializer rootDeserializer)
    {
        var start = reader.Current?.Start;
        var handled = _inner.Deserialize(reader, expectedType, nestedObjectDeserializer, out value, rootDeserializer);
        if (handled && value is IRawLocated located && start is { Line: > 0 } s)
        {
            located.Location = new RawLocation((int)s.Line, (int)s.Column);
        }
        return handled;
    }
}
