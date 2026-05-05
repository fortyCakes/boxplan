using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class DividerTests
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
    public void Drawer_frame_emits_three_divider_panels()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - split: { x: 3, y: 2 }
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());

        var dividers = pieces.Where(p => p.Id.Contains(".divider-")).ToArray();
        Assert.Equal(3, dividers.Length);

        var xDividers = dividers.Where(p => p.Id.Contains("divider-x")).ToArray();
        Assert.Equal(2, xDividers.Length);
        var yDividers = dividers.Where(p => p.Id.Contains("divider-y")).ToArray();
        Assert.Single(yDividers);
    }

    [Fact]
    public void Facing_front_shrinks_x_divider_depth_by_material_thickness()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - split: { x: 3 }
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var xDivider = pieces.First(p => p.Id.Contains("divider-x"));

        Assert.Equal(200, xDivider.BoundingBoxMax.X, 6);
        Assert.Equal(147, xDivider.BoundingBoxMax.Y, 6);
    }

    [Fact]
    public void Bottom_face_gets_slot_per_x_divider_position()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - axis: "x"
                    positions: [100.0, 200.0]
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var bottom = pieces.Single(p => p.Id == "frame.bottom");
        Assert.Equal(2, bottom.InteriorCuts.Count);
    }

    [Fact]
    public void Front_face_has_no_slot_when_facing_is_front()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - axis: "x"
                    positions: [100.0, 200.0]
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "frame.front");
        Assert.Empty(front.InteriorCuts);

        var back = pieces.Single(p => p.Id == "frame.back");
        Assert.Equal(2, back.InteriorCuts.Count);
    }

    [Fact]
    public void No_facing_yields_full_panel_size()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - axis: "x"
                    positions: [150.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var xDivider = pieces.First(p => p.Id.Contains("divider-x"));

        Assert.Equal(200, xDivider.BoundingBoxMax.X, 6);
        Assert.Equal(150, xDivider.BoundingBoxMax.Y, 6);
    }
}
