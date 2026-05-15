using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

// Describes a rotated rectangular slot on a face panel, used for oblique scoop cap cuts.
// Coordinates are in the face panel's local (pre-translation) coordinate system.
internal sealed record ObliqueSlotSpec(
    Vec2 Center,   // center of the slot in face local coords
    Vec2 Dir,      // unit vector along the slot (length axis)
    Vec2 Perp,     // unit vector perpendicular to slot (width axis), 90° CCW from Dir
    double Length, // slot length along Dir
    double Width); // slot width along Perp (= material thickness)

// An edge segment [Start, End) on a face panel that should be smooth — no finger joints,
// no corner notches — because the mating panel no longer extends to that region.
internal readonly record struct SmoothSegment(int EdgeIndex, double Start, double End);

// Phase-2 scoop cutting. Each scoop panel gets finger joints on all 4 edges, and the
// anchor wall, host face, and cap panels receive matching interior slot cuts.
//
// Constraints currently enforced (all violations throw at cutting time):
//   - Host face must be Bottom. Other hosts are accepted by the resolver but not yet cut.
//   - Opposing scoops must leave a strip >= material thickness on the host face.
//   - Toes-meeting (combined inset == axis length) is not yet implemented.
internal static class ScoopCutter
{
    private const double ObliqueNotchReliefFactor = 1.2;

    public static bool HasScoops(BoxShape box) => box.Scoops.Count > 0;

    // Returns smooth segments per face: edge regions where no finger joints or corner
    // notches should be cut because the mating panel no longer extends there.
    //   - Anchor face: the entire edge bordering the host face (host pulled back inset-t).
    //   - Cap faces: the anchor-end portion of the edge bordering the host face, length inset-t.
    public static IReadOnlyDictionary<FaceName, IReadOnlyList<SmoothSegment>> CollectSmoothEdges(
        BoxShape box, Vec3 dims, double t)
    {
        var result = new Dictionary<FaceName, List<SmoothSegment>>();
        if (!HasScoops(box)) return new Dictionary<FaceName, IReadOnlyList<SmoothSegment>>();

        foreach (var sc in box.Scoops)
        {
            EnsureSupportedHost(sc);
            var g = ScoopGeometry.Compute(sc, dims);

            AddSmooth(result, g.Anchor, sc.Face, g.Anchor, dims, 0);

            var capLen = sc.Inset - t;
            if (capLen > 0)
            {
                AddSmooth(result, g.CapLow,  sc.Face, g.Anchor, dims, capLen);
                AddSmooth(result, g.CapHigh, sc.Face, g.Anchor, dims, capLen);
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SmoothSegment>)kv.Value);
    }

    // Adds a smooth segment on `face`'s edge that borders `hostFace`.
    // For the anchor face (face == anchorFace), the entire edge is smooth (smoothLen=0 → full).
    // For a cap face, `smoothLen` mm at the anchor end of the edge is smooth.
    private static void AddSmooth(
        Dictionary<FaceName, List<SmoothSegment>> result,
        FaceName face, FaceName hostFace, FaceName anchorFace, Vec3 dims, double smoothLen)
    {
        var ccw = FaceLayout.EdgesCcw(face);
        for (var i = 0; i < ccw.Length; i++)
        {
            if (ccw[i].Neighbor != hostFace) continue;
            var (u, v) = FaceLayout.PanelSize(face, dims);
            double[] lens = { u, v, u, v };
            var edgeLen = lens[i];

            double start, end;
            if (face == anchorFace)
            {
                start = 0; end = edgeLen; // entire edge
            }
            else
            {
                // Determine which end of this edge is adjacent to the anchor face.
                var startNeighbor = ccw[(i + 3) % 4].Neighbor;
                start = startNeighbor == anchorFace ? 0 : edgeLen - smoothLen;
                end = start + smoothLen;
            }

            if (!result.TryGetValue(face, out var list))
                result[face] = list = new List<SmoothSegment>();
            list.Add(new SmoothSegment(i, start, end));
            break;
        }
    }

