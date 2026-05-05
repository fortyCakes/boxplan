using BoxPlanLib;
using BoxPlanLib.Model;
using BoxPlanLib.Parsing;

namespace BoxPlanLib.Tests;

public class ResolverTests
{
    private static BoxPlan ResolveOk(string yaml)
    {
        var parser = new PlanParser();
        var resolver = new PlanResolver();
        var raw = parser.Parse(yaml);
        Assert.True(raw.Success, string.Join("; ", raw.Errors));
        var resolved = resolver.Resolve(raw.Value!);
        Assert.True(resolved.Success, string.Join("; ", resolved.Errors));
        return resolved.Value!;
    }

    private static IReadOnlyList<PlanError> ResolveErrors(string yaml)
    {
        var parser = new PlanParser();
        var resolver = new PlanResolver();
        var raw = parser.Parse(yaml);
        Assert.True(raw.Success, "parser failed: " + string.Join("; ", raw.Errors));
        var resolved = resolver.Resolve(raw.Value!);
        Assert.False(resolved.Success);
        return resolved.Errors;
    }

    [Fact]
    public void Box_without_dimensions_or_fit_is_invalid()
    {
        var yaml = """
            shapes:
              - id: "a"
                type: "box"
            """;

        var errors = ResolveErrors(yaml);
        var error = Assert.Single(errors, e => e.Message.Contains("dimensions") && e.Message.Contains("fit"));
        Assert.NotNull(error.Line);
        Assert.True(error.Line is >= 2 and <= 3);
    }

    [Fact]
    public void Box_with_both_dimensions_and_fit_is_invalid()
    {
        var yaml = """
            shapes:
              - id: "a"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                fit:
                  mode: "cell"
            """;

        var errors = ResolveErrors(yaml);
        Assert.Contains(errors, e => e.Message.Contains("not both"));
    }

    [Fact]
    public void Faces_default_to_closed_when_unspecified()
    {
        var plan = ResolveOk("""
            shapes:
              - id: "a"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                faces:
                  - name: "top"
                    type: "open"
            """);

        var box = Assert.Single(plan.Shapes);
        Assert.Equal(6, box.Faces.Count);
        Assert.Equal(FaceType.Open, box.Faces.Single(f => f.Name == FaceName.Top).Type);
        Assert.Equal(FaceType.Closed, box.Faces.Single(f => f.Name == FaceName.Bottom).Type);
    }

    [Fact]
    public void Split_xy_expands_to_two_divider_sets_with_equal_positions()
    {
        var plan = ResolveOk("""
            shapes:
              - id: "a"
                type: "box"
                dimensions: [9.0, 4.0, 1.0]
                dividers:
                  - split: { x: 3, y: 2 }
                    facing: front
            """);

        var box = Assert.Single(plan.Shapes);
        Assert.Equal(2, box.Dividers.Count);

        var x = box.Dividers.Single(d => d.Axis == Axis.X);
        Assert.Equal(new[] { 3.0, 6.0 }, x.Positions);
        Assert.Equal(FaceName.Front, x.Facing);

        var y = box.Dividers.Single(d => d.Axis == Axis.Y);
        Assert.Equal(new[] { 2.0 }, y.Positions);
    }

    [Fact]
    public void Fill_all_cells_expands_to_one_insert_per_grid_cell()
    {
        var plan = ResolveOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [9.0, 4.0, 1.0]
                dividers:
                  - split: { x: 3, y: 2 }
                inserts:
                  - fill: "all-cells"
                    ref: "drawer"
              - id: "drawer"
                type: "box"
                fit:
                  mode: "cell"
            """);

        var frame = plan.ShapesById["frame"];
        var drawer = plan.ShapesById["drawer"];
        Assert.Equal(6, frame.Inserts.Count);
        var cells = frame.Inserts.Select(i => i.Cell!.Value).ToHashSet();
        for (var r = 0; r < 2; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                Assert.Contains((c, r, 0), cells);
            }
        }
        Assert.All(frame.Inserts, i => Assert.Same(drawer, i.Target));
    }

    [Fact]
    public void Unknown_ref_is_reported()
    {
        var errors = ResolveErrors("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [3.0, 2.0, 1.0]
                inserts:
                  - cell: [0, 0]
                    ref: "missing"
            """);

