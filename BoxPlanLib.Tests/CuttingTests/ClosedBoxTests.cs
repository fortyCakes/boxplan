using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class ClosedBoxTests
{
    private static BoxPlan ParseOk(string yaml)
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan(yaml);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.Value!;
    }

    private static BoxPlanSettings Settings(double kerf = 0.0, double t = 3.0, double s = 20.0) =>
        new()
        {
            Kerf = kerf,
            MaterialThickness = t,
            FingerJointSize = s,
            SheetWidth = 300,
            SheetHeight = 300,
        };

    private static IReadOnlyList<Vec2> Points(BoxPlanCuttableShape shape)
    {
        var pts = new List<Vec2> { shape.Outline.Start };
        foreach (var seg in shape.Outline.Segments)
        {
            if (seg is LineSegment ls) pts.Add(ls.To);
            else throw new InvalidOperationException("Phase 1 outlines should be polylines.");
        }
        return pts;
    }

    [Fact]
    public void Closed_cube_emits_six_pieces_one_per_face()
    {
        var plan = ParseOk("""
            shapes:
              - id: "cube"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings());

        Assert.Equal(6, pieces.Length);
        var ids = pieces.Select(p => p.Id).ToHashSet();
        Assert.Contains("cube.bottom", ids);
        Assert.Contains("cube.top", ids);
        Assert.Contains("cube.front", ids);
        Assert.Contains("cube.back", ids);
        Assert.Contains("cube.left", ids);
        Assert.Contains("cube.right", ids);
    }

    [Fact]
    public void Open_face_is_skipped()
    {
        var plan = ParseOk("""
            shapes:
              - id: "open-cube"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                faces:
                  - name: "top"
                    type: "open"
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings());

        Assert.Equal(5, pieces.Length);
        Assert.DoesNotContain(pieces, p => p.Id == "open-cube.top");
    }

    [Fact]
    public void Adjacent_edge_to_open_face_is_smooth()
    {
        var plan = ParseOk("""
            shapes:
              - id: "open-cube"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                faces:
                  - name: "top"
                    type: "open"
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings(kerf: 0.0));

        var front = pieces.Single(p => p.Id == "open-cube.front");
        var pts = Points(front);
        // No slot fingers strictly above the corner-extension band: nothing in (panelV-t, panelV).
        var notchPoints = pts.Count(p => p.Y > 100 - 3 + 1e-6 && p.Y < 100 - 1e-6);
        Assert.Equal(0, notchPoints);
        var atTop = pts.Count(p => Math.Abs(p.Y - 100) < 1e-6);
        Assert.True(atTop >= 2, $"expected at least 2 points at the top edge corners; got {atTop}");
    }

    [Fact]
    public void Outline_is_closed()
    {
        var plan = ParseOk("""
            shapes:
              - id: "cube"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings());

        foreach (var piece in pieces)
        {
            Assert.True(piece.Outline.Closed);
            var pts = Points(piece);
            var start = pts[0];
            var end = pts[^1];
            Assert.True(Math.Abs(start.X - end.X) < 1e-6 && Math.Abs(start.Y - end.Y) < 1e-6,
                $"piece {piece.Id} outline not closed: {start} != {end}");
        }
    }

    [Fact]
    public void Bottom_panel_bbox_at_origin()
    {
        var plan = ParseOk("""
            shapes:
              - id: "cube"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings(kerf: 0.0));

        var bottom = pieces.Single(p => p.Id == "cube.bottom");
        Assert.Equal(0, bottom.BoundingBoxMin.X);
        Assert.Equal(0, bottom.BoundingBoxMin.Y);
    }

    [Fact]
    public void Kerf_grows_outline_bbox_by_kerf_per_axis()
    {
        var plan = ParseOk("""
            shapes:
              - id: "cube"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
            """);

        var lib = new BoxPlanLib();
        var noKerf = lib.GetCuttableShapes(plan, Settings(kerf: 0.0))
                         .Single(p => p.Id == "cube.bottom");
        var withKerf = lib.GetCuttableShapes(plan, Settings(kerf: 0.2))
                          .Single(p => p.Id == "cube.bottom");

        Assert.Equal(noKerf.BoundingBoxMax.X + 0.2, withKerf.BoundingBoxMax.X, 6);
        Assert.Equal(noKerf.BoundingBoxMax.Y + 0.2, withKerf.BoundingBoxMax.Y, 6);
    }

    [Fact]
    public void Bottom_and_front_panels_have_inverse_jog_pattern_along_shared_edge()
    {
        var plan = ParseOk("""
            shapes:
              - id: "cube"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings(kerf: 0.0));

        var bottom = pieces.Single(p => p.Id == "cube.bottom");
        var front = pieces.Single(p => p.Id == "cube.front");

        var bottomPts = Points(bottom);
        var frontPts = Points(front);

        var bottomCutCount = bottomPts.Count(p => p.Y > 1e-6 && p.Y < 50);
        var frontCutCount = frontPts.Count(p => p.Y > 1e-6 && p.Y < 50);

        Assert.True(bottomCutCount + frontCutCount > 0,
            "Expected at least one face to have inward jogs along the shared edge.");
        Assert.NotEqual(bottomCutCount, frontCutCount);
    }
}