    // Per-face host-panel inset (mm to strip from each of the 4 CCW edges).
    public static IReadOnlyDictionary<FaceName, double[]> HostShrinkByEdge(
        BoxShape box, Vec3 dims, double t)
    {
        if (!HasScoops(box))
            return new Dictionary<FaceName, double[]>();

        var result = new Dictionary<FaceName, double[]>();
        foreach (var sc in box.Scoops)
        {
            EnsureSupportedHost(sc);
            var g = ScoopGeometry.Compute(sc, dims);
            if (!result.TryGetValue(sc.Face, out var arr))
            {
                arr = new double[4];
                result[sc.Face] = arr;
            }
            arr[g.EdgeIndex] = sc.Inset - t;
        }
        return result;
    }

    // Computes axis-aligned and oblique interior cuts for the faces that host scoop joints:
    //   AxisAligned — heel slots (anchor wall) and toe slots (host face)
    //   Oblique     — cap panel diagonal slots where the scoop panel passes through
    public static (
        IReadOnlyDictionary<FaceName, List<SlotSpec>> AxisAligned,
        IReadOnlyDictionary<FaceName, List<ObliqueSlotSpec>> Oblique)
        CollectFaceSlots(
            BoxShape box,
            Vec3 dims,
            BoxPlanSettings settings,
            IReadOnlyDictionary<string, SharedEdge> edges,
            IReadOnlySet<FaceName> openFaces,
            IReadOnlyDictionary<FaceName, IReadOnlyList<SmoothSegment>> smoothEdges)
    {
        var axisAligned = new Dictionary<FaceName, List<SlotSpec>>();
        var oblique = new Dictionary<FaceName, List<ObliqueSlotSpec>>();

        if (!HasScoops(box)) return (axisAligned, oblique);

        var t = settings.MaterialThickness;
        var s = settings.FingerJointSize;

        foreach (var sc in box.Scoops)
        {
            EnsureSupportedHost(sc);
            var g = ScoopGeometry.Compute(sc, dims);

            foreach (var slot in BuildHeelSlots(g, t, s))
                AppendSlot(axisAligned, g.Anchor, slot);

            foreach (var slot in BuildToeSlots(g, t, s))
                AppendSlot(axisAligned, sc.Face, slot);

            smoothEdges.TryGetValue(g.CapLow, out var capLowSmooth);
            foreach (var obl in BuildCapObliques(g, g.CapLow, dims, t, s, edges, openFaces, capLowSmooth))
                AppendOblique(oblique, g.CapLow, obl);

            smoothEdges.TryGetValue(g.CapHigh, out var capHighSmooth);
            foreach (var obl in BuildCapObliques(g, g.CapHigh, dims, t, s, edges, openFaces, capHighSmooth))
                AppendOblique(oblique, g.CapHigh, obl);
        }

        return (axisAligned, oblique);
    }

    public static IEnumerable<BoxPlanCuttableShape> BuildScoopPanels(
        string shapeId,
        BoxShape box,
        Vec3 dims,
        BoxPlanSettings settings,
        IReadOnlyDictionary<string, SharedEdge> edges,
        IReadOnlySet<FaceName> openFaces,
        IReadOnlyDictionary<FaceName, IReadOnlyList<SmoothSegment>> smoothEdges,
        PipelineLogger? logger = null)
    {
        foreach (var sc in box.Scoops)
        {
            EnsureSupportedHost(sc);
            var g = ScoopGeometry.Compute(sc, dims);
            var id = $"{shapeId}.scoop-{sc.Face.ToString().ToLowerInvariant()}-{sc.Edge.ToString().ToLowerInvariant()}";
            yield return BuildScoopPanel(id, g, dims, settings, edges, openFaces, smoothEdges, logger);
        }
    }

    public static void Validate(BoxShape box, Vec3 dims, BoxPlanSettings settings)
    {
        if (!HasScoops(box)) return;
        foreach (var sc in box.Scoops) EnsureSupportedHost(sc);
        ValidateThickness(box, dims, settings);
    }

