using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

internal sealed record JointBlock(double Length, bool PrimaryOwns);

internal enum DividerJointSpanKind
{
    Smooth,
    EndInset,
    DividerTab,
    FaceSlot,
}

internal sealed record FingerBlock(double Length, FaceName Owner);

internal sealed record SlotSpec(double U, double V, double Width, double Height);

internal sealed record DividerEdgeSpec(FaceName Face, bool Joined, bool DividerOwnsPrimary);

internal sealed record DividerJointSpan(double Length, DividerJointSpanKind Kind);

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
        var blocks = FingerJointPattern.Build(length, s);
        return blocks
            .Select(block => new FingerBlock(block.Length, block.PrimaryOwns ? lower : higher))
            .ToList();
    }
}

internal static class FingerJointPattern
{
    public static IReadOnlyList<JointBlock> Build(double length, double s)
    {
        var blocks = new List<JointBlock>();
        if (length <= 0)
        {
            return blocks;
        }
        if (length < 3 * s)
        {
            blocks.Add(new JointBlock(length, true));
            return blocks;
        }

        var minEndLength = 0.75 * s;
        var interiorCount = (int)Math.Floor(length / s);
        if (interiorCount % 2 == 0)
        {
            interiorCount--;
        }

        while (interiorCount > 1)
        {
            var endLength = (length - interiorCount * s) / 2.0;
            if (endLength >= minEndLength)
            {
                break;
            }
            interiorCount -= 2;
        }

        var balancedEndLength = (length - interiorCount * s) / 2.0;
        blocks.Add(new JointBlock(balancedEndLength, true));
        for (var index = 0; index < interiorCount; index++)
        {
            blocks.Add(new JointBlock(s, index % 2 != 0));
        }
        blocks.Add(new JointBlock(balancedEndLength, true));

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

    public static bool StartsWithNeighborBlock(FaceEdgeMap edge, SharedEdge shared)
    {
        var ordered = edge.ForwardAlongShared
            ? shared.Blocks
            : shared.Blocks.Reverse().ToList();
        return ordered.Count > 0 && ordered[0].Owner != edge.Face;
    }

    public static bool EndsWithNeighborBlock(FaceEdgeMap edge, SharedEdge shared)
    {
        var ordered = edge.ForwardAlongShared
            ? shared.Blocks
            : shared.Blocks.Reverse().ToList();
        return ordered.Count > 0 && ordered[^1].Owner != edge.Face;
    }

    public static List<LineSegment> EmitEdge(
        FaceEdgeMap edge,
        SharedEdge shared,
        int edgeIndex,
        double t,
        Vec2 startPos,
        bool omitLeadingInset,
        bool omitTrailingReturn)
    {
        var along = AlongCcw[edgeIndex];
        var inward = InwardCcw[edgeIndex];
        var segments = new List<LineSegment>();

        var ordered = edge.ForwardAlongShared
            ? shared.Blocks
            : shared.Blocks.Reverse().ToList();

        var p = startPos;
        for (var blockIndex = 0; blockIndex < ordered.Count; blockIndex++)
        {
            var block = ordered[blockIndex];
            var L = block.Length;
            if (block.Owner == edge.Face)
            {
                p = Add(p, along, L);
                segments.Add(new LineSegment(p));
            }
            else
            {
                var isFirstBlock = blockIndex == 0;
                if (!(omitLeadingInset && isFirstBlock))
                {
                    p = Add(p, inward, t);
                    segments.Add(new LineSegment(p));
                }
                p = Add(p, along, L);
                segments.Add(new LineSegment(p));
                var isLastBlock = blockIndex == ordered.Count - 1;
                if (!(omitTrailingReturn && isLastBlock))
                {
                    p = Add(p, inward, -t);
                    segments.Add(new LineSegment(p));
                }
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
        var w = Math.Abs(feature.Width) - kerf;
        var h = Math.Abs(feature.Height) - kerf;
        var cx = center.X + translation.X;
        var cy = center.Y + translation.Y;

        return feature.Shape switch
        {
            CutoutShape.Rectangle  => BuildRectangle(cx, cy, w, h),
            CutoutShape.Circle     => BuildCircle(cx, cy, w / 2),
            CutoutShape.Semicircle => BuildSemicircle(cx, cy, feature.Width >= 0 ? w / 2 : -(w / 2)),
            _ => throw new InvalidOperationException($"Unsupported cutout shape {feature.Shape}"),
        };
    }

    public static CuttablePath BuildSlotRectangle(SlotSpec slot, double kerf, Vec2 translation)
    {
        var w = Math.Abs(slot.Width) - kerf;
        var h = Math.Abs(slot.Height) - kerf;
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
        r = Math.Abs(r);
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
        var radius = Math.Abs(r);
        var start = r < 0 ? new Vec2(cx - radius, cy) : new Vec2(cx + radius, cy);
        var end = r < 0 ? new Vec2(cx + radius, cy) : new Vec2(cx - radius, cy);

        return new CuttablePath
        {
            Start = start,
            Segments = new List<PathSegment>
            {
                new ArcSegment(end, radius, Clockwise: false, LargeArc: false),
                new LineSegment(start),
            },
            Closed = true,
        };
    }
}

internal static class CutoutClipper
{
    private const double Epsilon = 1e-6;

    public static IReadOnlyList<CuttablePath> ClipToOutline(CuttablePath path, CuttablePath outline)
    {
        var polygon = BuildPolygon(outline);
        if (polygon.Count < 3 || PathFullyInside(path, polygon))
        {
            return new[] { path };
        }

        var clipped = new List<CuttablePath>();
        var current = path.Start;
        foreach (var segment in path.Segments)
        {
            switch (segment)
            {
                case LineSegment line:
                    AddClippedLineSegments(clipped, current, line.To, polygon);
                    current = line.To;
                    break;

                case ArcSegment arc:
                    AddClippedArcSegments(clipped, current, arc, polygon);
                    current = arc.To;
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported cut segment {segment.GetType().Name}");
            }
        }

        return clipped;
    }

    private static List<Vec2> BuildPolygon(CuttablePath outline)
    {
        var points = new List<Vec2> { outline.Start };
        foreach (var segment in outline.Segments)
        {
            if (segment is not LineSegment line)
            {
                throw new InvalidOperationException("Panel outlines must be polylines.");
            }

            points.Add(line.To);
        }

        if (points.Count > 1 && NearlyEqual(points[0], points[^1]))
        {
            points.RemoveAt(points.Count - 1);
        }

        return points;
    }

    private static bool PathFullyInside(CuttablePath path, IReadOnlyList<Vec2> polygon)
    {
        var current = path.Start;
        foreach (var segment in path.Segments)
        {
            if (!ContainsPoint(polygon, current))
            {
                return false;
            }

            switch (segment)
            {
                case LineSegment line:
                    if (GetLineIntersections(current, line.To, polygon).Any(t => t > Epsilon && t < 1.0 - Epsilon))
                    {
                        return false;
                    }

                    if (!ContainsPoint(polygon, line.To)
                        || !ContainsPoint(polygon, Lerp(current, line.To, 0.5)))
                    {
                        return false;
                    }

                    current = line.To;
                    break;

                case ArcSegment arc:
                    var geometry = ArcGeometry.Create(current, arc);
                    if (GetArcIntersections(geometry, polygon).Any(t => t > Epsilon && t < 1.0 - Epsilon))
                    {
                        return false;
                    }

                    if (!ContainsPoint(polygon, arc.To)
                        || !ContainsPoint(polygon, geometry.PointAt(0.5)))
                    {
                        return false;
                    }

                    current = arc.To;
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported cut segment {segment.GetType().Name}");
            }
        }

        return true;
    }

    private static void AddClippedLineSegments(List<CuttablePath> clipped, Vec2 start, Vec2 end, IReadOnlyList<Vec2> polygon)
    {
        var cuts = GetLineIntersections(start, end, polygon);
        var parameters = BuildSplitParameters(cuts);
        for (var index = 0; index < parameters.Count - 1; index++)
        {
            var t0 = parameters[index];
            var t1 = parameters[index + 1];
            if (t1 - t0 <= Epsilon)
            {
                continue;
            }

            var mid = Lerp(start, end, (t0 + t1) / 2.0);
            if (!ContainsPoint(polygon, mid))
            {
                continue;
            }

            var clippedStart = Lerp(start, end, t0);
            var clippedEnd = Lerp(start, end, t1);
            if (NearlyEqual(clippedStart, clippedEnd))
            {
                continue;
            }

            clipped.Add(new CuttablePath
            {
                Start = clippedStart,
                Segments = new PathSegment[] { new LineSegment(clippedEnd) },
                Closed = false,
            });
        }
    }

    private static void AddClippedArcSegments(List<CuttablePath> clipped, Vec2 start, ArcSegment arc, IReadOnlyList<Vec2> polygon)
    {
        var geometry = ArcGeometry.Create(start, arc);
        var cuts = GetArcIntersections(geometry, polygon);
        var parameters = BuildSplitParameters(cuts);
        for (var index = 0; index < parameters.Count - 1; index++)
        {
            var t0 = parameters[index];
            var t1 = parameters[index + 1];
            if (t1 - t0 <= Epsilon)
            {
                continue;
            }

            var mid = geometry.PointAt((t0 + t1) / 2.0);
            if (!ContainsPoint(polygon, mid))
            {
                continue;
            }

            var clippedStart = geometry.PointAt(t0);
            var clippedEnd = geometry.PointAt(t1);
            if (NearlyEqual(clippedStart, clippedEnd))
            {
                continue;
            }

            clipped.Add(new CuttablePath
            {
                Start = clippedStart,
                Segments = new PathSegment[]
                {
                    new ArcSegment(clippedEnd, geometry.Radius, geometry.Clockwise, geometry.IsLargeArc(t0, t1)),
                },
                Closed = false,
            });
        }
    }

    private static List<double> GetLineIntersections(Vec2 start, Vec2 end, IReadOnlyList<Vec2> polygon)
    {
        var hits = new List<double>();
        for (var index = 0; index < polygon.Count; index++)
        {
            var edgeStart = polygon[index];
            var edgeEnd = polygon[(index + 1) % polygon.Count];
            if (TryIntersectSegments(start, end, edgeStart, edgeEnd, out var t))
            {
                hits.Add(t);
            }
        }

        return hits;
    }

    private static List<double> GetArcIntersections(ArcGeometry geometry, IReadOnlyList<Vec2> polygon)
    {
        var hits = new List<double>();
        for (var index = 0; index < polygon.Count; index++)
        {
            var edgeStart = polygon[index];
            var edgeEnd = polygon[(index + 1) % polygon.Count];
            AddSegmentArcIntersections(hits, geometry, edgeStart, edgeEnd);
        }

        return hits;
    }

    private static void AddSegmentArcIntersections(List<double> hits, ArcGeometry geometry, Vec2 segmentStart, Vec2 segmentEnd)
    {
        var dx = segmentEnd.X - segmentStart.X;
        var dy = segmentEnd.Y - segmentStart.Y;
        var fx = segmentStart.X - geometry.Center.X;
        var fy = segmentStart.Y - geometry.Center.Y;

        var a = (dx * dx) + (dy * dy);
        if (a <= Epsilon)
        {
            return;
        }

        var b = 2.0 * ((fx * dx) + (fy * dy));
        var c = (fx * fx) + (fy * fy) - (geometry.Radius * geometry.Radius);
        var discriminant = (b * b) - (4.0 * a * c);
        if (discriminant < -Epsilon)
        {
            return;
        }

        var sqrtDiscriminant = Math.Sqrt(Math.Max(0.0, discriminant));
        AddSegmentArcIntersectionCandidate(hits, geometry, segmentStart, dx, dy, a, (-b - sqrtDiscriminant) / (2.0 * a));
        if (sqrtDiscriminant > Epsilon)
        {
            AddSegmentArcIntersectionCandidate(hits, geometry, segmentStart, dx, dy, a, (-b + sqrtDiscriminant) / (2.0 * a));
        }
    }

    private static void AddSegmentArcIntersectionCandidate(
        List<double> hits,
        ArcGeometry geometry,
        Vec2 segmentStart,
        double dx,
        double dy,
        double a,
        double segmentParameter)
    {
        if (segmentParameter < -Epsilon || segmentParameter > 1.0 + Epsilon)
        {
            return;
        }

        var point = new Vec2(segmentStart.X + (dx * segmentParameter), segmentStart.Y + (dy * segmentParameter));

        if (geometry.TryGetParameter(point, out var t))
        {
            hits.Add(t);
        }
    }

    private static List<double> BuildSplitParameters(IEnumerable<double> hits)
    {
        var parameters = new List<double> { 0.0, 1.0 };
        foreach (var hit in hits)
        {
            var clamped = Math.Max(0.0, Math.Min(1.0, hit));
            if (parameters.All(existing => Math.Abs(existing - clamped) > Epsilon))
            {
                parameters.Add(clamped);
            }
        }

        parameters.Sort();
        return parameters;
    }

    private static bool ContainsPoint(IReadOnlyList<Vec2> polygon, Vec2 point)
    {
        for (var index = 0; index < polygon.Count; index++)
        {
            var a = polygon[index];
            var b = polygon[(index + 1) % polygon.Count];
            if (PointOnSegment(point, a, b))
            {
                return true;
            }
        }

        var inside = false;
        var previous = polygon.Count - 1;
        for (var index = 0; index < polygon.Count; previous = index++)
        {
            var a = polygon[index];
            var b = polygon[previous];
            var intersects = ((a.Y > point.Y) != (b.Y > point.Y))
                && (point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X);
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool PointOnSegment(Vec2 point, Vec2 start, Vec2 end)
    {
        var cross = (point.Y - start.Y) * (end.X - start.X) - (point.X - start.X) * (end.Y - start.Y);
        if (Math.Abs(cross) > Epsilon)
        {
            return false;
        }

        var dot = (point.X - start.X) * (end.X - start.X) + (point.Y - start.Y) * (end.Y - start.Y);
        if (dot < -Epsilon)
        {
            return false;
        }

        var squaredLength = Square(end.X - start.X) + Square(end.Y - start.Y);
        return dot <= squaredLength + Epsilon;
    }

    private static bool TryIntersectSegments(Vec2 aStart, Vec2 aEnd, Vec2 bStart, Vec2 bEnd, out double t)
    {
        t = 0.0;
        var r = new Vec2(aEnd.X - aStart.X, aEnd.Y - aStart.Y);
        var s = new Vec2(bEnd.X - bStart.X, bEnd.Y - bStart.Y);
        var denominator = Cross(r, s);
        if (Math.Abs(denominator) <= Epsilon)
        {
            return false;
        }

        var delta = new Vec2(bStart.X - aStart.X, bStart.Y - aStart.Y);
        var tValue = Cross(delta, s) / denominator;
        var uValue = Cross(delta, r) / denominator;
        if (tValue < -Epsilon || tValue > 1.0 + Epsilon || uValue < -Epsilon || uValue > 1.0 + Epsilon)
        {
            return false;
        }

        t = tValue;
        return true;
    }

    private static Vec2 Lerp(Vec2 start, Vec2 end, double t) =>
        new(start.X + ((end.X - start.X) * t), start.Y + ((end.Y - start.Y) * t));

    private static double Cross(Vec2 a, Vec2 b) => (a.X * b.Y) - (a.Y * b.X);

    private static double Square(double value) => value * value;

    private static bool NearlyEqual(Vec2 a, Vec2 b) =>
        Math.Abs(a.X - b.X) <= Epsilon && Math.Abs(a.Y - b.Y) <= Epsilon;

    private static double NormalizeAngle(double angle)
    {
        var fullTurn = Math.PI * 2.0;
        var normalized = angle % fullTurn;
        return normalized < 0 ? normalized + fullTurn : normalized;
    }

    private static double NormalizePositiveSweep(double angle) =>
        NormalizeAngle(angle + (Math.PI * 2.0));

    private sealed class ArcGeometry
    {
        private ArcGeometry(Vec2 center, double radius, double startAngle, double sweepAngle, bool clockwise)
        {
            Center = center;
            Radius = radius;
            StartAngle = startAngle;
            SweepAngle = sweepAngle;
            Clockwise = clockwise;
        }

        public Vec2 Center { get; }
        public double Radius { get; }
        public double StartAngle { get; }
        public double SweepAngle { get; }
        public bool Clockwise { get; }

        public static ArcGeometry Create(Vec2 start, ArcSegment arc)
        {
            var dx = arc.To.X - start.X;
            var dy = arc.To.Y - start.Y;
            var chordLength = Math.Sqrt((dx * dx) + (dy * dy));
            if (chordLength <= Epsilon)
            {
                throw new InvalidOperationException("Degenerate arc segment is not supported.");
            }

            var halfChord = chordLength / 2.0;
            var heightSquared = (arc.Radius * arc.Radius) - (halfChord * halfChord);
            if (heightSquared < -Epsilon)
            {
                throw new InvalidOperationException("Arc radius is too small for the requested segment.");
            }

            var midpoint = new Vec2((start.X + arc.To.X) / 2.0, (start.Y + arc.To.Y) / 2.0);
            var height = Math.Sqrt(Math.Max(0.0, heightSquared));
            var perp = new Vec2(-dy / chordLength, dx / chordLength);
            var centers = new[]
            {
                new Vec2(midpoint.X + (perp.X * height), midpoint.Y + (perp.Y * height)),
                new Vec2(midpoint.X - (perp.X * height), midpoint.Y - (perp.Y * height)),
            };

            foreach (var center in centers)
            {
                var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
                var endAngle = Math.Atan2(arc.To.Y - center.Y, arc.To.X - center.X);
                var ccwSweep = NormalizePositiveSweep(endAngle - startAngle);
                var sweepAngle = arc.Clockwise
                    ? -(Math.Abs(ccwSweep) <= Epsilon ? Math.PI * 2.0 : (Math.PI * 2.0) - ccwSweep)
                    : (Math.Abs(ccwSweep) <= Epsilon ? Math.PI * 2.0 : ccwSweep);

                var isLargeArc = Math.Abs(sweepAngle) > Math.PI + Epsilon;
                if (isLargeArc == arc.LargeArc || Math.Abs(Math.Abs(sweepAngle) - Math.PI) <= Epsilon)
                {
                    return new ArcGeometry(center, arc.Radius, startAngle, sweepAngle, arc.Clockwise);
                }
            }

            throw new InvalidOperationException("Unable to resolve arc geometry.");
        }

        public Vec2 PointAt(double t)
        {
            var angle = StartAngle + (SweepAngle * t);
            return new Vec2(Center.X + (Radius * Math.Cos(angle)), Center.Y + (Radius * Math.Sin(angle)));
        }

        public bool TryGetParameter(Vec2 point, out double t)
        {
            var angle = Math.Atan2(point.Y - Center.Y, point.X - Center.X);
            var span = Math.Abs(SweepAngle);
            var travelled = Clockwise
                ? NormalizePositiveSweep(StartAngle - angle)
                : NormalizePositiveSweep(angle - StartAngle);

            if (travelled > span + Epsilon)
            {
                t = 0.0;
                return false;
            }

            t = span <= Epsilon ? 0.0 : travelled / span;
            return t >= -Epsilon && t <= 1.0 + Epsilon;
        }

        public bool IsLargeArc(double startT, double endT) => Math.Abs(SweepAngle) * (endT - startT) > Math.PI + Epsilon;
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
        {
            LogDividerTabSizes(id, edges, spans);
        }

        var corners = new[]
        {
            new Vec2(0, 0),
            new Vec2(u, 0),
            new Vec2(u, v),
            new Vec2(0, v),
        };

        // When edge 3 ends with a trailing EndInset whose outward return is
        // skipped (because edge 0 is smooth), the path closes at the L-cut's
        // post-corner point — `t` along edge 0 from the panel's origin corner —
        // rather than the corner itself.
        var start = corners[0];
        if (spans[0].Count > 0 && spans[0][0].Kind == DividerJointSpanKind.Smooth
            && spans[3].Count > 0 && spans[3][^1].Kind == DividerJointSpanKind.EndInset)
        {
            start = JointGeometry.Move(corners[0], JointGeometry.AlongCcw[0], t);
        }
        var segments = new List<LineSegment>();
        var p = start;
        var skipLeadingInset = false;
        for (var edgeIndex = 0; edgeIndex < 4; edgeIndex++)
        {
            var along = JointGeometry.AlongCcw[edgeIndex];
            var inward = JointGeometry.InwardCcw[edgeIndex];
            var prevEdgeIndex = (edgeIndex + 3) % spans.Length;
            var nextEdgeIndex = (edgeIndex + 1) % spans.Length;
            var prevEndsEndInset = spans[prevEdgeIndex].Count > 0
                && spans[prevEdgeIndex][^1].Kind == DividerJointSpanKind.EndInset;
            var nextStartsEndInset = spans[nextEdgeIndex].Count > 0
                && spans[nextEdgeIndex][0].Kind == DividerJointSpanKind.EndInset;
            var prevEndsSmooth = spans[prevEdgeIndex].Count > 0
                && spans[prevEdgeIndex][^1].Kind == DividerJointSpanKind.Smooth;
            var nextStartsSmooth = spans[nextEdgeIndex].Count > 0
                && spans[nextEdgeIndex][0].Kind == DividerJointSpanKind.Smooth;

            for (var spanIndex = 0; spanIndex < spans[edgeIndex].Count; spanIndex++)
            {
                var span = spans[edgeIndex][spanIndex];
                var isFirstSpan = spanIndex == 0;
                var isLastSpan = spanIndex == spans[edgeIndex].Count - 1;

                if (span.Kind == DividerJointSpanKind.Smooth)
                {
                    // When the corner at either end is shared with an EndInset on
                    // the neighboring tabbed edge, shorten this smooth walk by t
                    // so the EndInset's lateral leg lands on a clean L-cut instead
                    // of doubling back along this edge (which would leave a kerf
                    // sliver — the visible "spike").
                    var len = span.Length;
                    if (isFirstSpan && prevEndsEndInset) len -= t;
                    if (isLastSpan && nextStartsEndInset) len -= t;
                    if (len > 0)
                    {
                        p = JointGeometry.Move(p, along, len);
                        segments.Add(new LineSegment(p));
                    }
                    continue;
                }

                if (span.Kind is DividerJointSpanKind.DividerTab or DividerJointSpanKind.EndInset)
                {
                    var skipInwardDip = span.Kind == DividerJointSpanKind.EndInset
                        && isFirstSpan && prevEndsSmooth;
                    var skipOutwardReturn = span.Kind == DividerJointSpanKind.EndInset
                        && isLastSpan && nextStartsSmooth;
                    var nextStartsWithEndInset = isLastSpan
                        && span.Kind == DividerJointSpanKind.EndInset
                        && spans[nextEdgeIndex].Count > 0
                        && spans[nextEdgeIndex][0].Kind == DividerJointSpanKind.EndInset;

                    if (!skipLeadingInset && !skipInwardDip)
                    {
                        p = JointGeometry.Move(p, inward, t);
                        segments.Add(new LineSegment(p));
                    }
                    else if (skipLeadingInset)
                    {
                        skipLeadingInset = false;
                    }

                    var spanLength = span.Length;
                    if (spanLength > 0)
                    {
                        p = JointGeometry.Move(p, along, spanLength);
                        segments.Add(new LineSegment(p));
                    }

                    if (nextStartsWithEndInset)
                    {
                        var nextInward = JointGeometry.InwardCcw[nextEdgeIndex];
                        p = JointGeometry.Move(p, nextInward, t);
                        segments.Add(new LineSegment(p));
                        p = JointGeometry.Move(p, inward, -t);
                        segments.Add(new LineSegment(p));
                        skipLeadingInset = true;
                    }
                    else if (!skipOutwardReturn)
                    {
                        p = JointGeometry.Move(p, inward, -t);
                        segments.Add(new LineSegment(p));
                    }

                    continue;
                }

                p = JointGeometry.Move(p, along, span.Length);
                segments.Add(new LineSegment(p));
            }
        }

        var (path, bbMin, bbMax, translation) = KerfOffset.OffsetOutwardAndTranslate(start, segments, settings.Kerf);
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
        bool EdgeStartsInset(FaceEdgeMap edgeMap, int cornerIndex)
        {
            if (openFaces.Contains(edgeMap.Neighbor) || cornerOwned[cornerIndex]) return false;
            var shared = edges[SharedEdgeTable.Id(edgeMap.Face, edgeMap.Neighbor)];
            return JointGeometry.StartsWithNeighborBlock(edgeMap, shared);
        }

        bool EdgeEndsInset(FaceEdgeMap edgeMap, int nextCornerIndex)
        {
            if (openFaces.Contains(edgeMap.Neighbor) || cornerOwned[nextCornerIndex]) return false;
            var shared = edges[SharedEdgeTable.Id(edgeMap.Face, edgeMap.Neighbor)];
            return JointGeometry.EndsWithNeighborBlock(edgeMap, shared);
        }

        var startAlong = JointGeometry.AlongCcw[3];
        var startPath = JointGeometry.Move(corners[0], startAlong, -t);
        if (EdgeEndsInset(ccw[3], 0))
        {
            startPath = JointGeometry.Move(startPath, JointGeometry.AlongCcw[0], t);
        }

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
                if (!EdgeEndsInset(ccw[(i + 3) % 4], i))
                {
                    segments.Add(new LineSegment(notchInner));
                }
            }
            if (!EdgeStartsInset(ccw[i], i))
            {
                segments.Add(new LineSegment(innerStart));
            }

            // Edge i emission (over the inner region).
            var edgeMap = ccw[i];
            if (openFaces.Contains(edgeMap.Neighbor))
            {
                segments.Add(new LineSegment(innerEnd));
            }
            else
            {
                var shared = edges[SharedEdgeTable.Id(edgeMap.Face, edgeMap.Neighbor)];
                var omitLeadingInset = !cornerOwned[i] && JointGeometry.StartsWithNeighborBlock(edgeMap, shared);
                var omitTrailingReturn = !cornerOwned[(i + 1) % 4];
                var edgeStart = omitLeadingInset
                    ? JointGeometry.Move(JointGeometry.Move(corner, prevAlong, -t), along, t)
                    : innerStart;
                var emitted = JointGeometry.EmitEdge(edgeMap, shared, i, t, edgeStart, omitLeadingInset, omitTrailingReturn);
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
