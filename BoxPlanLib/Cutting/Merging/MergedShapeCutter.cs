using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting.Merging;

internal static class MergedShapeCutter
{
    private const double Eps = 1e-6;

    public static void Emit(
        IReadOnlyList<MergedFace> faces,
        IReadOnlyList<SharedSegment> sharedSegments,
        BoxPlanSettings settings,
        List<BoxPlanCuttableShape> output)
    {
        var t = settings.MaterialThickness;
        var s = settings.FingerJointSize;

        var byFace = new List<List<SharedSegment>>(faces.Count);
        for (var i = 0; i < faces.Count; i++) byFace.Add(new List<SharedSegment>());
        foreach (var seg in sharedSegments) byFace[seg.FaceIndex].Add(seg);

        for (var fi = 0; fi < faces.Count; fi++)
        {
            var face = faces[fi];
            var faceName = face.Direction.ToFaceName();
            var builder = new Cutting.PolygonPanelShapeBuilder(face.Outline);
            var n = face.Outline.Count;

            // Group shared segments by edge index, sorted by start position.
            var byEdge = byFace[fi]
                .GroupBy(seg => seg.EdgeIndex)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Start).ToList());

            // Subtract finger-joint notches owned by the neighbour face.
            // The standard joint pattern reserves t at each end of a shared
                // segment for the perpendicular face's corner-cube to slot into.
                // At a REFLEX 2D corner, however, no corner cube exists — the
                // panel naturally extends into the corner — so no reservation
                // is needed and the finger pattern should run all the way to
                // the corner vertex.
            foreach (var (edgeIndex, segs) in byEdge)
            {
                var edgeLen = EdgeLength(face, edgeIndex);
                foreach (var seg in segs)
                {
                    var neighbourName = seg.NeighbourDirection.ToFaceName();
                    var lowerName = FacePriority.Lower(faceName, neighbourName);
                    var thisIsLower = lowerName == faceName;

                    var startAtVertex = seg.Start <= Eps;
                    var endAtVertex = Math.Abs(seg.Start + seg.Length - edgeLen) <= Eps;
                    var startVertexIndex = edgeIndex;
                    var endVertexIndex = (edgeIndex + 1) % n;
                    var reserveStart = !(startAtVertex && IsReflex(face.Outline, startVertexIndex));
                    var reserveEnd = !(endAtVertex && IsReflex(face.Outline, endVertexIndex));

                    var startInset = reserveStart ? t : 0;
                    var endInset = reserveEnd ? t : 0;
                    var inner = Math.Max(0, seg.Length - startInset - endInset);
                    if (inner <= Eps) continue;
                    var blocks = FingerJointPattern.Build(inner, s);
                    var cursor = seg.Start + startInset;
                    foreach (var block in blocks)
                    {
                        // PrimaryOwns blocks belong to the lower-priority face.
                        var ownedByThis = block.PrimaryOwns == thisIsLower;
                        if (!ownedByThis)
                            builder.SubtractEdgeNotch(edgeIndex, cursor, block.Length, t);
                        cursor += block.Length;
                    }
                }
            }

            // Convex-corner ownership: at each polygon vertex, three faces
            // (this one + the two neighbours touching the vertex) share a
            // t×t corner cube. The lowest-priority face owns it; the others
            // subtract a notch.
            for (var vi = 0; vi < n; vi++)
            {
                var prevEdge = (vi + n - 1) % n;
                var nextEdge = vi;
                if (!byEdge.TryGetValue(prevEdge, out var prevSegs) || prevSegs.Count == 0) continue;
                if (!byEdge.TryGetValue(nextEdge, out var nextSegs) || nextSegs.Count == 0) continue;

                var prevEdgeLength = EdgeLength(face, prevEdge);

                // Sub-segments touching this vertex: the LAST of prev edge
                // (its end is at the corner) and the FIRST of next edge.
                var prevSeg = prevSegs.Last(seg => Math.Abs(seg.Start + seg.Length - prevEdgeLength) < Eps);
                var nextSeg = nextSegs.First(seg => Math.Abs(seg.Start) < Eps);

                if (!IsConvex(face.Outline, vi)) continue;

                var prevName = prevSeg.NeighbourDirection.ToFaceName();
                var nextName = nextSeg.NeighbourDirection.ToFaceName();
                var p = FacePriority.Of(faceName);
                var owns = FacePriority.Of(prevName) >= p && FacePriority.Of(nextName) >= p;
                if (owns) continue;

                builder.SubtractEdgeNotch(prevEdge, prevEdgeLength - t, t, t);
                builder.SubtractEdgeNotch(nextEdge, 0, t, t);
            }

            var polygon = builder.Build();
            if (polygon.Count == 0) continue;
            var (path, bbMin, bbMax, _) = KerfOffset.OffsetOutwardAndTranslate(polygon, settings.Kerf);

            output.Add(new BoxPlanCuttableShape
            {
                Id = face.Id,
                BoundingBoxMin = bbMin,
                BoundingBoxMax = bbMax,
                Outline = path,
                InteriorCuts = Array.Empty<CuttablePath>(),
                Engravings = Array.Empty<CuttablePath>(),
            });
        }
    }

    private static double EdgeLength(MergedFace face, int edgeIndex)
    {
        var n = face.Outline.Count;
        var p0 = face.Outline[edgeIndex];
        var p1 = face.Outline[(edgeIndex + 1) % n];
        var dx = p1.X - p0.X;
        var dy = p1.Y - p0.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsConvex(IReadOnlyList<Vec2> polygon, int vi)
    {
        var n = polygon.Count;
        var prev = polygon[(vi + n - 1) % n];
        var curr = polygon[vi];
        var next = polygon[(vi + 1) % n];
        var ax = curr.X - prev.X;
        var ay = curr.Y - prev.Y;
        var bx = next.X - curr.X;
        var by = next.Y - curr.Y;
        var cross = ax * by - ay * bx;
        return cross > Eps;
    }

    private static bool IsReflex(IReadOnlyList<Vec2> polygon, int vi)
    {
        var n = polygon.Count;
        var prev = polygon[(vi + n - 1) % n];
        var curr = polygon[vi];
        var next = polygon[(vi + 1) % n];
        var ax = curr.X - prev.X;
        var ay = curr.Y - prev.Y;
        var bx = next.X - curr.X;
        var by = next.Y - curr.Y;
        var cross = ax * by - ay * bx;
        return cross < -Eps;
    }
}