    private static void EnsureSupportedHost(Scoop sc)
    {
        if (sc.Face != FaceName.Bottom)
            throw new NotImplementedException(
                $"Scoops on face '{sc.Face.ToString().ToLowerInvariant()}' are not yet supported by the " +
                "cutting pipeline; only 'bottom' is implemented in this build.");
    }

    private static void ValidateThickness(BoxShape box, Vec3 dims, BoxPlanSettings settings)
    {
        var t = settings.MaterialThickness;
        var byKey = new Dictionary<(FaceName Face, bool InsetAlongU), (double Sum, double Axis)>();
        foreach (var sc in box.Scoops)
        {
            var g = ScoopGeometry.Compute(sc, dims);
            var key = (sc.Face, g.InsetAlongU);
            byKey.TryGetValue(key, out var prev);
            byKey[key] = (prev.Sum + sc.Inset, g.InsetAxisLength);
        }

        foreach (var ((face, _), (sum, axis)) in byKey)
        {
            var strip = axis - sum;
            if (strip > 1e-9 && strip < t)
                throw new InvalidOperationException(
                    $"Box '{box.Id}' scoops on face '{face.ToString().ToLowerInvariant()}' " +
                    $"leave a strip of {strip:F3} mm < material thickness {t:F3} mm.");
            if (Math.Abs(strip) <= 1e-9)
                throw new NotImplementedException(
                    $"Box '{box.Id}' has opposing scoops on face '{face.ToString().ToLowerInvariant()}' " +
                    "whose toes meet exactly; toe-to-toe joining is not yet implemented.");
        }
    }

    // ── Heel slots (anchor wall) ──────────────────────────────────────────────

    // The heel edge runs along U of the anchor wall at V = rise.
    // edgeAxisLength == the anchor wall's full U dimension.
    // The scoop panel meets the anchor wall at an angle; the slot must be wider
    // by t/cos(theta) where cos(theta) = rise/slant.
    private static IReadOnlyList<SlotSpec> BuildHeelSlots(ScoopGeometry g, double t, double s)
    {
        var effectiveT = t * g.Slant / g.Scoop.Rise * 1.5;
        return DividerJointBuilder.BuildFingerSlots(
            g.EdgeAxisLength / 2.0, g.Scoop.Rise - 0.5 * t,
            w: g.EdgeAxisLength, h: effectiveT,
            t, s, dividerOwnsPrimary: true);
    }

    // ── Toe slots (host face) ─────────────────────────────────────────────────

    // The slot sits just inside the new narrowed edge, going inward by effectiveT.
    // The scoop panel meets the host face at an angle; slot depth = t/cos(theta)
    // where cos(theta) = inset/slant.
    private static IReadOnlyList<SlotSpec> BuildToeSlots(ScoopGeometry g, double t, double s)
    {
        var effectiveT = t * g.Slant / g.Scoop.Inset;
        double u, v, w, h;
        if (g.InsetAlongU)
        {
            u = g.EdgeAtHigh
                ? g.InsetAxisLength - g.Scoop.Inset + t - effectiveT / 2.0
                : g.Scoop.Inset - t + effectiveT / 2.0;
            v = g.EdgeAxisLength / 2.0;
            w = effectiveT;
            h = g.EdgeAxisLength;
        }
        else
        {
            u = g.EdgeAxisLength / 2.0;
            v = g.EdgeAtHigh
                ? g.InsetAxisLength - g.Scoop.Inset + t - effectiveT / 2.0
                : g.Scoop.Inset - t + effectiveT / 2.0;
            w = g.EdgeAxisLength;
            h = effectiveT;
        }
        return DividerJointBuilder.BuildFingerSlots(u, v, w, h, t, s, dividerOwnsPrimary: true);
    }

    // ── Cap oblique slots (cap panels) ────────────────────────────────────────

