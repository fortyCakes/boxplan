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

internal static class DividerJointBuilder
{
    public static IReadOnlyList<DividerJointSpan> BuildSpans(
        double length, double t, double s, bool joined, bool dividerOwnsPrimary)
    {
        if (!joined || length <= 0)
            return [new DividerJointSpan(length, DividerJointSpanKind.Smooth)];

        var edgeInset = t * 1.5;
        var innerLength = Math.Max(0, length - 2 * edgeInset);
        var blocks = FingerJointPattern.Build(innerLength, s, t);
        if (!blocks.Any(b => !b.PrimaryOwns))
            return [new DividerJointSpan(length, DividerJointSpanKind.Smooth)];

        var spans = new List<DividerJointSpan>();
        void Add(double spanLength, DividerJointSpanKind kind)
        {
            if (spanLength <= 0) return;
            if (spans.Count > 0 && spans[^1].Kind == kind)
            {
                spans[^1] = spans[^1] with { Length = spans[^1].Length + spanLength };
                return;
            }
            spans.Add(new DividerJointSpan(spanLength, kind));
        }

        Add(edgeInset, DividerJointSpanKind.EndInset);
        foreach (var block in blocks)
        {
            var dividerOwns = block.PrimaryOwns == dividerOwnsPrimary;
            Add(block.Length, dividerOwns ? DividerJointSpanKind.FaceSlot : DividerJointSpanKind.DividerTab);
        }
        Add(edgeInset, DividerJointSpanKind.EndInset);

        return spans;
    }

    public static IReadOnlyList<SlotSpec> BuildFingerSlots(
        double u, double v, double w, double h, double t, double s, bool dividerOwnsPrimary)
    {
        var slots = new List<SlotSpec>();
        var vertical = h >= w;
        var length = vertical ? h : w;
        var spans = BuildSpans(length, t, s, joined: true, dividerOwnsPrimary);
        var cursor = -length / 2.0;
        foreach (var span in spans)
        {
            if (span.Kind == DividerJointSpanKind.FaceSlot)
            {
                if (vertical)
                    slots.Add(new SlotSpec(u, v + cursor + span.Length / 2.0, w, span.Length));
                else
                    slots.Add(new SlotSpec(u + cursor + span.Length / 2.0, v, span.Length, h));
            }
            cursor += span.Length;
        }
        return slots;
    }
}
