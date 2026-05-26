using BoxPlanLib.Cutting.Merging;
using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

public sealed class CuttingPipeline
{
    private const double Eps = 1e-9;

    private readonly record struct AxisAlignedRect(double MinU, double MinV, double MaxU, double MaxV);
    private readonly record struct EdgeSlotSpan(double Start, double End);

    public BoxPlanCuttableShape[] Run(BoxPlan plan, BoxPlanSettings settings)
    {
        var logger = new PipelineLogger(settings.Debug);
        logger.Log($"[pipeline] Starting pipeline run");
        var output = new List<BoxPlanCuttableShape>();
        var insertTargets = CollectInsertTargets(plan);
        var groupCandidates = plan.Shapes
            .OfType<BoxShape>()
            .Where(b => b.Dimensions is not null)
            .Where(b => IsMergeCandidate(b) && !insertTargets.Contains(b.Id))
            .ToList();
        var candidateSet = new HashSet<string>(groupCandidates.Select(b => b.Id));

        logger.Log($"[pipeline] Found {groupCandidates.Count} group candidates");
        var groups = BoxGrouper.Compute(groupCandidates, logger);
        var multiBoxIds = new HashSet<string>(
            groups.Where(g => g.Members.Count > 1).SelectMany(g => g.Members.Select(m => m.Box.Id)));

        var groupIndex = 0;
        foreach (var group in groups.Where(g => g.Members.Count > 1))
        {
            var groupId = $"merged-{groupIndex}";
            logger.Log($"[pipeline] Merging group {groupId} with {group.Members.Count} boxes");
            var faces = MergedFacePolygons.Compute(group, groupId, logger);
            var shared = SharedEdgeGraph.Build(faces, logger);
            MergedShapeCutter.Emit(faces, shared, settings, output, logger);
            groupIndex++;
        }

        var mergeablePanels = plan.Shapes.OfType<PanelShape>().Where(p => !p.Disjoint).ToList();
        var (mergedPanels, mergedPanelIds) = PanelMerger.Build(mergeablePanels, settings, logger);
        output.AddRange(mergedPanels);

        foreach (var shape in plan.Shapes)
        {
            switch (shape)
            {
                case BoxShape box when box.Dimensions is { } dims:
                    if (multiBoxIds.Contains(box.Id)) continue;
                    logger.Log($"[pipeline] Emitting shape {box.Id}");
                    EmitShape(box.Id, box, dims, output, settings, logger);
                    EmitInserts(box.Id, box, output, settings, logger);
                    break;
                case PrismShape prism when prism.Depth is { } prismDepth:
                    logger.Log($"[pipeline] Emitting prism {prism.Id}");
                    output.AddRange(PrismFaceBuilder.Build(prism.Id, prism, prismDepth, settings, logger));
                    break;
                case PanelShape panel:
                    if (mergedPanelIds.Contains(panel.Id)) continue;
                    logger.Log($"[pipeline] Emitting panel {panel.Id}");
                    var panelShape = PanelFaceBuilder.Build(panel.Id, panel, settings, logger);
                    if (panelShape is not null) output.Add(panelShape);
                    break;
            }
        }
        logger.Log($"[pipeline] Pipeline run complete");
        return output.ToArray();
    }

    private static HashSet<string> CollectInsertTargets(BoxPlan plan, PipelineLogger? logger = null)
    {
        var ids = new HashSet<string>();
        foreach (var shape in plan.Shapes)
            CollectFromShape(shape, ids, logger);
        return ids;
    }

    private static void CollectFromShape(Shape shape, HashSet<string> ids, PipelineLogger? logger = null)
    {
        foreach (var insert in shape.Inserts)
        {
            if (insert.Target is null) continue;
            ids.Add(insert.Target.Id);
            CollectFromShape(insert.Target, ids, logger);
        }
    }

    private static bool IsMergeCandidate(BoxShape box) =>
        !box.Disjoint
        && box.Dividers.Count == 0
        && box.Inserts.Count == 0
        && box.Features.Count == 0;

    private static void EmitDividerPanels(
        string parentId,
        BoxShape parent,
        Vec3 dims,
        IReadOnlySet<FaceName> openFaces,
        List<BoxPlanCuttableShape> output,
        BoxPlanSettings settings,
        PipelineLogger? logger = null)
    {
        logger?.Log($"[divider] Emitting divider panels for {parentId}");
        var t = settings.MaterialThickness;
        for (var di = 0; di < parent.Dividers.Count; di++)
        {
            var dset = parent.Dividers[di];
            for (var pi = 0; pi < dset.Positions.Count; pi++)
            {
                var pos = dset.Positions[pi];
                var (panelU, panelV) = DividerPanelSize(dset.Axis, dims, dset.Facing, t);
                var edges = BuildDividerEdges(dset.Axis, dset.Facing, openFaces);
                var dividerSlots = BuildDividerAssemblySlots(dset.Axis, parent.Dividers, panelV, t, settings.Kerf);
                var scoopClips = BuildDividerScoopClipPolygons(dset.Axis, pos, dims, dset.Facing, parent.Scoops, t);
                output.Add(BuildDividerPanel(
                    $"{parentId}.divider-{dset.Axis.ToString().ToLowerInvariant()}@{pos}",
                    panelU,
                    panelV,
                    edges,
                    dividerSlots,
                    scoopClips,
                    settings,
                    logger));
            }
        }
    }

    private static (double U, double V) DividerPanelSize(Axis axis, Vec3 dims, FaceName? facing, double t)
    {
        return axis switch
        {
            Axis.X => (dims.Y, dims.Z),
            Axis.Y => (dims.X, dims.Z),
            Axis.Z => (dims.X, dims.Y),
            _ => (0.0, 0.0),
        };
    }

    private static (double Center, double Length) DividerSpan(double fullLength)
    {
        return (fullLength / 2.0, fullLength);
    }


    private static DividerEdgeSpec[] BuildDividerEdges(Axis axis, FaceName? facing, IReadOnlySet<FaceName> openFaces)
    {
        var faces = axis switch
        {
            Axis.X => new[] { FaceName.Front, FaceName.Top, FaceName.Back, FaceName.Bottom },
            Axis.Y => new[] { FaceName.Front, FaceName.Right, FaceName.Back, FaceName.Left },
            Axis.Z => new[] { FaceName.Bottom, FaceName.Right, FaceName.Top, FaceName.Left },
            _ => throw new InvalidOperationException(),
        };

        return faces
            .Select((face, index) => new DividerEdgeSpec(face, face != facing && !openFaces.Contains(face), index % 2 == 0))
            .ToArray();
    }

