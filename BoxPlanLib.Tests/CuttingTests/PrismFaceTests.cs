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

    private static IReadOnlyList<Vec2> OutlinePoints(CuttablePath path)
    {
        var points = new List<Vec2> { path.Start };
        foreach (var segment in path.Segments)
        {
            var line = Assert.IsType<LineSegment>(segment);
            points.Add(line.To);
        }

        return points;
    }

    private static Vec2[] ProfileVertices(PrismProfile profile)
    {
        var vertices = new Vec2[profile.Segments.Count + 1];
        vertices[0] = profile.StartPoint;
        for (var i = 0; i < profile.Segments.Count; i++)
            vertices[i + 1] = profile.Segments[i].EndPoint;
        return vertices;
    }

    private static int CountInsetParallelSegments(CuttablePath path, Vec2 edgeStart, Vec2 edgeEnd, double inset)
    {
        const double minSegmentLength = 2.0;
        const double angleTolerance = 0.02;
        const double distanceTolerance = 0.25;
        const double endpointMargin = 3.0;

        var counts = 0;
        var edgeDx = edgeEnd.X - edgeStart.X;
        var edgeDy = edgeEnd.Y - edgeStart.Y;
        var edgeLength = Math.Sqrt(edgeDx * edgeDx + edgeDy * edgeDy);
        Assert.True(edgeLength > 0.0);

        var ux = edgeDx / edgeLength;
        var uy = edgeDy / edgeLength;
        var nx = -uy;
        var ny = ux;

        var points = OutlinePoints(path);
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var start = points[i];
            var end = points[i + 1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < minSegmentLength)
                continue;

            var parallelError = Math.Abs(dx * uy - dy * ux) / length;
            if (parallelError > angleTolerance)
                continue;

            var mid = new Vec2((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0);
            var along = (mid.X - edgeStart.X) * ux + (mid.Y - edgeStart.Y) * uy;
            if (along <= endpointMargin || along >= edgeLength - endpointMargin)
                continue;

            var distance = Math.Abs((mid.X - edgeStart.X) * nx + (mid.Y - edgeStart.Y) * ny);
            if (Math.Abs(distance - inset) <= distanceTolerance)
                counts++;
        }

        return counts;
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
    public void Triangle_back_cap_edges_match_mated_lateral_long_edges()
    {
        var yaml = """
            shapes:
              - id: "tri"
                type: "triangle"
                width: 120.0
                depth: 40.0
                faces:
                  - name: "front"
                    type: "open"
            """;
        var settings = new BoxPlanSettings
        {
            Kerf = 0.0,
            MaterialThickness = 3.0,
            FingerJointSize = 5.0,
            SheetWidth = 1000,
            SheetHeight = 1000,
        };

        var plan = ParseOk(yaml);
        var prism = Assert.IsType<PrismShape>(plan.ShapesById["tri"]);
        var shapes = new BoxPlanLib().GetCuttableShapes(plan, settings);
        var back = shapes.First(s => s.Id == "tri.back");

        var rawVertices = ProfileVertices(prism.Profile);
        var minX = rawVertices.Min(v => v.X);
        var minY = rawVertices.Min(v => v.Y);
        var translatedVertices = rawVertices
            .Take(prism.Profile.Segments.Count)
            .Select(v => new Vec2(v.X - minX, v.Y - minY))
            .ToArray();

        var capCounts = new List<int>();
        var lateralCounts = new List<int>();

        for (var i = 0; i < prism.Profile.Segments.Count; i++)
        {
            var edgeStart = translatedVertices[i];
            var edgeEnd = translatedVertices[(i + 1) % translatedVertices.Length];
            capCounts.Add(CountInsetParallelSegments(back.Outline, edgeStart, edgeEnd, settings.MaterialThickness));

            var lateral = shapes.First(s => s.Id == $"tri.lateral-{i}");
            var lateralWidth = lateral.BoundingBoxMax.X - lateral.BoundingBoxMin.X;
            lateralCounts.Add(CountInsetParallelSegments(
                lateral.Outline,
                new Vec2(0, 0),
                new Vec2(lateralWidth, 0),
                settings.MaterialThickness));
        }

        Assert.True(capCounts.Min() > 0, $"Expected every cap edge to have notch floors, got [{string.Join(", ", capCounts)}]");
        Assert.True(capCounts.Max() - capCounts.Min() <= 1, $"Expected cap edge counts to match within 1, got [{string.Join(", ", capCounts)}]");
        Assert.True(lateralCounts.Max() - lateralCounts.Min() <= 1, $"Expected lateral long-edge counts to match within 1, got [{string.Join(", ", lateralCounts)}]");

        for (var i = 0; i < capCounts.Count; i++)
        {
            Assert.InRange(
                Math.Abs(capCounts[i] - lateralCounts[i]),
                0,
                1);
        }
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