    // Both CapLow and CapHigh see the same local-coord diagonal, so the same specs
    // are measured from the same local-coord diagonal. In cap face local coords the scoop runs from
    // (0, rise) to (inset, 0) when EdgeAtHigh=false, or from (capU, rise) to
    // (capU-inset, 0) when EdgeAtHigh=true.
    private static IReadOnlyList<ObliqueSlotSpec> BuildCapObliques(
        ScoopGeometry g,
        FaceName face,
        Vec3 dims,
        double t,
        double s,
        IReadOnlyDictionary<string, SharedEdge> edges,
        IReadOnlySet<FaceName> openFaces,
        IReadOnlyList<SmoothSegment>? smoothSegments)
    {
        var inset = g.Scoop.Inset;
        var rise = g.Scoop.Rise;
        var slant = g.Slant;

        var dirU = g.EdgeAtHigh ? -inset / slant : inset / slant;
        var dir = new Vec2(dirU, -rise / slant); // always pointing downward in V
        var perp = new Vec2(-dir.Y, dir.X);       // 90° CCW from dir

        var anchorPoint = new Vec2(g.EdgeAtHigh ? g.InsetAxisLength : 0.0, rise);
        var spans = BuildCapJointSpans(g, face, dims, t, s, edges, openFaces, smoothSegments);
        var obliques = new List<ObliqueSlotSpec>();
        var cursor = 0.0;
        foreach (var span in spans)
        {
            var spanStart = cursor;
            var spanEnd = cursor + span.Length;
            if (span.Kind == DividerJointSpanKind.FaceSlot)
            {
                var centerDistance = spanStart + span.Length / 2.0;
                var slotCenter = new Vec2(
                    anchorPoint.X + centerDistance * dir.X,
                    anchorPoint.Y + centerDistance * dir.Y);
                obliques.Add(new ObliqueSlotSpec(slotCenter, dir, perp, span.Length, t));
            }
            cursor = spanEnd;
        }
        return obliques;
    }

    private static IReadOnlyList<DividerJointSpan> BuildCapJointSpans(
        ScoopGeometry g,
        FaceName face,
        Vec3 dims,
        double t,
        double s,
        IReadOnlyDictionary<string, SharedEdge> edges,
        IReadOnlySet<FaceName> openFaces,
        IReadOnlyList<SmoothSegment>? smoothSegments)
    {
        var slant = g.Slant;
        var inset = g.Scoop.Inset;
        var rise = g.Scoop.Rise;
        var dir = new Vec2(
            g.EdgeAtHigh ? -inset / slant : inset / slant,
            -rise / slant);
        var anchorPoint = new Vec2(g.EdgeAtHigh ? g.InsetAxisLength : 0.0, rise);
        var hostPoint = new Vec2(g.EdgeAtHigh ? g.InsetAxisLength - inset : inset, 0.0);

        var anchorExcluded = ExcludedLengthFromFaceTab(
            face,
            g.Anchor,
            anchorPoint,
            dir,
            dims,
            t,
            edges,
            openFaces,
            smoothSegments);
        var hostExcluded = ExcludedLengthFromFaceTab(
            face,
            g.Scoop.Face,
            hostPoint,
            new Vec2(-dir.X, -dir.Y),
            dims,
            t,
            edges,
            openFaces,
            smoothSegments);

        var anchorHasTabs = EdgeHasFaceTabs(face, g.Anchor, edges, openFaces);
        var hostHasTabs = EdgeHasFaceTabs(face, g.Scoop.Face, edges, openFaces);
        if (anchorHasTabs)
            anchorExcluded = Math.Max(anchorExcluded, ProjectedThicknessAlong(face, g.Anchor, dir, dims, t));
        if (hostHasTabs)
            hostExcluded = Math.Max(hostExcluded, ProjectedThicknessAlong(face, g.Scoop.Face, new Vec2(-dir.X, -dir.Y), dims, t));

        if (!anchorHasTabs && !hostHasTabs)
            return DividerJointBuilder.BuildSpans(slant, t, s, joined: true, dividerOwnsPrimary: true);

        var usableLength = Math.Max(0, slant - anchorExcluded - hostExcluded);
        if (usableLength <= 1e-9)
            return [new DividerJointSpan(slant, DividerJointSpanKind.Smooth)];

        var spans = new List<DividerJointSpan>();
        AddSpan(spans, anchorExcluded, DividerJointSpanKind.EndInset);

        var blocks = FingerJointPattern.Build(usableLength, s, t);
        const bool dividerOwnsPrimary = false;
        foreach (var block in blocks)
        {
            var dividerOwns = block.PrimaryOwns == dividerOwnsPrimary;
            AddSpan(spans, block.Length, dividerOwns ? DividerJointSpanKind.FaceSlot : DividerJointSpanKind.DividerTab);
        }

        if (!spans.Any(span => span.Kind == DividerJointSpanKind.FaceSlot))
            return [new DividerJointSpan(slant, DividerJointSpanKind.Smooth)];

        AddSpan(spans, hostExcluded, DividerJointSpanKind.EndInset);
        return spans;
    }

