using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting.Merging;

// One outward-facing polygon of a merged group. The Outline is in local
// (U, V) coordinates of the FaceBasis, oriented CCW when viewed from outside
// the solid (after orientation normalisation).
internal sealed class MergedFace
{
    public required string Id { get; init; }
    public required FaceDirection Direction { get; init; }
    public required double Plane { get; init; }
    public required IReadOnlyList<Vec2> Outline { get; init; }
    public required FaceBasis Basis { get; init; }

    // Lift a local 2D point on the outline back to a world 3D coordinate on
    // the face plane.
    public Vec3 ToWorld(Vec2 uv) => Basis.Lift(uv, Plane);
}
