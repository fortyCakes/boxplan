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

// Phase-2 scoop cutting. Each scoop panel gets finger joints on all 4 edges, and the
// anchor wall, host face, and cap panels receive matching interior slot cuts.
//
// Constraints currently enforced (all violations throw at cutting time):
//   - Host face must be Bottom. Other hosts are accepted by the resolver but not yet cut.
//   - Opposing scoops must leave a strip >= material thickness on the host face.
//   - Toes-meeting (combined inset == axis length) is not yet implemented.
internal static class ScoopCutter
{
    public static bool HasScoops(BoxShape box) => box.Scoops.Count > 0;

    // Per-face host-panel inset (mm to strip from each of the 4 CCW edges).
    public static IReadOnlyDictionary<FaceName, double[]> HostShrinkByEdge(
        BoxShape box, Vec3 dims)
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
            arr[g.EdgeIndex] = sc.Inset;
        }
        return result;
    }

    // Computes axis-aligned and oblique interior cuts for the faces that host scoop joints:
    //   AxisAligned — heel slots (anchor wall) and toe slots (host face)
    //   Oblique     — cap panel diagonal slots where the scoop panel passes through
    public static (
        IReadOnlyDictionary<FaceName, List<SlotSpec>> AxisAligned,
        IReadOnlyDictionary<FaceName, List<ObliqueSlotSpec>> Oblique)
        CollectFaceSlots(BoxShape box, Vec3 dims, BoxPlanSettings settings)
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

            foreach (var obl in BuildCapObliques(g, t, s))
            {
                AppendOblique(oblique, g.CapLow, obl);
                AppendOblique(oblique, g.CapHigh, obl);
            }
        }

        return (axisAligned, oblique);
    }

    public static IEnumerable<BoxPlanCuttableShape> BuildScoopPanels(
        string shapeId,
        BoxShape box,
        Vec3 dims,
        BoxPlanSettings settings,
        PipelineLogger? logger = null)
    {
        foreach (var sc in box.Scoops)
        {
            EnsureSupportedHost(sc);
            var g = ScoopGeometry.Compute(sc, dims);
            var id = $"{shapeId}.scoop-{sc.Face.ToString().ToLowerInvariant()}-{sc.Edge.ToString().ToLowerInvariant()}";
            yield return BuildScoopPanel(id, g, settings, logger);
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
        var effectiveT = t * g.Slant / g.Scoop.Rise;
        return DividerJointBuilder.BuildFingerSlots(
            g.EdgeAxisLength / 2.0, g.Scoop.Rise,
            w: g.EdgeAxisLength, h: effectiveT,
            effectiveT, s, dividerOwnsPrimary: true);
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
                ? g.InsetAxisLength - g.Scoop.Inset - effectiveT / 2.0
                : g.Scoop.Inset + effectiveT / 2.0;
            v = g.EdgeAxisLength / 2.0;
            w = effectiveT;
            h = g.EdgeAxisLength;
        }
        else
        {
            u = g.EdgeAxisLength / 2.0;
            v = g.EdgeAtHigh
                ? g.InsetAxisLength - g.Scoop.Inset - effectiveT / 2.0
                : g.Scoop.Inset + effectiveT / 2.0;
            w = g.EdgeAxisLength;
            h = effectiveT;
        }
        return DividerJointBuilder.BuildFingerSlots(u, v, w, h, effectiveT, s, dividerOwnsPrimary: true);
    }

    // ── Cap oblique slots (cap panels) ────────────────────────────────────────

    // Both CapLow and CapHigh see the same local-coord diagonal, so the same specs
    // are added to both faces. In cap face local coords the scoop runs from
    // (0, rise) to (inset, 0) when EdgeAtHigh=false, or from (capU, rise) to
    // (capU-inset, 0) when EdgeAtHigh=true.
    private static IReadOnlyList<ObliqueSlotSpec> BuildCapObliques(ScoopGeometry g, double t, double s)
    {
        var inset = g.Scoop.Inset;
        var rise = g.Scoop.Rise;
        var slant = g.Slant;

        var dirU = g.EdgeAtHigh ? -inset / slant : inset / slant;
        var dir = new Vec2(dirU, -rise / slant); // always pointing downward in V
        var perp = new Vec2(-dir.Y, dir.X);       // 90° CCW from dir

        var centerU = g.EdgeAtHigh ? g.InsetAxisLength - inset / 2.0 : inset / 2.0;
        var centerV = rise / 2.0;

        var spans = DividerJointBuilder.BuildSpans(slant, t, s, joined: true, dividerOwnsPrimary: true);
        var obliques = new List<ObliqueSlotSpec>();
        var cursor = 0.0;
        foreach (var span in spans)
        {
            if (span.Kind == DividerJointSpanKind.FaceSlot)
            {
                var offset = cursor + span.Length / 2.0 - slant / 2.0;
                var slotCenter = new Vec2(centerU + offset * dir.X, centerV + offset * dir.Y);
                obliques.Add(new ObliqueSlotSpec(slotCenter, dir, perp, span.Length, t));
            }
            cursor += span.Length;
        }
        return obliques;
    }

    // ── Scoop panel construction ──────────────────────────────────────────────

    private static BoxPlanCuttableShape BuildScoopPanel(
        string id,
        ScoopGeometry g,
        BoxPlanSettings settings,
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
        var toeT  = t * g.Slant / g.Scoop.Inset;
        var heelT = t * g.Slant / g.Scoop.Rise;
        SubtractDividerEdge(builder, 0, g.Slant,          t,     s);
        SubtractDividerEdge(builder, 1, g.EdgeAxisLength, toeT,  s);
        SubtractDividerEdge(builder, 2, g.Slant,          t,     s);
        SubtractDividerEdge(builder, 3, g.EdgeAxisLength, heelT, s);

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
        };
    }

    private static void SubtractDividerEdge(
        PanelShapeBuilder builder, int edgeIndex, double length, double t, double s)
    {
        var spans = DividerJointBuilder.BuildSpans(length, t, s, joined: true, dividerOwnsPrimary: true);
        var cursor = 0.0;
        foreach (var span in spans)
        {
            if (span.Kind is DividerJointSpanKind.DividerTab or DividerJointSpanKind.EndInset)
                builder.SubtractEdgeNotch(edgeIndex, cursor, span.Length, t);
            cursor += span.Length;
        }
    }

    private static void AppendSlot(Dictionary<FaceName, List<SlotSpec>> dict, FaceName face, SlotSpec slot)
    {
        if (!dict.TryGetValue(face, out var list))
            dict[face] = list = new List<SlotSpec>();
        list.Add(slot);
    }

    private static void AppendOblique(Dictionary<FaceName, List<ObliqueSlotSpec>> dict, FaceName face, ObliqueSlotSpec obl)
    {
        if (!dict.TryGetValue(face, out var list))
            dict[face] = list = new List<ObliqueSlotSpec>();
        list.Add(obl);
    }
}
