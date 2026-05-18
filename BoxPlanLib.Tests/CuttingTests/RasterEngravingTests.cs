using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class RasterEngravingTests
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
    public void Raster_engraving_appears_in_face_output()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "raster-engraving"
                    source: "Spirit-Island-Logo.png"
                    width: 20.0
                    height: 10.0
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");
        var engraving = Assert.Single(front.RasterEngravings);

        Assert.Equal("Spirit-Island-Logo.png", engraving.Href);
        Assert.Equal(20.0, engraving.Width);
        Assert.Equal(10.0, engraving.Height);
        Assert.Equal(Anchor.Center, engraving.Anchor);
    }

    [Fact]
    public void Raster_engraving_anchor_and_offset_are_applied()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "raster-engraving"
                    source: "Spirit-Island-Logo.png"
                    width: 24.0
                    height: 12.0
                    position:
                      anchor: "top-left"
                      offset: [8.0, -6.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");
        var engraving = Assert.Single(front.RasterEngravings);

        Assert.Equal(front.BoundingBoxMin.X + 8.0, engraving.X, precision: 3);
        Assert.Equal(front.BoundingBoxMax.Y - 6.0, engraving.Y, precision: 3);
        Assert.Equal(Anchor.TopLeft, engraving.Anchor);
    }

    [Fact]
    public void Raster_engraving_svg_output_contains_image_element()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "raster-engraving"
                    source: "Spirit-Island-Logo.png"
                    width: 30.0
                    height: 15.0
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings());
        var svg = lib.GenerateSimpleSVG(pieces, Settings());

        Assert.Contains("<image", svg);
        Assert.Contains("href=\"Spirit-Island-Logo.png\"", svg);
        Assert.Contains("width=\"30.000\"", svg);
        Assert.Contains("height=\"15.000\"", svg);
        Assert.Contains("preserveAspectRatio=\"none\"", svg);
    }

    [Fact]
    public void Raster_engraving_without_source_is_invalid()
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "raster-engraving"
                    width: 20.0
                    height: 10.0
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("source"));
    }
}
