using BoxPlanLib.Cutting;
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

        // Pre-compute divider-to-wall tab patterns and wall slot specs.
        ComputeDividerJoints(faces, settings,
            out var wallSlots,
            out var dividerEdgeTabs,
            out var dividerInteriorSlots,
            logger);

        for (var fi = 0; fi < faces.Count; fi++)
        {
            var face = faces[fi];
            var faceName = face.Direction.ToFaceName();
            var panelOutline = GrowBasePanelOutline(face, faces, byFace[fi], t);
            var builder = new Cutting.PolygonPanelShapeBuilder(panelOutline, logger);
            var n = face.Outline.Count;

            if (face.IsInternalDivider)
            {
                // Apply tab notches on each connected edge of the divider panel.
                if (dividerEdgeTabs.TryGetValue(fi, out var tabs))
                {
                    foreach (var (edgeIndex, spans) in tabs)
                    {
                        var cursor = 0.0;
                        foreach (var span in spans)
                        {
                            if (span.Kind is DividerJointSpanKind.DividerTab or DividerJointSpanKind.EndInset)
                                builder.SubtractEdgeNotch(edgeIndex, cursor, span.Length, t);
                            cursor += span.Length;
                        }
                    }
                }
            }
            else
            {
                // Group shared segments by edge index, sorted by start position.
                var byEdge = byFace[fi]
                    .GroupBy(seg => seg.EdgeIndex)
                    .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Start).ToList());

                var cornerOwnership = ComputeCornerOwnership(face, faces, byEdge, faceName);

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
                    var panelEdgeLen = EdgeLength(panelOutline, edgeIndex);
                    foreach (var seg in segs)
                    {
                        var neighbourName = seg.NeighbourDirection.ToFaceName();
                        var lowerName = FacePriority.Lower(faceName, neighbourName);
                        var thisIsLower = lowerName == faceName;

                        var coversFullEdge = seg.Start <= Eps && Math.Abs(seg.Start + seg.Length - edgeLen) <= Eps;
                        var mappedStart = coversFullEdge
                            ? 0
                            : MapDistanceToPanelEdge(face.Outline, panelOutline, edgeIndex, seg.Start);
                        var mappedEnd = coversFullEdge
                            ? panelEdgeLen
                            : MapDistanceToPanelEdge(face.Outline, panelOutline, edgeIndex, seg.Start + seg.Length);
                        var startAtVertex = mappedStart <= Eps;
                        var endAtVertex = Math.Abs(mappedEnd - panelEdgeLen) <= Eps;
                        var startVertexIndex = edgeIndex;
                        var endVertexIndex = (edgeIndex + 1) % n;
                        var reserveStart = startAtVertex && ShouldReserveCornerCube(face.Outline, cornerOwnership, startVertexIndex);
                        var reserveEnd = endAtVertex && ShouldReserveCornerCube(face.Outline, cornerOwnership, endVertexIndex);

                        var startInset = reserveStart ? t : 0;
                        var endInset = reserveEnd ? t : 0;
                        var mappedLength = Math.Max(0, mappedEnd - mappedStart);
                        var inner = Math.Max(0, mappedLength - startInset - endInset);
                        if (inner <= Eps) continue;

                        var blocks = FingerJointPattern.Build(inner, s, t, msg => logger?.Warn($"[merged {faceName}] {msg}"));

                        var cursor = mappedStart + startInset;
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
                    var prevEdgeLength = EdgeLength(panelOutline, prevEdge);
                    builder.SubtractEdgeNotch(prevEdge, prevEdgeLength - t, t, t);
                    builder.SubtractEdgeNotch(nextEdge, 0, t, t);
                }
            }

            var polygon = builder.Build();
            if (polygon.Count == 0) continue;
            var (path, bbMin, bbMax, translation) = KerfOffset.OffsetOutwardAndTranslate(polygon, settings.Kerf, logger);

            var interiorCuts = new List<CuttablePath>();
            if (!face.IsInternalDivider && wallSlots.TryGetValue(fi, out var wSlots))
            {
                foreach (var slot in wSlots)
                    interiorCuts.Add(CutoutBuilder.BuildSlotRectangle(slot, settings.Kerf, translation, logger));
            }
            if (face.IsInternalDivider && dividerInteriorSlots.TryGetValue(fi, out var dSlots))
            {
                foreach (var slot in dSlots)
                    interiorCuts.Add(CutoutBuilder.BuildSlotRectangle(slot, settings.Kerf, translation, logger));
            }

            output.Add(new BoxPlanCuttableShape
            {
                Id = face.Id,
                BoundingBoxMin = bbMin,
                BoundingBoxMax = bbMax,
                Outline = path,
                InteriorCuts = interiorCuts.ToArray(),
                Engravings = Array.Empty<CuttablePath>(),
                TextEngravings = Array.Empty<TextEngraving>(),
            });
            logger?.Log($"[merge] Emitted merged shape {face.Id}");
        }
    }

    private static void ComputeDividerJoints(
        IReadOnlyList<MergedFace> faces,
        BoxPlanSettings settings,
        out Dictionary<int, List<SlotSpec>> wallSlotsOut,
        out Dictionary<int, List<(int EdgeIndex, IReadOnlyList<DividerJointSpan> Spans)>> dividerTabsOut,
        out Dictionary<int, List<SlotSpec>> dividerInteriorSlotsOut,
        PipelineLogger? logger = null)
    {
        wallSlotsOut = new Dictionary<int, List<SlotSpec>>();
        dividerTabsOut = new Dictionary<int, List<(int, IReadOnlyList<DividerJointSpan>)>>();
        dividerInteriorSlotsOut = new Dictionary<int, List<SlotSpec>>();

        var t = settings.MaterialThickness;
        var s = settings.FingerJointSize;

        // Build a lookup from (axis, sign, roundedPlane) → face index for outer walls.
        var wallFaceMap = new Dictionary<(Axis, int, long), int>();
        for (var i = 0; i < faces.Count; i++)
        {
            if (faces[i].IsInternalDivider) continue;
            var f = faces[i];
            wallFaceMap[(f.Direction.Axis, f.Direction.Sign, RoundKey(f.Plane))] = i;
        }

        for (var fi = 0; fi < faces.Count; fi++)
        {
            var face = faces[fi];
            if (!face.IsInternalDivider) continue;

            var divAxis = face.Direction.Axis;
            var divPlane = face.Plane;
            var n = face.Outline.Count;
            var tabs = new List<(int EdgeIndex, IReadOnlyList<DividerJointSpan> Spans)>();

            for (var ei = 0; ei < n; ei++)
            {
                var p0 = face.ToWorld(face.Outline[ei]);
                var p1 = face.ToWorld(face.Outline[(ei + 1) % n]);

                // Determine which world axis this edge runs along.
                Axis edgeAxis;
                if (Math.Abs(p1.X - p0.X) > Eps) edgeAxis = Axis.X;
                else if (Math.Abs(p1.Y - p0.Y) > Eps) edgeAxis = Axis.Y;
                else if (Math.Abs(p1.Z - p0.Z) > Eps) edgeAxis = Axis.Z;
                else continue;

                // The wall axis is the third axis (not divAxis, not edgeAxis).
                var wallAxis = ThirdAxis(divAxis, edgeAxis);
                var wallPlane = GetCoord(p0, wallAxis);

                // Determine sign: is this edge at the min or max of wallAxis?
                var worldVerts = face.Outline.Select(uv => face.ToWorld(uv)).ToList();
                var divMin = worldVerts.Min(p => GetCoord(p, wallAxis));
                var divMax = worldVerts.Max(p => GetCoord(p, wallAxis));
                int wallSign;
                if (Math.Abs(wallPlane - divMin) <= Eps) wallSign = -1;
                else if (Math.Abs(wallPlane - divMax) <= Eps) wallSign = +1;
                else continue;

                if (!wallFaceMap.TryGetValue((wallAxis, wallSign, RoundKey(wallPlane)), out var wallFi))
                    continue;

                var wallFace = faces[wallFi];
                var edgeMin = Math.Min(GetCoord(p0, edgeAxis), GetCoord(p1, edgeAxis));
                var edgeMax = Math.Max(GetCoord(p0, edgeAxis), GetCoord(p1, edgeAxis));
                var edgeCenter = (edgeMin + edgeMax) / 2.0;
                var edgeLength = edgeMax - edgeMin;

                double slotU, slotV, slotW, slotH;
                if (wallFace.Basis.UAxis == edgeAxis)
                {
                    slotU = edgeCenter; slotV = divPlane;
                    slotW = edgeLength; slotH = t;
                }
                else
                {
                    slotU = divPlane; slotV = edgeCenter;
                    slotW = t; slotH = edgeLength;
                }

                var dividerOwnsPrimary = DividerOwnsPrimary(divAxis, wallFace.Direction.Axis);
                var fingerSlots = DividerJointBuilder.BuildFingerSlots(slotU, slotV, slotW, slotH, t, s, dividerOwnsPrimary);

                if (!wallSlotsOut.TryGetValue(wallFi, out var wSlots))
                    wallSlotsOut[wallFi] = wSlots = new List<SlotSpec>();
                wSlots.AddRange(fingerSlots);

                var edgePanelLength = EdgeLength(face, ei);
                var spans = DividerJointBuilder.BuildSpans(edgePanelLength, t, s, joined: fingerSlots.Count > 0, dividerOwnsPrimary);
                tabs.Add((ei, spans));
                logger?.Log($"[merge] Divider {face.Id} edge {ei}: {spans.Count} tab spans, {fingerSlots.Count} wall slots on face {wallFace.Id}");
            }

            if (tabs.Count > 0)
                dividerTabsOut[fi] = tabs;
        }

        // Cross-divider half-lap slots: pairs of perpendicular internal dividers.
        var dividerList = faces.Select((f, i) => (Face: f, Index: i))
            .Where(x => x.Face.IsInternalDivider)
            .ToList();
        for (var i = 0; i < dividerList.Count; i++)
        {
            for (var j = i + 1; j < dividerList.Count; j++)
            {
                var dA = dividerList[i];
                var dB = dividerList[j];
                if (dA.Face.Direction.Axis == dB.Face.Direction.Axis) continue;

                ComputeCrossingSlots(dA.Face, dA.Index, dB.Face, dB.Index, t, settings.Kerf,
                    dividerInteriorSlotsOut, logger);
            }
        }
    }

    // Compute half-lap slots where two perpendicular divider faces cross.
    private static void ComputeCrossingSlots(
        MergedFace dA, int iA,
        MergedFace dB, int iB,
        double t, double kerf,
        Dictionary<int, List<SlotSpec>> dividerInteriorSlotsOut,
        PipelineLogger? logger = null)
    {
        // Find the crossing axis (neither dA.Axis nor dB.Axis).
        var crossAxis = ThirdAxis(dA.Direction.Axis, dB.Direction.Axis);

        // Find the overlap of dA and dB along the cross axis.
        var aVerts = dA.Outline.Select(uv => dA.ToWorld(uv)).ToList();
        var bVerts = dB.Outline.Select(uv => dB.ToWorld(uv)).ToList();
        var aMin = aVerts.Min(p => GetCoord(p, crossAxis));
        var aMax = aVerts.Max(p => GetCoord(p, crossAxis));
        var bMin = bVerts.Min(p => GetCoord(p, crossAxis));
        var bMax = bVerts.Max(p => GetCoord(p, crossAxis));
        var lo = Math.Max(aMin, bMin);
        var hi = Math.Min(aMax, bMax);
        if (hi - lo <= Eps) return;

        // The crossing position on each divider in its own UV.
        // dA: where does dB's plane sit in dA's UV space?
        // dB's plane is dB.Plane along dB.Direction.Axis; that maps to one of dA's UV axes.
        var crossLength = hi - lo;
        var crossCenter = (lo + hi) / 2.0;
        var halfDepth = crossLength / 2.0 + kerf;
        var slotThickness = t + kerf;

        // For dA: slot position in its (U,V) coords at dB's plane, running along crossAxis.
        // dB's plane maps to whichever of dA.Basis.UAxis or VAxis == dB.Direction.Axis.
        // The crossAxis maps to the other.
        double aU, aV, aW, aH;
        if (dA.Basis.UAxis == dB.Direction.Axis)
        {
            aU = dB.Plane; aV = crossCenter;
            aW = slotThickness; aH = crossLength;
        }
        else
        {
            aU = crossCenter; aV = dB.Plane;
            aW = crossLength; aH = slotThickness;
        }

        // Which end of dA does the slot come from? Higher-axis-ordinal divider gets from high-V end.
        // Concretely: the divider whose axis has a higher ordinal (Z>Y>X) gets the slot from V-high.
        var aGetsHighEnd = dA.Direction.Axis > dB.Direction.Axis;
        if (aGetsHighEnd)
        {
            // Slot at high-V (or high-U) end of dA.
            var aVertsForAxis = aVerts.Select(p => GetCoord(p, dA.Basis.VAxis == dB.Direction.Axis ? dA.Basis.UAxis : dA.Basis.VAxis)).ToList();
            // Actually simplify: the slot center shifts toward the high or low end.
            // Half-lap: cut from the top half. Center = (max - halfDepth/2).
            var divPanelDim = dA.Basis.VAxis == crossAxis
                ? (aVerts.Max(p => GetCoord(p, dA.Basis.VAxis)) - aVerts.Min(p => GetCoord(p, dA.Basis.VAxis)))
                : (aVerts.Max(p => GetCoord(p, dA.Basis.UAxis)) - aVerts.Min(p => GetCoord(p, dA.Basis.UAxis)));

            if (dA.Basis.VAxis == crossAxis)
            {
                var vMax = aVerts.Max(p => GetCoord(p, dA.Basis.VAxis));
                aV = vMax - halfDepth / 2.0;
                aH = halfDepth;
            }
            else
            {
                var uMax = aVerts.Max(p => GetCoord(p, dA.Basis.UAxis));
                aU = uMax - halfDepth / 2.0;
                aW = halfDepth;
            }
        }
        else
        {
            if (dA.Basis.VAxis == crossAxis)
            {
                var vMin = aVerts.Min(p => GetCoord(p, dA.Basis.VAxis));
                aV = vMin + halfDepth / 2.0;
                aH = halfDepth;
            }
            else
            {
                var uMin = aVerts.Min(p => GetCoord(p, dA.Basis.UAxis));
                aU = uMin + halfDepth / 2.0;
                aW = halfDepth;
            }
        }

        // For dB: symmetric — opposite end.
        double bU, bV, bW, bH;
        if (dB.Basis.UAxis == dA.Direction.Axis)
        {
            bU = dA.Plane; bV = crossCenter;
            bW = slotThickness; bH = crossLength;
        }
        else
        {
            bU = crossCenter; bV = dA.Plane;
            bW = crossLength; bH = slotThickness;
        }

        var bGetsHighEnd = !aGetsHighEnd;
        if (bGetsHighEnd)
        {
            if (dB.Basis.VAxis == crossAxis)
            {
                var vMax = bVerts.Max(p => GetCoord(p, dB.Basis.VAxis));
                bV = vMax - halfDepth / 2.0;
                bH = halfDepth;
            }
            else
            {
                var uMax = bVerts.Max(p => GetCoord(p, dB.Basis.UAxis));
                bU = uMax - halfDepth / 2.0;
                bW = halfDepth;
            }
        }
        else
        {
            if (dB.Basis.VAxis == crossAxis)
            {
                var vMin = bVerts.Min(p => GetCoord(p, dB.Basis.VAxis));
                bV = vMin + halfDepth / 2.0;
                bH = halfDepth;
            }
            else
            {
                var uMin = bVerts.Min(p => GetCoord(p, dB.Basis.UAxis));
                bU = uMin + halfDepth / 2.0;
                bW = halfDepth;
            }
        }

        if (!dividerInteriorSlotsOut.TryGetValue(iA, out var aSlots))
            dividerInteriorSlotsOut[iA] = aSlots = new List<SlotSpec>();
        aSlots.Add(new SlotSpec(aU, aV, aW, aH));

        if (!dividerInteriorSlotsOut.TryGetValue(iB, out var bSlots))
            dividerInteriorSlotsOut[iB] = bSlots = new List<SlotSpec>();
        bSlots.Add(new SlotSpec(bU, bV, bW, bH));

        logger?.Log($"[merge] Cross-divider slot: {dA.Id} slot=({aU:F2},{aV:F2},{aW:F2}×{aH:F2}), {dB.Id} slot=({bU:F2},{bV:F2},{bW:F2}×{bH:F2})");
    }

    private static Axis ThirdAxis(Axis a, Axis b) => (a, b) switch
    {
        (Axis.X, Axis.Y) or (Axis.Y, Axis.X) => Axis.Z,
        (Axis.X, Axis.Z) or (Axis.Z, Axis.X) => Axis.Y,
        (Axis.Y, Axis.Z) or (Axis.Z, Axis.Y) => Axis.X,
        _ => throw new InvalidOperationException($"Cannot find third axis of {a} and {b}"),
    };

    private static double GetCoord(Vec3 p, Axis a) => a switch
    {
        Axis.X => p.X,
        Axis.Y => p.Y,
        Axis.Z => p.Z,
        _ => throw new InvalidOperationException(),
    };

    private static bool DividerOwnsPrimary(Axis divAxis, Axis wallAxis) => (divAxis, wallAxis) switch
    {
        (Axis.X, Axis.Y) => false,
        (Axis.X, Axis.Z) => true,
        (Axis.Y, Axis.X) => false,
        (Axis.Y, Axis.Z) => true,
        (Axis.Z, Axis.X) => false,
        (Axis.Z, Axis.Y) => true,
        _ => throw new InvalidOperationException($"Invalid divider+wall axis pair: {divAxis}, {wallAxis}"),
    };

    private static long RoundKey(double v) => (long)Math.Round(v * 1e6);

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
        int vertexIndex)
    {
        if (IsReflex(polygon, vertexIndex))
        {
            return false;
        }
        // Keep shared-edge end reservation symmetric between mating faces.
        // Ownership affects corner-notch placement, not the edge inner span.
        return true;
    }

    // At a CONCAVE 3D edge of the merged solid (an inner step corner where
    // three of the four perpendicular quadrants are filled with material),
    // both meeting face panels must extend into the joint cube by t so the
    // finger-joint material has somewhere to interlock. Detection is purely
    // topological: project the neighbour face's outward 3D normal into this
    // face's 2D basis; if it points opposite to the polygon edge's outward
    // normal (i.e. *into* the polygon interior), the 3D edge is concave.
    // Convex 3D edges, where the neighbour normal points outward alongside
    // the edge normal, get no growth - the corner-cube notch logic handles
    // those joints instead.
    private static IReadOnlyList<Vec2> GrowBasePanelOutline(
        MergedFace face,
        IReadOnlyList<MergedFace> allFaces,
        IReadOnlyList<SharedSegment> faceSegments,
        double t)
    {
        if (t <= 0 || face.Direction.Axis == Axis.Y || face.IsInternalDivider)
        {
            return face.Outline;
        }

        var n = face.Outline.Count;
        var growEdge = new bool[n];
        foreach (var seg in faceSegments)
        {
            if (IsConcaveSharedEdge(face, seg.EdgeIndex, allFaces[seg.NeighbourFaceIndex]))
            {
                growEdge[seg.EdgeIndex] = true;
            }
        }

        if (!growEdge.Any(g => g)) return face.Outline;

        var grown = new List<Vec2>(n);
        for (var vi = 0; vi < n; vi++)
        {
            var prevEdge = (vi + n - 1) % n;
            var nextEdge = vi;
            var p = face.Outline[vi];
            double dx = 0, dy = 0;
            if (growEdge[prevEdge])
            {
                var (nx, ny) = OutwardNormal(face.Outline, prevEdge);
                dx += nx * t;
                dy += ny * t;
            }
            if (growEdge[nextEdge])
            {
                var (nx, ny) = OutwardNormal(face.Outline, nextEdge);
                dx += nx * t;
                dy += ny * t;
            }
            grown.Add(new Vec2(p.X + dx, p.Y + dy));
        }
        return grown;
    }

    private static bool IsConcaveSharedEdge(MergedFace face, int edgeIndex, MergedFace neighbour)
    {
        var (ox, oy) = OutwardNormal(face.Outline, edgeIndex);
        var nx = neighbour.Direction.Axis == face.Basis.UAxis ? neighbour.Direction.Sign : 0;
        var ny = neighbour.Direction.Axis == face.Basis.VAxis ? neighbour.Direction.Sign : 0;
        var dot = ox * nx + oy * ny;
        return dot < -Eps;
    }

    private static (double X, double Y) OutwardNormal(IReadOnlyList<Vec2> outline, int edgeIndex)
    {
        var n = outline.Count;
        var p0 = outline[edgeIndex];
        var p1 = outline[(edgeIndex + 1) % n];
        var dx = p1.X - p0.X;
        var dy = p1.Y - p0.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= Eps) return (0, 0);
        return (dy / len, -dx / len);
    }

    private static double EdgeLength(MergedFace face, int edgeIndex)
    {
        return EdgeLength(face.Outline, edgeIndex);
    }

    private static double EdgeLength(IReadOnlyList<Vec2> polygon, int edgeIndex)
    {
        var n = polygon.Count;
        var p0 = polygon[edgeIndex];
        var p1 = polygon[(edgeIndex + 1) % n];
        var dx = p1.X - p0.X;
        var dy = p1.Y - p0.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double MapDistanceToPanelEdge(
        IReadOnlyList<Vec2> original,
        IReadOnlyList<Vec2> panel,
        int edgeIndex,
        double originalDistance)
    {
        var n = original.Count;
        var p0 = original[edgeIndex % n];
        var p1 = original[(edgeIndex + 1) % n];
        var g0 = panel[edgeIndex % n];
        var g1 = panel[(edgeIndex + 1) % n];

        var dx = p1.X - p0.X;
        var dy = p1.Y - p0.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= Eps) return originalDistance;

        var ex = dx / len;
        var ey = dy / len;
        var startShiftX = g0.X - p0.X;
        var startShiftY = g0.Y - p0.Y;
        var endShiftX = g1.X - p1.X;
        var endShiftY = g1.Y - p1.Y;
        var startShiftAlongEdge = startShiftX * ex + startShiftY * ey;
        var endShiftAlongEdge = endShiftX * ex + endShiftY * ey;
        var fraction = originalDistance / len;
        var shiftAlongEdge = startShiftAlongEdge + ((endShiftAlongEdge - startShiftAlongEdge) * fraction);
        return originalDistance + shiftAlongEdge;
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
