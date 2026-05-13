using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace BoxPlanLib.Parsing;

// Handles three YAML shapes for a scoop's `edge:` field:
//   edge: left            → RawScoopEdge.Single("left")
//   edge: [left, right]   → RawScoopEdge.List(["left", "right"])
//   edge: all-edges       → RawScoopEdge.AllEdges
internal sealed class RawScoopEdgeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(RawScoopEdge);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.Current is SequenceStart)
        {
            parser.Consume<SequenceStart>();
            var names = new List<string>();
            while (parser.Current is not SequenceEnd)
            {
                var item = parser.Consume<Scalar>();
                names.Add(item.Value.Trim());
            }
            parser.Consume<SequenceEnd>();
            return RawScoopEdge.List(names);
        }

        var scalar = parser.Consume<Scalar>();
        var raw = scalar.Value.Trim();
        if (string.Equals(raw, "all-edges", StringComparison.OrdinalIgnoreCase))
        {
            return RawScoopEdge.AllEdges;
        }
        return RawScoopEdge.Single(raw);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var edge = (RawScoopEdge?)value;
        if (edge is null || edge.IsAllEdges)
        {
            emitter.Emit(new Scalar("all-edges"));
            return;
        }
        if (edge.Names.Count == 1)
        {
            emitter.Emit(new Scalar(edge.Names[0]));
            return;
        }
        emitter.Emit(new SequenceStart(null, null, isImplicit: true, SequenceStyle.Flow));
        foreach (var name in edge.Names) emitter.Emit(new Scalar(name));
        emitter.Emit(new SequenceEnd());
    }
}
