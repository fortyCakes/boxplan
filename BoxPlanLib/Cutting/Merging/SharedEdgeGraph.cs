using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting.Merging;

// One contiguous sub-segment of a polygon edge that is shared with a sub-segment
// of an edge belonging to a different MergedFace. Coordinates are in the local
// (U, V) basis of the face that owns this record.
internal sealed record SharedSegment(
    int FaceIndex,
    int EdgeIndex,
    double Start,
    double Length,
    int NeighbourFaceIndex,
    FaceDirection NeighbourDirection);

internal static class SharedEdgeGraph
{
    private const double Eps = 1e-6;

    // For every outline edge of every MergedFace, find which sub-ranges are
    // shared with edges of other MergedFaces. Each shared sub-range is
    // emitted twice — once from each face's perspective — in local (U, V)
    // start/length coordinates.
    public static IReadOnlyList<SharedSegment> Build(IReadOnlyList<MergedFace> faces, PipelineLogger? logger = null)
    {
        logger?.Log($"[merge] Building shared edge graph for {faces.Count} faces");
        // Precompute every outline edge as a 3D axis-aligned segment.
        var edges = new List<Edge3D>();
        for (var fi = 0; fi < faces.Count; fi++)
        {
            var face = faces[fi];
            var n = face.Outline.Count;
            for (var ei = 0; ei < n; ei++)
            {
                var p0Local = face.Outline[ei];
                var p1Local = face.Outline[(ei + 1) % n];
                var p0 = face.ToWorld(p0Local);
                var p1 = face.ToWorld(p1Local);
                var axis = WorldEdgeAxis(p0, p1);
                if (axis is null) continue; // degenerate or non-axis-aligned (shouldn't happen)
                var (cA, cB) = OtherCoords(p0, axis.Value);
                var t0 = Coord(p0, axis.Value);
                var t1 = Coord(p1, axis.Value);
                edges.Add(new Edge3D(fi, ei, axis.Value, cA, cB, Math.Min(t0, t1), Math.Max(t0, t1), t1 >= t0, p0Local, p1Local));
            }
        }

        // Index edges by their 3D line so we only compare edges that are colinear.
        var byLine = edges
            .GroupBy(e => (e.Axis, RoundKey(e.CoordA), RoundKey(e.CoordB)))
            .Select(g => g.ToList())
            .ToList();

        var output = new List<SharedSegment>();
        foreach (var group in byLine)
        {
            for (var i = 0; i < group.Count; i++)
            {
                for (var j = i + 1; j < group.Count; j++)
                {
                    var a = group[i];
                    var b = group[j];
                    if (a.FaceIndex == b.FaceIndex) continue;
                    if (faces[a.FaceIndex].Direction.Axis == faces[b.FaceIndex].Direction.Axis) continue;

                    var lo = Math.Max(a.TMin, b.TMin);
                    var hi = Math.Min(a.TMax, b.TMax);
                    if (hi - lo <= Eps) continue;

                    // Convert the [lo, hi] world-axis interval into local Start/Length
                    // for both faces' outline edges.
                    output.Add(MakeSegment(faces[a.FaceIndex], a, lo, hi, faces[b.FaceIndex].Direction, b.FaceIndex));
                    output.Add(MakeSegment(faces[b.FaceIndex], b, lo, hi, faces[a.FaceIndex].Direction, a.FaceIndex));
                }
            }
        }

        return output;
    }

    private static SharedSegment MakeSegment(
        MergedFace face, Edge3D edge, double tLo, double tHi, FaceDirection neighbourDir, int neighbourFaceIndex)
    {
        // The outline edge runs from Local0 to Local1 in local (U, V). The
        // 3D parameter t corresponds to whichever local axis carries the
        // edge's world-axis direction. Compute start/length along the edge
        // direction (Local0 → Local1).
        var dx = edge.Local1.X - edge.Local0.X;
        var dy = edge.Local1.Y - edge.Local0.Y;
        var totalLength = Math.Sqrt(dx * dx + dy * dy);
        var tSpan = edge.TMax - edge.TMin;
        // Mapping of t to local position along the edge (Local0 → Local1).
        // The world-axis t increases in the direction encoded by edge.Forward.
        double startT, endT;
        if (edge.Forward)
        {
            startT = tLo - edge.TMin;
            endT = tHi - edge.TMin;
        }
        else
        {
            startT = edge.TMax - tHi;
            endT = edge.TMax - tLo;
        }
        var startLocal = totalLength * (startT / tSpan);
        var endLocal = totalLength * (endT / tSpan);
        return new SharedSegment(
            FaceIndex: edge.FaceIndex,
            EdgeIndex: edge.EdgeIndex,
            Start: startLocal,
            Length: endLocal - startLocal,
            NeighbourFaceIndex: neighbourFaceIndex,
            NeighbourDirection: neighbourDir);
    }

    private static Axis? WorldEdgeAxis(Vec3 p0, Vec3 p1)
    {
        var dx = Math.Abs(p1.X - p0.X);
        var dy = Math.Abs(p1.Y - p0.Y);
        var dz = Math.Abs(p1.Z - p0.Z);
        var nonZero = (dx > Eps ? 1 : 0) + (dy > Eps ? 1 : 0) + (dz > Eps ? 1 : 0);
        if (nonZero != 1) return null;
        if (dx > Eps) return Axis.X;
        if (dy > Eps) return Axis.Y;
        return Axis.Z;
    }

    private static (double, double) OtherCoords(Vec3 p, Axis axis) => axis switch
    {
        Axis.X => (p.Y, p.Z),
        Axis.Y => (p.X, p.Z),
        Axis.Z => (p.X, p.Y),
        _ => throw new InvalidOperationException(),
    };

    private static double Coord(Vec3 p, Axis a) => a switch
    {
        Axis.X => p.X,
        Axis.Y => p.Y,
        Axis.Z => p.Z,
        _ => throw new InvalidOperationException(),
    };

    private static long RoundKey(double v) => (long)Math.Round(v * 1e6);

    private sealed record Edge3D(
        int FaceIndex,
        int EdgeIndex,
        Axis Axis,
        double CoordA,
        double CoordB,
        double TMin,
        double TMax,
        bool Forward,
        Vec2 Local0,
        Vec2 Local1);
}
