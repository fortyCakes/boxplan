using BoxPlanLib.Model;
using Clipper2Lib;

namespace BoxPlanLib.Cutting.Merging;

internal static class MergedFacePolygons
{
    private const int ClipperPrecision = 8;
    private const double Eps = 1e-6;

    // Compute every outward-facing polygon of the merged solid formed by the
    // boxes in this group, plus internal divider faces where two hollow boxes
    // (both with at least one open face) share a contact plane.
    //
    // For each face direction A and each plane c:
    //   outer = footprints of boxes whose face in direction A at c is NOT open
    //   other = footprints of boxes whose opposing face in direction -A at c is NOT open
    //   visible(c) = Difference(outer, other)
    //
    // Open-faced members are excluded from the footprint unions so that their
    // missing panels don't contribute area to the outer shell. When all boxes
    // at a given plane have that face open the visible area is automatically
    // empty (no footprints to contribute), so open faces are suppressed
    // without a separate check.
    //
    // Additionally, for each plane c where both + and - contributors exist and
    // every contributing box on at least one side is hollow, the intersection
    // of the full (unfiltered) footprints is emitted as an IsInternalDivider
    // face. Only the positive direction is used to avoid duplicate emission.
    public static IReadOnlyList<MergedFace> Compute(BoxGroup group, string idPrefix, PipelineLogger? logger = null)
    {
        logger?.Log($"[merge] Computing merged face polygons for {idPrefix}");
        var faces = new List<MergedFace>();
        foreach (var dir in FaceDirection.All)
        {
            var basis = new FaceBasis(dir);
            var planes = CollectPlanes(group, dir.Axis);
            var faceName = dir.ToFaceName();
            var oppFaceName = new FaceDirection(dir.Axis, -dir.Sign).ToFaceName();

            foreach (var plane in planes)
            {
                var posMembers = GetMembersAtPlane(group, dir.Axis, plane, +1);
                var negMembers = GetMembersAtPlane(group, dir.Axis, plane, -1);

                var outerMembers = dir.Sign > 0 ? posMembers : negMembers;
                var otherMembers = dir.Sign > 0 ? negMembers : posMembers;

                // Exclude members whose face at this plane is open — they have
                // no panel there and must not contribute to the outer union.
                // The other (cancellation) side is not filtered: a box whose
                // inner face is at this plane still occupies that space and
                // correctly blocks the outer face, open or not.
                var outerFootprints = ToFootprints(outerMembers.Where(m => !IsOpenFace(m, faceName)), basis);
                var otherFootprints = ToFootprints(otherMembers, basis);

                if (outerFootprints.Count > 0)
                {
                    var diff = Clipper.Difference(outerFootprints, otherFootprints, FillRule.NonZero, ClipperPrecision);
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

                // Internal divider: emit only for the positive direction to
                // avoid a duplicate from the negative pass over the same plane.
                // Condition: both sides have contributors AND both sides are
                // hollow (at least one box on each side has an open face).
                if (dir.Sign > 0 && posMembers.Count > 0 && negMembers.Count > 0)
                {
                    var posHollow = posMembers.Any(m => m.Box.Faces.Any(f => f.Type == FaceType.Open));
                    var negHollow = negMembers.Any(m => m.Box.Faces.Any(f => f.Type == FaceType.Open));

                    if (posHollow && negHollow)
                    {
                        var posFootprints = ToFootprints(posMembers, basis);
                        var negFootprints = ToFootprints(negMembers, basis);
                        // Union after intersection to merge adjacent fragments into a
                        // single connected panel (e.g. 2×2 grid where each side of the
                        // contact plane contributes two separate rectangles).
                        var rawIntersection = Clipper.Intersect(posFootprints, negFootprints, FillRule.NonZero, ClipperPrecision);
                        var intersection = rawIntersection.Count > 1
                            ? Clipper.Union(rawIntersection, new PathsD(), FillRule.NonZero, ClipperPrecision)
                            : rawIntersection;
                        foreach (var path in intersection)
                        {
                            var area = Clipper.Area(path);
                            if (Math.Abs(area) < Eps) continue;
                            var ccw = area > 0
                                ? path
                                : new PathD(((IEnumerable<PointD>)path).Reverse());
                            var outline = RemoveCollinearVertices(ccw.Select(p => new Vec2(p.x, p.y)).ToList());
                            if (outline.Count < 3) continue;
                            logger?.Log($"[merge] Adding internal divider {idPrefix}.{dir.ShortName()}@{Format(plane)}");
                            faces.Add(new MergedFace
                            {
                                Id = $"{idPrefix}.{dir.ShortName()}@{Format(plane)}#{faces.Count}",
                                Direction = dir,
                                Plane = plane,
                                Outline = outline,
                                Basis = basis,
                                IsInternalDivider = true,
                            });
                        }
                    }
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

    private static IReadOnlyList<GroupedBox> GetMembersAtPlane(BoxGroup group, Axis axis, double plane, int sign)
    {
        var result = new List<GroupedBox>();
        foreach (var member in group.Members)
        {
            var coord = sign > 0 ? GetMax(member.Aabb, axis) : GetMin(member.Aabb, axis);
            if (Math.Abs(coord - plane) <= Eps) result.Add(member);
        }
        return result;
    }

    private static PathsD ToFootprints(IEnumerable<GroupedBox> members, FaceBasis basis)
    {
        var paths = new PathsD();
        foreach (var member in members)
            paths.Add(BoxFootprint(member.Aabb, basis));
        return paths;
    }

    private static bool IsOpenFace(GroupedBox member, FaceName faceName) =>
        member.Box.Faces.Any(f => f.Name == faceName && f.Type == FaceType.Open);

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
