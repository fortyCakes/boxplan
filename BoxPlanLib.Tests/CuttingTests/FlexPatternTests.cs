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

    // Helper: spacing=5, fraction=0.9, height=10 → cutLen=9, pitch=14 → only one cut fits per column.
    // panelWidth controls colCount: floor(panelWidth/5) columns × 1 cut each.
    private static IReadOnlyList<CuttablePath> SingleCutPerColumn(double panelWidth = 25.0) =>
        FlexPatternBuilder.Build(panelWidth, 10.0, FlexSettings(spacing: 5.0, fraction: 0.9), null);

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
    public void Returns_empty_when_panel_too_narrow_for_any_column()
    {
        var cuts = FlexPatternBuilder.Build(50.0, 60.0, FlexSettings(spacing: 100.0), null);
        Assert.Empty(cuts);
    }

    // ── Single cut per column ─────────────────────────────────────────────────

    [Fact]
    public void Single_cut_per_column_when_only_one_segment_fits()
    {
        // fraction=0.9, height=10: cutLen=9, pitch=14 — second segment would start at y=14 >= 10
        // width=25, spacing=5: floor(25/5)=5 columns × 1 cut = 5 total
        var cuts = SingleCutPerColumn();
        Assert.Equal(5, cuts.Count);
    }

    [Fact]
    public void Even_columns_start_at_bottom_edge()
    {
        var cuts = SingleCutPerColumn();
        // Flat list with 1 cut per column: even columns at indices 0, 2, 4
        for (var i = 0; i < cuts.Count; i += 2)
            Assert.Equal(0.0, cuts[i].Start.Y, precision: 9);
    }

    [Fact]
    public void Odd_columns_end_at_top_edge()
    {
        var cuts = SingleCutPerColumn();
        for (var i = 1; i < cuts.Count; i += 2)
        {
            var seg = Assert.IsType<LineSegment>(Assert.Single(cuts[i].Segments));
            Assert.Equal(10.0, seg.To.Y, precision: 9);
        }
    }

    [Fact]
    public void Full_length_cuts_have_length_equal_to_fraction_times_height()
    {
        // In the single-cut-per-column case no cuts are clipped, so all have the full length.
        var cuts = SingleCutPerColumn();
        var expectedLen = 10.0 * 0.9;
        Assert.All(cuts, cut =>
        {
            var seg = Assert.IsType<LineSegment>(Assert.Single(cut.Segments));
            Assert.Equal(expectedLen, Math.Abs(seg.To.Y - cut.Start.Y), precision: 9);
        });
    }

    // ── Multiple cuts per column ──────────────────────────────────────────────

    [Fact]
    public void Multiple_cuts_per_column_when_panel_is_tall()
    {
        // fraction=0.3, height=100, spacing=5: cutLen=30, pitch=35
        // Even col: [0,30],[35,65],[70,100] = 3 cuts (100 is exact multiple)
        // Odd col:  [70,100],[35,65],[0,30] = 3 cuts
        // width=20, spacing=5: floor(20/5)=4 columns × 3 cuts = 12 total
        var cuts = FlexPatternBuilder.Build(20.0, 100.0, FlexSettings(spacing: 5.0, fraction: 0.3), null);
        Assert.Equal(12, cuts.Count);
    }

    [Fact]
    public void All_y_coordinates_stay_within_panel_bounds()
    {
        var panelHeight = 100.0;
        var cuts = FlexPatternBuilder.Build(50.0, panelHeight, FlexSettings(spacing: 5.0, fraction: 0.75), null);
        Assert.All(cuts, cut =>
        {
            Assert.True(cut.Start.Y >= -1e-9, $"Start.Y={cut.Start.Y} is negative");
            var seg = Assert.IsType<LineSegment>(Assert.Single(cut.Segments));
            Assert.True(seg.To.Y <= panelHeight + 1e-9, $"End.Y={seg.To.Y} exceeds panel height");
        });
    }

    // ── Horizontal centering ──────────────────────────────────────────────────

    [Fact]
    public void Cuts_are_centred_horizontally_within_panel()
    {
        var panelW = 50.0;
        var cuts = SingleCutPerColumn(panelW);
        var minX = cuts.Min(c => c.Start.X);
        var maxX = cuts.Max(c => c.Start.X);
        var centre = (minX + maxX) / 2.0;
        Assert.Equal(panelW / 2.0, centre, precision: 6);
    }

    // ── Pipeline integration ──────────────────────────────────────────────────

    [Fact]
    public void Circle_prism_emits_single_merged_lateral_with_flex_cuts()
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
        var lateral = shapes.Single(s => s.Id == "cyl.lateral");
        Assert.NotEmpty(lateral.InteriorCuts);
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
