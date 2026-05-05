using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

internal sealed record FingerBlock(double Length, FaceName Owner);

internal sealed record SlotSpec(double U, double V, double Width, double Height);

internal sealed class SharedEdge
{
    public required string Id { get; init; }
    public required FaceName FaceA { get; init; }
    public required FaceName FaceB { get; init; }
    public required double Length { get; init; }
    // Blocks describe the joint geometry over the *inner* region of the shared edge —
    // i.e. excluding a t-wide strip at each end which is reserved for corner-cube
    // geometry handled at the panel level. Sum(Blocks) == Length - 2t.
    public required IReadOnlyList<FingerBlock> Blocks { get; init; }
}

internal static class FacePriority
{
    private static readonly FaceName[] Order =
        { FaceName.Bottom, FaceName.Top, FaceName.Front, FaceName.Back, FaceName.Left, FaceName.Right };

    public static int Of(FaceName f) => Array.IndexOf(Order, f);

    public static FaceName Lower(FaceName a, FaceName b) => Of(a) < Of(b) ? a : b;
}

internal static class SharedEdgeTable
{
    public static IReadOnlyDictionary<string, SharedEdge> Build(Vec3 dims, BoxPlanSettings settings)
    {
        var t = settings.MaterialThickness;
        var s = settings.FingerJointSize;

        var edges = new Dictionary<string, SharedEdge>();

        Add(edges, FaceName.Bottom, FaceName.Front, dims.X, t, s);
        Add(edges, FaceName.Bottom, FaceName.Back,  dims.X, t, s);
        Add(edges, FaceName.Bottom, FaceName.Left,  dims.Z, t, s);
        Add(edges, FaceName.Bottom, FaceName.Right, dims.Z, t, s);
        Add(edges, FaceName.Top,    FaceName.Front, dims.X, t, s);
        Add(edges, FaceName.Top,    FaceName.Back,  dims.X, t, s);
        Add(edges, FaceName.Top,    FaceName.Left,  dims.Z, t, s);
        Add(edges, FaceName.Top,    FaceName.Right, dims.Z, t, s);
        Add(edges, FaceName.Front,  FaceName.Left,  dims.Y, t, s);
        Add(edges, FaceName.Front,  FaceName.Right, dims.Y, t, s);
        Add(edges, FaceName.Back,   FaceName.Left,  dims.Y, t, s);
        Add(edges, FaceName.Back,   FaceName.Right, dims.Y, t, s);

        return edges;
    }

    public static string Id(FaceName a, FaceName b) =>
        FacePriority.Of(a) < FacePriority.Of(b)
            ? $"{a}-{b}"
            : $"{b}-{a}";

    private static void Add(Dictionary<string, SharedEdge> sink, FaceName a, FaceName b, double length, double t, double s)
    {
        var lower = FacePriority.Lower(a, b);
        var higher = lower == a ? b : a;
        // Reserve a t-wide strip at each end of the edge for corner-cube geometry,
        // emitted at the panel level (TAB-around-corner if the panel owns the cube
        // vertex, NOTCH-into-corner otherwise). Blocks describe the joint over the
        // remaining inner region only.
        var innerLength = Math.Max(0, length - 2 * t);
        var blocks = BuildBlocks(innerLength, s, lower, higher);
        var id = Id(a, b);
        sink[id] = new SharedEdge { Id = id, FaceA = a, FaceB = b, Length = length, Blocks = blocks };
    }

    private static IReadOnlyList<FingerBlock> BuildBlocks(double length, double s, FaceName lower, FaceName higher)
    {
        var blocks = new List<FingerBlock>();
        if (length <= 0)
        {
            return blocks;
        }
        if (length < 3 * s)
        {
            blocks.Add(new FingerBlock(length, lower));
            return blocks;
        }

        blocks.Add(new FingerBlock(s, lower));
        var remaining = length - s;
        var nextIsHigher = true;

        while (remaining >= 0.75 * s)
        {
            var owner = nextIsHigher ? higher : lower;
            blocks.Add(new FingerBlock(s, owner));
            remaining -= s;
            nextIsHigher = !nextIsHigher;
        }

        if (blocks[^1].Owner == lower)
        {
            blocks[^1] = new FingerBlock(blocks[^1].Length + remaining, lower);
        }
        else
        {
            blocks.Add(new FingerBlock(remaining, lower));
        }

        return blocks;
    }
}

