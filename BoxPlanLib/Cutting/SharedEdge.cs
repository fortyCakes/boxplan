using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

internal static class FacePriority
{
    private static readonly FaceName[] Order =
        { FaceName.Bottom, FaceName.Top, FaceName.Front, FaceName.Back, FaceName.Left, FaceName.Right };

    public static int Of(FaceName f) => Array.IndexOf(Order, f);

    public static FaceName Lower(FaceName a, FaceName b) => Of(a) < Of(b) ? a : b;
}

internal sealed class SharedEdge
{
    public required string Id { get; init; }
    public required FaceName FaceA { get; init; }
    public required FaceName FaceB { get; init; }
    public required double Length { get; init; }
    // Blocks describe the joint geometry over the *inner* region of the shared edge —
    // i.e. excluding a t-wide strip at each end which is reserved for corner-cube
    // geometry handled at the panel level. Sum(Blocks) == Length - 2t.
    public required IReadOnlyList<FingerBlock> Blocks { get; init; }
}

internal static class SharedEdgeTable
{
    public static IReadOnlyDictionary<string, SharedEdge> Build(Vec3 dims, BoxPlanSettings settings, PipelineLogger? logger = null)
    {
        var t = settings.MaterialThickness;
        var s = settings.FingerJointSize;

        var edges = new Dictionary<string, SharedEdge>();

        Add(edges, FaceName.Bottom, FaceName.Front, dims.X, t, s, logger);
        Add(edges, FaceName.Bottom, FaceName.Back,  dims.X, t, s, logger);
        Add(edges, FaceName.Bottom, FaceName.Left,  dims.Z, t, s, logger);
        Add(edges, FaceName.Bottom, FaceName.Right, dims.Z, t, s, logger);
        Add(edges, FaceName.Top,    FaceName.Front, dims.X, t, s, logger);
        Add(edges, FaceName.Top,    FaceName.Back,  dims.X, t, s, logger);
        Add(edges, FaceName.Top,    FaceName.Left,  dims.Z, t, s, logger);
        Add(edges, FaceName.Top,    FaceName.Right, dims.Z, t, s, logger);
        Add(edges, FaceName.Front,  FaceName.Left,  dims.Y, t, s, logger);
        Add(edges, FaceName.Front,  FaceName.Right, dims.Y, t, s, logger);
        Add(edges, FaceName.Back,   FaceName.Left,  dims.Y, t, s, logger);
        Add(edges, FaceName.Back,   FaceName.Right, dims.Y, t, s, logger);

        return edges;
    }

    public static string Id(FaceName a, FaceName b) =>
        FacePriority.Of(a) < FacePriority.Of(b)
            ? $"{a}-{b}"
            : $"{b}-{a}";

    private static void Add(Dictionary<string, SharedEdge> sink, FaceName a, FaceName b, double length, double t, double s, PipelineLogger? logger)
    {
        var lower = FacePriority.Lower(a, b);
        var higher = lower == a ? b : a;
        // Reserve a t-wide strip at each end of the edge for corner-cube geometry,
        // emitted at the panel level (TAB-around-corner if the panel owns the cube
        // vertex, NOTCH-into-corner otherwise). Blocks describe the joint over the
        // remaining inner region only.
        var innerLength = Math.Max(0, length - 2 * t);
        var blocks = BuildBlocks(innerLength, t, s, lower, higher, logger);
        var id = Id(a, b);
        sink[id] = new SharedEdge { Id = id, FaceA = a, FaceB = b, Length = length, Blocks = blocks };
    }

    private static IReadOnlyList<FingerBlock> BuildBlocks(double length, double t, double s, FaceName lower, FaceName higher, PipelineLogger? logger)
    {
        var blocks = FingerJointPattern.Build(length, s, t, msg => logger?.Warn($"[edge {lower}-{higher}] {msg}"));
        return blocks
            .Select(block => new FingerBlock(block.Length, block.PrimaryOwns ? lower : higher))
            .ToList();
    }
}
