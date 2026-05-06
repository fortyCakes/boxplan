using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

public sealed class CuttingPipeline
{
    public BoxPlanCuttableShape[] Run(BoxPlan plan, BoxPlanSettings settings)
    {
        var output = new List<BoxPlanCuttableShape>();
        foreach (var shape in plan.Shapes)
        {
            if (shape is not BoxShape box || box.Dimensions is not { } dims) continue;
            EmitShape(box.Id, box, dims, output, settings);
            EmitInserts(box.Id, box, output, settings);
        }
        return output.ToArray();
    }

    private static void EmitDividerPanels(
        string parentId,
        BoxShape parent,
        Vec3 dims,
        IReadOnlySet<FaceName> openFaces,
        List<BoxPlanCuttableShape> output,
        BoxPlanSettings settings)
    {
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
                output.Add(BuildDividerPanel(
                    $"{parentId}.divider-{dset.Axis.ToString().ToLowerInvariant()}@{pos}",
                    panelU,
                    panelV,
                    edges,
                    dividerSlots,
                    settings));
            }
        }
    }

    private static (double U, double V) DividerPanelSize(Axis axis, Vec3 dims, FaceName? facing, double t)
    {
        var (uExt, vExt) = axis switch
        {
            Axis.X => (dims.Y, dims.Z),
            Axis.Y => (dims.X, dims.Z),
            Axis.Z => (dims.X, dims.Y),
            _ => (0.0, 0.0),
        };
        if (facing is { } f)
        {
            switch (axis, f)
            {
                case (Axis.X, FaceName.Front) or (Axis.X, FaceName.Back): vExt -= t; break;
                case (Axis.X, FaceName.Top) or (Axis.X, FaceName.Bottom): uExt -= t; break;
                case (Axis.Y, FaceName.Front) or (Axis.Y, FaceName.Back): vExt -= t; break;
                case (Axis.Y, FaceName.Left) or (Axis.Y, FaceName.Right): uExt -= t; break;
                case (Axis.Z, FaceName.Top) or (Axis.Z, FaceName.Bottom): vExt -= t; break;
                case (Axis.Z, FaceName.Left) or (Axis.Z, FaceName.Right): uExt -= t; break;
            }
        }
        return (uExt, vExt);
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
        BoxPlanSettings settings)
    {
        var t = settings.MaterialThickness;
        var spans = new[]
        {
            BuildDividerJointSpans(u, t, settings.FingerJointSize, edges[0].Joined, edges[0].DividerOwnsPrimary),
            BuildDividerJointSpans(v, t, settings.FingerJointSize, edges[1].Joined, edges[1].DividerOwnsPrimary),
            BuildDividerJointSpans(u, t, settings.FingerJointSize, edges[2].Joined, edges[2].DividerOwnsPrimary),
            BuildDividerJointSpans(v, t, settings.FingerJointSize, edges[3].Joined, edges[3].DividerOwnsPrimary),
        };

        if (settings.Debug)
            LogDividerTabSizes(id, edges, spans);

        var builder = new PanelShapeBuilder(u, v);

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
        var (path, bbMin, bbMax, translation) = KerfOffset.OffsetOutwardAndTranslate(polygon, settings.Kerf);
        var interiorCuts = dividerSlots
            .Select(slot => CutoutBuilder.BuildSlotRectangle(slot, settings.Kerf, translation))
            .ToArray();
        return new BoxPlanCuttableShape
        {
            Id = id,
            BoundingBoxMin = bbMin,
            BoundingBoxMax = bbMax,
            Outline = path,
            InteriorCuts = interiorCuts,
            Engravings = Array.Empty<CuttablePath>(),
        };
    }

    private static void LogDividerTabSizes(
        string id,
        IReadOnlyList<DividerEdgeSpec> edges,
        IReadOnlyList<DividerJointSpan>[] spans)
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

            Console.WriteLine(
                $"[divider-joint] {id} edge={edges[index].Face} tabs=[{string.Join(", ", tabSizes)}] slots=[{string.Join(", ", slotSizes)}]");
        }
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
    {
        if (!joined || length <= 0)
        {
            return new[] { new DividerJointSpan(length, DividerJointSpanKind.Smooth) };
        }

        var edgeInset = t * 1.5;
        var innerLength = Math.Max(0, length - 2 * edgeInset);
        var blocks = FingerJointPattern.Build(innerLength, s);
        if (!blocks.Any(block => !block.PrimaryOwns))
        {
            return new[] { new DividerJointSpan(length, DividerJointSpanKind.Smooth) };
        }

        var spans = new List<DividerJointSpan>();

        void Add(double spanLength, DividerJointSpanKind kind)
        {
            if (spanLength <= 0)
            {
                return;
            }

            if (spans.Count > 0 && spans[^1].Kind == kind)
            {
                spans[^1] = spans[^1] with { Length = spans[^1].Length + spanLength };
                return;
            }

            spans.Add(new DividerJointSpan(spanLength, kind));
        }

        Add(edgeInset, DividerJointSpanKind.EndInset);
        foreach (var block in blocks)
        {
            var dividerOwnsBlock = block.PrimaryOwns == dividerOwnsPrimary;
            Add(block.Length, dividerOwnsBlock ? DividerJointSpanKind.FaceSlot : DividerJointSpanKind.DividerTab);
        }
        Add(edgeInset, DividerJointSpanKind.EndInset);

        return spans;
    }

    private static void EmitInserts(
        string parentId,
        Shape parent,
        List<BoxPlanCuttableShape> output,
        BoxPlanSettings settings)
    {
        for (var i = 0; i < parent.Inserts.Count; i++)
        {
            var insert = parent.Inserts[i];
            if (insert.Target is not BoxShape target || insert.ResolvedDimensions is not { } dims) continue;
            var idPrefix = $"{parentId}/{i}/{target.Id}";
            EmitShape(idPrefix, target, dims, output, settings);
        }
    }

    private static void EmitShape(
        string idPrefix,
        BoxShape box,
        Vec3 dims,
        List<BoxPlanCuttableShape> output,
        BoxPlanSettings settings)
    {
        var edges = SharedEdgeTable.Build(dims, settings);
        var openFaces = box.Faces.Where(f => f.Type == FaceType.Open).Select(f => f.Name).ToHashSet();
        var slotsByFace = BuildSlotsByFace(box.Dividers, dims, settings.MaterialThickness, settings.FingerJointSize);

        foreach (var face in box.Faces)
        {
            if (face.Type != FaceType.Closed) continue;
            var faceFeatures = box.Features.Where(f => f.Face == face.Name).ToArray();
            var faceSlots = slotsByFace.TryGetValue(face.Name, out var s) ? s : Array.Empty<SlotSpec>();
            output.Add(BuildFacePiece(idPrefix, face.Name, dims, edges, openFaces, faceFeatures, faceSlots, settings));
        }

        EmitDividerPanels(idPrefix, box, dims, openFaces, output, settings);
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

        static (double Center, double Length) Span(double fullLength, FaceName? facingFace, FaceName lowFace, FaceName highFace, double thickness)
        {
            if (facingFace == lowFace)
            {
                return ((fullLength - thickness) / 2.0, fullLength - thickness);
            }

            if (facingFace == highFace)
            {
                return ((fullLength + thickness) / 2.0, fullLength - thickness);
            }

            return (fullLength / 2.0, fullLength);
        }

        switch (axis)
        {
            case Axis.X:
                var xOnBottomTop = Span(dims.Z, facing, FaceName.Front, FaceName.Back, t);
                var xOnFrontBack = Span(dims.Y, facing, FaceName.Bottom, FaceName.Top, t);
                Add(FaceName.Bottom, pos, xOnBottomTop.Center, t, xOnBottomTop.Length, dividerOwnsPrimary: false);
                Add(FaceName.Top,    pos, xOnBottomTop.Center, t, xOnBottomTop.Length, dividerOwnsPrimary: false);
                Add(FaceName.Front,  pos, xOnFrontBack.Center, t, xOnFrontBack.Length, dividerOwnsPrimary: true);
                Add(FaceName.Back,   pos, xOnFrontBack.Center, t, xOnFrontBack.Length, dividerOwnsPrimary: true);
                break;
            case Axis.Y:
                var yOnFrontBack = Span(dims.X, facing, FaceName.Left, FaceName.Right, t);
                var yOnLeftRight = Span(dims.Z, facing, FaceName.Front, FaceName.Back, t);
                Add(FaceName.Front, yOnFrontBack.Center, pos, yOnFrontBack.Length, t, dividerOwnsPrimary: true);
                Add(FaceName.Back,  yOnFrontBack.Center, pos, yOnFrontBack.Length, t, dividerOwnsPrimary: true);
                Add(FaceName.Left,  yOnLeftRight.Center, pos, yOnLeftRight.Length, t, dividerOwnsPrimary: false);
                Add(FaceName.Right, yOnLeftRight.Center, pos, yOnLeftRight.Length, t, dividerOwnsPrimary: false);
                break;
            case Axis.Z:
                var zOnBottomTop = Span(dims.X, facing, FaceName.Left, FaceName.Right, t);
                var zOnLeftRight = Span(dims.Y, facing, FaceName.Bottom, FaceName.Top, t);
                Add(FaceName.Bottom, zOnBottomTop.Center, pos, zOnBottomTop.Length, t, dividerOwnsPrimary: true);
                Add(FaceName.Top,    zOnBottomTop.Center, pos, zOnBottomTop.Length, t, dividerOwnsPrimary: true);
                Add(FaceName.Left,   pos, zOnLeftRight.Center, t, zOnLeftRight.Length, dividerOwnsPrimary: false);
                Add(FaceName.Right,  pos, zOnLeftRight.Center, t, zOnLeftRight.Length, dividerOwnsPrimary: false);
                break;
        }
    }

    private static IReadOnlyList<SlotSpec> BuildFingerSlots(double u, double v, double w, double h, double t, double s, bool dividerOwnsPrimary)
    {
        var slots = new List<SlotSpec>();
        var vertical = h >= w;
        var length = vertical ? h : w;
        var spans = BuildDividerJointSpans(length, t, s, joined: true, dividerOwnsPrimary);
        var cursor = -length / 2.0;
        foreach (var span in spans)
        {
            if (span.Kind == DividerJointSpanKind.FaceSlot)
            {
                if (vertical)
                {
                    slots.Add(new SlotSpec(u, v + cursor + span.Length / 2.0, w, span.Length));
                }
                else
                {
                    slots.Add(new SlotSpec(u + cursor + span.Length / 2.0, v, span.Length, h));
                }
            }

            cursor += span.Length;
        }

        return slots;
    }

    // True when `face` is the lowest-priority of the present faces meeting at this
    // panel corner. Open neighbors are treated as absent — they don't contribute a
    // panel and therefore can't claim the corner cube.
    private static bool OwnsCorner(FaceName face, FaceName neighborPrev, FaceName neighborNext, IReadOnlySet<FaceName> openFaces)
    {
        var p = FacePriority.Of(face);
        if (!openFaces.Contains(neighborPrev) && FacePriority.Of(neighborPrev) < p) return false;
        if (!openFaces.Contains(neighborNext) && FacePriority.Of(neighborNext) < p) return false;
        return true;
    }

    private static BoxPlanCuttableShape BuildFacePiece(
        string shapeId,
        FaceName face,
        Vec3 dims,
        IReadOnlyDictionary<string, SharedEdge> edges,
        IReadOnlySet<FaceName> openFaces,
        IReadOnlyList<Feature> features,
        IReadOnlyList<SlotSpec> slots,
        BoxPlanSettings settings)
    {
        var (panelU, panelV) = FaceLayout.PanelSize(face, dims);
        var t = settings.MaterialThickness;
        var ccw = FaceLayout.EdgesCcw(face);
        double[] edgeLengths = { panelU, panelV, panelU, panelV };

        var cornerOwned = new bool[4];
        for (var i = 0; i < 4; i++)
        {
            var prevNeighbor = ccw[(i + 3) % 4].Neighbor;
            var nextNeighbor = ccw[i].Neighbor;
            cornerOwned[i] = OwnsCorner(face, prevNeighbor, nextNeighbor, openFaces);
        }

        var builder = new PanelShapeBuilder(panelU, panelV);

        // Corner notches at each non-owned corner: subtract a t×t square shared between
        // the two edges meeting at that corner.
        for (var i = 0; i < 4; i++)
        {
            if (cornerOwned[i]) continue;
            var prevI = (i + 3) % 4;
            builder.SubtractEdgeNotch(prevI, edgeLengths[prevI] - t, t, t);
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
                if (block.Owner != face)
                    builder.SubtractEdgeNotch(i, cursor, block.Length, t);
                cursor += block.Length;
            }
        }

        var polygon = builder.Build();
        var (path, bbMin, bbMax, translation) = KerfOffset.OffsetOutwardAndTranslate(polygon, settings.Kerf);

        var interiorCuts = new List<CuttablePath>();
        foreach (var feature in features)
        {
            if (feature is CutoutFeature cutout)
            {
                var center = CutoutBuilder.ResolveCenter(cutout.Position, panelU, panelV);
                var cutPath = CutoutBuilder.Build(cutout, center, settings.Kerf, translation);
                interiorCuts.AddRange(CutoutClipper.ClipToOutline(cutPath, path));
            }
        }
        foreach (var slot in slots)
        {
            interiorCuts.Add(CutoutBuilder.BuildSlotRectangle(slot, settings.Kerf, translation));
        }

        return new BoxPlanCuttableShape
        {
            Id = $"{shapeId}.{face.ToString().ToLowerInvariant()}",
            BoundingBoxMin = bbMin,
            BoundingBoxMax = bbMax,
            Outline = path,
            InteriorCuts = interiorCuts,
            Engravings = Array.Empty<CuttablePath>(),
        };
    }
}
