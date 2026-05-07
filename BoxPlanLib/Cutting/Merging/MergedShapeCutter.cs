using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting.Merging;

internal static class MergedShapeCutter
{
    private const double Eps = 1e-6;

    public static void Emit(
        IReadOnlyList<MergedFace> faces,
        IReadOnlyList<SharedSegment> sharedSegments,
        BoxPlanSettings settings,
        List<BoxPlanCuttableShape> output,
        PipelineLogger? logger = null)
    {
        logger?.Log($"[merge] Emitting merged shapes for {faces.Count} faces");
        var t = settings.MaterialThickness;
        var s = settings.FingerJointSize;

        var byFace = new List<List<SharedSegment>>(faces.Count);
        for (var i = 0; i < faces.Count; i++) byFace.Add(new List<SharedSegment>());
        foreach (var seg in sharedSegments) byFace[seg.FaceIndex].Add(seg);

        for (var fi = 0; fi < faces.Count; fi++)
        {
            var face = faces[fi];
            var faceName = face.Direction.ToFaceName();
            var builder = new Cutting.PolygonPanelShapeBuilder(face.Outline, logger);
            var n = face.Outline.Count;

            // Group shared segments by edge index, sorted by start position.
            var byEdge = byFace[fi]
                .GroupBy(seg => seg.EdgeIndex)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Start).ToList());

            var cornerOwnership = ComputeCornerOwnership(face, faces, byEdge, faceName);

            // Concave edge joints are interior to the model. The mating tab must
            // extend outside this face outline by t so the cut part has the
            // required physical extent.
            foreach (var (edgeIndex, segs) in byEdge)
            {
                foreach (var seg in segs)
                {
                    if (faceName != FaceName.Top && IsConcaveSharedSegment(face, seg, faces))
                    {
                        builder.AddEdgeExtension(edgeIndex, seg.Start, seg.Length, t);
                    }
                }
            }

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
                    var reserveStart = startAtVertex && ShouldReserveCornerCube(face.Outline, cornerOwnership, startVertexIndex, faceName);
                    var reserveEnd = endAtVertex && ShouldReserveCornerCube(face.Outline, cornerOwnership, endVertexIndex, faceName);

                    var startInset = reserveStart ? t : 0;
                    var endInset = reserveEnd ? t : 0;
                    var inner = Math.Max(0, seg.Length - startInset - endInset);
                    if (inner <= Eps) continue;
                    var blocks = FingerJointPattern.Build(inner, s, t, msg => logger?.Warn($"[merged {faceName}] {msg}"));
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
            //
            // Special case – convex-to-reflex: when one of the neighbours has a
            // REFLEX vertex at the same 3D world point its material naturally
            // wraps around the inner corner and fills the cube. Both convex faces
            // must then subtract a notch regardless of priority order.
            for (var vi = 0; vi < n; vi++)
            {
                if (!IsConvex(face.Outline, vi)) continue;
                var owns = cornerOwnership[vi];
                if (owns is not false) continue;

                var prevEdge = (vi + n - 1) % n;
                var nextEdge = vi;
                var prevEdgeLength = EdgeLength(face, prevEdge);
                builder.SubtractEdgeNotch(prevEdge, prevEdgeLength - t, t, t);
                builder.SubtractEdgeNotch(nextEdge, 0, t, t);
            }

            var polygon = builder.Build();
            if (polygon.Count == 0) continue;
            var (path, bbMin, bbMax, _) = KerfOffset.OffsetOutwardAndTranslate(polygon, settings.Kerf, logger);

