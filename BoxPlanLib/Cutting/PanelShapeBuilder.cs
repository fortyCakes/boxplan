using Clipper2Lib;

namespace BoxPlanLib.Cutting;

// Builds the outline polygon for a rectangular panel after subtracting rectangular notches
// from its edges. All notches must be axis-aligned and touch an edge; depths must not exceed
// half the panel dimension. Adjacent / overlapping notches and L-cuts at corners are handled
// implicitly by the Clipper2 boolean difference.
//
// The outline is returned as a CCW list of Vec2 vertices (no closing duplicate).
// Coordinate system: (0,0) at bottom-left, X right, Y up.
// Edge indices (CCW): 0=bottom, 1=right, 2=top, 3=left.
internal sealed class PanelShapeBuilder
{
    private const double Eps = 1e-9;
    private const int ClipperPrecision = 8;

    private readonly double _width;
    private readonly double _height;
    private readonly List<EdgeNotch>[] _notches;

    public PanelShapeBuilder(double width, double height, PipelineLogger? logger = null)
    {
        logger?.Log($"[panel] Creating PanelShapeBuilder width={width} height={height}");
        _width = width;
        _height = height;
        _notches = new[] { new List<EdgeNotch>(), new List<EdgeNotch>(), new List<EdgeNotch>(), new List<EdgeNotch>() };
    }

    // edgeIndex: 0=bottom 1=right 2=top 3=left
    // start: distance from the CCW-start corner of this edge
    // length: notch length along the edge
    // depth: how far the notch extends into the panel interior
    public void SubtractEdgeNotch(int edgeIndex, double start, double length, double depth)
    {
        if (length <= Eps || depth <= Eps) return;
        _notches[edgeIndex].Add(new EdgeNotch(start, length, depth));
    }

    public List<Model.Vec2> Build()
    {
        var subjects = new PathsD
        {
            new PathD
            {
                new PointD(0, 0),
                new PointD(_width, 0),
                new PointD(_width, _height),
                new PointD(0, _height),
            },
        };

        var clips = new PathsD();
        for (var ei = 0; ei < 4; ei++)
        {
            foreach (var n in _notches[ei])
                clips.Add(NotchPath(ei, n));
        }

        var solution = Clipper.Difference(subjects, clips, FillRule.NonZero, ClipperPrecision);

        // Pick the largest positive-area path. We expect exactly one outer with no holes
        // for valid notch configurations; "largest area" is a defensive fallback.
        PathD? best = null;
        var bestArea = double.NegativeInfinity;
        foreach (var p in solution)
        {
            var a = Clipper.Area(p);
            if (a > bestArea)
            {
                bestArea = a;
                best = p;
            }
        }

        if (best is null || bestArea <= Eps)
            return new List<Model.Vec2>();

        // Clipper.Area > 0 ⇒ CCW in y-up coords, which is what callers expect.
        var result = new List<Model.Vec2>(best.Count);
        foreach (var p in best)
            result.Add(new Model.Vec2(p.x, p.y));
        return result;
    }

    private PathD NotchPath(int edgeIndex, EdgeNotch n)
    {
        // Each clip is a CCW rectangle covering the notch volume inside the panel.
        // Outer edge sits exactly on the panel boundary; Clipper handles the coincident edge.
        return edgeIndex switch
        {
            0 => new PathD
            {
                new PointD(n.Start, 0),
                new PointD(n.Start + n.Length, 0),
                new PointD(n.Start + n.Length, n.Depth),
                new PointD(n.Start, n.Depth),
            },
            1 => new PathD
            {
                new PointD(_width - n.Depth, n.Start),
                new PointD(_width, n.Start),
                new PointD(_width, n.Start + n.Length),
                new PointD(_width - n.Depth, n.Start + n.Length),
            },
            2 => new PathD
            {
                new PointD(_width - n.Start - n.Length, _height - n.Depth),
                new PointD(_width - n.Start, _height - n.Depth),
                new PointD(_width - n.Start, _height),
                new PointD(_width - n.Start - n.Length, _height),
            },
            3 => new PathD
            {
                new PointD(0, _height - n.Start - n.Length),
                new PointD(n.Depth, _height - n.Start - n.Length),
                new PointD(n.Depth, _height - n.Start),
                new PointD(0, _height - n.Start),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(edgeIndex)),
        };
    }
}

internal record EdgeNotch(double Start, double Length, double Depth);
