using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

internal sealed record JointBlock(double Length, bool PrimaryOwns);

internal sealed record FingerBlock(double Length, FaceName Owner);

internal static class FingerJointPattern
{
    public static IReadOnlyList<JointBlock> Build(double length, double s)
    {
        var blocks = new List<JointBlock>();
        if (length <= 0)
        {
            return blocks;
        }
        if (length < 3 * s)
        {
            blocks.Add(new JointBlock(length, true));
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