    private static bool EdgeHasFaceTabs(
        FaceName face,
        FaceName neighbor,
        IReadOnlyDictionary<string, SharedEdge> edges,
        IReadOnlySet<FaceName> openFaces)
    {
        if (openFaces.Contains(face) || openFaces.Contains(neighbor))
            return false;

        var edgeIndex = FindEdgeIndex(face, neighbor);
        if (edgeIndex < 0)
            return false;

        var edgeMap = FaceLayout.EdgesCcw(face)[edgeIndex];
        var shared = edges[SharedEdgeTable.Id(edgeMap.Face, edgeMap.Neighbor)];
        return shared.Blocks.Any(block => block.Owner == face);
    }

    private static double ProjectedThicknessAlong(
        FaceName face,
        FaceName neighbor,
        Vec2 inwardDir,
        Vec3 dims,
        double t)
    {
        var edgeIndex = FindEdgeIndex(face, neighbor);
        if (edgeIndex < 0)
            return 0;

        var inwardRate = InwardDistanceRate(edgeIndex, inwardDir);
        return inwardRate <= 1e-9 ? 0 : t / inwardRate;
    }

    private static void AddSpan(List<DividerJointSpan> spans, double length, DividerJointSpanKind kind)
    {
        if (length <= 1e-9)
            return;

        if (spans.Count > 0 && spans[^1].Kind == kind)
        {
            spans[^1] = spans[^1] with { Length = spans[^1].Length + length };
            return;
        }

        spans.Add(new DividerJointSpan(length, kind));
    }

    private static double ExcludedLengthFromFaceTab(
        FaceName face,
        FaceName neighbor,
        Vec2 edgePoint,
        Vec2 inwardDir,
        Vec3 dims,
        double t,
        IReadOnlyDictionary<string, SharedEdge> edges,
        IReadOnlySet<FaceName> openFaces,
        IReadOnlyList<SmoothSegment>? smoothSegments)
    {
        if (openFaces.Contains(face) || openFaces.Contains(neighbor))
            return 0;

        var edgeIndex = FindEdgeIndex(face, neighbor);
        if (edgeIndex < 0)
            return 0;

        var (panelU, panelV) = FaceLayout.PanelSize(face, dims);
        var edgeCoord = EdgeCoordinate(edgeIndex, edgePoint, panelU, panelV);
        var tabSpan = FindFaceTabSpan(face, edgeIndex, edgeCoord, t, edges, smoothSegments);
        if (tabSpan is null)
            return 0;

        var inwardRate = InwardDistanceRate(edgeIndex, inwardDir);
        if (inwardRate <= 1e-9)
            return 0;

        var edgeRate = EdgeCoordinateRate(edgeIndex, inwardDir);
        var edgeLimit = EdgeExitDistance(edgeCoord, edgeRate, tabSpan.Value.Start, tabSpan.Value.End);
        var depthLimit = t / inwardRate;
        return Math.Max(0, Math.Min(depthLimit, edgeLimit));
    }