        var error = Assert.Single(errors, e => e.Message.Contains("missing"));
        Assert.NotNull(error.Line);
        Assert.True(error.Line is >= 6 and <= 7,
            $"expected error at insert mapping (line ~6), got line {error.Line}");
    }

    [Fact]
    public void Duplicate_shape_id_is_reported()
    {
        var errors = ResolveErrors("""
            shapes:
              - id: "a"
                type: "box"
                dimensions: [1.0, 1.0, 1.0]
              - id: "a"
                type: "box"
                dimensions: [2.0, 2.0, 2.0]
            """);

        var error = Assert.Single(errors, e => e.Message.Contains("Duplicate"));
        Assert.NotNull(error.Line);
        Assert.True(error.Line is >= 5 and <= 6,
            $"expected duplicate error at second shape (line ~5), got line {error.Line}");
    }

    [Fact]
    public void Cutout_with_diameter_normalises_to_equal_width_and_height()
    {
        var plan = ResolveOk("""
            shapes:
              - id: "a"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "semicircle"
                    diameter: 50.0
                    position:
                      anchor: "top-center"
                      offset: [0.0, 15.0]
            """);

        var feature = Assert.Single(Assert.Single(plan.Shapes).Features);
        var cutout = Assert.IsType<CutoutFeature>(feature);
        Assert.Equal(CutoutShape.Semicircle, cutout.Shape);
        Assert.Equal(50.0, cutout.Width);
        Assert.Equal(50.0, cutout.Height);
        Assert.Equal(FaceName.Front, cutout.Face);
        Assert.Equal(Anchor.TopCenter, cutout.Position!.Anchor);
        Assert.Equal(new Vec2(0.0, 15.0), cutout.Position!.Offset);
    }

    [Fact]
    public void Fit_auto_depth_resolves_to_FitDimension_Auto()
    {
        var plan = ResolveOk("""
            shapes:
              - id: "d"
                type: "box"
                fit:
                  mode: "cell"
                  clearance: 1.0
                  depth: auto
            """);

        var fit = Assert.Single(plan.Shapes).Fit!;
        Assert.IsType<FitDimension.Auto>(fit.Depth);
        Assert.Equal(1.0, fit.Clearance);
    }

    [Fact]
    public void Fit_numeric_depth_resolves_to_FitDimension_Fixed()
    {
        var plan = ResolveOk("""
            shapes:
              - id: "d"
                type: "box"
                fit:
                  mode: "cell"
                  depth: 145.5
            """);

        var fit = Assert.Single(plan.Shapes).Fit!;
        var fixedDepth = Assert.IsType<FitDimension.Fixed>(fit.Depth);
        Assert.Equal(145.5, fixedDepth.Value);
    }

    [Fact]
    public void Invalid_face_name_in_feature_is_reported()
    {
        var errors = ResolveErrors("""
            shapes:
              - id: "a"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                features:
                  - face: "northeast"
                    type: "cutout"
                    shape: "circle"
                    diameter: 5.0
            """);

        var error = Assert.Single(errors, e => e.Message.Contains("northeast"));
        Assert.NotNull(error.Line);
        Assert.True(error.Line is >= 6 and <= 7,
            $"expected error at feature mapping (line ~6), got line {error.Line}");
    }

    [Fact]
    public void Cutout_missing_shape_is_reported_with_line()
    {
        var errors = ResolveErrors("""
            shapes:
              - id: "a"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                features:
                  - face: "front"
                    type: "cutout"
                    diameter: 5.0
            """);

        var error = Assert.Single(errors, e => e.Message.Contains("Cutout requires 'shape'"));
        Assert.NotNull(error.Line);
        Assert.True(error.Line is >= 6 and <= 8,
            $"expected error at cutout mapping (line ~6), got line {error.Line}");
    }
}
