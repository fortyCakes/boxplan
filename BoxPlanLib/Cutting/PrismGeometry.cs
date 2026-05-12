using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

// Shared geometry helpers for prism-family shapes. Handles the case where the
// back profile is a uniform, centroid-aligned scaling of the front profile.
internal static class PrismGeometry
{
    private const int DefaultArcSamples = 32;

    // Area centroid of the (discretised) front profile polygon.
    public static Vec2 ComputeProfileCentroid(PrismProfile profile)
    {
        var poly = DiscretiseProfile(profile);
        if (poly.Count < 3) return profile.StartPoint;

        double area2 = 0;
        double cx = 0;
        double cy = 0;
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var cross = a.X * b.Y - b.X * a.Y;
            area2 += cross;
            cx += (a.X + b.X) * cross;
            cy += (a.Y + b.Y) * cross;
        }
        if (Math.Abs(area2) < 1e-12) return profile.StartPoint;
        var area6 = 3 * area2; // 6 * signed area
        return new Vec2(cx / area6, cy / area6);
    }

    // Discretise the profile to a CCW (or CW — same as the source profile) polygon vertex list.
    // Arcs and Beziers are sampled into line segments. Closing vertex removed.
    public static List<Vec2> DiscretiseProfile(PrismProfile profile, int arcSamples = DefaultArcSamples)
    {
        var result = new List<Vec2> { profile.StartPoint };
        var prev = profile.StartPoint;
        foreach (var seg in profile.Segments)
        {
            switch (seg)
            {
                case ProfileSegment.Arc arc:
                    result.AddRange(SampleArc(prev, arc, arcSamples));
                    break;
                case ProfileSegment.Bezier bez:
                    result.AddRange(SampleBezier(prev, bez, arcSamples));
                    break;
                default:
                    result.Add(seg.EndPoint);
                    break;
            }
            prev = seg.EndPoint;
        }
        if (result.Count > 1)
        {
            var last = result[^1];
            if (Math.Abs(last.X - result[0].X) < 1e-9 && Math.Abs(last.Y - result[0].Y) < 1e-9)
                result.RemoveAt(result.Count - 1);
        }
        return result;
    }

    public static Vec2 ScalePoint(Vec2 v, Vec2 centroid, double scale)
        => new(centroid.X + scale * (v.X - centroid.X), centroid.Y + scale * (v.Y - centroid.Y));

    // True if back-scaling changes the geometry — use the trapezoidal path.
    public static bool IsAsymmetric(double backScale) => Math.Abs(backScale - 1.0) > 1e-9;

    // ── 3D normals and dihedrals ──────────────────────────────────────────────

    // Inward-facing normal of lateral face i (length not normalised).
    // 3D coords: (X, depth, Z) where X/Z are profile axes and depth runs front→back.
    // Returns the cross product (B-A) × (D-A) which, for a CCW profile, yields an
    // inward-facing normal — i.e. it points roughly toward the prism interior.
    public static (double X, double Y, double Z) LateralInwardNormal(
        Vec2 edgeStart, Vec2 edgeEnd, Vec2 centroid, double backScale, double depth)
    {
        var ex = edgeEnd.X - edgeStart.X;
        var ez = edgeEnd.Y - edgeStart.Y;
        var qx = (backScale - 1.0) * (edgeStart.X - centroid.X);
        var qz = (backScale - 1.0) * (edgeStart.Y - centroid.Y);
        // (ex, 0, ez) × (qx, depth, qz)
        var nx = 0 * qz - ez * depth;
        var ny = ez * qx - ex * qz;
        var nz = ex * depth - 0 * qx;
        return (nx, ny, nz);
    }

    // Interior 3D dihedral angle (degrees) between two adjacent lateral faces
    // sharing the slant edge at the END vertex of face i.
    public static double Dihedral3D(
        Vec2 edgeStartI, Vec2 edgeEndI, Vec2 edgeEndNext,
        Vec2 centroid, double backScale, double depth)
    {
        var n1 = LateralInwardNormal(edgeStartI, edgeEndI, centroid, backScale, depth);
        var n2 = LateralInwardNormal(edgeEndI, edgeEndNext, centroid, backScale, depth);
        return InteriorDihedralFromInwardNormals(n1, n2);
    }

    // Interior dihedral angle (degrees) between a cap face (at depth 0 if isFront,
    // else at depth=D) and the lateral face spanning edgeStart→edgeEnd.
    public static double CapToLateralAngle(
        Vec2 edgeStart, Vec2 edgeEnd, Vec2 centroid, double backScale, double depth, bool isFront)
    {
        var n = LateralInwardNormal(edgeStart, edgeEnd, centroid, backScale, depth);
        // Cap inward normal points into the prism interior along the depth axis.
        // Front cap interior is at depth>0 → inward = +Y. Back cap → inward = -Y.
        var capY = isFront ? 1.0 : -1.0;
        return InteriorDihedralFromInwardNormals(n, (0, capY, 0));
    }

    private static double InteriorDihedralFromInwardNormals(
        (double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        var dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        var la = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
        var lb = Math.Sqrt(b.X * b.X + b.Y * b.Y + b.Z * b.Z);
        if (la < 1e-12 || lb < 1e-12) return 90.0;
        var cos = Math.Clamp(dot / (la * lb), -1.0, 1.0);
        var angleBetween = Math.Acos(cos) * 180.0 / Math.PI;
        return 180.0 - angleBetween;
    }

    // ── Flat trapezoid layout ─────────────────────────────────────────────────

    // Lays out the trapezoidal lateral face flat in 2D panel coordinates.
    // Edge convention matches the existing rectangular path:
    //   edge 0 = bottom = BACK cap edge
    //   edge 1 = right slant (to next face)
    //   edge 2 = top    = FRONT cap edge (traversed right→left)
    //   edge 3 = left slant (to prev face)
    // Vertices in CCW order:
    //   [0] back-left (Da at depth)
    //   [1] back-right (C  at depth)
    //   [2] front-right (B at front)
    //   [3] front-left  (A at front)
    public static (Vec2[] Polygon, double FrontLen, double BackLen, double LeftSlant, double RightSlant)
        TrapezoidLayout(Vec2 edgeStart, Vec2 edgeEnd, Vec2 centroid, double backScale, double depth)
    {
        var frontLen = Distance(edgeStart, edgeEnd);
        var backLen = frontLen * backScale;

        var backStart = ScalePoint(edgeStart, centroid, backScale);
        var backEnd = ScalePoint(edgeEnd, centroid, backScale);
        // 3D slant lengths: sqrt(|V'-V|^2 + depth^2)
        var leftOffset = Distance(backStart, edgeStart);
        var rightOffset = Distance(backEnd, edgeEnd);
        var leftSlant = Math.Sqrt(leftOffset * leftOffset + depth * depth);
        var rightSlant = Math.Sqrt(rightOffset * rightOffset + depth * depth);

        // Place back edge along X axis from (0,0) to (backLen, 0); front edge sits
        // above at y = hPanel, centred such that |front-left - back-left| = leftSlant
        // and |front-right - back-right| = rightSlant. The top edge of length frontLen
        // is traversed right→left in CCW order.
        double xFrontLeft;
        double hPanel;
        var a = frontLen - backLen;
        if (Math.Abs(a) < 1e-9)
        {
            // Parallelogram (slants may still differ if profile is asymmetric).
            xFrontLeft = (leftSlant * leftSlant - rightSlant * rightSlant) / (2 * Math.Max(backLen, 1e-9));
            hPanel = Math.Sqrt(Math.Max(0, leftSlant * leftSlant - xFrontLeft * xFrontLeft));
        }
        else
        {
            // Solve x_FL from: x_FL² + h² = leftSlant²
            //                  (x_FL + a)² + h² = rightSlant²  (where a = frontLen - backLen)
            // (Geometrically: front-left is at x_FL, front-right at x_FL + frontLen = backLen + (x_FL + a))
            xFrontLeft = (leftSlant * leftSlant - rightSlant * rightSlant - a * a) / (2 * a) - a;
            // Above came out unintuitive — re-derive cleanly:
            // back-left = (0, 0); front-left = (x_FL, h); leftSlant = sqrt(x_FL² + h²)
            // back-right = (backLen, 0); front-right = (x_FL + frontLen, h)
            //   → rightSlant² = (x_FL + frontLen - backLen)² + h² = (x_FL + a)² + h²
            // Subtract: rightSlant² - leftSlant² = (x_FL + a)² - x_FL² = 2*a*x_FL + a²
            // → x_FL = (rightSlant² - leftSlant² - a²) / (2 a)
            xFrontLeft = (rightSlant * rightSlant - leftSlant * leftSlant - a * a) / (2 * a);
            hPanel = Math.Sqrt(Math.Max(0, leftSlant * leftSlant - xFrontLeft * xFrontLeft));
        }

        var polygon = new[]
        {
            new Vec2(0, 0),                          // [0] back-left  (Da)
            new Vec2(backLen, 0),                    // [1] back-right (C)
            new Vec2(xFrontLeft + frontLen, hPanel), // [2] front-right (B)
            new Vec2(xFrontLeft, hPanel),            // [3] front-left  (A)
        };
        return (polygon, frontLen, backLen, leftSlant, rightSlant);
    }

    public static double Distance(Vec2 a, Vec2 b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static IEnumerable<Vec2> SampleArc(Vec2 start, ProfileSegment.Arc arc, int n)
    {
        var (center, thetaStart, thetaTotal) = ArcGeometry(start, arc);
        for (var i = 1; i <= n; i++)
        {
            var theta = thetaStart + thetaTotal * i / n;
            yield return new Vec2(center.X + arc.Radius * Math.Cos(theta), center.Y + arc.Radius * Math.Sin(theta));
        }
    }

    private static IEnumerable<Vec2> SampleBezier(Vec2 start, ProfileSegment.Bezier bez, int n)
    {
        for (var i = 1; i <= n; i++)
        {
            var u = (double)i / n;
            var u2 = 1 - u;
            yield return new Vec2(
                u2 * u2 * u2 * start.X + 3 * u2 * u2 * u * bez.Control1.X + 3 * u2 * u * u * bez.Control2.X + u * u * u * bez.To.X,
                u2 * u2 * u2 * start.Y + 3 * u2 * u2 * u * bez.Control1.Y + 3 * u2 * u * u * bez.Control2.Y + u * u * u * bez.To.Y);
        }
    }

    // Returns (center, thetaStart, thetaTotal) for an arc from start to arc.To.
    // thetaTotal is positive CCW, negative CW.
    private static (Vec2 center, double thetaStart, double thetaTotal) ArcGeometry(Vec2 start, ProfileSegment.Arc arc)
    {
        var p2 = arc.To;
        var cx = p2.X - start.X;
        var cz = p2.Y - start.Y;
        var chordLen = Math.Sqrt(cx * cx + cz * cz);
        if (chordLen < 1e-9) return (start, 0, 0);
        var halfChord = chordLen / 2;
        var h = halfChord < arc.Radius ? Math.Sqrt(arc.Radius * arc.Radius - halfChord * halfChord) : 0.0;
        double perpX, perpZ;
        if (!arc.Clockwise) { perpX = -cz / chordLen; perpZ = cx / chordLen; }
        else { perpX = cz / chordLen; perpZ = -cx / chordLen; }
        var center = new Vec2((start.X + p2.X) / 2 + h * perpX, (start.Y + p2.Y) / 2 + h * perpZ);
        var thetaStart = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var thetaEnd = Math.Atan2(p2.Y - center.Y, p2.X - center.X);
        double thetaTotal;
        if (!arc.Clockwise)
        {
            thetaTotal = thetaEnd - thetaStart;
            if (thetaTotal <= 0) thetaTotal += 2 * Math.PI;
        }
        else
        {
            thetaTotal = thetaEnd - thetaStart;
            if (thetaTotal >= 0) thetaTotal -= 2 * Math.PI;
        }
        return (center, thetaStart, thetaTotal);
    }
}