    private static (double Start, double End)? FindFaceTabSpan(
        FaceName face,
        int edgeIndex,
        double edgeCoord,
        double t,
        IReadOnlyDictionary<string, SharedEdge> edges,
        IReadOnlyList<SmoothSegment>? smoothSegments)
    {
        const double eps = 1e-9;

        var edgeMap = FaceLayout.EdgesCcw(face)[edgeIndex];
        var shared = edges[SharedEdgeTable.Id(edgeMap.Face, edgeMap.Neighbor)];
        var ordered = edgeMap.ForwardAlongShared
            ? shared.Blocks
            : shared.Blocks.Reverse().ToList();

        var cursor = t;
        foreach (var block in ordered)
        {
            var spanStart = cursor;
            var spanEnd = cursor + block.Length;
            cursor = spanEnd;

            if (block.Owner != face)
                continue;

            var trimmed = TrimmedSpanContainingPoint(edgeIndex, spanStart, spanEnd, edgeCoord, smoothSegments);
            if (trimmed is { } match)
                return match;

            if (edgeCoord < spanStart - eps)
                break;
        }

        return null;
    }

    private static (double Start, double End)? TrimmedSpanContainingPoint(
        int edgeIndex,
        double start,
        double end,
        double point,
        IReadOnlyList<SmoothSegment>? smoothSegments)
    {
        const double eps = 1e-9;

        var cursor = start;
        foreach (var smooth in (smoothSegments ?? Array.Empty<SmoothSegment>())
            .Where(seg => seg.EdgeIndex == edgeIndex)
            .OrderBy(seg => seg.Start))
        {
            if (smooth.End <= cursor + eps)
                continue;
            if (smooth.Start >= end - eps)
                break;

            var pieceEnd = Math.Min(end, smooth.Start);
            if (point >= cursor - eps && point < pieceEnd - eps)
                return (cursor, pieceEnd);

            cursor = Math.Max(cursor, smooth.End);
            if (cursor >= end - eps)
                return null;
        }

        return point >= cursor - eps && point < end - eps ? (cursor, end) : null;
    }

    private static int FindEdgeIndex(FaceName face, FaceName neighbor)
    {
        var ccw = FaceLayout.EdgesCcw(face);
        for (var i = 0; i < ccw.Length; i++)
        {
            if (ccw[i].Neighbor == neighbor)
                return i;
        }

        return -1;
    }

    private static double EdgeCoordinate(int edgeIndex, Vec2 point, double panelU, double panelV) => edgeIndex switch
    {
        0 => point.X,
        1 => point.Y,
        2 => panelU - point.X,
        3 => panelV - point.Y,
        _ => throw new InvalidOperationException(),
    };

    private static double EdgeCoordinateRate(int edgeIndex, Vec2 dir) => edgeIndex switch
    {
        0 => dir.X,
        1 => dir.Y,
        2 => -dir.X,
        3 => -dir.Y,
        _ => throw new InvalidOperationException(),
    };

    private static double InwardDistanceRate(int edgeIndex, Vec2 dir) => edgeIndex switch
    {
        0 => dir.Y,
        1 => -dir.X,
        2 => -dir.Y,
        3 => dir.X,
        _ => throw new InvalidOperationException(),
    };

    private static double EdgeExitDistance(double point, double rate, double start, double end)
    {
        if (Math.Abs(rate) <= 1e-9)
            return double.PositiveInfinity;

        return rate > 0
            ? Math.Max(0, (end - point) / rate)
            : Math.Max(0, (point - start) / -rate);
    }

    // ── Scoop panel construction ──────────────────────────────────────────────

