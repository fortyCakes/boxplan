using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class EngravingTests
{
    private static BoxPlan ParseOk(string yaml)
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan(yaml);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.Value!;
    }

    private static BoxPlanSettings Settings() =>
        new() { Kerf = 0.0, MaterialThickness = 3.0, FingerJointSize = 20.0, SheetWidth = 300, SheetHeight = 300 };

    [Fact]
    public void Engraving_text_appears_in_text_engravings_of_face()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving"
                    text: "Hello"
                    size: 8.0
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var engraving = Assert.Single(front.TextEngravings);
        Assert.Equal("Hello", engraving.Text);
        Assert.Equal(8.0, engraving.Size);
    }

    [Fact]
    public void Engraving_defaults_to_center_of_face()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving"
                    text: "X"
                    size: 5.0
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");
        var engraving = Assert.Single(front.TextEngravings);

        var cx = (front.BoundingBoxMin.X + front.BoundingBoxMax.X) / 2.0;
        var cy = (front.BoundingBoxMin.Y + front.BoundingBoxMax.Y) / 2.0;
        Assert.Equal(cx, engraving.X, precision: 3);
        Assert.Equal(cy, engraving.Y, precision: 3);
    }

    [Fact]
    public void Engraving_font_and_style_are_resolved()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving"
                    text: "Hi"
                    size: 10.0
                    font: "Arial"
                    style: "bold italic"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");
        var engraving = Assert.Single(front.TextEngravings);

        Assert.Equal("Arial", engraving.Font);
        Assert.True(engraving.Bold);
        Assert.True(engraving.Italic);
    }

    [Fact]
    public void Engraving_without_text_is_invalid()
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving"
                    size: 8.0
            """);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("text"));
    }

    [Fact]
    public void Engraving_without_size_is_invalid()
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving"
                    text: "Hi"
            """);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("size"));
    }

    [Fact]
    public void Engraving_svg_output_is_purple_text_element()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving"
                    text: "Mark"
                    size: 6.0
                    font: "sans-serif"
                    style: "bold"
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings());
        var svg = lib.GenerateSimpleSVG(pieces, Settings());

        Assert.Contains("fill=\"purple\"", svg);
        Assert.Contains(">Mark<", svg);
        Assert.Contains("font-weight=\"bold\"", svg);
        Assert.Contains("font-style=\"normal\"", svg);
        Assert.Contains("font-family=\"sans-serif\"", svg);
        Assert.Contains("font-size=\"6.000\"", svg);
    }

    [Fact]
    public void Engraving_does_not_affect_interior_cuts()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving"
                    text: "Test"
                    size: 5.0
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        Assert.Empty(front.InteriorCuts);
    }
}
