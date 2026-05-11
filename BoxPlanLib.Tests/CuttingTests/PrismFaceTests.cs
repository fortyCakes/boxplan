using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class PrismFaceTests
{
    private static BoxPlan ParseOk(string yaml)
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan(yaml);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.Value!;
    }

    private static BoxPlanSettings Settings(double t = 3.0, double s = 20.0) =>
        new()
        {
            Kerf = 0.0,
            MaterialThickness = t,
            FingerJointSize = s,
            SheetWidth = 1000,
            SheetHeight = 1000,
        };

    private static BoxPlanCuttableShape[] Cut(string yaml, BoxPlanSettings? settings = null)
    {
        var lib = new BoxPlanLib();
        var plan = ParseOk(yaml);
        return lib.GetCuttableShapes(plan, settings ?? Settings());
    }

    // ── Shape count ───────────────────────────────────────────────────────────

    [Fact]
    public void Hexagon_prism_emits_eight_shapes()
    {
        var shapes = Cut("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """);

        // 6 lateral + 2 cap faces
        Assert.Equal(8, shapes.Length);
    }

    [Fact]
    public void Triangle_prism_emits_five_shapes()
    {
        var shapes = Cut("""
            shapes:
              - id: "tri"
                type: "triangle"
                width: 100.0
                depth: 40.0
            """);

        // 3 lateral + 2 cap faces
        Assert.Equal(5, shapes.Length);
    }

    [Fact]
    public void Triangle_with_open_front_emits_four_shapes()
    {
        var shapes = Cut("""
            shapes:
              - id: "tri"
                type: "triangle"
                width: 100.0
                depth: 40.0
                faces:
                  - name: "front"
                    type: "open"
            """);

        // 3 lateral + 1 cap (back only)
        Assert.Equal(4, shapes.Length);
    }

    [Fact]
    public void Hexagon_with_two_open_lateral_faces_emits_six_shapes()
    {
        var shapes = Cut("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
                lateral-faces:
                  - index: 0
                    type: "open"
                  - index: 3
                    type: "open"
            """);

        // 4 lateral (0 and 3 omitted) + 2 caps
        Assert.Equal(6, shapes.Length);
    }

    // ── Shape IDs ─────────────────────────────────────────────────────────────

    [Fact]
    public void Hexagon_shapes_have_expected_ids()
    {
        var shapes = Cut("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """);

        Assert.Contains(shapes, s => s.Id == "hex.lateral-0");
        Assert.Contains(shapes, s => s.Id == "hex.lateral-5");
        Assert.Contains(shapes, s => s.Id == "hex.front");
        Assert.Contains(shapes, s => s.Id == "hex.back");
    }

    // ── Lateral face bounding boxes ───────────────────────────────────────────

    [Fact]
    public void Hexagon_lateral_face_bounding_box_height_equals_depth()
    {
        var shapes = Cut("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """, Settings(t: 0.0));

        // With zero kerf and zero thickness, bb height should equal depth
        var lateral0 = shapes.First(s => s.Id == "hex.lateral-0");
        var bbHeight = lateral0.BoundingBoxMax.Y - lateral0.BoundingBoxMin.Y;
        Assert.Equal(60.0, bbHeight, precision: 4);
    }

    [Fact]
    public void Hexagon_lateral_faces_have_equal_bounding_box_widths()
    {
        // Regular hexagon has equal-length edges, so all lateral faces should have equal width
        var shapes = Cut("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """, Settings(t: 0.0));

        var laterals = shapes.Where(s => s.Id.StartsWith("hex.lateral-")).ToArray();
        Assert.Equal(6, laterals.Length);

        var firstWidth = laterals[0].BoundingBoxMax.X - laterals[0].BoundingBoxMin.X;
        foreach (var face in laterals)
        {
            var w = face.BoundingBoxMax.X - face.BoundingBoxMin.X;
            Assert.Equal(firstWidth, w, precision: 4);
        }
    }

    // ── Square prism produces same output as a box ────────────────────────────

    [Fact]
    public void Square_prism_lateral_count_matches_four_box_sides()
    {
        var shapes = Cut("""
            shapes:
              - id: "sq"
                type: "regular-polygon"
                sides: 4
                width: 80.0
                depth: 50.0
            """);

        // 4 lateral + 2 cap = 6 total (same as closed box)
        Assert.Equal(6, shapes.Length);
    }

    // ── Cap face has non-trivial polygon outline ───────────────────────────────

    [Fact]
    public void Hexagon_cap_face_outline_has_more_than_four_points()
    {
        var shapes = Cut("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """);

        var front = shapes.First(s => s.Id == "hex.front");
        // Hexagon with notches should have many more points than a simple rectangle
        var pointCount = front.Outline.Segments.Count + 1;
        Assert.True(pointCount > 6, $"Expected > 6 outline points, got {pointCount}");
    }

    // ── Cap face bounding box covers the polygon width ────────────────────────

    [Fact]
    public void Hexagon_cap_face_bounding_box_width_matches_requested_width()
    {
        var shapes = Cut("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """, Settings(t: 0.0));

        var front = shapes.First(s => s.Id == "hex.front");
        var bbWidth = front.BoundingBoxMax.X - front.BoundingBoxMin.X;
        Assert.Equal(100.0, bbWidth, precision: 4);
    }

    // ── Pentagon ──────────────────────────────────────────────────────────────

    [Fact]
    public void Pentagon_prism_emits_seven_shapes()
    {
        var shapes = Cut("""
            shapes:
              - id: "pent"
                type: "pentagon"
                width: 80.0
                depth: 50.0
            """);

        // 5 lateral + 2 cap faces
        Assert.Equal(7, shapes.Length);
    }
}
