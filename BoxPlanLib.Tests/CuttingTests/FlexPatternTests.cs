using BoxPlanLib;
using BoxPlanLib.Cutting;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class FlexPatternTests
{
    private static BoxPlanSettings FlexSettings(
        double spacing = 5.0,
        double fraction = 0.75,
        double compensation = 1.0,
        double t = 3.0) =>
        new()
        {
            MaterialThickness = t,
            FingerJointSize = 20.0,
            Kerf = 0.0,
            SheetWidth = 2000,
            SheetHeight = 2000,
            FlexLineSpacing = spacing,
            FlexLineLengthFraction = fraction,
            FlexLengthCompensationFactor = compensation,
        };

    // Helper: fraction=0.9, width=10 → cutLen=9, pitch=14 → only [0,9] fits per even row,
    // only [1,10] fits per odd row; exactly 1 cut per row.
    private static IReadOnlyList<CuttablePath> SingleCutPerRow(double panelHeight = 25.0) =>
        FlexPatternBuilder.Build(10.0, panelHeight, FlexSettings(spacing: 5.0, fraction: 0.9), null);

    // ── Empty cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Zero_spacing_returns_empty()
    {
        var cuts = FlexPatternBuilder.Build(100.0, 60.0, FlexSettings(spacing: 0.0), null);
        Assert.Empty(cuts);
    }

    [Fact]
    public void Zero_fraction_returns_empty()
    {
        var cuts = FlexPatternBuilder.Build(100.0, 60.0, FlexSettings(fraction: 0.0), null);
        Assert.Empty(cuts);
    }

    [Fact]
    public void Returns_empty_when_panel_height_too_short()
    {
        var cuts = FlexPatternBuilder.Build(60.0, 50.0, FlexSettings(spacing: 100.0), null);
        Assert.Empty(cuts);
    }

    // ── Single cut per row ────────────────────────────────────────────────────

    [Fact]
    public void Single_cut_per_row_when_only_one_segment_fits()
    {
        // fraction=0.9, width=10: cutLen=9, pitch=14 — second segment would start at x=14 >= 10
        // height=25, spacing=5: floor(25/5)=5 rows × 1 cut = 5 total
        var cuts = SingleCutPerRow();
        Assert.Equal(5, cuts.Count);
    }

    [Fact]
    public void Even_rows_start_at_left_edge()
    {
        var cuts = SingleCutPerRow();
        // Flat list with 1 cut per row: even rows at indices 0, 2, 4
        for (var i = 0; i < cuts.Count; i += 2)
            Assert.Equal(0.0, cuts[i].Start.X, precision: 9);
    }

    [Fact]
    public void Odd_rows_end_at_right_edge()
    {
        var cuts = SingleCutPerRow();
        for (var i = 1; i < cuts.Count; i += 2)
        {
            var seg = Assert.IsType<LineSegment>(Assert.Single(cuts[i].Segments));
            Assert.Equal(10.0, seg.To.X, precision: 9);
        }
    }

    [Fact]
    public void Full_length_cuts_have_length_equal_to_fraction_times_width()
    {
        // In the single-cut-per-row case no cuts are clipped, so all have the full length.
        var cuts = SingleCutPerRow();
        var expectedLen = 10.0 * 0.9;
        Assert.All(cuts, cut =>
        {
            var seg = Assert.IsType<LineSegment>(Assert.Single(cut.Segments));
            Assert.Equal(expectedLen, Math.Abs(seg.To.X - cut.Start.X), precision: 9);
        });
    }

    // ── Multiple cuts per row ─────────────────────────────────────────────────

    [Fact]
    public void Multiple_cuts_per_row_when_panel_is_wide()
    {
        // fraction=0.3, width=100, spacing=5: cutLen=30, pitch=35
        // Even row: [0,30],[35,65],[70,100] = 3 cuts (100 is exact multiple)
        // Odd row:  [70,100],[35,65],[0,30] = 3 cuts
        // height=20, spacing=5: floor(20/5)=4 rows × 3 cuts = 12 total
        var cuts = FlexPatternBuilder.Build(100.0, 20.0, FlexSettings(spacing: 5.0, fraction: 0.3), null);
        Assert.Equal(12, cuts.Count);
    }

    [Fact]
    public void All_x_coordinates_stay_within_panel_bounds()
    {
        var panelWidth = 100.0;
        var cuts = FlexPatternBuilder.Build(panelWidth, 50.0, FlexSettings(spacing: 5.0, fraction: 0.75), null);
        Assert.All(cuts, cut =>
        {
            Assert.True(cut.Start.X >= -1e-9, $"Start.X={cut.Start.X} is negative");
            var seg = Assert.IsType<LineSegment>(Assert.Single(cut.Segments));
            Assert.True(seg.To.X <= panelWidth + 1e-9, $"End.X={seg.To.X} exceeds panel width");
        });
    }

    // ── Vertical centering ────────────────────────────────────────────────────

    [Fact]
    public void Cuts_are_centred_vertically_within_panel()
    {
        var panelH = 50.0;
        var cuts = SingleCutPerRow(panelH);
        var minY = cuts.Min(c => c.Start.Y);
        var maxY = cuts.Max(c => c.Start.Y);
        var centre = (minY + maxY) / 2.0;
        Assert.Equal(panelH / 2.0, centre, precision: 6);
    }

    // ── Pipeline integration ──────────────────────────────────────────────────

    [Fact]
    public void Circle_prism_curved_face_has_interior_flex_cuts()
    {
        var lib = new BoxPlanLib();
        var plan = lib.ParsePlan("""
            shapes:
              - id: "cyl"
                type: "circle"
                diameter: 80.0
                depth: 60.0
            """);
        Assert.True(plan.Success, string.Join("; ", plan.Errors));

        var settings = new BoxPlanSettings
        {
            MaterialThickness = 3.0,
            FingerJointSize = 20.0,
            Kerf = 0.0,
            SheetWidth = 2000,
            SheetHeight = 2000,
            FlexLineSpacing = 5.0,
            FlexLineLengthFraction = 0.75,
            FlexLengthCompensationFactor = 1.0,
        };

        var shapes = lib.GetCuttableShapes(plan.Value!, settings);
        var laterals = shapes.Where(s => s.Id.StartsWith("cyl.lateral-")).ToArray();
        Assert.Equal(2, laterals.Length);
        Assert.All(laterals, s => Assert.NotEmpty(s.InteriorCuts));
    }

    [Fact]
    public void Straight_prism_lateral_face_has_no_flex_cuts()
    {
        var lib = new BoxPlanLib();
        var plan = lib.ParsePlan("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """);
        Assert.True(plan.Success, string.Join("; ", plan.Errors));

        var settings = new BoxPlanSettings
        {
            MaterialThickness = 3.0,
            FingerJointSize = 20.0,
            Kerf = 0.0,
            SheetWidth = 1000,
            SheetHeight = 1000,
            FlexLineSpacing = 5.0,
            FlexLineLengthFraction = 0.75,
            FlexLengthCompensationFactor = 1.0,
        };

        var shapes = lib.GetCuttableShapes(plan.Value!, settings);
        var laterals = shapes.Where(s => s.Id.StartsWith("hex.lateral-")).ToArray();
        Assert.All(laterals, s => Assert.Empty(s.InteriorCuts));
    }
}
