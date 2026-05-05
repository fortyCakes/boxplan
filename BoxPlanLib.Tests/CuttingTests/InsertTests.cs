using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class InsertTests
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
    public void Each_insert_emits_drawer_pieces_using_resolved_dimensions()
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
                faces:
                  - name: "top"
                    type: "open"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());

        var framePieces = pieces.Where(p => p.Id.StartsWith("frame.") && !p.Id.Contains(".divider-")).ToArray();
        Assert.Equal(6, framePieces.Length);

        var drawerPieces = pieces.Where(p => p.Id.Contains("/drawer.")).ToArray();
        Assert.Equal(6 * 5, drawerPieces.Length);

        Assert.Equal(6, drawerPieces.Select(p => p.Id.Split('/')[1]).Distinct().Count());
    }

    [Fact]
    public void Drawer_bottom_panel_dimensions_match_resolved_cell()
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
                faces:
                  - name: "top"
                    type: "open"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());

        var drawerBottom = pieces.First(p => p.Id.Contains("/drawer.bottom"));
    Assert.Equal(95, drawerBottom.BoundingBoxMax.X, 6);
    Assert.Equal(145, drawerBottom.BoundingBoxMax.Y, 6);

        var drawerFront = pieces.First(p => p.Id.Contains("/drawer.front"));
    Assert.Equal(95, drawerFront.BoundingBoxMax.X, 6);
    Assert.Equal(95, drawerFront.BoundingBoxMax.Y, 6);
    }

    [Fact]
    public void Asymmetric_dividers_yield_different_resolved_drawer_panel_sizes()
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
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());

        var bottoms = pieces.Where(p => p.Id.Contains("/drawer.bottom")).ToArray();
        Assert.Equal(2, bottoms.Length);
        var widths = bottoms.Select(b => b.BoundingBoxMax.X).OrderBy(x => x).ToArray();
    Assert.Equal(77, widths[0], 6);
    Assert.Equal(217, widths[1], 6);
    }
}
