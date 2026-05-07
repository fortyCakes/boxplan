using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting.Merging;

// Outward-facing direction of a planar face on an axis-aligned solid.
// The pair (axis, sign) identifies which world axis the face's outward
// normal points along.
internal readonly record struct FaceDirection(Axis Axis, int Sign)
{
    public static readonly FaceDirection PlusX  = new(Axis.X,  +1);
    public static readonly FaceDirection MinusX = new(Axis.X,  -1);
    public static readonly FaceDirection PlusY  = new(Axis.Y,  +1);
    public static readonly FaceDirection MinusY = new(Axis.Y,  -1);
    public static readonly FaceDirection PlusZ  = new(Axis.Z,  +1);
    public static readonly FaceDirection MinusZ = new(Axis.Z,  -1);

    public static IEnumerable<FaceDirection> All =>
        new[] { PlusX, MinusX, PlusY, MinusY, PlusZ, MinusZ };

    // Map this outward direction to one of the original FaceName values so
    // we can reuse FacePriority for ownership decisions.
    public FaceName ToFaceName() => (Axis, Sign) switch
    {
        (Axis.X, +1) => FaceName.Right,
        (Axis.X, -1) => FaceName.Left,
        (Axis.Y, +1) => FaceName.Top,
        (Axis.Y, -1) => FaceName.Bottom,
        (Axis.Z, +1) => FaceName.Back,
        (Axis.Z, -1) => FaceName.Front,
        _ => throw new InvalidOperationException(),
    };

    public string ShortName() => (Axis, Sign) switch
    {
        (Axis.X, +1) => "x+",
        (Axis.X, -1) => "x-",
        (Axis.Y, +1) => "y+",
        (Axis.Y, -1) => "y-",
        (Axis.Z, +1) => "z+",
        (Axis.Z, -1) => "z-",
        _ => throw new InvalidOperationException(),
    };
}
