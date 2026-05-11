using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class EngravingGridTests
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
    public void Grid_produces_horizontal_and_vertical_lines()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [60.0, 60.0, 60.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
                    cell-size: 20.0
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        // All engravings are open single-segment paths (lines).
        Assert.True(front.Engravings.Count > 0);
        Assert.All(front.Engravings, p =>
        {
            Assert.False(p.Closed);
            Assert.Single(p.Segments);
            Assert.IsType<LineSegment>(p.Segments[0]);
        });
    }

    [Fact]
    public void Grid_default_center_is_space()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
                    cell-size: 20.0
            """);

        var planExplicit = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
                    cell-size: 20.0
                    center: "space"
            """);

        var lib = new BoxPlanLib();
        var piecesDefault = lib.GetCuttableShapes(plan, Settings());
        var piecesExplicit = lib.GetCuttableShapes(planExplicit, Settings());

        var frontDefault = piecesDefault.Single(p => p.Id == "box.front");
        var frontExplicit = piecesExplicit.Single(p => p.Id == "box.front");
        Assert.Equal(frontDefault.Engravings.Count, frontExplicit.Engravings.Count);
    }

    [Fact]
    public void Space_centered_grid_has_cell_center_at_face_center()
    {
        // Panel U = 100, cellSize = 20: lines at 10, 30, 50, 70, 90 (cell center at 50 = face center).
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
                    cell-size: 20.0
                    center: "space"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        // Vertical lines have constant X; extract unique X positions.
        var verticals = front.Engravings
            .Where(p =>
            {
                var seg = (LineSegment)p.Segments[0];
                return Math.Abs(p.Start.X - seg.To.X) < 1e-6;
            })
            .Select(p => p.Start.X)
            .OrderBy(x => x)
            .ToArray();

        // No vertical should pass through the centre (50mm, ignoring translation).
        var faceCx = (front.BoundingBoxMin.X + front.BoundingBoxMax.X) / 2.0;
        Assert.DoesNotContain(verticals, x => Math.Abs(x - faceCx) < 1e-6);

        // The two nearest verticals should straddle the centre equally.
        var left = verticals.LastOrDefault(x => x < faceCx - 1e-6);
        var right = verticals.FirstOrDefault(x => x > faceCx + 1e-6);
        Assert.True(right > 0 && left > 0, "Should have lines on both sides of centre");
        Assert.Equal(faceCx - left, right - faceCx, precision: 3);
    }

    [Fact]
    public void Corner_centered_grid_has_line_at_face_corner()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
                    cell-size: 25.0
                    center: "corner"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        // The leftmost vertical line should sit at BoundingBoxMin.X (the corner).
        var verticals = front.Engravings
            .Where(p =>
            {
                var seg = (LineSegment)p.Segments[0];
                return Math.Abs(p.Start.X - seg.To.X) < 1e-6;
            })
            .Select(p => p.Start.X)
            .OrderBy(x => x)
            .ToArray();

        Assert.True(verticals.Length > 0);
        Assert.Equal(front.BoundingBoxMin.X, verticals[0], precision: 3);
    }

    [Fact]
    public void Grid_lines_stay_within_face_bounds()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
                    cell-size: 20.0
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        // After clipping, every point of every engraving segment must be within the
        // face bounding box.
        const double tol = 1e-3;
        foreach (var line in front.Engravings)
        {
            var seg = (LineSegment)line.Segments[0];
            foreach (var pt in new[] { line.Start, seg.To })
            {
                Assert.True(pt.X >= front.BoundingBoxMin.X - tol, $"X={pt.X} is left of face");
                Assert.True(pt.X <= front.BoundingBoxMax.X + tol, $"X={pt.X} is right of face");
                Assert.True(pt.Y >= front.BoundingBoxMin.Y - tol, $"Y={pt.Y} is below face");
                Assert.True(pt.Y <= front.BoundingBoxMax.Y + tol, $"Y={pt.Y} is above face");
            }
        }
    }

    [Fact]
    public void Grid_without_cell_size_is_invalid()
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
            """);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("cell-size"));
    }

    [Fact]
    public void Grid_svg_uses_black_stroke()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [60.0, 60.0, 60.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
                    cell-size: 20.0
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings());
        var svg = lib.GenerateSimpleSVG(pieces, Settings());

        Assert.Contains("stroke=\"black\"", svg);
    }

    // --- Maximize mode ---

    [Fact]
    public void Maximize_exact_fit_produces_correct_cell_count()
    {
        // 50mm / 10mm = 5 exactly → 5 cells, lines at 0,10,20,30,40,50
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [50.0, 60.0, 60.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
                    cell-size: 10.0
                    center: "maximize"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var verticalXs = VerticalLineXs(front, front.BoundingBoxMin.X);
        // 5 cells = 6 lines (including both edges at 0 and 50mm in local coords)
        Assert.Equal(6, verticalXs.Count);
    }

    [Fact]
    public void Maximize_49mm_still_produces_five_cells()
    {
        // 49mm / 10mm = 4.9 → rounds to 5 → 5 cells; edge lines fall outside panel
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [49.0, 60.0, 60.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
                    cell-size: 10.0
                    center: "maximize"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        // 5 cells → 6 candidate lines, but start=-0.5 and end=49.5 fall outside [0,49]
        // so only 4 interior lines are emitted
        var verticalXs = VerticalLineXs(front, front.BoundingBoxMin.X);
        Assert.Equal(4, verticalXs.Count);
    }

    [Fact]
    public void Maximize_40mm_produces_four_cells()
    {
        // 40mm / 10mm = 4.0 → 4 cells
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [40.0, 60.0, 60.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
                    cell-size: 10.0
                    center: "maximize"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var verticalXs = VerticalLineXs(front, front.BoundingBoxMin.X);
        Assert.Equal(5, verticalXs.Count); // 4 cells = 5 lines
    }

    [Fact]
    public void Maximize_grid_is_centred_on_face()
    {
        // 49mm with 5 cells (50mm total): the grid overflows by 0.5mm each side,
        // so the lines should be symmetric about the face centre.
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [49.0, 60.0, 60.0]
                features:
                  - face: "front"
                    type: "engraving-grid"
                    cell-size: 10.0
                    center: "maximize"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var verticalXs = VerticalLineXs(front, front.BoundingBoxMin.X);
        var faceCx = (front.BoundingBoxMin.X + front.BoundingBoxMax.X) / 2.0;

        // Lines should be symmetric: for every line at cx-d there should be one at cx+d
        foreach (var x in verticalXs)
        {
            var mirror = 2 * faceCx - x;
            Assert.Contains(verticalXs, v => Math.Abs(v - mirror) < 1e-3);
        }
    }

    private static List<double> VerticalLineXs(BoxPlanCuttableShape shape, double originX)
    {
        // After clipping, one logical grid line may become several segments; deduplicate
        // by X position so we count lines, not segments.
        return shape.Engravings
            .Where(p =>
            {
                var seg = (LineSegment)p.Segments[0];
                return Math.Abs(p.Start.X - seg.To.X) < 1e-6;
            })
            .Select(p => Math.Round(p.Start.X - originX, 3))
            .Distinct()
            .OrderBy(x => x)
            .ToList();
    }
}
