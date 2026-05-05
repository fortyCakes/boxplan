using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class CutoutTests
{
    private static BoxPlan ParseOk(string yaml)
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan(yaml);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.Value!;
    }

    private static BoxPlanSettings Settings(double kerf = 0.0) =>
        new() { Kerf = kerf, MaterialThickness = 3.0, FingerJointSize = 20.0, SheetWidth = 300, SheetHeight = 300 };

    [Fact]
    public void Rectangle_cutout_emits_four_line_segments_in_interior_cuts()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "rectangle"
                    width: 30.0
                    height: 20.0
                    position:
                      anchor: "center"
                      offset: [0.0, 0.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var cut = Assert.Single(front.InteriorCuts);
        Assert.True(cut.Closed);
        Assert.Equal(4, cut.Segments.Count);
        Assert.All(cut.Segments, s => Assert.IsType<LineSegment>(s));
    }

    [Fact]
    public void Circle_cutout_uses_two_arc_segments()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "circle"
                    diameter: 40.0
                    position:
                      anchor: "center"
                      offset: [0.0, 0.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var cut = Assert.Single(front.InteriorCuts);
        Assert.Equal(2, cut.Segments.Count);
        Assert.All(cut.Segments, s => Assert.IsType<ArcSegment>(s));
    }

    [Fact]
    public void Semicircle_cutout_emits_arc_plus_line()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "semicircle"
                    diameter: 50.0
                    position:
                      anchor: "top-center"
                      offset: [0.0, 15.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var cut = Assert.Single(front.InteriorCuts);
        Assert.Equal(2, cut.Segments.Count);
        Assert.IsType<ArcSegment>(cut.Segments[0]);
        Assert.IsType<LineSegment>(cut.Segments[1]);
    }

    [Fact]
    public void Cutout_translates_with_outline_when_kerf_offsets_panel()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "rectangle"
                    width: 10.0
                    height: 10.0
                    position:
                      anchor: "center"
                      offset: [0.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        var noKerf = lib.GetCuttableShapes(plan, Settings(kerf: 0.0))
                        .Single(p => p.Id == "box.front").InteriorCuts[0].Start;
        var withKerf = lib.GetCuttableShapes(plan, Settings(kerf: 0.2))
                          .Single(p => p.Id == "box.front").InteriorCuts[0].Start;

        Assert.Equal(noKerf.X + 0.2, withKerf.X, 6);
        Assert.Equal(noKerf.Y + 0.2, withKerf.Y, 6);
    }

    [Fact]
    public void Closed_cube_with_no_features_has_no_interior_cuts()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
            """);
        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        Assert.All(pieces, p => Assert.Empty(p.InteriorCuts));
    }
}