    private static BoxPlanCuttableShape BuildScoopPanel(
        string id,
        ScoopGeometry g,
        Vec3 dims,
        BoxPlanSettings settings,
        IReadOnlyDictionary<string, SharedEdge> edges,
        IReadOnlySet<FaceName> openFaces,
        IReadOnlyDictionary<FaceName, IReadOnlyList<SmoothSegment>> smoothEdges,
        PipelineLogger? logger)
    {
        logger?.Log($"[scoop] Building scoop panel {id} slant={g.Slant:F3} edge={g.EdgeAxisLength:F3}");
        var t = settings.MaterialThickness;
        var s = settings.FingerJointSize;

        var builder = new PanelShapeBuilder(g.Slant, g.EdgeAxisLength, logger);

        // Edge 0 (V=0, CapLow):        joint with CapLow cap panel, spanning slant
        // Edge 1 (U=slant, Toe):        joint with host face; meets at oblique angle
        // Edge 2 (V=edgeAxis, CapHigh): joint with CapHigh cap panel, spanning slant
        // Edge 3 (U=0, Heel):           joint with anchor wall; meets at oblique angle
        var toeT  = WithObliqueNotchRelief(t * g.Slant / g.Scoop.Inset);
        var heelT = WithObliqueNotchRelief(t * g.Slant / g.Scoop.Rise);
        smoothEdges.TryGetValue(g.CapLow, out var capLowSmooth);
        var capLowSpans = BuildCapJointSpans(g, g.CapLow, dims, t, s, edges, openFaces, capLowSmooth);
        SubtractDividerEdge(builder, 0, capLowSpans,       t,     t);
        SubtractDividerEdge(builder, 1, g.EdgeAxisLength, toeT,  t, s);
        smoothEdges.TryGetValue(g.CapHigh, out var capHighSmooth);
        var capHighSpans = BuildCapJointSpans(g, g.CapHigh, dims, t, s, edges, openFaces, capHighSmooth);
        SubtractDividerEdge(builder, 2, capHighSpans,      t,     t);
        SubtractDividerEdge(builder, 3, g.EdgeAxisLength, heelT, t, s);

        var polygon = builder.Build();
        var (path, bbMin, bbMax, _) = KerfOffset.OffsetOutwardAndTranslate(polygon, settings.Kerf, logger);
        return new BoxPlanCuttableShape
        {
            Id = id,
            BoundingBoxMin = bbMin,
            BoundingBoxMax = bbMax,
            Outline = path,
            InteriorCuts = Array.Empty<CuttablePath>(),
            Engravings = Array.Empty<CuttablePath>(),
            TextEngravings = Array.Empty<TextEngraving>(),
            RasterEngravings = Array.Empty<RasterEngraving>(),
        };
    }

    // tabDepth: how deep to cut the edge recesses (= t for straight joints,
    //           effectiveT for oblique joints where the mating panel is angled).
    // patternT: raw material thickness used only for span sizing and spacing.
    private static void SubtractDividerEdge(
        PanelShapeBuilder builder, int edgeIndex, double length,
        double tabDepth, double patternT, double s)
    {
        var spans = DividerJointBuilder.BuildSpans(length, patternT, s, joined: true, dividerOwnsPrimary: true);
        SubtractDividerEdge(builder, edgeIndex, spans, tabDepth, patternT);
    }

    private static void SubtractDividerEdge(
        PanelShapeBuilder builder,
        int edgeIndex,
        IReadOnlyList<DividerJointSpan> spans,
        double tabDepth,
        double patternT)
    {
        var cursor = 0.0;
        foreach (var span in spans)
        {
            if (span.Kind is DividerJointSpanKind.DividerTab or DividerJointSpanKind.EndInset)
            {
                builder.SubtractEdgeNotch(edgeIndex, cursor, span.Length, tabDepth);
            }
            cursor += span.Length;
        }
    }

    private static void AppendSlot(Dictionary<FaceName, List<SlotSpec>> dict, FaceName face, SlotSpec slot)
    {
        if (!dict.TryGetValue(face, out var list))
            dict[face] = list = new List<SlotSpec>();
        list.Add(slot);
    }

    private static double WithObliqueNotchRelief(double projectedDepth)
        => projectedDepth * ObliqueNotchReliefFactor;

    private static void AppendOblique(Dictionary<FaceName, List<ObliqueSlotSpec>> dict, FaceName face, ObliqueSlotSpec obl)
    {
        if (!dict.TryGetValue(face, out var list))
            dict[face] = list = new List<ObliqueSlotSpec>();
        list.Add(obl);
    }
}