internal sealed record FaceEdgeMap(FaceName Face, FaceName Neighbor, bool ForwardAlongShared);

internal static class FaceLayout
{
    public static (double U, double V) PanelSize(FaceName face, Vec3 dims) => face switch
    {
        FaceName.Bottom or FaceName.Top   => (dims.X, dims.Z),
        FaceName.Front  or FaceName.Back  => (dims.X, dims.Y),
        FaceName.Left   or FaceName.Right => (dims.Z, dims.Y),
        _ => throw new InvalidOperationException()
    };

    public static FaceEdgeMap[] EdgesCcw(FaceName face) => face switch
    {
        FaceName.Bottom => new[]
        {
            new FaceEdgeMap(face, FaceName.Front, true),
            new FaceEdgeMap(face, FaceName.Right, true),
            new FaceEdgeMap(face, FaceName.Back,  false),
            new FaceEdgeMap(face, FaceName.Left,  false),
        },
        FaceName.Top => new[]
        {
            new FaceEdgeMap(face, FaceName.Front, true),
            new FaceEdgeMap(face, FaceName.Right, true),
            new FaceEdgeMap(face, FaceName.Back,  false),
            new FaceEdgeMap(face, FaceName.Left,  false),
        },
        FaceName.Front => new[]
        {
            new FaceEdgeMap(face, FaceName.Bottom, true),
            new FaceEdgeMap(face, FaceName.Right,  true),
            new FaceEdgeMap(face, FaceName.Top,    false),
            new FaceEdgeMap(face, FaceName.Left,   false),
        },
        FaceName.Back => new[]
        {
            new FaceEdgeMap(face, FaceName.Bottom, true),
            new FaceEdgeMap(face, FaceName.Right,  true),
            new FaceEdgeMap(face, FaceName.Top,    false),
            new FaceEdgeMap(face, FaceName.Left,   false),
        },
        FaceName.Left => new[]
        {
            new FaceEdgeMap(face, FaceName.Bottom, true),
            new FaceEdgeMap(face, FaceName.Back,   true),
            new FaceEdgeMap(face, FaceName.Top,    false),
            new FaceEdgeMap(face, FaceName.Front,  false),
        },
        FaceName.Right => new[]
        {
            new FaceEdgeMap(face, FaceName.Bottom, true),
            new FaceEdgeMap(face, FaceName.Back,   true),
            new FaceEdgeMap(face, FaceName.Top,    false),
            new FaceEdgeMap(face, FaceName.Front,  false),
        },
        _ => throw new InvalidOperationException()
    };
}

internal static class JointGeometry
{
    public static readonly Vec2[] AlongCcw = { new(1, 0), new(0, 1), new(-1, 0), new(0, -1) };
    public static readonly Vec2[] InwardCcw = { new(0, 1), new(-1, 0), new(0, -1), new(1, 0) };

    public static Vec2 Move(Vec2 p, Vec2 dir, double scale) => Add(p, dir, scale);

    public static List<LineSegment> EmitEdge(
        FaceEdgeMap edge,
        SharedEdge shared,
        int edgeIndex,
        double t,
        Vec2 startPos)
    {
        var along = AlongCcw[edgeIndex];
        var inward = InwardCcw[edgeIndex];
        var segments = new List<LineSegment>();

        var ordered = edge.ForwardAlongShared
            ? shared.Blocks
            : shared.Blocks.Reverse().ToList();

        var p = startPos;
        foreach (var block in ordered)
        {
            var L = block.Length;
            if (block.Owner == edge.Face)
            {
                p = Add(p, along, L);
                segments.Add(new LineSegment(p));
            }
            else
            {
                p = Add(p, inward, t);
                segments.Add(new LineSegment(p));
                p = Add(p, along, L);
                segments.Add(new LineSegment(p));
                p = Add(p, inward, -t);
                segments.Add(new LineSegment(p));
            }
        }
        return segments;
    }

