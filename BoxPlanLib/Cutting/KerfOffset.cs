using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

internal static class KerfOffset
{
    // Compute a closed-polygon kerf offset using miter joins. Each vertex is moved
    // outward by k along the bisector of the two adjacent edge normals. This naturally
    // handles closure without needing a separate start/end convention.
    public static (CuttablePath path, Vec2 bbMin, Vec2 bbMax, Vec2 translation) OffsetOutwardAndTranslate(
        IReadOnlyList<Vec2> polygon, double kerf, PipelineLogger? logger = null)
    {
        logger?.Log($"[kerf] Offsetting polygon with {polygon.Count} vertices by kerf={kerf}");
        var k = kerf / 2.0;
        var n = polygon.Count;
        var offsetPts = new Vec2[n];

        for (var i = 0; i < n; i++)
        {
            var prev = polygon[(i + n - 1) % n];
            var curr = polygon[i];
            var next = polygon[(i + 1) % n];

            var dxIn = curr.X - prev.X;
            var dyIn = curr.Y - prev.Y;
            var lenIn = Math.Sqrt(dxIn * dxIn + dyIn * dyIn);

            var dxOut = next.X - curr.X;
            var dyOut = next.Y - curr.Y;
            var lenOut = Math.Sqrt(dxOut * dxOut + dyOut * dyOut);

            if (lenIn < 1e-9 || lenOut < 1e-9)
            {
                offsetPts[i] = curr;
                continue;
            }

            // Outward normal = right-perpendicular of edge direction (same convention
            // as the per-segment offset used elsewhere in this class).
            var nxIn  =  dyIn  / lenIn;
            var nyIn  = -dxIn  / lenIn;
            var nxOut =  dyOut / lenOut;
            var nyOut = -dxOut / lenOut;

            offsetPts[i] = new Vec2(curr.X + k * (nxIn + nxOut), curr.Y + k * (nyIn + nyOut));
        }

        var minX = double.MaxValue; var minY = double.MaxValue;
        var maxX = double.MinValue; var maxY = double.MinValue;
        foreach (var p in offsetPts)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        var translated = offsetPts.Select(p => new Vec2(p.X - minX, p.Y - minY)).ToList();
        var pathSegments = new List<PathSegment>(n);
        for (var i = 1; i < n; i++)
            pathSegments.Add(new LineSegment(translated[i]));
        pathSegments.Add(new LineSegment(translated[0])); // close

        var path = new CuttablePath
        {
            Start = translated[0],
            Segments = pathSegments,
            Closed = true,
        };
        var bbMin = new Vec2(0, 0);
        var bbMax = new Vec2(maxX - minX, maxY - minY);
        return (path, bbMin, bbMax, new Vec2(-minX, -minY));
    }
}
