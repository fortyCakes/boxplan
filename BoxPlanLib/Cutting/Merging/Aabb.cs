using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting.Merging;

internal readonly record struct Aabb(Vec3 Min, Vec3 Max)
{
    public static Aabb FromBox(BoxShape box, Vec3 dims)
    {
        var loc = box.Location;
        var min = box.Origin switch
        {
            Origin.Center => new Vec3(loc.X - dims.X / 2.0, loc.Y - dims.Y / 2.0, loc.Z - dims.Z / 2.0),
            _             => loc,
        };
        var max = new Vec3(min.X + dims.X, min.Y + dims.Y, min.Z + dims.Z);
        return new Aabb(min, max);
    }

    public bool TouchesOrOverlaps(Aabb other, double eps = 1e-9)
    {
        var overlapX = IntervalOverlap(Min.X, Max.X, other.Min.X, other.Max.X, eps);
        var overlapY = IntervalOverlap(Min.Y, Max.Y, other.Min.Y, other.Max.Y, eps);
        var overlapZ = IntervalOverlap(Min.Z, Max.Z, other.Min.Z, other.Max.Z, eps);
        if (overlapX < -eps || overlapY < -eps || overlapZ < -eps) return false;

        var positiveAxes =
            (overlapX > eps ? 1 : 0) +
            (overlapY > eps ? 1 : 0) +
            (overlapZ > eps ? 1 : 0);
        return positiveAxes >= 2;
    }

    private static double IntervalOverlap(double aMin, double aMax, double bMin, double bMax, double eps)
    {
        var lo = Math.Max(aMin, bMin);
        var hi = Math.Min(aMax, bMax);
        return hi - lo;
    }
}
