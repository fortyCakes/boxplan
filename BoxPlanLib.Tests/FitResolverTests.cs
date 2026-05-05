using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests;

public class FitResolverTests
{
  private static BoxPlanSettings Settings(double materialThickness = 3.0) =>
    new() { MaterialThickness = materialThickness };

  private static BoxPlan ParseOk(string yaml, BoxPlanSettings? settings = null)
    {
        var lib = new BoxPlanLib();
    var result = lib.ParsePlan(yaml, settings);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.Value!;
    }

  private static IReadOnlyList<PlanError> ParseErrors(string yaml, BoxPlanSettings? settings = null)
    {
        var lib = new BoxPlanLib();
    var result = lib.ParsePlan(yaml, settings);
        Assert.False(result.Success);
        return result.Errors;
    }

    [Fact]
    public void Drawer_frame_resolves_each_insert_to_98x98x148_with_clearance()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - split: { x: 3, y: 2 }
                inserts:
                  - fill: "all-cells"
                    ref: "drawer"
              - id: "drawer"
                type: "box"
                fit:
                  mode: "cell"
                  clearance: 1.0
            """, Settings());

        var frame = plan.ShapesById["frame"];
        Assert.Equal(6, frame.Inserts.Count);
        Assert.All(frame.Inserts, i => Assert.Equal(new Vec3(95, 95, 145), i.ResolvedDimensions));

        var byCell = frame.Inserts.ToDictionary(i => i.Cell!.Value, i => i.ResolvedLocation!.Value);
        Assert.Equal(new Vec3(2.5, 2.5, 2.5), byCell[(0, 0, 0)]);
        Assert.Equal(new Vec3(102.5, 2.5, 2.5), byCell[(1, 0, 0)]);
        Assert.Equal(new Vec3(202.5, 102.5, 2.5), byCell[(2, 1, 0)]);
    }

    [Fact]
    public void Asymmetric_dividers_produce_distinct_resolved_dimensions_per_insert()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 100.0, 50.0]
                dividers:
                  - axis: "x"
                    positions: [80.0]
                inserts:
                  - cell: [0, 0]
                    ref: "drawer"
                  - cell: [1, 0]
                    ref: "drawer"
              - id: "drawer"
                type: "box"
                fit:
                  mode: "cell"
            """, Settings());

        var frame = plan.ShapesById["frame"];
        var left = frame.Inserts.Single(i => i.Cell!.Value == (0, 0, 0));
        var right = frame.Inserts.Single(i => i.Cell!.Value == (1, 0, 0));

        Assert.Equal(new Vec3(77, 97, 47), left.ResolvedDimensions);
        Assert.Equal(new Vec3(1.5, 1.5, 1.5), left.ResolvedLocation);
        Assert.Equal(new Vec3(217, 97, 47), right.ResolvedDimensions);
        Assert.Equal(new Vec3(81.5, 1.5, 1.5), right.ResolvedLocation);
    }

    [Fact]
    public void Mixed_fixed_and_auto_dimensions_apply_clearance_only_to_auto_axes()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [200.0, 200.0, 200.0]
                dividers:
                  - split: { x: 2 }
                inserts:
                  - cell: [0, 0]
                    ref: "drawer"
              - id: "drawer"
                type: "box"
                fit:
                  mode: "cell"
                  clearance: 1.0
                  depth: 145.5
            """, Settings());

        var insert = plan.ShapesById["frame"].Inserts.Single();
              Assert.Equal(new Vec3(95, 195, 145.5), insert.ResolvedDimensions);
              Assert.Equal(new Vec3(2.5, 2.5, 0), insert.ResolvedLocation);
    }

    [Fact]
    public void Auto_with_zero_clearance_yields_full_cell_size()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [60.0, 40.0, 20.0]
                dividers:
                  - split: { x: 2, y: 2 }
                inserts:
                  - fill: "all-cells"
                    ref: "drawer"
              - id: "drawer"
                type: "box"
                fit:
                  mode: "cell"
            """, Settings());

        var insert = plan.ShapesById["frame"].Inserts.Single(i => i.Cell!.Value == (0, 0, 0));
        Assert.Equal(new Vec3(27, 17, 17), insert.ResolvedDimensions);
        Assert.Equal(new Vec3(1.5, 1.5, 1.5), insert.ResolvedLocation);
    }

    [Fact]
    public void Insert_with_fixed_dimension_target_uses_target_dimensions_at_cell_origin()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                dividers:
                  - split: { x: 2 }
                inserts:
                  - cell: [1, 0]
                    ref: "block"
              - id: "block"
                type: "box"
                dimensions: [10.0, 20.0, 30.0]
            """);

        var insert = plan.ShapesById["frame"].Inserts.Single();
        Assert.Equal(new Vec3(10, 20, 30), insert.ResolvedDimensions);
        Assert.Equal(new Vec3(50, 0, 0), insert.ResolvedLocation);
    }

    [Fact]
    public void Orphan_fit_shape_is_reported_as_error()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "lonely"
                type: "box"
                fit:
                  mode: "cell"
            """);

        Assert.Contains(errors, e => e.Severity == Severity.Error && e.Message.Contains("'lonely'") && e.Message.Contains("never placed"));
    }

    [Fact]
    public void Clearance_too_large_for_cell_is_reported_as_error()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                dividers:
                  - split: { x: 2 }
                inserts:
                  - cell: [0, 0]
                    ref: "drawer"
              - id: "drawer"
                type: "box"
                fit:
                  mode: "cell"
                  clearance: 5.0
            """, Settings());

        Assert.Contains(errors, e => e.Severity == Severity.Error && e.Message.Contains("clearance") && e.Message.Contains("material thickness"));
    }
}
