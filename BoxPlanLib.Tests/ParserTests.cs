using BoxPlanLib;
using BoxPlanLib.Parsing;

namespace BoxPlanLib.Tests;

public class ParserTests
{
    private static RawPlan ParseOk(string yaml)
    {
        var result = new PlanParser().Parse(yaml);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.NotNull(result.Value);
        return result.Value!;
    }

    [Fact]
    public void Parses_box_with_polymorphic_type_discriminator()
    {
        var yaml = """
            shapes:
              - id: "a"
                type: "box"
                dimensions: [10.0, 20.0, 30.0]
            """;

        var raw = ParseOk(yaml);

        var box = Assert.Single(raw.Shapes);
        var rawBox = Assert.IsType<RawBoxShape>(box);
        Assert.Equal("a", rawBox.Id);
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, rawBox.Dimensions);
    }

    [Fact]
    public void Parses_split_dividers_and_facing()
    {
        var yaml = """
            shapes:
              - id: "f"
                type: "box"
                dimensions: [9.0, 4.0, 1.0]
                dividers:
                  - split: { x: 3, y: 2 }
                    facing: front
            """;

        var raw = ParseOk(yaml);

        var divs = Assert.Single(raw.Shapes).Dividers!;
        var div = Assert.Single(divs);
        Assert.NotNull(div.Split);
        Assert.Equal(3, div.Split!.X);
        Assert.Equal(2, div.Split!.Y);
        Assert.Equal("front", div.Facing);
    }

    [Fact]
    public void Parses_inserts_with_fill_and_ref()
    {
        var yaml = """
            shapes:
              - id: "f"
                type: "box"
                dimensions: [3.0, 2.0, 1.0]
                inserts:
                  - fill: "all-cells"
                    ref: "x"
            """;

        var raw = ParseOk(yaml);

        var insert = Assert.Single(Assert.Single(raw.Shapes).Inserts!);
        Assert.Equal("all-cells", insert.Fill);
        Assert.Equal("x", insert.Ref);
    }

    [Fact]
    public void Parses_cutout_feature_via_polymorphic_type()
    {
        var yaml = """
            shapes:
              - id: "d"
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
            """;

        var raw = ParseOk(yaml);

        var feature = Assert.Single(Assert.Single(raw.Shapes).Features!);
        var cutout = Assert.IsType<RawCutoutFeature>(feature);
        Assert.Equal("semicircle", cutout.Shape);
        Assert.Equal(50.0, cutout.Diameter);
        Assert.Equal("top-center", cutout.Position!.Anchor);
        Assert.Equal(new[] { 0.0, 15.0 }, cutout.Position!.Offset);
    }

    [Fact]
    public void Parses_top_left_anchor_in_feature_position()
    {
        var yaml = """
            shapes:
              - id: "d"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "rectangle"
                    width: 4.0
                    height: 2.0
                    position:
                      anchor: "top-left"
                      offset: [1.0, -2.0]
            """;

        var raw = ParseOk(yaml);

        var feature = Assert.Single(Assert.Single(raw.Shapes).Features!);
        var cutout = Assert.IsType<RawCutoutFeature>(feature);
        Assert.Equal("top-left", cutout.Position!.Anchor);
        Assert.Equal(new[] { 1.0, -2.0 }, cutout.Position!.Offset);
    }

    [Fact]
    public void Parses_bottom_right_anchor_in_feature_position()
    {
        var yaml = """
            shapes:
              - id: "d"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "rectangle"
                    width: 4.0
                    height: 2.0
                    position:
                      anchor: "bottom-right"
                      offset: [-1.0, 2.0]
            """;

        var raw = ParseOk(yaml);

        var feature = Assert.Single(Assert.Single(raw.Shapes).Features!);
        var cutout = Assert.IsType<RawCutoutFeature>(feature);
        Assert.Equal("bottom-right", cutout.Position!.Anchor);
        Assert.Equal(new[] { -1.0, 2.0 }, cutout.Position!.Offset);
    }

    [Fact]
    public void Parses_fit_with_auto_depth()
    {
        var yaml = """
            shapes:
              - id: "d"
                type: "box"
                fit:
                  mode: "cell"
                  clearance: 1.0
                  depth: auto
            """;

        var raw = ParseOk(yaml);
        var fit = Assert.Single(raw.Shapes).Fit!;
        Assert.Equal("cell", fit.Mode);
        Assert.Equal(1.0, fit.Clearance);
        Assert.True(fit.Depth!.Value.IsAuto);
    }

    [Fact]
    public void Parses_fit_with_numeric_depth()
    {
        var yaml = """
            shapes:
              - id: "d"
                type: "box"
                fit:
                  mode: "cell"
                  depth: 145.5
            """;

        var raw = ParseOk(yaml);
        var depth = Assert.Single(raw.Shapes).Fit!.Depth!.Value;
        Assert.False(depth.IsAuto);
        Assert.Equal(145.5, depth.Value);
    }

    [Theory]
    [InlineData("auto+10", 10.0)]
    [InlineData("auto + 15.5", 15.5)]
    [InlineData("auto - 5.0", -5.0)]
    [InlineData("auto-3", -3.0)]
    public void Parses_fit_with_auto_offset_depth(string depthValue, double expectedOffset)
    {
        var yaml = $"""
            shapes:
              - id: "d"
                type: "box"
                fit:
                  mode: "cell"
                  depth: {depthValue}
            """;

        var raw = ParseOk(yaml);
        var depth = Assert.Single(raw.Shapes).Fit!.Depth!.Value;
        Assert.True(depth.IsAuto);
        Assert.Equal(expectedOffset, depth.Offset);
    }

    [Fact]
    public void Reports_error_for_unknown_key_with_line_number()
    {
        var yaml = """
            shapes:
              - id: "a"
                type: "box"
                dimentions: [10.0, 20.0, 30.0]
            """;

        var result = new PlanParser().Parse(yaml);
        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal(4, error.Line);
        Assert.NotNull(error.Column);
        Assert.True(error.Column > 0);
    }

    [Fact]
    public void Reports_error_for_unknown_shape_type()
    {
        var yaml = """
            shapes:
              - id: "a"
                type: "pyramid"
                dimensions: [10.0, 10.0, 10.0]
            """;

        var result = new PlanParser().Parse(yaml);
        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.NotNull(error.Line);
    }

    [Fact]
    public void Reports_line_for_invalid_fit_dimension()
    {
        var yaml = """
            shapes:
              - id: "d"
                type: "box"
                fit:
                  mode: "cell"
                  depth: bogus
            """;

        var result = new PlanParser().Parse(yaml);
        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal(6, error.Line);
        Assert.NotNull(error.Column);
    }

    [Fact]
    public void Reports_line_for_malformed_yaml()
    {
        var yaml = """
            shapes:
              - id: "a"
                type: "box"
                dimensions: [1.0, 2.0
            """;

        var result = new PlanParser().Parse(yaml);
        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.NotNull(error.Line);
        Assert.True(error.Line >= 2);
    }

    [Fact]
    public void Empty_document_reports_line_one()
    {
        var result = new PlanParser().Parse("");
        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal(1, error.Line);
        Assert.Equal(1, error.Column);
    }
}
