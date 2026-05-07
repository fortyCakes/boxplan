using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

internal enum DividerJointSpanKind
{
    Smooth,
    EndInset,
    DividerTab,
    FaceSlot,
}

internal sealed record DividerEdgeSpec(FaceName Face, bool Joined, bool DividerOwnsPrimary);

internal sealed record DividerJointSpan(double Length, DividerJointSpanKind Kind);

internal sealed record SlotSpec(double U, double V, double Width, double Height);
