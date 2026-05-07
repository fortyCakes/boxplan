using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

internal sealed record JointBlock(double Length, bool PrimaryOwns);

internal sealed record FingerBlock(double Length, FaceName Owner);

internal static class FingerJointPattern
{
    /// <summary>
    /// Builds a finger-joint block pattern for an edge of the given <paramref name="length"/>.
    /// </summary>
    /// <param name="length">Inner edge length to fill with tabs/slots.</param>
    /// <param name="s">Nominal finger-joint size.</param>
    /// <param name="t">Material thickness — used as the absolute minimum tab/end-piece size.</param>
    /// <param name="warn">Optional callback invoked when the edge is too small to produce any tabs.</param>
    public static IReadOnlyList<JointBlock> Build(double length, double s, double t, Action<string>? warn = null)
    {
        var blocks = new List<JointBlock>();
        if (length <= 0)
        {
            return blocks;
        }

        // Below 3t there is not enough room for a tab plus two end pieces of at least t each.
        if (length < 3 * t)
        {
            warn?.Invoke(
                $"Edge inner length {length:F3} mm is less than 3× material thickness ({3 * t:F3} mm); " +
                "no tabs can be generated for this edge.");
            blocks.Add(new JointBlock(length, true));
            return blocks;
        }

        // Below 3s the normal pattern can't fit even one full-size tab with balanced ends.
        // Fall back to a single central tab, capped so each end piece is at least t.
        if (length < 3 * s)
        {
            var tabSize = Math.Min(s, length - 2 * t);
            var endSize = (length - tabSize) / 2.0;
            blocks.Add(new JointBlock(endSize, true));
            blocks.Add(new JointBlock(tabSize, false));
            blocks.Add(new JointBlock(endSize, true));
            return blocks;
        }

        var minEndLength = 0.75 * s;
        var interiorCount = (int)Math.Floor(length / s);
        if (interiorCount % 2 == 0)
        {
            interiorCount--;
        }

        while (interiorCount > 1)
        {
            var endLength = (length - interiorCount * s) / 2.0;
            if (endLength >= minEndLength)
            {
                break;
            }
            interiorCount -= 2;
        }

        var balancedEndLength = (length - interiorCount * s) / 2.0;
        blocks.Add(new JointBlock(balancedEndLength, true));
        for (var index = 0; index < interiorCount; index++)
        {
            blocks.Add(new JointBlock(s, index % 2 != 0));
        }
        blocks.Add(new JointBlock(balancedEndLength, true));

        return blocks;
    }
}