    private static BoxPlanCuttableShape BuildDividerPanel(
        string id,
        double u,
        double v,
        IReadOnlyList<DividerEdgeSpec> edges,
        IReadOnlyList<SlotSpec> dividerSlots,
        IReadOnlyList<IReadOnlyList<Vec2>> scoopClips,
        BoxPlanSettings settings,
        PipelineLogger? logger = null)
    {
        var t = settings.MaterialThickness;
        var spans = new[]
        {
            BuildDividerJointSpans(u, t, settings.FingerJointSize, edges[0].Joined, edges[0].DividerOwnsPrimary),
            BuildDividerJointSpans(v, t, settings.FingerJointSize, edges[1].Joined, edges[1].DividerOwnsPrimary),
            BuildDividerJointSpans(u, t, settings.FingerJointSize, edges[2].Joined, edges[2].DividerOwnsPrimary),
            BuildDividerJointSpans(v, t, settings.FingerJointSize, edges[3].Joined, edges[3].DividerOwnsPrimary),
        };

        logger?.Log($"[divider] Building divider panel {id}");
        LogDividerTabSizes(id, edges, spans, logger);

        var builder = new PanelShapeBuilder(u, v, logger);

        // Subtract notches for DividerTab and EndInset spans. FaceSlot and Smooth spans
        // leave the outline untouched (FaceSlots are interior cuts on the face panels).
        for (var edgeIndex = 0; edgeIndex < 4; edgeIndex++)
        {
            var cursor = 0.0;
            foreach (var span in spans[edgeIndex])
            {
                if (span.Kind is DividerJointSpanKind.DividerTab or DividerJointSpanKind.EndInset)
                    builder.SubtractEdgeNotch(edgeIndex, cursor, span.Length, t);
                cursor += span.Length;
            }
        }

        var polygon = builder.Build();
        if (scoopClips.Count > 0)
        {
            var polygonBuilder = new PolygonPanelShapeBuilder(polygon, logger);
            foreach (var scoopClip in scoopClips)
            {
                polygonBuilder.SubtractPolygon(scoopClip);
            }
            polygon = polygonBuilder.Build();
        }

        var (path, bbMin, bbMax, translation) = KerfOffset.OffsetOutwardAndTranslate(polygon, settings.Kerf, logger);
        var interiorCuts = dividerSlots
            .SelectMany(slot => CutoutClipper.ClipToOutline(CutoutBuilder.BuildSlotRectangle(slot, settings.Kerf, translation, logger), path, logger))
            .ToArray();
        return new BoxPlanCuttableShape
        {
            Id = id,
            BoundingBoxMin = bbMin,
            BoundingBoxMax = bbMax,
            Outline = path,
            InteriorCuts = interiorCuts,
            Engravings = Array.Empty<CuttablePath>(),
            TextEngravings = Array.Empty<TextEngraving>(),
            RasterEngravings = Array.Empty<RasterEngraving>(),
            SvgEngravings = Array.Empty<SvgEngraving>(),
        };
    }

    private static void LogDividerTabSizes(
        string id,
        IReadOnlyList<DividerEdgeSpec> edges,
        IReadOnlyList<DividerJointSpan>[] spans,
        PipelineLogger? logger = null)
    {
        for (var index = 0; index < spans.Length; index++)
        {
            var tabSizes = spans[index]
                .Where(span => span.Kind == DividerJointSpanKind.DividerTab)
                .Select(span => span.Length.ToString("0.###"))
                .ToArray();
            var slotSizes = spans[index]
                .Where(span => span.Kind == DividerJointSpanKind.FaceSlot)
                .Select(span => span.Length.ToString("0.###"))
                .ToArray();

            if (tabSizes.Length == 0 && slotSizes.Length == 0)
            {
                continue;
            }

            logger?.Log(
                $"[divider-joint] {id} edge={edges[index].Face} tabs=[{string.Join(", ", tabSizes)}] slots=[{string.Join(", ", slotSizes)}]");
        }
    }

    private static IReadOnlyList<IReadOnlyList<Vec2>> BuildDividerScoopClipPolygons(
        Axis dividerAxis,
        double dividerPos,
        Vec3 dims,
        FaceName? facing,
        IReadOnlyList<Scoop> scoops,
        double t)
    {
        var clips = new List<IReadOnlyList<Vec2>>();
        var start = (U: 0.0, V: 0.0);

        foreach (var scoop in scoops)
        {
            if (scoop.Face != FaceName.Bottom) continue;

            var section = BuildScoopSectionPolygon(scoop, t);
            switch (dividerAxis)
            {
                case Axis.Z:
                    switch (scoop.Edge)
                    {
                        case FaceName.Left:
                            clips.Add(TranslatePolygon(section.Select(p => new Vec2(p.X, p.Y)), start));
                            break;
                        case FaceName.Right:
                            clips.Add(TranslatePolygon(section.Select(p => new Vec2(dims.X - p.X, p.Y)), start));
                            break;
                        case FaceName.Front:
                            AppendRectClip(clips, IntersectPolygonWithVerticalLine(section, dividerPos), 0.0, dims.X, start);
                            break;
                        case FaceName.Back:
                            AppendRectClip(clips, IntersectPolygonWithVerticalLine(section, dims.Z - dividerPos), 0.0, dims.X, start);
                            break;
                    }
                    break;
                case Axis.X:
                    switch (scoop.Edge)
                    {
                        case FaceName.Front:
                            clips.Add(TranslatePolygon(section.Select(p => new Vec2(p.Y, p.X)), start));
                            break;
                        case FaceName.Back:
                            clips.Add(TranslatePolygon(section.Select(p => new Vec2(p.Y, dims.Z - p.X)), start));
                            break;
                        case FaceName.Left:
                            AppendRectClip(clips, IntersectPolygonWithVerticalLine(section, dividerPos), 0.0, dims.Z, start, swapAxes: true);
                            break;
                        case FaceName.Right:
                            AppendRectClip(clips, IntersectPolygonWithVerticalLine(section, dims.X - dividerPos), 0.0, dims.Z, start, swapAxes: true);
                            break;
                    }
                    break;
                case Axis.Y:
                    switch (scoop.Edge)
                    {
                        case FaceName.Left:
                            AppendHorizontalClip(clips, IntersectPolygonWithHorizontalLine(section, dividerPos), 0.0, dims.Z, start, mirrorHighAxis: false);
                            break;
                        case FaceName.Right:
                            AppendHorizontalClip(clips, IntersectPolygonWithHorizontalLine(section, dividerPos), 0.0, dims.Z, start, mirrorHighAxis: true, highAxisExtent: dims.X);
                            break;
                        case FaceName.Front:
                            AppendVerticalBandClip(clips, IntersectPolygonWithHorizontalLine(section, dividerPos), 0.0, dims.X, start, bandAlongV: true);
                            break;
                        case FaceName.Back:
                            AppendVerticalBandClip(clips, IntersectPolygonWithHorizontalLine(section, dividerPos), 0.0, dims.X, start, bandAlongV: true, mirrorHighAxis: true, highAxisExtent: dims.Z);
                            break;
                    }
                    break;
            }
        }

        return clips;
    }

