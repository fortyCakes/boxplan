using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

internal static class CutoutClipper
{
    private const double Epsilon = 1e-6;

    public static IReadOnlyList<CuttablePath> ClipToOutline(CuttablePath path, CuttablePath outline, PipelineLogger? logger = null)
    {
        logger?.Log($"[clipper] Clipping path to outline");
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
