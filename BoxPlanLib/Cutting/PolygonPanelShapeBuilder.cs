using BoxPlanLib.Model;
using Clipper2Lib;

namespace BoxPlanLib.Cutting;

// Like PanelShapeBuilder but for an arbitrary CCW polygon panel (e.g. an
// L-shape produced by merging boxes). Notches are specified by which polygon
// edge they sit on, the distance along that edge from its start vertex, the
// notch length and depth into the panel interior.
internal sealed class PolygonPanelShapeBuilder
{
    private const double Eps = 1e-9;
    private const int ClipperPrecision = 8;

    private readonly IReadOnlyList<Vec2> _polygon;
    private readonly List<PathD> _notchClips = new();
    private readonly List<PathD> _edgeAdditions = new();

    public PolygonPanelShapeBuilder(IReadOnlyList<Vec2> polygon, PipelineLogger? logger = null)
    {
        logger?.Log($"[panel] Creating PolygonPanelShapeBuilder with {polygon.Count} vertices");
        _polygon = polygon;
    }

    // edgeIndex: index of the polygon edge (i.e. segment from polygon[i] to polygon[i+1])
    // start: distance from polygon[i] toward polygon[i+1]
    // length: notch length along the edge
    // depth: how far the notch extends inward (CCW left-normal direction)
    public void SubtractEdgeNotch(int edgeIndex, double start, double length, double depth)
    {
        if (length <= Eps || depth <= Eps) return;
        var n = _polygon.Count;
        var p0 = _polygon[edgeIndex % n];
        var p1 = _polygon[(edgeIndex + 1) % n];
        var dx = p1.X - p0.X;
        var dy = p1.Y - p0.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < Eps) return;
        var ex = dx / len;
        var ey = dy / len;
        // Left-normal of the edge — points into the interior for a CCW polygon.
        var nx = -ey;
        var ny = ex;

        var aX = p0.X + ex * start;
        var aY = p0.Y + ey * start;
        var bX = p0.X + ex * (start + length);
        var bY = p0.Y + ey * (start + length);

        // Overshoot the clip slightly outward of the polygon boundary so Clipper2
        // doesn't treat an exactly-coincident clip edge as a separate hole. Without
        // this, a notch whose outer edge sits exactly on a non-axis-aligned polygon
        // edge can come back as a negative-area hole rather than a concave indent.
        const double BoundaryOverlap = 1e-4;
        _notchClips.Add(new PathD
        {
            new PointD(aX - nx * BoundaryOverlap, aY - ny * BoundaryOverlap),
            new PointD(bX - nx * BoundaryOverlap, bY - ny * BoundaryOverlap),
            new PointD(bX + nx * depth, bY + ny * depth),
            new PointD(aX + nx * depth, aY + ny * depth),
        });
    }

    // Adds material outward from the polygon edge over [start, start+length].
    // For CCW polygons, outward is the right-normal of the edge direction.
    public void AddEdgeExtension(int edgeIndex, double start, double length, double depth)
    {
        if (length <= Eps || depth <= Eps) return;
        var n = _polygon.Count;
        var p0 = _polygon[edgeIndex % n];
        var p1 = _polygon[(edgeIndex + 1) % n];
        var dx = p1.X - p0.X;
        var dy = p1.Y - p0.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < Eps) return;
        var ex = dx / len;
        var ey = dy / len;

        // Right-normal points outward for CCW polygons.
        var ox = ey;
        var oy = -ex;

        var aX = p0.X + ex * start;
        var aY = p0.Y + ey * start;
        var bX = p0.X + ex * (start + length);
        var bY = p0.Y + ey * (start + length);

        _edgeAdditions.Add(new PathD
        {
            new PointD(aX, aY),
            new PointD(bX, bY),
            new PointD(bX + ox * depth, bY + oy * depth),
            new PointD(aX + ox * depth, aY + oy * depth),
        });
    }

    public void SubtractPolygon(IReadOnlyList<Vec2> polygon)
    {
        if (polygon.Count < 3) return;
        _notchClips.Add(new PathD(polygon.Select(p => new PointD(p.X, p.Y))));
    }

    public List<Vec2> Build()
    {
        var subjects = new PathsD
        {
            new PathD(_polygon.Select(p => new PointD(p.X, p.Y))),
        };

        if (_edgeAdditions.Count > 0)
        {
            subjects = Clipper.Union(subjects, new PathsD(_edgeAdditions), FillRule.NonZero, ClipperPrecision);
        }

        var clips = new PathsD(_notchClips);

        var solution = Clipper.Difference(subjects, clips, FillRule.NonZero, ClipperPrecision);

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

        if (best is null || bestArea <= Eps) return new List<Vec2>();

        return RemoveCollinearVertices(best.Select(p => new Vec2(p.x, p.y)).ToList());
    }

    // Clipper2 may leave collinear vertices at the boundary between adjacent
    // notch clip rectangles or where a notch flattens an originally reflex
    // polygon corner into a straight edge. Strip them so KerfOffset's miter
    // logic doesn't shift the false vertex.
    private static List<Vec2> RemoveCollinearVertices(IReadOnlyList<Vec2> polygon)
    {
        const double SimplifyEps = 1e-6;
        var result = new List<Vec2>(polygon.Count);
        var n = polygon.Count;
        for (var i = 0; i < n; i++)
        {
            var prev = polygon[(i + n - 1) % n];
            var curr = polygon[i];
            var next = polygon[(i + 1) % n];
            var ax = curr.X - prev.X;
            var ay = curr.Y - prev.Y;
            var bx = next.X - curr.X;
            var by = next.Y - curr.Y;
            var cross = ax * by - ay * bx;
            var dot = ax * bx + ay * by;
            if (Math.Abs(cross) > SimplifyEps || dot <= 0) result.Add(curr);
        }
        return result;
    }
}
