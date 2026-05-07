using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting.Merging;

// Per-face-direction 2D basis and projection. The chosen (U, V) axes are
// global world axes (no mirroring) — outline orientation is normalised
// later via signed area.
internal readonly record struct FaceBasis(FaceDirection Direction)
{
    public Axis UAxis => Direction.Axis switch
    {
        Axis.X => Axis.Y,
        Axis.Y => Axis.X,
        Axis.Z => Axis.X,
        _ => throw new InvalidOperationException(),
    };

    public Axis VAxis => Direction.Axis switch
    {
        Axis.X => Axis.Z,
        Axis.Y => Axis.Z,
        Axis.Z => Axis.Y,
        _ => throw new InvalidOperationException(),
    };

    public Vec2 Project(Vec3 p) =>
        new(Get(p, UAxis), Get(p, VAxis));

    public Vec3 Lift(Vec2 uv, double plane)
    {
        double x = 0, y = 0, z = 0;
        Set(ref x, ref y, ref z, Direction.Axis, plane);
        Set(ref x, ref y, ref z, UAxis, uv.X);
        Set(ref x, ref y, ref z, VAxis, uv.Y);
        return new Vec3(x, y, z);
    }

    private static double Get(Vec3 p, Axis a) => a switch
    {
        Axis.X => p.X,
        Axis.Y => p.Y,
        Axis.Z => p.Z,
        _ => throw new InvalidOperationException(),
    };

    private static void Set(ref double x, ref double y, ref double z, Axis a, double v)
    {
        switch (a)
        {
            case Axis.X: x = v; break;
            case Axis.Y: y = v; break;
            case Axis.Z: z = v; break;
        }
    }
}