    private static IReadOnlyDictionary<FaceName, IReadOnlyList<AxisAlignedRect>> BuildScoopFaceSlotExclusions(
        BoxShape box,
        Vec3 dims,
        double t)
    {
        var exclusions = new Dictionary<FaceName, List<AxisAlignedRect>>();
        foreach (var scoop in box.Scoops)
        {
            if (scoop.Face != FaceName.Bottom) continue;

            var section = BuildScoopSectionPolygon(scoop, t);
            var toe = IntersectPolygonWithHorizontalLine(section, 0.0);
            var heel = IntersectPolygonWithVerticalLine(section, 0.0);

            switch (scoop.Edge)
            {
                case FaceName.Left:
                    AppendFaceExclusion(exclusions, FaceName.Bottom, toe, 0.0, dims.Z);
                    AppendFaceExclusion(exclusions, FaceName.Left, heel, 0.0, dims.Z, intervalOnV: true);
                    break;
                case FaceName.Right:
                    AppendFaceExclusion(exclusions, FaceName.Bottom, MirrorInterval(toe, dims.X), 0.0, dims.Z);
                    AppendFaceExclusion(exclusions, FaceName.Right, heel, 0.0, dims.Z, intervalOnV: true);
                    break;
                case FaceName.Front:
                    AppendFaceExclusion(exclusions, FaceName.Bottom, toe, 0.0, dims.X, intervalOnV: true);
                    AppendFaceExclusion(exclusions, FaceName.Front, heel, 0.0, dims.X, intervalOnV: true);
                    break;
                case FaceName.Back:
                    AppendFaceExclusion(exclusions, FaceName.Bottom, MirrorInterval(toe, dims.Z), 0.0, dims.X, intervalOnV: true);
                    AppendFaceExclusion(exclusions, FaceName.Back, heel, 0.0, dims.X, intervalOnV: true);
                    break;
            }
        }

        return exclusions.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<AxisAlignedRect>)kv.Value);
    }

    private static IReadOnlyList<Vec2> BuildScoopSectionPolygon(Scoop scoop, double t)
    {
        var slant = Math.Sqrt(scoop.Inset * scoop.Inset + scoop.Rise * scoop.Rise);
        var toeDepth = t * slant / scoop.Inset;
        var toeStart = scoop.Inset - t;
        var toeEnd = toeStart + toeDepth;
        var heelDepth = t * slant / scoop.Rise * 1.5;
        var heelStart = scoop.Rise - 0.5 * t - heelDepth / 2.0;
        var heelEnd = heelStart + heelDepth;

        return NormalizePolygon(new[]
        {
            new Vec2(0.0, heelStart),
            new Vec2(0.0, heelEnd),
            new Vec2(toeEnd, 0.0),
            new Vec2(toeStart, 0.0),
        });
    }

    private static IReadOnlyList<Vec2> TranslatePolygon(IEnumerable<Vec2> polygon, (double U, double V) start)
        => NormalizePolygon(polygon.Select(p => new Vec2(p.X - start.U, p.Y - start.V)).ToArray());

    private static IReadOnlyList<Vec2> NormalizePolygon(IReadOnlyList<Vec2> polygon)
    {
        if (polygon.Count < 3) return polygon;
        return SignedArea(polygon) >= 0 ? polygon : polygon.Reverse().ToArray();
    }

    private static double SignedArea(IReadOnlyList<Vec2> polygon)
    {
        var area = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            area += a.X * b.Y - b.X * a.Y;
        }

        return area / 2.0;
    }

    private static (double Min, double Max)? IntersectPolygonWithVerticalLine(IReadOnlyList<Vec2> polygon, double x)
    {
        var hits = new List<double>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if (Math.Abs(a.X - b.X) <= Eps)
            {
                if (Math.Abs(x - a.X) <= Eps)
                {
                    hits.Add(a.Y);
                    hits.Add(b.Y);
                }
                continue;
            }

            var minX = Math.Min(a.X, b.X) - Eps;
            var maxX = Math.Max(a.X, b.X) + Eps;
            if (x < minX || x > maxX) continue;

            var t = (x - a.X) / (b.X - a.X);
            if (t < -Eps || t > 1 + Eps) continue;
            hits.Add(a.Y + (b.Y - a.Y) * t);
        }

        return NormalizeHits(hits);
    }

    private static (double Min, double Max)? IntersectPolygonWithHorizontalLine(IReadOnlyList<Vec2> polygon, double y)
    {
        var hits = new List<double>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if (Math.Abs(a.Y - b.Y) <= Eps)
            {
                if (Math.Abs(y - a.Y) <= Eps)
                {
                    hits.Add(a.X);
                    hits.Add(b.X);
                }
                continue;
            }

            var minY = Math.Min(a.Y, b.Y) - Eps;
            var maxY = Math.Max(a.Y, b.Y) + Eps;
            if (y < minY || y > maxY) continue;

            var t = (y - a.Y) / (b.Y - a.Y);
            if (t < -Eps || t > 1 + Eps) continue;
            hits.Add(a.X + (b.X - a.X) * t);
        }

        return NormalizeHits(hits);
    }

    private static (double Min, double Max)? NormalizeHits(List<double> hits)
    {
        if (hits.Count == 0) return null;

        hits.Sort();
        var unique = new List<double>();
        foreach (var hit in hits)
        {
            if (unique.Count == 0 || Math.Abs(hit - unique[^1]) > 1e-6)
            {
                unique.Add(hit);
            }
        }

        return unique.Count >= 2 ? (unique.First(), unique.Last()) : null;
    }

    private static (double Min, double Max)? MirrorInterval((double Min, double Max)? interval, double extent)
        => interval is { } value ? (extent - value.Max, extent - value.Min) : null;

    private static void AppendRectClip(
        List<IReadOnlyList<Vec2>> clips,
        (double Min, double Max)? interval,
        double fixedMin,
        double fixedMax,
        (double U, double V) start,
        bool swapAxes = false)
    {
        if (interval is not { } yRange || yRange.Max - yRange.Min <= Eps) return;
        var polygon = swapAxes
            ? RectPolygon(yRange.Min, fixedMin, yRange.Max, fixedMax)
            : RectPolygon(fixedMin, yRange.Min, fixedMax, yRange.Max);
        clips.Add(TranslatePolygon(polygon, start));
    }

    private static void AppendHorizontalClip(
        List<IReadOnlyList<Vec2>> clips,
        (double Min, double Max)? interval,
        double fixedMin,
        double fixedMax,
        (double U, double V) start,
        bool mirrorHighAxis,
        double highAxisExtent = 0.0)
    {
        if (interval is not { } value || value.Max - value.Min <= Eps) return;
        var uMin = mirrorHighAxis ? highAxisExtent - value.Max : value.Min;
        var uMax = mirrorHighAxis ? highAxisExtent - value.Min : value.Max;
        clips.Add(TranslatePolygon(RectPolygon(uMin, fixedMin, uMax, fixedMax), start));
    }

    private static void AppendVerticalBandClip(
        List<IReadOnlyList<Vec2>> clips,
        (double Min, double Max)? interval,
        double fixedMin,
        double fixedMax,
        (double U, double V) start,
        bool bandAlongV,
        bool mirrorHighAxis = false,
        double highAxisExtent = 0.0)
    {
        if (interval is not { } value || value.Max - value.Min <= Eps) return;
        var vMin = mirrorHighAxis ? highAxisExtent - value.Max : value.Min;
        var vMax = mirrorHighAxis ? highAxisExtent - value.Min : value.Max;
        var polygon = bandAlongV
            ? RectPolygon(fixedMin, vMin, fixedMax, vMax)
            : RectPolygon(vMin, fixedMin, vMax, fixedMax);
        clips.Add(TranslatePolygon(polygon, start));
    }

    private static IReadOnlyList<Vec2> RectPolygon(double minU, double minV, double maxU, double maxV)
        => new[]
        {
            new Vec2(minU, minV),
            new Vec2(maxU, minV),
            new Vec2(maxU, maxV),
            new Vec2(minU, maxV),
        };

    private static void AppendFaceExclusion(
        Dictionary<FaceName, List<AxisAlignedRect>> exclusions,
        FaceName face,
        (double Min, double Max)? interval,
        double fixedMin,
        double fixedMax,
        bool intervalOnV = false)
    {
        if (interval is not { } value || value.Max - value.Min <= Eps) return;
        var rect = intervalOnV
            ? new AxisAlignedRect(fixedMin, value.Min, fixedMax, value.Max)
            : new AxisAlignedRect(value.Min, fixedMin, value.Max, fixedMax);
        if (!exclusions.TryGetValue(face, out var list))
        {
            exclusions[face] = list = new List<AxisAlignedRect>();
        }
        list.Add(rect);
    }

    private static IReadOnlyList<SlotSpec> BuildDividerAssemblySlots(
        Axis axis,
        IReadOnlyList<DividerSet> dividers,
        double panelV,
        double t,
        double kerf)
    {
        if (panelV <= 0)
        {
            return Array.Empty<SlotSpec>();
        }

        var finalSlotDepth = panelV / 2.0 + kerf;
        var slotHeight = finalSlotDepth + kerf;
        var slots = new List<SlotSpec>();

        switch (axis)
        {
            case Axis.X:
                foreach (var ds in dividers.Where(ds => ds.Axis == Axis.Y))
                {
                    foreach (var pos in ds.Positions)
                    {
                        slots.Add(new SlotSpec(pos, panelV - finalSlotDepth / 2.0, t, slotHeight));
                    }
                }
                break;
            case Axis.Y:
                foreach (var ds in dividers.Where(ds => ds.Axis == Axis.X))
                {
                    foreach (var pos in ds.Positions)
                    {
                        slots.Add(new SlotSpec(pos, finalSlotDepth / 2.0, t, slotHeight));
                    }
                }
                break;
        }

        return slots;
    }

    private static IReadOnlyList<DividerJointSpan> BuildDividerJointSpans(double length, double t, double s, bool joined, bool dividerOwnsPrimary)
        => DividerJointBuilder.BuildSpans(length, t, s, joined, dividerOwnsPrimary);

    private static void EmitInserts(
        string parentId,
        Shape parent,
        List<BoxPlanCuttableShape> output,
        BoxPlanSettings settings,
        PipelineLogger logger)
    {
        logger?.Log($"[insert] Emitting inserts for {parentId}");
        for (var i = 0; i < parent.Inserts.Count; i++)
        {
            var insert = parent.Inserts[i];
            if (insert.Target is not BoxShape target || insert.ResolvedDimensions is not { } dims) continue;
            var idPrefix = $"{parentId}/{i}/{target.Id}";
            EmitShape(idPrefix, target, dims, output, settings, logger);
        }
    }

    private static void EmitShape(
        string idPrefix,
        BoxShape box,
        Vec3 dims,
        List<BoxPlanCuttableShape> output,
        BoxPlanSettings settings,
        PipelineLogger? logger = null)
    {
        logger?.Log($"[shape] Emitting shape {idPrefix}");
        ScoopCutter.Validate(box, dims, settings);
        var scoopShrink = ScoopCutter.HostShrinkByEdge(box, dims, settings.MaterialThickness);
        var scoopSmoothEdges = ScoopCutter.CollectSmoothEdges(box, dims, settings.MaterialThickness);
        var edges = SharedEdgeTable.Build(dims, settings, logger);
        var openFaces = box.Faces.Where(f => f.Type == FaceType.Open).Select(f => f.Name).ToHashSet();
        var (scoopAxisSlots, scoopObliqueSlots) = ScoopCutter.CollectFaceSlots(box, dims, settings, edges, openFaces, scoopSmoothEdges);
        var slotsByFace = BuildSlotsByFace(box.Dividers, dims, settings.MaterialThickness, settings.FingerJointSize);
        var scoopFaceSlotExclusions = BuildScoopFaceSlotExclusions(box, dims, settings.MaterialThickness);

        foreach (var face in box.Faces)
        {
            if (face.Type != FaceType.Closed) continue;
            var faceFeatures = box.Features.Where(f => f.Face == face.Name).ToArray();
            var dividerSlots = slotsByFace.TryGetValue(face.Name, out var ds) ? ds : Array.Empty<SlotSpec>();
            var extraSlots = scoopAxisSlots.TryGetValue(face.Name, out var ss) ? ss : null;
            var obliqueSlots = scoopObliqueSlots.TryGetValue(face.Name, out var os) ? os : null;
            scoopSmoothEdges.TryGetValue(face.Name, out var smoothSegments);
            output.Add(BuildFacePiece(
                idPrefix,
                face.Name,
                dims,
                edges,
                openFaces,
                faceFeatures,
                dividerSlots,
                extraSlots,
                scoopFaceSlotExclusions,
                scoopShrink,
                smoothSegments,
                obliqueSlots,
                settings,
                logger));
        }

        EmitDividerPanels(idPrefix, box, dims, openFaces, output, settings, logger);
        output.AddRange(ScoopCutter.BuildScoopPanels(idPrefix, box, dims, settings, edges, openFaces, scoopSmoothEdges, logger));
    }

    private static IReadOnlyDictionary<FaceName, IReadOnlyList<SlotSpec>> BuildSlotsByFace(
        IReadOnlyList<DividerSet> dividers, Vec3 dims, double t, double s)
    {
        var slots = Enum.GetValues<FaceName>().ToDictionary(f => f, f => new List<SlotSpec>());
        foreach (var ds in dividers)
        {
            foreach (var p in ds.Positions)
            {
                AddSlots(slots, ds.Axis, p, ds.Facing, dims, t, s);
            }
        }
        return slots.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SlotSpec>)kv.Value);
    }

    private static void AddSlots(
        Dictionary<FaceName, List<SlotSpec>> slots,
        Axis axis,
        double pos,
        FaceName? facing,
        Vec3 dims,
        double t,
        double s)
    {
        void Add(FaceName face, double u, double v, double w, double h, bool dividerOwnsPrimary)
        {
            if (face == facing) return;
            foreach (var slot in BuildFingerSlots(u, v, w, h, t, s, dividerOwnsPrimary))
            {
                slots[face].Add(slot);
            }
        }

        switch (axis)
        {
            case Axis.X:
                var xOnBottomTop = DividerSpan(dims.Z);
                var xOnFrontBack = DividerSpan(dims.Y);
                Add(FaceName.Bottom, pos, xOnBottomTop.Center, t, xOnBottomTop.Length, dividerOwnsPrimary: false);
                Add(FaceName.Top,    pos, xOnBottomTop.Center, t, xOnBottomTop.Length, dividerOwnsPrimary: false);
                Add(FaceName.Front,  pos, xOnFrontBack.Center, t, xOnFrontBack.Length, dividerOwnsPrimary: true);
                Add(FaceName.Back,   pos, xOnFrontBack.Center, t, xOnFrontBack.Length, dividerOwnsPrimary: true);
                break;
            case Axis.Y:
                var yOnFrontBack = DividerSpan(dims.X);
                var yOnLeftRight = DividerSpan(dims.Z);
                Add(FaceName.Front, yOnFrontBack.Center, pos, yOnFrontBack.Length, t, dividerOwnsPrimary: true);
                Add(FaceName.Back,  yOnFrontBack.Center, pos, yOnFrontBack.Length, t, dividerOwnsPrimary: true);
                Add(FaceName.Left,  yOnLeftRight.Center, pos, yOnLeftRight.Length, t, dividerOwnsPrimary: false);
                Add(FaceName.Right, yOnLeftRight.Center, pos, yOnLeftRight.Length, t, dividerOwnsPrimary: false);
                break;
            case Axis.Z:
                var zOnBottomTop = DividerSpan(dims.X);
                var zOnLeftRight = DividerSpan(dims.Y);
                Add(FaceName.Bottom, zOnBottomTop.Center, pos, zOnBottomTop.Length, t, dividerOwnsPrimary: true);
                Add(FaceName.Top,    zOnBottomTop.Center, pos, zOnBottomTop.Length, t, dividerOwnsPrimary: true);
                Add(FaceName.Left,   pos, zOnLeftRight.Center, t, zOnLeftRight.Length, dividerOwnsPrimary: false);
                Add(FaceName.Right,  pos, zOnLeftRight.Center, t, zOnLeftRight.Length, dividerOwnsPrimary: false);
                break;
        }
    }

    private static IReadOnlyList<SlotSpec> BuildFingerSlots(double u, double v, double w, double h, double t, double s, bool dividerOwnsPrimary)
        => DividerJointBuilder.BuildFingerSlots(u, v, w, h, t, s, dividerOwnsPrimary);

    // True when `face` is the lowest-priority face that still reaches this 3D corner.
    // Open neighbors and faces trimmed away from the corner by scoop host shrink are
    // treated as absent.
    private static bool OwnsCorner(
        FaceName face,
        FaceName neighborPrev,
        FaceName neighborNext,
        IReadOnlySet<FaceName> openFaces,
        IReadOnlyDictionary<FaceName, double[]> hostShrinkByFace)
    {
        if (!FaceReachesCorner(face, neighborPrev, neighborNext, hostShrinkByFace))
            return false;

        var p = FacePriority.Of(face);
        if (!openFaces.Contains(neighborPrev)
            && FaceReachesCorner(neighborPrev, neighborNext, face, hostShrinkByFace)
            && FacePriority.Of(neighborPrev) < p)
            return false;

        if (!openFaces.Contains(neighborNext)
            && FaceReachesCorner(neighborNext, face, neighborPrev, hostShrinkByFace)
            && FacePriority.Of(neighborNext) < p)
            return false;

        return true;
    }

    private static bool FaceReachesCorner(
        FaceName face,
        FaceName adjacentA,
        FaceName adjacentB,
        IReadOnlyDictionary<FaceName, double[]> hostShrinkByFace)
    {
        if (!hostShrinkByFace.TryGetValue(face, out var shrink))
            return true;

        var cornerIndex = FindCornerIndex(face, adjacentA, adjacentB);
        var prevEdgeIndex = (cornerIndex + 3) % 4;
        return shrink[prevEdgeIndex] <= 0 && shrink[cornerIndex] <= 0;
    }

    private static int FindCornerIndex(FaceName face, FaceName adjacentA, FaceName adjacentB)
    {
        var ccw = FaceLayout.EdgesCcw(face);
        for (var i = 0; i < 4; i++)
        {
            var prevNeighbor = ccw[(i + 3) % 4].Neighbor;
            var nextNeighbor = ccw[i].Neighbor;
            if ((prevNeighbor == adjacentA && nextNeighbor == adjacentB)
                || (prevNeighbor == adjacentB && nextNeighbor == adjacentA))
                return i;
        }

        throw new InvalidOperationException(
            $"Faces '{adjacentA}' and '{adjacentB}' do not meet at a corner of face '{face}'.");
    }

    private static bool InSmooth(IReadOnlyList<SmoothSegment>? segs, int edgeIndex, double start, double end)
    {
        if (segs is null) return false;
        foreach (var s in segs)
            if (s.EdgeIndex == edgeIndex && start < s.End && end > s.Start) return true;
        return false;
    }

    private static BoxPlanCuttableShape BuildFacePiece(
        string shapeId,
        FaceName face,
        Vec3 dims,
        IReadOnlyDictionary<string, SharedEdge> edges,
        IReadOnlySet<FaceName> openFaces,
        IReadOnlyList<Feature> features,
        IReadOnlyList<SlotSpec> dividerSlots,
        IReadOnlyList<SlotSpec>? scoopSlots,
        IReadOnlyDictionary<FaceName, IReadOnlyList<AxisAlignedRect>> scoopFaceSlotExclusions,
        IReadOnlyDictionary<FaceName, double[]> hostShrinkByFace,
        IReadOnlyList<SmoothSegment>? smoothSegments,
        IReadOnlyList<ObliqueSlotSpec>? obliqueSlots,
        BoxPlanSettings settings,
        PipelineLogger? logger = null)
    {
        logger?.Log($"[face] Building face piece {shapeId}.{face}");
        var (panelU, panelV) = FaceLayout.PanelSize(face, dims);
        var t = settings.MaterialThickness;
        var ccw = FaceLayout.EdgesCcw(face);
        double[] edgeLengths = { panelU, panelV, panelU, panelV };

        var cornerOwned = new bool[4];
        for (var i = 0; i < 4; i++)
        {
            var prevNeighbor = ccw[(i + 3) % 4].Neighbor;
            var nextNeighbor = ccw[i].Neighbor;
            cornerOwned[i] = OwnsCorner(face, prevNeighbor, nextNeighbor, openFaces, hostShrinkByFace);
        }

        var builder = new PanelShapeBuilder(panelU, panelV, logger);

        // Scoop shrink: remove a full-edge strip on each scooped edge so the host
        // face panel is reduced to fit between the scoop toe lines. Joinery on the
        // new edge (toe joint to the scoop) is not generated in Phase 1.
        if (hostShrinkByFace.TryGetValue(face, out var hostShrink))
        {
            for (var i = 0; i < 4; i++)
            {
                if (hostShrink[i] <= 0) continue;
                builder.SubtractEdgeNotch(i, 0, edgeLengths[i], hostShrink[i]);
            }
        }

        // Corner notches at each non-owned corner: subtract a t×t square shared between
        // the two edges meeting at that corner.
        for (var i = 0; i < 4; i++)
        {
            if (cornerOwned[i]) continue;
            var prevI = (i + 3) % 4;
            if (!InSmooth(smoothSegments, prevI, edgeLengths[prevI] - t, edgeLengths[prevI]))
                builder.SubtractEdgeNotch(prevI, edgeLengths[prevI] - t, t, t);
            if (!InSmooth(smoothSegments, i, 0, t))
                builder.SubtractEdgeNotch(i, 0, t, t);
        }

        // Finger notches: for each edge, subtract the blocks owned by the neighbour face.
        for (var i = 0; i < 4; i++)
        {
            var edgeMap = ccw[i];
            if (openFaces.Contains(edgeMap.Neighbor)) continue;

            var shared = edges[SharedEdgeTable.Id(edgeMap.Face, edgeMap.Neighbor)];
            var ordered = edgeMap.ForwardAlongShared
                ? shared.Blocks
                : shared.Blocks.Reverse().ToList();

            var cursor = t; // inner region starts t from the CCW-start corner
            foreach (var block in ordered)
            {
                if (block.Owner != face && !InSmooth(smoothSegments, i, cursor, cursor + block.Length))
                    builder.SubtractEdgeNotch(i, cursor, block.Length, t);
                cursor += block.Length;
            }
        }

        var polygon = builder.Build();
        var (path, bbMin, bbMax, translation) = KerfOffset.OffsetOutwardAndTranslate(polygon, settings.Kerf, logger);

        var leftEdgeSlots = CollectEdgeSlotSpans(face, edgeIndex: 3, ccw, edges, openFaces, smoothSegments, t);
        var rightEdgeSlots = CollectEdgeSlotSpans(face, edgeIndex: 1, ccw, edges, openFaces, smoothSegments, t);

        var interiorCuts = new List<CuttablePath>();
        var engravings = new List<CuttablePath>();
        var textEngravings = new List<TextEngraving>();
        var rasterEngravings = new List<RasterEngraving>();
        var svgEngravings = new List<SvgEngraving>();
        foreach (var feature in features)
        {
            if (feature is SplitCutFeature splitCut)
            {
                var splitPath = BuildSplitCutPath(splitCut, panelU, panelV, t, translation, leftEdgeSlots, rightEdgeSlots, logger);
                interiorCuts.AddRange(CutoutClipper.ClipToOutline(splitPath, path, logger));
            }
            else if (feature is CutoutFeature cutout)
            {
                var seed = CutoutBuilder.ResolvePlacementCenter(cutout.Position, cutout.Shape, cutout.Width, cutout.Height, panelU, panelV, settings.Kerf, logger);
                var margin = cutout.Repeat is null ? t : 2 * t;
                var zone = new CutoutBuilder.SafeZone(
                    UMin: openFaces.Contains(ccw[3].Neighbor) ? 0 : margin,
                    UMax: openFaces.Contains(ccw[1].Neighbor) ? panelU : panelU - margin,
                    VMin: openFaces.Contains(ccw[0].Neighbor) ? 0 : margin,
                    VMax: openFaces.Contains(ccw[2].Neighbor) ? panelV : panelV - margin);
                foreach (var center in CutoutBuilder.ExpandCenters(cutout, seed, zone, settings.Kerf))
                {
                    var cutPath = CutoutBuilder.Build(cutout, center, settings.Kerf, translation, logger);
                    interiorCuts.AddRange(CutoutClipper.ClipToOutline(cutPath, path, logger));
                }
            }
            else if (feature is LineEngravingFeature lineEngraving)
            {
                var center = CutoutBuilder.ResolvePlacementCenter(lineEngraving.Position, lineEngraving.Shape, lineEngraving.Width, lineEngraving.Height, panelU, panelV, kerf: 0, logger);
                // Engravings don't need kerf adjustment — we engrave the exact specified size.
                var engravingPath = CutoutBuilder.Build(lineEngraving.Shape, lineEngraving.Width, lineEngraving.Height, center, kerf: 0, translation, logger);
                engravings.AddRange(CutoutClipper.ClipToOutline(engravingPath, path, logger));
            }
            else if (feature is EngravingGridFeature grid)
            {
                foreach (var gridLine in EngravingBuilder.BuildGrid(panelU, panelV, grid.CellSize, grid.Center, translation))
                    engravings.AddRange(CutoutClipper.ClipToOutline(gridLine, path, logger));
            }
            else if (feature is EngravingTextFeature engraving)
            {
                var center = CutoutBuilder.ResolveCenter(engraving.Position, panelU, panelV, logger);
                var anchorPoint = new Vec2(center.X + translation.X, center.Y + translation.Y);
                if (CutoutClipper.IsInsideOutline(anchorPoint, path))
                {
                    textEngravings.Add(new TextEngraving
                    {
                        Text = engraving.Text,
                        X = anchorPoint.X,
                        Y = anchorPoint.Y,
                        Anchor = engraving.Position?.Anchor ?? Anchor.Center,
                        Size = engraving.Size,
                        Font = engraving.Font,
                        Bold = engraving.Bold,
                        Italic = engraving.Italic,
                    });
                }
                else
                {
                    logger?.Warn($"[engraving-drop] Text engraving \"{engraving.Text}\" dropped — anchor ({anchorPoint.X:F2}, {anchorPoint.Y:F2}) is outside the panel outline.");
                }
            }
            else if (feature is RasterEngravingFeature raster)
            {
                var anchor = raster.Position?.Anchor ?? Anchor.Center;
                var center = CutoutBuilder.ResolvePlacementCenter(
                    raster.Position,
                    CutoutShape.Rectangle,
                    raster.Width ?? 0.0,
                    raster.Height ?? 0.0,
                    panelU,
                    panelV,
                    kerf: 0,
                    logger);
                var localAnchor = EngravingBuilder.AnchorPointFromCenter(center, anchor, raster.Width ?? 0.0, raster.Height ?? 0.0);
                var anchorPoint = new Vec2(localAnchor.X + translation.X, localAnchor.Y + translation.Y);
                if (CutoutClipper.IsInsideOutline(anchorPoint, path))
                {
                    rasterEngravings.Add(new RasterEngraving
                    {
                        Href = raster.Source,
                        X = anchorPoint.X,
                        Y = anchorPoint.Y,
                        Anchor = anchor,
                        Width = raster.Width,
                        Height = raster.Height,
                    });
                }
                else
                {
                    logger?.Warn($"[engraving-drop] Raster engraving \"{raster.Source}\" dropped — anchor ({anchorPoint.X:F2}, {anchorPoint.Y:F2}) is outside the panel outline.");
                }
            }
            else if (feature is SvgEngravingFeature svg)
            {
                var anchor = svg.Position?.Anchor ?? Anchor.Center;
                var center = CutoutBuilder.ResolvePlacementCenter(
                    svg.Position,
                    CutoutShape.Rectangle,
                    svg.Width ?? 0.0,
                    svg.Height ?? 0.0,
                    panelU,
                    panelV,
                    kerf: 0,
                    logger);
                var localAnchor = EngravingBuilder.AnchorPointFromCenter(center, anchor, svg.Width ?? 0.0, svg.Height ?? 0.0);
                var anchorPoint = new Vec2(localAnchor.X + translation.X, localAnchor.Y + translation.Y);
                if (CutoutClipper.IsInsideOutline(anchorPoint, path))
                {
                    svgEngravings.Add(new SvgEngraving
                    {
                        Href = svg.Source,
                        X = anchorPoint.X,
                        Y = anchorPoint.Y,
                        Anchor = anchor,
                        Width = svg.Width,
                        Height = svg.Height,
                    });
                }
                else
                {
                    logger?.Warn($"[engraving-drop] SVG engraving \"{svg.Source}\" dropped — anchor ({anchorPoint.X:F2}, {anchorPoint.Y:F2}) is outside the panel outline.");
                }
            }
        }
        var clippedDividerSlots = scoopFaceSlotExclusions.TryGetValue(face, out var exclusions)
            ? FilterSlotsByExclusions(dividerSlots, exclusions)
            : dividerSlots;
        foreach (var slot in clippedDividerSlots)
        {
            var slotPath = CutoutBuilder.BuildSlotRectangle(slot, settings.Kerf, translation, logger);
            interiorCuts.AddRange(CutoutClipper.ClipToOutline(slotPath, path, logger));
        }
        if (scoopSlots is not null)
        {
            foreach (var slot in scoopSlots)
            {
                var slotPath = CutoutBuilder.BuildSlotRectangle(slot, settings.Kerf, translation, logger);
                interiorCuts.AddRange(CutoutClipper.ClipToOutline(slotPath, path, logger));
            }
        }
        if (obliqueSlots is not null)
        {
            foreach (var obl in obliqueSlots)
            {
                var slotPath = CutoutBuilder.BuildObliqueSlotRectangle(obl, settings.Kerf, translation);
                interiorCuts.AddRange(CutoutClipper.ClipToOutline(slotPath, path, logger));
            }
        }

        return new BoxPlanCuttableShape
        {
            Id = $"{shapeId}.{face.ToString().ToLowerInvariant()}",
            BoundingBoxMin = bbMin,
            BoundingBoxMax = bbMax,
            Outline = path,
            InteriorCuts = interiorCuts,
            Engravings = engravings,
            TextEngravings = textEngravings,
            RasterEngravings = rasterEngravings,
            SvgEngravings = svgEngravings,
        };
    }

    private static CuttablePath BuildSplitCutPath(
        SplitCutFeature splitCut,
        double panelU,
        double panelV,
        double t,
        Vec2 translation,
        IReadOnlyList<EdgeSlotSpan> leftEdgeSlots,
        IReadOnlyList<EdgeSlotSpan> rightEdgeSlots,
        PipelineLogger? logger = null)
    {
        var leftTabbed = leftEdgeSlots.Count > 0;
        var rightTabbed = rightEdgeSlots.Count > 0;

        var startU = leftTabbed ? t : 0.0;
        var endU = rightTabbed ? panelU - t : panelU;
        if (endU - startU <= Eps)
        {
            startU = 0.0;
            endU = panelU;
        }

        var spanU = endU - startU;
        var points = splitCut.NormalizedCurve
            .Select(p => new Vec2(
                translation.X + startU + p.X * spanU,
                translation.Y + splitCut.Height + splitCut.Amplitude * p.Y))
            .ToList();

        logger?.Log($"[split-cut] Building split cut on {splitCut.Face} at height={splitCut.Height:F3} with {points.Count} samples");

        if (points.Count < 2)
        {
            points = new List<Vec2>
            {
                new(translation.X + startU, translation.Y + Math.Clamp(splitCut.Height, 0.0, panelV)),
                new(translation.X + endU, translation.Y + Math.Clamp(splitCut.Height, 0.0, panelV)),
            };
        }

        var leftOriginal = points[0].Y - translation.Y;
        var rightOriginal = points[^1].Y - translation.Y;

        if (TryComputeSplitCutVerticalShift(leftOriginal, rightOriginal, leftEdgeSlots, rightEdgeSlots, out var shiftY)
            && Math.Abs(shiftY) > Eps)
        {
            for (var i = 0; i < points.Count; i++)
            {
                points[i] = new Vec2(points[i].X, points[i].Y + shiftY);
            }

            if (leftTabbed)
            {
                var leftShifted = points[0].Y - translation.Y;
                if (TrySnapToNearestSlotBoundary(leftShifted, leftEdgeSlots, out var snapped, out var side))
                    LogSplitSnap("left", leftOriginal, leftShifted, snapped, side, logger);
            }

            if (rightTabbed)
            {
                var rightShifted = points[^1].Y - translation.Y;
                if (TrySnapToNearestSlotBoundary(rightShifted, rightEdgeSlots, out var snapped, out var side))
                    LogSplitSnap("right", rightOriginal, rightShifted, snapped, side, logger);
            }
        }

        return new CuttablePath
        {
            Start = points[0],
            Segments = points.Skip(1).Select(p => (PathSegment)new LineSegment(p)).ToList(),
            Closed = false,
        };
    }

    private static IReadOnlyList<EdgeSlotSpan> CollectEdgeSlotSpans(
        FaceName face,
        int edgeIndex,
        IReadOnlyList<FaceEdgeMap> ccw,
        IReadOnlyDictionary<string, SharedEdge> edges,
        IReadOnlySet<FaceName> openFaces,
        IReadOnlyList<SmoothSegment>? smoothSegments,
        double t)
    {
        var edgeMap = ccw[edgeIndex];
        if (openFaces.Contains(edgeMap.Neighbor))
            return Array.Empty<EdgeSlotSpan>();

        var shared = edges[SharedEdgeTable.Id(edgeMap.Face, edgeMap.Neighbor)];
        var ordered = edgeMap.ForwardAlongShared
            ? shared.Blocks
            : shared.Blocks.Reverse().ToList();

        var spans = new List<EdgeSlotSpan>();
        var cursor = t;
        foreach (var block in ordered)
        {
            var start = cursor;
            var end = cursor + block.Length;
            if (block.Owner != face && !InSmooth(smoothSegments, edgeIndex, start, end))
                spans.Add(new EdgeSlotSpan(start, end));
            cursor = end;
        }

        return spans;
    }

    private static bool TrySnapToNearestSlotBoundary(
        double v,
        IReadOnlyList<EdgeSlotSpan> spans,
        out double snapped,
        out string side)
    {
        snapped = v;
        side = "bottom";
        if (spans.Count == 0)
            return false;

        var bestDelta = double.MaxValue;
        foreach (var span in spans)
        {
            var dStart = Math.Abs(v - span.Start);
            if (dStart < bestDelta)
            {
                bestDelta = dStart;
                snapped = span.Start;
                side = "bottom";
            }

            var dEnd = Math.Abs(v - span.End);
            if (dEnd < bestDelta)
            {
                bestDelta = dEnd;
                snapped = span.End;
                side = "top";
            }
        }

        return true;
    }

    private static bool TryComputeSplitCutVerticalShift(
        double leftY,
        double rightY,
        IReadOnlyList<EdgeSlotSpan> leftSpans,
        IReadOnlyList<EdgeSlotSpan> rightSpans,
        out double shiftY)
    {
        shiftY = 0;
        var hasLeft = leftSpans.Count > 0;
        var hasRight = rightSpans.Count > 0;
        if (!hasLeft && !hasRight)
            return false;

        var leftBoundaries = EnumerateSlotBoundaries(leftSpans).ToList();
        var rightBoundaries = EnumerateSlotBoundaries(rightSpans).ToList();

        if (hasLeft && !hasRight)
        {
            var target = NearestBoundary(leftY, leftBoundaries);
            shiftY = target - leftY;
            return true;
        }

        if (!hasLeft && hasRight)
        {
            var target = NearestBoundary(rightY, rightBoundaries);
            shiftY = target - rightY;
            return true;
        }

        // Try to find a single translation that snaps both endpoints exactly.
        const double snapTol = 1e-6;
        var bestExact = double.MaxValue;
        var foundExact = false;
        foreach (var leftBoundary in leftBoundaries)
        {
            foreach (var rightBoundary in rightBoundaries)
            {
                var leftShift = leftBoundary - leftY;
                var rightShift = rightBoundary - rightY;
                if (Math.Abs(leftShift - rightShift) <= snapTol)
                {
                    var candidate = (leftShift + rightShift) / 2.0;
                    var abs = Math.Abs(candidate);
                    if (!foundExact || abs < bestExact)
                    {
                        foundExact = true;
                        bestExact = abs;
                        shiftY = candidate;
                    }
                }
            }
        }

        if (foundExact)
            return true;

        // Fallback: pick the translation that best matches both nearest boundaries.
        var candidates = leftBoundaries.Select(b => b - leftY)
            .Concat(rightBoundaries.Select(b => b - rightY))
            .Distinct()
            .ToList();

        var bestError = double.MaxValue;
        var bestAbsShift = double.MaxValue;
        foreach (var candidate in candidates)
        {
            var shiftedLeft = leftY + candidate;
            var shiftedRight = rightY + candidate;
            var error = Math.Abs(shiftedLeft - NearestBoundary(shiftedLeft, leftBoundaries))
                + Math.Abs(shiftedRight - NearestBoundary(shiftedRight, rightBoundaries));
            var abs = Math.Abs(candidate);
            if (error < bestError - Eps || (Math.Abs(error - bestError) <= Eps && abs < bestAbsShift))
            {
                bestError = error;
                bestAbsShift = abs;
                shiftY = candidate;
            }
        }

        return true;
    }

    private static IEnumerable<double> EnumerateSlotBoundaries(IReadOnlyList<EdgeSlotSpan> spans)
    {
        foreach (var span in spans)
        {
            yield return span.Start;
            yield return span.End;
        }
    }

    private static double NearestBoundary(double v, IReadOnlyList<double> boundaries)
    {
        var best = boundaries[0];
        var bestDelta = Math.Abs(v - best);
        for (var i = 1; i < boundaries.Count; i++)
        {
            var delta = Math.Abs(v - boundaries[i]);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = boundaries[i];
            }
        }

        return best;
    }

    private static void LogSplitSnap(
        string endpoint,
        double original,
        double shifted,
        double snapped,
        string slotSide,
        PipelineLogger? logger)
    {
        if (Math.Abs(shifted - original) <= Eps)
            return;

        var direction = shifted > original ? "up" : "down";
        logger?.Log($"[split-cut] Shifted {endpoint} endpoint {direction} toward slot {slotSide}: {original:F3} -> {shifted:F3} (target {snapped:F3})");
    }

    private static IReadOnlyList<SlotSpec> FilterSlotsByExclusions(
        IReadOnlyList<SlotSpec> slots,
        IReadOnlyList<AxisAlignedRect> exclusions)
    {
        if (slots.Count == 0 || exclusions.Count == 0) return slots;

        var result = new List<SlotSpec>();
        foreach (var slot in slots)
        {
            var bounds = SlotBounds(slot);
            var fullyCovered = false;
            foreach (var exclusion in exclusions)
            {
                if (ContainsRect(exclusion, bounds))
                {
                    fullyCovered = true;
                    break;
                }
            }

            if (!fullyCovered)
                result.Add(slot);
        }

        return result;
    }

    private static AxisAlignedRect SlotBounds(SlotSpec slot)
    {
        var halfW = Math.Abs(slot.Width) / 2.0;
        var halfH = Math.Abs(slot.Height) / 2.0;
        return new AxisAlignedRect(slot.U - halfW, slot.V - halfH, slot.U + halfW, slot.V + halfH);
    }

    private static bool ContainsRect(AxisAlignedRect outer, AxisAlignedRect inner)
        => outer.MinU <= inner.MinU + Eps
        && outer.MinV <= inner.MinV + Eps
        && outer.MaxU >= inner.MaxU - Eps
        && outer.MaxV >= inner.MaxV - Eps;
}