    private static Vec2 Add(Vec2 p, Vec2 dir, double scale) =>
        new(p.X + dir.X * scale, p.Y + dir.Y * scale);
}

internal static class KerfOffset
{
    public static (CuttablePath path, Vec2 bbMin, Vec2 bbMax, Vec2 translation) OffsetOutwardAndTranslate(
        Vec2 start, IReadOnlyList<LineSegment> segments, double kerf)
    {
        var k = kerf / 2.0;
        var pts = new List<Vec2> { start };
        foreach (var seg in segments) pts.Add(seg.To);

        var n = pts.Count - 1;
        var offsetPts = new Vec2[n + 1];
        for (var i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;
            var nx = dy / len;
            var ny = -dx / len;
            offsetPts[i] = new Vec2(a.X + nx * k, a.Y + ny * k);
            offsetPts[i + 1] = new Vec2(b.X + nx * k, b.Y + ny * k);
        }

        var resolved = new List<Vec2>();
        for (var i = 0; i < n; i++)
        {
            var a0 = offsetPts[i];
            var b0 = offsetPts[i + 1];
            if (i == 0)
            {
                resolved.Add(a0);
            }
            else
            {
                var prev = resolved[^1];
                if (Math.Abs(prev.X - a0.X) > 1e-7 || Math.Abs(prev.Y - a0.Y) > 1e-7)
                {
                    resolved.Add(a0);
                }
            }
            resolved.Add(b0);
        }

        var minX = double.MaxValue; var minY = double.MaxValue;
        var maxX = double.MinValue; var maxY = double.MinValue;
        foreach (var p in resolved)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        var translated = resolved.Select(p => new Vec2(p.X - minX, p.Y - minY)).ToList();
        var newSegments = new List<PathSegment>();
        for (var i = 1; i < translated.Count; i++)
        {
            newSegments.Add(new LineSegment(translated[i]));
        }

        var path = new CuttablePath
        {
            Start = translated[0],
            Segments = newSegments,
            Closed = true,
        };
        var bbMin = new Vec2(0, 0);
        var bbMax = new Vec2(maxX - minX, maxY - minY);
        return (path, bbMin, bbMax, new Vec2(-minX, -minY));
    }
}

internal static class CutoutBuilder
{
    public static Vec2 ResolveCenter(Position? position, double panelU, double panelV)
    {
        if (position is null) return new Vec2(panelU / 2, panelV / 2);
        var anchor = position.Anchor switch
        {
            Anchor.TopCenter    => new Vec2(panelU / 2, panelV),
            Anchor.BottomCenter => new Vec2(panelU / 2, 0),
            Anchor.LeftCenter   => new Vec2(0, panelV / 2),
            Anchor.RightCenter  => new Vec2(panelU, panelV / 2),
            Anchor.Center       => new Vec2(panelU / 2, panelV / 2),
            _ => new Vec2(panelU / 2, panelV / 2),
        };
        return new Vec2(anchor.X + position.Offset.X, anchor.Y + position.Offset.Y);
    }

    public static CuttablePath Build(CutoutFeature feature, Vec2 center, double kerf, Vec2 translation)
    {
        var k = kerf / 2.0;
        var w = feature.Width - kerf;
        var h = feature.Height - kerf;
        var cx = center.X + translation.X;
        var cy = center.Y + translation.Y;

        return feature.Shape switch
        {
            CutoutShape.Rectangle  => BuildRectangle(cx, cy, w, h),
            CutoutShape.Circle     => BuildCircle(cx, cy, w / 2),
            CutoutShape.Semicircle => BuildSemicircle(cx, cy, w / 2),
            _ => throw new InvalidOperationException($"Unsupported cutout shape {feature.Shape}"),
        };
    }

    public static CuttablePath BuildSlotRectangle(SlotSpec slot, double kerf, Vec2 translation)
    {
        var w = slot.Width - kerf;
        var h = slot.Height - kerf;
        var cx = slot.U + translation.X;
        var cy = slot.V + translation.Y;
        return BuildRectangle(cx, cy, w, h);
    }

