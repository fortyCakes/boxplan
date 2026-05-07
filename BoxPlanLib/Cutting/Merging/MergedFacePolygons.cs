using BoxPlanLib.Model;
using Clipper2Lib;

namespace BoxPlanLib.Cutting.Merging;

internal static class MergedFacePolygons
{
    private const int ClipperPrecision = 8;
    private const double Eps = 1e-6;

    // Compute every outward-facing polygon of the merged solid formed by the
    // boxes in this group. For each face direction A and each plane c that
    // appears as a min or max of any box on the A axis:
    //   visible(c) = union(boxes with +A face at c) − union(boxes with −A face at c)
    // for the +A direction, and vice versa.
    public static IReadOnlyList<MergedFace> Compute(BoxGroup group, string idPrefix, PipelineLogger? logger = null)
    {
        logger?.Log($"[merge] Computing merged face polygons for {idPrefix}");
        var faces = new List<MergedFace>();
        foreach (var dir in FaceDirection.All)
        {
            var basis = new FaceBasis(dir);
            var planes = CollectPlanes(group, dir.Axis);
            foreach (var plane in planes)
            {
                var positive = FootprintsAtPlane(group, dir.Axis, plane, sign: +1, basis);
                var negative = FootprintsAtPlane(group, dir.Axis, plane, sign: -1, basis);
                if (positive.Count == 0 && negative.Count == 0) continue;

                var (outerSign, otherSign) = (dir.Sign, -dir.Sign);
                var outer = outerSign > 0 ? positive : negative;
                var other = outerSign > 0 ? negative : positive;
                if (outer.Count == 0) continue;

                var diff = Clipper.Difference(outer, other, FillRule.NonZero, ClipperPrecision);
                foreach (var path in diff)
                {
                    var area = Clipper.Area(path);
                    if (Math.Abs(area) < Eps) continue;
                    var ccw = area > 0
                        ? path
                        : new PathD(((IEnumerable<PointD>)path).Reverse());
                    var outline = RemoveCollinearVertices(ccw.Select(p => new Vec2(p.x, p.y)).ToList());
                    if (outline.Count < 3) continue;
                    logger?.Log($"[merge] Adding merged face {idPrefix}.{dir.ShortName()}@{Format(plane)}");
                    faces.Add(new MergedFace
                    {
                        Id = $"{idPrefix}.{dir.ShortName()}@{Format(plane)}#{faces.Count}",
                        Direction = dir,
                        Plane = plane,
                        Outline = outline,
                        Basis = basis,
                    });
                }
            }
        }
        return faces;
    }

    private static IReadOnlyList<double> CollectPlanes(BoxGroup group, Axis axis)
    {
        var values = new HashSet<double>();
        foreach (var member in group.Members)
        {
            values.Add(GetMin(member.Aabb, axis));
            values.Add(GetMax(member.Aabb, axis));
        }
        return values.OrderBy(v => v).ToList();
    }

    private static PathsD FootprintsAtPlane(BoxGroup group, Axis axis, double plane, int sign, FaceBasis basis)
    {
        var paths = new PathsD();
        foreach (var member in group.Members)
        {
            var faceCoord = sign > 0 ? GetMax(member.Aabb, axis) : GetMin(member.Aabb, axis);
            if (Math.Abs(faceCoord - plane) > Eps) continue;
            paths.Add(BoxFootprint(member.Aabb, basis));
        }
        return paths;
    }

    private static PathD BoxFootprint(Aabb box, FaceBasis basis)
    {
        var u0 = GetMin(box, basis.UAxis);
        var u1 = GetMax(box, basis.UAxis);
        var v0 = GetMin(box, basis.VAxis);
        var v1 = GetMax(box, basis.VAxis);
        return new PathD
        {
            new PointD(u0, v0),
            new PointD(u1, v0),
            new PointD(u1, v1),
            new PointD(u0, v1),
        };
    }

    private static double GetMin(Aabb b, Axis a) => a switch
    {
        Axis.X => b.Min.X,
        Axis.Y => b.Min.Y,
        Axis.Z => b.Min.Z,
        _ => throw new InvalidOperationException(),
    };

    private static double GetMax(Aabb b, Axis a) => a switch
    {
        Axis.X => b.Max.X,
        Axis.Y => b.Max.Y,
        Axis.Z => b.Max.Z,
        _ => throw new InvalidOperationException(),
    };

    private static string Format(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // Clipper2 preserves redundant collinear vertices when unioning rectangles
    // that share boundary edges (e.g. three stacked footprints that meet on
    // x=0 leave spurious vertices at the box-meeting Y values). Strip them so
    // each outline edge is a single continuous segment again — otherwise
    // SharedEdgeGraph splits the edge into multiple sub-segments and each
    // sub-segment's t-reservation eats finger material it shouldn't.
    private static List<Vec2> RemoveCollinearVertices(IReadOnlyList<Vec2> polygon)
    {
        var result = new List<Vec2>(polygon.Count);
        var n = polygon.Count;
        for (var i = 0; i < n; i++)
        {
            var prev = polygon[(i + n - 1) % n];
            var curr = polygon[i];
            var next = polygon[(i + 1) % n];
            var ax = curr.X - prev.X;
            var ay = curr.Y - prev.Y;
            var bx = next.X - curr.X;
            var by = next.Y - curr.Y;
            var cross = ax * by - ay * bx;
            var dot = ax * bx + ay * by;
            // cross ≈ 0 with positive dot ⇒ same-direction (truly redundant);
            // negative dot would be a 180° backtrack which Clipper output
            // shouldn't contain — keep it just in case so we don't collapse a
            // pathological polygon.
            if (Math.Abs(cross) > Eps || dot <= 0) result.Add(curr);
        }
        return result;
    }
}
