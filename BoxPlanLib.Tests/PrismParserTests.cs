using BoxPlanLib;
using BoxPlanLib.Parsing;

namespace BoxPlanLib.Tests;

public class PrismParserTests
{
    private static RawPlan ParseOk(string yaml)
    {
        var result = new PlanParser().Parse(yaml);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.NotNull(result.Value);
        return result.Value!;
    }

    // ── type: "prism" with points ────────────────────────────────────────────

    [Fact]
    public void Parses_prism_with_points_list()
    {
        var yaml = """
            shapes:
              - id: "diamond"
                type: "prism"
                depth: 60.0
                points:
                  - [50, 0]
                  - [100, 60]
                  - [50, 120]
                  - [0, 60]
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawPrismShape>(Assert.Single(raw.Shapes));
        Assert.Equal("diamond", shape.Id);
        Assert.Equal(60.0, shape.Depth);
        Assert.NotNull(shape.Points);
        Assert.Equal(4, shape.Points!.Count);
        Assert.Equal(new[] { 50.0, 0.0 }, shape.Points[0]);
        Assert.Equal(new[] { 100.0, 60.0 }, shape.Points[1]);
        Assert.Equal(new[] { 50.0, 120.0 }, shape.Points[2]);
        Assert.Equal(new[] { 0.0, 60.0 }, shape.Points[3]);
        Assert.Null(shape.Segments);
    }

    // ── type: "prism" with segment list ─────────────────────────────────────

    [Fact]
    public void Parses_prism_with_line_segments()
    {
        var yaml = """
            shapes:
              - id: "d-box"
                type: "prism"
                depth: 80.0
                segments:
                  - line-to: [100, 0]
                  - line-to: [100, 80]
                  - line-to: [0, 80]
                  - line-to: [0, 0]
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawPrismShape>(Assert.Single(raw.Shapes));
        Assert.NotNull(shape.Segments);
        Assert.Equal(4, shape.Segments!.Count);
        Assert.Equal(new[] { 100.0, 0.0 }, shape.Segments[0].LineTo);
        Assert.Equal(new[] { 100.0, 80.0 }, shape.Segments[1].LineTo);
        Assert.Null(shape.Segments[0].ArcTo);
        Assert.Null(shape.Segments[0].BezierTo);
    }

    [Fact]
    public void Parses_prism_with_arc_segment()
    {
        var yaml = """
            shapes:
              - id: "d-box"
                type: "prism"
                depth: 80.0
                segments:
                  - line-to: [100, 0]
                  - line-to: [100, 80]
                  - arc-to: [0, 80]
                    radius: 50.0
                    clockwise: false
                  - line-to: [0, 0]
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawPrismShape>(Assert.Single(raw.Shapes));
        Assert.NotNull(shape.Segments);
        Assert.Equal(4, shape.Segments!.Count);
        var arc = shape.Segments[2];
        Assert.Null(arc.LineTo);
        Assert.Equal(new[] { 0.0, 80.0 }, arc.ArcTo);
        Assert.Equal(50.0, arc.Radius);
        Assert.Equal(false, arc.Clockwise);
    }

    [Fact]
    public void Parses_prism_with_bezier_segment()
    {
        var yaml = """
            shapes:
              - id: "curved-box"
                type: "prism"
                depth: 60.0
                segments:
                  - line-to: [100, 0]
                  - bezier-to: [0, 80]
                    control-1: [100, 40]
                    control-2: [0, 40]
                  - line-to: [0, 0]
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawPrismShape>(Assert.Single(raw.Shapes));
        Assert.NotNull(shape.Segments);
        Assert.Equal(3, shape.Segments!.Count);
        var bez = shape.Segments[1];
        Assert.Null(bez.LineTo);
        Assert.Equal(new[] { 0.0, 80.0 }, bez.BezierTo);
        Assert.Equal(new[] { 100.0, 40.0 }, bez.Control1);
        Assert.Equal(new[] { 0.0, 40.0 }, bez.Control2);
    }

    // ── Sugar: triangle / pentagon / hexagon ─────────────────────────────────

    [Fact]
    public void Parses_hexagon_shape()
    {
        var yaml = """
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawNamedPolygonShape>(Assert.Single(raw.Shapes));
        Assert.Equal("hexagon", shape.Type);
        Assert.Equal(100.0, shape.Width);
        Assert.Null(shape.Height);
        Assert.Equal(60.0, shape.Depth);
    }

    [Fact]
    public void Parses_triangle_shape_with_explicit_height()
    {
        var yaml = """
            shapes:
              - id: "tri"
                type: "triangle"
                width: 100.0
                height: 86.6
                depth: 40.0
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawNamedPolygonShape>(Assert.Single(raw.Shapes));
        Assert.Equal("triangle", shape.Type);
        Assert.Equal(100.0, shape.Width);
        Assert.Equal(86.6, shape.Height);
        Assert.Equal(40.0, shape.Depth);
    }