            output.Add(new BoxPlanCuttableShape
            {
                Id = face.Id,
                BoundingBoxMin = bbMin,
                BoundingBoxMax = bbMax,
                Outline = path,
                InteriorCuts = Array.Empty<CuttablePath>(),
                Engravings = Array.Empty<CuttablePath>(),
            });
            logger?.Log($"[merge] Emitted merged shape {face.Id}");
        }
    }

    private static bool IsConcaveSharedSegment(
        MergedFace face,
        SharedSegment seg,
        IReadOnlyList<MergedFace> faces)
    {
        if (seg.Length <= Eps) return false;

        var startWorld = PointAlongEdgeWorld(face, seg.EdgeIndex, seg.Start);
        var endWorld = PointAlongEdgeWorld(face, seg.EdgeIndex, seg.Start + seg.Length);

        var excludeA = seg.FaceIndex;
        var excludeB = seg.NeighbourFaceIndex;
        var startReflexFaces = GetOtherReflexFacesAt(faces, startWorld, excludeA, excludeB);
        var endReflexFaces = GetOtherReflexFacesAt(faces, endWorld, excludeA, excludeB);
        return startReflexFaces.Overlaps(endReflexFaces);
    }

    private static Vec3 PointAlongEdgeWorld(MergedFace face, int edgeIndex, double distanceAlong)
    {
        var n = face.Outline.Count;
        var p0 = face.Outline[edgeIndex % n];
        var p1 = face.Outline[(edgeIndex + 1) % n];
        var dx = p1.X - p0.X;
        var dy = p1.Y - p0.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= Eps) return face.ToWorld(p0);

        var t = Math.Clamp(distanceAlong / len, 0, 1);
        var u = p0.X + dx * t;
        var v = p0.Y + dy * t;
        return face.ToWorld(new Vec2(u, v));
    }

    private static HashSet<int> GetOtherReflexFacesAt(
        IReadOnlyList<MergedFace> faces,
        Vec3 worldPt,
        int excludeFaceA,
        int excludeFaceB)
    {
        var reflexFaces = new HashSet<int>();
        for (var i = 0; i < faces.Count; i++)
        {
            if (i == excludeFaceA || i == excludeFaceB) continue;
            if (FaceIsReflexAt(faces[i], worldPt)) reflexFaces.Add(i);
        }

        return reflexFaces;
    }

    // null => no corner-cube ownership available (missing adjacency data).
    // true => this face owns the convex corner cube.
    // false => this face must yield the corner cube.
    private static bool?[] ComputeCornerOwnership(
        MergedFace face,
        IReadOnlyList<MergedFace> faces,
        IReadOnlyDictionary<int, List<SharedSegment>> byEdge,
        FaceName faceName)
    {
        var n = face.Outline.Count;
        var ownership = new bool?[n];
        for (var vi = 0; vi < n; vi++)
        {
            if (!IsConvex(face.Outline, vi))
            {
                ownership[vi] = null;
                continue;
            }

            if (!TryGetCornerSegments(face, byEdge, vi, out var prevSeg, out var nextSeg))
            {
                ownership[vi] = null;
                continue;
            }

            // If either neighbour is reflex at this world vertex, that neighbour's
            // material wraps the corner and this convex face cannot own the cube.
            var worldVertex = face.ToWorld(face.Outline[vi]);
            var prevNeighbourReflex = FaceIsReflexAt(faces[prevSeg.NeighbourFaceIndex], worldVertex);
            var nextNeighbourReflex = FaceIsReflexAt(faces[nextSeg.NeighbourFaceIndex], worldVertex);
            if (prevNeighbourReflex || nextNeighbourReflex)
            {
                ownership[vi] = false;
                continue;
            }

            var prevName = prevSeg.NeighbourDirection.ToFaceName();
            var nextName = nextSeg.NeighbourDirection.ToFaceName();
            var p = FacePriority.Of(faceName);
            ownership[vi] = FacePriority.Of(prevName) >= p && FacePriority.Of(nextName) >= p;
        }

        return ownership;
    }

    private static bool TryGetCornerSegments(
        MergedFace face,
        IReadOnlyDictionary<int, List<SharedSegment>> byEdge,
        int vertexIndex,
        out SharedSegment prevSeg,
        out SharedSegment nextSeg)
    {
        var n = face.Outline.Count;
        var prevEdge = (vertexIndex + n - 1) % n;
        var nextEdge = vertexIndex;
        prevSeg = null!;
        nextSeg = null!;

        if (!byEdge.TryGetValue(prevEdge, out var prevSegs) || prevSegs.Count == 0) return false;
        if (!byEdge.TryGetValue(nextEdge, out var nextSegs) || nextSegs.Count == 0) return false;

        var prevEdgeLength = EdgeLength(face, prevEdge);
        prevSeg = prevSegs.LastOrDefault(seg => Math.Abs(seg.Start + seg.Length - prevEdgeLength) < Eps)!;
        nextSeg = nextSegs.FirstOrDefault(seg => Math.Abs(seg.Start) < Eps)!;
        return prevSeg is not null && nextSeg is not null;
    }

    private static bool ShouldReserveCornerCube(
        IReadOnlyList<Vec2> polygon,
        IReadOnlyList<bool?> cornerOwnership,
        int vertexIndex,
        FaceName faceName)
    {
        if (IsReflex(polygon, vertexIndex))
        {
            return false;
        }

        var owns = cornerOwnership[vertexIndex];
        if (owns is bool ownsCorner)
        {
            if (!ownsCorner)
            {
                return true;
            }

            // The staircase run/rise issue shows up on front/back merged faces:
            // allow owned convex tabs to run to the corner there.
            if (faceName is FaceName.Front or FaceName.Back)
            {
                return false;
            }

            return true;
        }

        return true;
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

    // Returns true if the given face has a REFLEX vertex at the specified 3D world point.
    // A reflex vertex means the face material naturally wraps around the inner corner and
    // physically occupies the corner-cube volume — both convex neighbours must yield notches.
    private static bool FaceIsReflexAt(MergedFace face, Vec3 worldPt)
    {
        var n = face.Outline.Count;
        for (var vi = 0; vi < n; vi++)
        {
            var wp = face.ToWorld(face.Outline[vi]);
            if (Math.Abs(wp.X - worldPt.X) < Eps &&
                Math.Abs(wp.Y - worldPt.Y) < Eps &&
                Math.Abs(wp.Z - worldPt.Z) < Eps)
            {
                return IsReflex(face.Outline, vi);
            }
        }
        return false;
    }
}
