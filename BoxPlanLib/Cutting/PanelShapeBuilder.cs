namespace BoxPlanLib.Cutting;

// Builds the outline polygon for a rectangular panel after subtracting rectangular notches
// from its edges. All notches must be axis-aligned and touch an edge; depths must not exceed
// half the panel dimension. Adjacent notches on the same edge are merged before tracing so
// that the resulting polygon has no degenerate spikes.
//
// The outline is returned as a CCW list of Vec2 vertices (no closing duplicate).
// Coordinate system: (0,0) at bottom-left, X right, Y up.
// Edge indices (CCW): 0=bottom, 1=right, 2=top, 3=left.
internal sealed class PanelShapeBuilder
{
    private static readonly Model.Vec2[] AlongCcw =
    {
        new(1, 0), new(0, 1), new(-1, 0), new(0, -1),
    };

    private static readonly Model.Vec2[] InwardCcw =
    {
        new(0, 1), new(-1, 0), new(0, -1), new(1, 0),
    };

    private const double Eps = 1e-9;

    private readonly double _width;
    private readonly double _height;
    private readonly List<EdgeNotch>[] _notches;

    public PanelShapeBuilder(double width, double height)
    {
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
        double[] edgeLengths = { _width, _height, _width, _height };

        Model.Vec2[] corners =
        {
            new(0, 0),
            new(_width, 0),
            new(_width, _height),
            new(0, _height),
        };

        // Sort and merge adjacent/overlapping notches per edge.
        var sorted = _notches.Select(ns => MergeNotches(ns)).ToArray();

        // Determine which corners are L-cuts: prevEdge's last notch ends at the corner
        // AND this edge's first notch starts at the corner.
        var cornerIsLCut = new bool[4];
        for (var i = 0; i < 4; i++)
        {
            var prevI = (i + 3) % 4;
            var prev = sorted[prevI];
            var curr = sorted[i];
            cornerIsLCut[i] =
                prev.Count > 0 &&
                prev[^1].Start + prev[^1].Length >= edgeLengths[prevI] - Eps &&
                curr.Count > 0 &&
                curr[0].Start <= Eps;
        }

        var result = new List<Model.Vec2>();

        void Emit(Model.Vec2 p)
        {
            if (result.Count == 0 || !NearlyEqual(result[^1], p))
                result.Add(p);
        }

        Model.Vec2 EdgePt(int ei, double pos)
        {
            var c = corners[ei];
            var a = AlongCcw[ei];
            return new Model.Vec2(c.X + a.X * pos, c.Y + a.Y * pos);
        }

        Model.Vec2 Offset(Model.Vec2 p, Model.Vec2 dir, double d) =>
            new(p.X + dir.X * d, p.Y + dir.Y * d);

        for (var ei = 0; ei < 4; ei++)
        {
            var nextEi = (ei + 1) % 4;
            var inward = InwardCcw[ei];
            var edgeNotches = sorted[ei];

            // Emit start corner if this is not an L-cut corner
            // (L-cut corner was already emitted by the previous edge's last-notch handling)
            if (!cornerIsLCut[ei])
                Emit(corners[ei]);

            // If this edge begins with an L-cut corner, skip the first notch (already handled)
            var startNotchIdx = cornerIsLCut[ei] ? 1 : 0;
            var cursor = cornerIsLCut[ei] && edgeNotches.Count > 0
                ? edgeNotches[0].Start + edgeNotches[0].Length
                : 0.0;

            for (var ni = startNotchIdx; ni < edgeNotches.Count; ni++)
            {
                var notch = edgeNotches[ni];
                var isLast = ni == edgeNotches.Count - 1;
                var endsAtCorner = notch.Start + notch.Length >= edgeLengths[ei] - Eps;
                var isEndLCut = isLast && endsAtCorner && cornerIsLCut[nextEi];

                // Flat segment up to notch start
                if (cursor < notch.Start - Eps)
                {
                    Emit(EdgePt(ei, notch.Start));
                    cursor = notch.Start;
                }

                if (isEndLCut)
                {
                    // L-cut: the last notch on this edge shares its corner with the first notch
                    // on the next edge. Emit the L-shaped corner geometry instead of the
                    // normal step-in / step-out cycle.
                    var nextAlong = AlongCcw[nextEi];
                    var nextInward = InwardCcw[nextEi];
                    var nextFirstNotch = sorted[nextEi][0];

                    // Step in from this edge
                    var pStepIn = Offset(EdgePt(ei, notch.Start), inward, notch.Depth);
                    Emit(pStepIn);

                    // Inner corner of the L (where the two inward depths meet)
                    // = corner[nextEi] + notch.Depth * along[nextEi] + nextFirstNotch.Depth * inward[nextEi]
                    var pInner = Offset(
                        Offset(corners[nextEi], nextAlong, notch.Depth),
                        nextInward, nextFirstNotch.Depth);
                    Emit(pInner);

                    // Along next edge to end of first notch of next edge
                    var pAlong = Offset(
                        Offset(corners[nextEi], nextAlong, nextFirstNotch.Start + nextFirstNotch.Length),
                        nextInward, nextFirstNotch.Depth);
                    Emit(pAlong);

                    // Step out from next edge
                    var pStepOut = EdgePt(nextEi, nextFirstNotch.Start + nextFirstNotch.Length);
                    Emit(pStepOut);

                    cursor = edgeLengths[ei];
                }
                else
                {
                    // Normal notch: step in, along, step out
                    Emit(Offset(EdgePt(ei, notch.Start), inward, notch.Depth));
                    Emit(Offset(EdgePt(ei, notch.Start + notch.Length), inward, notch.Depth));
                    Emit(EdgePt(ei, notch.Start + notch.Length));
                    cursor = notch.Start + notch.Length;
                }
            }
            // Remaining flat section to end of edge is implicit — the next iteration
            // emits the corner point (or the L-cut handles it).
        }

        return result;
    }

    // Sort notches by start, then merge those that are adjacent or overlapping (same depth).
    private static List<EdgeNotch> MergeNotches(List<EdgeNotch> raw)
    {
        if (raw.Count == 0) return raw;

        var sorted = raw.OrderBy(n => n.Start).ToList();
        var merged = new List<EdgeNotch> { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            var n = sorted[i];
            var prev = merged[^1];

            // Merge if same depth and touching or overlapping
            if (Math.Abs(prev.Depth - n.Depth) < Eps && prev.Start + prev.Length >= n.Start - Eps)
            {
                var newEnd = Math.Max(prev.Start + prev.Length, n.Start + n.Length);
                merged[^1] = new EdgeNotch(prev.Start, newEnd - prev.Start, prev.Depth);
            }
            else
            {
                merged.Add(n);
            }
        }

        return merged;
    }

    private bool NearlyEqual(Model.Vec2 a, Model.Vec2 b) =>
        Math.Abs(a.X - b.X) < Eps && Math.Abs(a.Y - b.Y) < Eps;
}

internal record EdgeNotch(double Start, double Length, double Depth);
