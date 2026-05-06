using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

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
