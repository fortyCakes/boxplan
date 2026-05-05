using BoxPlanLib.Model;
using System.Reflection;

namespace BoxPlanLib.Tests.CuttingTests;

public class SharedEdgeBlockTests
{
    private static IReadOnlyList<(double Length, FaceName Owner)> BuildBlocks(double length, double s)
    {
        var assembly = typeof(BoxPlanLib).Assembly;
        var sharedEdgeTable = assembly.GetType("BoxPlanLib.Cutting.SharedEdgeTable")!;
        var buildBlocks = sharedEdgeTable.GetMethod("BuildBlocks", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rawBlocks = (System.Collections.IEnumerable)buildBlocks.Invoke(null, new object[] { length, s, FaceName.Bottom, FaceName.Top })!;

        var blocks = new List<(double Length, FaceName Owner)>();
        foreach (var rawBlock in rawBlocks)
        {
            var rawType = rawBlock!.GetType();
            var blockLength = (double)rawType.GetProperty("Length")!.GetValue(rawBlock)!;
            var blockOwner = (FaceName)rawType.GetProperty("Owner")!.GetValue(rawBlock)!;
            blocks.Add((blockLength, blockOwner));
        }

        return blocks;
    }

    [Fact]
    public void Multi_block_edges_have_equal_end_blocks()
    {
        var blocks = BuildBlocks(length: 94.0, s: 20.0);

        Assert.Equal(5, blocks.Count);
        Assert.Equal(17.0, blocks[0].Length, 6);
        Assert.Equal(17.0, blocks[^1].Length, 6);
        Assert.Equal(FaceName.Bottom, blocks[0].Owner);
        Assert.Equal(FaceName.Bottom, blocks[^1].Owner);
        Assert.Equal(94.0, blocks.Sum(block => block.Length), 6);
    }

    [Fact]
    public void Edge_builder_reduces_block_count_when_balanced_ends_would_be_too_small()
    {
        var blocks = BuildBlocks(length: 86.0, s: 20.0);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(33.0, blocks[0].Length, 6);
        Assert.Equal(20.0, blocks[1].Length, 6);
        Assert.Equal(33.0, blocks[2].Length, 6);
        Assert.Equal(86.0, blocks.Sum(block => block.Length), 6);
    }
}