    [Fact]
    public void Parses_pentagon_shape()
    {
        var yaml = """
            shapes:
              - id: "pent"
                type: "pentagon"
                width: 80.0
                depth: 50.0
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawNamedPolygonShape>(Assert.Single(raw.Shapes));
        Assert.Equal("pentagon", shape.Type);
        Assert.Equal(80.0, shape.Width);
    }

    // ── Sugar: regular-polygon ────────────────────────────────────────────────

    [Fact]
    public void Parses_regular_polygon_with_sides()
    {
        var yaml = """
            shapes:
              - id: "oct"
                type: "regular-polygon"
                sides: 8
                width: 120.0
                depth: 60.0
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawRegularPolygonShape>(Assert.Single(raw.Shapes));
        Assert.Equal(8, shape.Sides);
        Assert.Equal(120.0, shape.Width);
        Assert.Equal(60.0, shape.Depth);
    }

    // ── Sugar: circle / semicircle / quarter-circle ───────────────────────────

    [Fact]
    public void Parses_circle_shape()
    {
        var yaml = """
            shapes:
              - id: "round"
                type: "circle"
                diameter: 80.0
                depth: 60.0
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawCircleShape>(Assert.Single(raw.Shapes));
        Assert.Equal(80.0, shape.Diameter);
        Assert.Equal(60.0, shape.Depth);
    }

    [Fact]
    public void Parses_semicircle_shape()
    {
        var yaml = """
            shapes:
              - id: "half"
                type: "semicircle"
                diameter: 80.0
                depth: 40.0
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawSemicircleShape>(Assert.Single(raw.Shapes));
        Assert.Equal(80.0, shape.Diameter);
        Assert.Equal(40.0, shape.Depth);
    }

    [Fact]
    public void Parses_quarter_circle_shape()
    {
        var yaml = """
            shapes:
              - id: "quarter"
                type: "quarter-circle"
                radius: 60.0
                depth: 30.0
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawQuarterCircleShape>(Assert.Single(raw.Shapes));
        Assert.Equal(60.0, shape.Radius);
        Assert.Equal(30.0, shape.Depth);
    }

    // ── lateral-faces override ────────────────────────────────────────────────

    [Fact]
    public void Parses_lateral_faces_open()
    {
        var yaml = """
            shapes:
              - id: "open-hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
                lateral-faces:
                  - index: 0
                    type: "open"
                  - index: 3
                    type: "open"
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawNamedPolygonShape>(Assert.Single(raw.Shapes));
        Assert.NotNull(shape.LateralFaces);
        Assert.Equal(2, shape.LateralFaces!.Count);
        Assert.Equal(0, shape.LateralFaces[0].Index);
        Assert.Equal("open", shape.LateralFaces[0].Type);
        Assert.Equal(3, shape.LateralFaces[1].Index);
        Assert.Equal("open", shape.LateralFaces[1].Type);
    }

    // ── cap faces (front/back) on prism shapes ────────────────────────────────

    [Fact]
    public void Parses_open_cap_face_on_prism()
    {
        var yaml = """
            shapes:
              - id: "open-top-hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
                faces:
                  - name: "front"
                    type: "open"
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawNamedPolygonShape>(Assert.Single(raw.Shapes));
        Assert.NotNull(shape.Faces);
        var face = Assert.Single(shape.Faces!);
        Assert.Equal("front", face.Name);
        Assert.Equal("open", face.Type);
    }

    // ── prism shape inherits common shape fields ──────────────────────────────

    [Fact]
    public void Parses_prism_location_and_origin()
    {
        var yaml = """
            shapes:
              - id: "placed"
                type: "prism"
                origin: "center"
                location: [10, 20, 5]
                depth: 40.0
                points:
                  - [0, 0]
                  - [50, 0]
                  - [50, 50]
                  - [0, 50]
            """;

        var raw = ParseOk(yaml);

        var shape = Assert.IsType<RawPrismShape>(Assert.Single(raw.Shapes));
        Assert.Equal("center", shape.Origin);
        Assert.Equal(new[] { 10.0, 20.0, 5.0 }, shape.Location);
    }
}
