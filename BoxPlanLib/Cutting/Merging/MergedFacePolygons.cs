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
    public static IReadOnlyList<MergedFace> Compute(BoxGroup group, string idPrefix)
    {
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
                    var outline = ccw.Select(p => new Vec2(p.x, p.y)).ToList();
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
}
