using BoxPlanLib.Cutting;

namespace BoxPlanLib.Tests.CuttingTests;

public class AngleJointTests
{
    // ── SlotDepth formula ─────────────────────────────────────────────────────

    [Fact]
    public void SlotDepth_at_90_degrees_equals_t()
    {
        var result = PrismFaceBuilder.SlotDepth(90.0, 3.0);
        Assert.Equal(3.0, result, precision: 9);
    }

    [Fact]
    public void SlotDepth_at_120_degrees_is_approximately_1155_t()
    {
        // slot = t / sin(120°) = t / (√3/2) ≈ 1.1547 * t
        var t = 3.0;
        var result = PrismFaceBuilder.SlotDepth(120.0, t);
        var expected = t / Math.Sin(120.0 * Math.PI / 180.0);
        Assert.Equal(expected, result, precision: 9);
        Assert.Equal(1.1547, result / t, precision: 3);
    }

    [Fact]
    public void SlotDepth_at_60_degrees_is_approximately_1155_t()
    {
        // slot = t / sin(60°) = t / (√3/2) ≈ 1.1547 * t
        var t = 3.0;
        var result = PrismFaceBuilder.SlotDepth(60.0, t);
        var expected = t / Math.Sin(60.0 * Math.PI / 180.0);
        Assert.Equal(expected, result, precision: 9);
        Assert.Equal(1.1547, result / t, precision: 3);
    }

    [Fact]
    public void SlotDepth_at_108_degrees_pentagon_is_correct()
    {
        // Interior angle of regular pentagon = 108°
        var t = 3.0;
        var result = PrismFaceBuilder.SlotDepth(108.0, t);
        var expected = t / Math.Sin(108.0 * Math.PI / 180.0);
        Assert.Equal(expected, result, precision: 9);
    }

    [Fact]
    public void SlotDepth_at_180_degrees_is_t()
    {
        // Flat (180°) → sin(180°) = 0 → clamped to t
        var result = PrismFaceBuilder.SlotDepth(180.0, 3.0);
        Assert.Equal(3.0, result, precision: 6);
    }

    [Fact]
    public void SlotDepth_never_less_than_t()
    {
        var t = 3.0;
        // Test a variety of interior angles; slot depth should always be >= t
        double[] angles = { 30.0, 45.0, 60.0, 90.0, 120.0, 135.0, 150.0 };
        Assert.All(angles, angle =>
            Assert.True(PrismFaceBuilder.SlotDepth(angle, t) >= t,
                $"SlotDepth at {angle}° should be >= t={t}"));
    }
}