    private static CuttablePath BuildRectangle(double cx, double cy, double w, double h)
    {
        var x0 = cx - w / 2;
        var x1 = cx + w / 2;
        var y0 = cy - h / 2;
        var y1 = cy + h / 2;
        return new CuttablePath
        {
            Start = new Vec2(x0, y0),
            Segments = new List<PathSegment>
            {
                new LineSegment(new Vec2(x1, y0)),
                new LineSegment(new Vec2(x1, y1)),
                new LineSegment(new Vec2(x0, y1)),
                new LineSegment(new Vec2(x0, y0)),
            },
            Closed = true,
        };
    }

    private static CuttablePath BuildCircle(double cx, double cy, double r)
    {
        return new CuttablePath
        {
            Start = new Vec2(cx + r, cy),
            Segments = new List<PathSegment>
            {
                new ArcSegment(new Vec2(cx - r, cy), r, Clockwise: false, LargeArc: false),
                new ArcSegment(new Vec2(cx + r, cy), r, Clockwise: false, LargeArc: false),
            },
            Closed = true,
        };
    }

    private static CuttablePath BuildSemicircle(double cx, double cy, double r)
    {
        return new CuttablePath
        {
            Start = new Vec2(cx + r, cy),
            Segments = new List<PathSegment>
            {
                new ArcSegment(new Vec2(cx - r, cy), r, Clockwise: false, LargeArc: false),
                new LineSegment(new Vec2(cx + r, cy)),
            },
            Closed = true,
        };
    }
}

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
                output.Add(BuildDividerPanel($"{parentId}.divider-{dset.Axis.ToString().ToLowerInvariant()}@{pos}", panelU, panelV, settings.Kerf));
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

    private static BoxPlanCuttableShape BuildDividerPanel(string id, double u, double v, double kerf)
    {
        var k = kerf / 2.0;
        var x0 = -k;
        var y0 = -k;
        var x1 = u + k;
        var y1 = v + k;
        var path = new CuttablePath
        {
            Start = new Vec2(0, 0),
            Segments = new List<PathSegment>
            {
                new LineSegment(new Vec2(x1 - x0, 0)),
                new LineSegment(new Vec2(x1 - x0, y1 - y0)),
                new LineSegment(new Vec2(0, y1 - y0)),
                new LineSegment(new Vec2(0, 0)),
            },
            Closed = true,
        };
        return new BoxPlanCuttableShape
        {
            Id = id,
            BoundingBoxMin = new Vec2(0, 0),
            BoundingBoxMax = new Vec2(x1 - x0, y1 - y0),
            Outline = path,
            InteriorCuts = Array.Empty<CuttablePath>(),
            Engravings = Array.Empty<CuttablePath>(),
        };
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
        var slotsByFace = BuildSlotsByFace(box.Dividers, dims, settings.MaterialThickness);

        foreach (var face in box.Faces)
        {
            if (face.Type != FaceType.Closed) continue;
            var faceFeatures = box.Features.Where(f => f.Face == face.Name).ToArray();
            var faceSlots = slotsByFace.TryGetValue(face.Name, out var s) ? s : Array.Empty<SlotSpec>();
            output.Add(BuildFacePiece(idPrefix, face.Name, dims, edges, openFaces, faceFeatures, faceSlots, settings));
        }

        EmitDividerPanels(idPrefix, box, dims, output, settings);
    }

    private static IReadOnlyDictionary<FaceName, IReadOnlyList<SlotSpec>> BuildSlotsByFace(
        IReadOnlyList<DividerSet> dividers, Vec3 dims, double t)
    {
        var slots = Enum.GetValues<FaceName>().ToDictionary(f => f, f => new List<SlotSpec>());
        foreach (var ds in dividers)
        {
            foreach (var p in ds.Positions)
            {
                AddSlots(slots, ds.Axis, p, ds.Facing, dims, t);
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
        double t)
    {
        void Add(FaceName face, double u, double v, double w, double h)
        {
            if (face == facing) return;
            slots[face].Add(new SlotSpec(u, v, w, h));
        }

        switch (axis)
        {
            case Axis.X:
                Add(FaceName.Bottom, pos, dims.Z / 2, t, dims.Z);
                Add(FaceName.Top,    pos, dims.Z / 2, t, dims.Z);
                Add(FaceName.Front,  pos, dims.Y / 2, t, dims.Y);
                Add(FaceName.Back,   pos, dims.Y / 2, t, dims.Y);
                break;
            case Axis.Y:
                Add(FaceName.Front, dims.X / 2, pos, dims.X, t);
                Add(FaceName.Back,  dims.X / 2, pos, dims.X, t);
                Add(FaceName.Left,  dims.Z / 2, pos, dims.Z, t);
                Add(FaceName.Right, dims.Z / 2, pos, dims.Z, t);
                break;
            case Axis.Z:
                Add(FaceName.Bottom, dims.X / 2, pos, dims.X, t);
                Add(FaceName.Top,    dims.X / 2, pos, dims.X, t);
                Add(FaceName.Left,   pos, dims.Y / 2, t, dims.Y);
                Add(FaceName.Right,  pos, dims.Y / 2, t, dims.Y);
                break;
        }
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

        var corners = new[]
        {
            new Vec2(0, 0),
            new Vec2(panelU, 0),
            new Vec2(panelU, panelV),
            new Vec2(0, panelV),
        };

        // For each panel corner (cube vertex), determine whether this face is the
        // lowest-priority of the 3 faces that meet there. The lowest face owns the
        // corner cube; the other two need a t×t notch cut at this corner so the
        // owning face's tab can pass through.
        var cornerOwned = new bool[4];
        for (var i = 0; i < 4; i++)
        {
            var prevNeighbor = ccw[(i + 3) % 4].Neighbor;
            var nextNeighbor = ccw[i].Neighbor;
            cornerOwned[i] = OwnsCorner(face, prevNeighbor, nextNeighbor, openFaces);
        }

        // Path starts at the inner-end of the last edge (= the point where the path
        // first arrives at corner 0 from edge 3, before the corner geometry is drawn).
        var startAlong = JointGeometry.AlongCcw[3];
        var startPath = JointGeometry.Move(corners[0], startAlong, -t);

        var segments = new List<LineSegment>();
        for (var i = 0; i < 4; i++)
        {
            var corner = corners[i];
            var nextCorner = corners[(i + 1) % 4];
            var along = JointGeometry.AlongCcw[i];
            var prevAlong = JointGeometry.AlongCcw[(i + 3) % 4];

            var innerStart = JointGeometry.Move(corner, along, t);
            var innerEnd = JointGeometry.Move(nextCorner, along, -t);

            // Corner geometry at corner i (between edge i-1 and edge i).
            if (cornerOwned[i])
            {
                // Path goes through the panel corner: inner-end-of-prev → corner → inner-start-of-next.
                segments.Add(new LineSegment(corner));
            }
            else
            {
                // t×t notch: path detours around the corner cut.
                var notchInner = JointGeometry.Move(JointGeometry.Move(corner, prevAlong, -t), along, t);
                segments.Add(new LineSegment(notchInner));
            }
            segments.Add(new LineSegment(innerStart));

            // Edge i emission (over the inner region).
            var edgeMap = ccw[i];
            if (openFaces.Contains(edgeMap.Neighbor))
            {
                segments.Add(new LineSegment(innerEnd));
            }
            else
            {
                var shared = edges[SharedEdgeTable.Id(edgeMap.Face, edgeMap.Neighbor)];
                var emitted = JointGeometry.EmitEdge(edgeMap, shared, i, t, innerStart);
                segments.AddRange(emitted);
            }
        }

        var (path, bbMin, bbMax, translation) = KerfOffset.OffsetOutwardAndTranslate(startPath, segments, settings.Kerf);

        var interiorCuts = new List<CuttablePath>();
        foreach (var feature in features)
        {
            if (feature is CutoutFeature cutout)
            {
                var center = CutoutBuilder.ResolveCenter(cutout.Position, panelU, panelV);
                interiorCuts.Add(CutoutBuilder.Build(cutout, center, settings.Kerf, translation));
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
