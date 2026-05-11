using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests;

public class PrismResolverTests
{
    private static BoxPlan ParseOk(string yaml, BoxPlanSettings? settings = null)
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan(yaml, settings ?? new BoxPlanSettings { MaterialThickness = 3.0 });
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.Value!;
    }

    private static IReadOnlyList<PlanError> ParseErrors(string yaml)
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan(yaml, new BoxPlanSettings { MaterialThickness = 3.0 });
        Assert.False(result.Success);
        return result.Errors;
    }

    // ── Hexagon ──────────────────────────────────────────────────────────────

    [Fact]
    public void Hexagon_produces_six_lateral_faces()
    {
        var plan = ParseOk("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["hex"]);
        Assert.Equal(6, shape.LateralFaces.Count);
    }

    [Fact]
    public void Hexagon_lateral_faces_have_120_degree_dihedral_angles()
    {
        var plan = ParseOk("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["hex"]);
        Assert.All(shape.LateralFaces, face =>
            Assert.Equal(120.0, face.DihedralAngleToNext, precision: 1));
    }

    [Fact]
    public void Hexagon_lateral_faces_have_equal_edge_lengths()
    {
        var plan = ParseOk("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["hex"]);
        var firstLen = shape.LateralFaces[0].EdgeLength;
        Assert.All(shape.LateralFaces, face =>
            Assert.Equal(firstLen, face.EdgeLength, precision: 6));
    }

    [Fact]
    public void Hexagon_lateral_faces_are_not_curved()
    {
        var plan = ParseOk("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["hex"]);
        Assert.All(shape.LateralFaces, face => Assert.False(face.IsCurved));
    }

    // ── Triangle ─────────────────────────────────────────────────────────────

    [Fact]
    public void Triangle_produces_three_lateral_faces_with_60_degree_dihedral()
    {
        var plan = ParseOk("""
            shapes:
              - id: "tri"
                type: "triangle"
                width: 100.0
                depth: 40.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["tri"]);
        Assert.Equal(3, shape.LateralFaces.Count);
        Assert.All(shape.LateralFaces, face =>
            Assert.Equal(60.0, face.DihedralAngleToNext, precision: 1));
    }

    // ── Square (regular-polygon, 4 sides) ────────────────────────────────────

    [Fact]
    public void Square_prism_has_four_lateral_faces_with_90_degree_dihedral()
    {
        var plan = ParseOk("""
            shapes:
              - id: "sq"
                type: "regular-polygon"
                sides: 4
                width: 80.0
                depth: 50.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["sq"]);
        Assert.Equal(4, shape.LateralFaces.Count);
        Assert.All(shape.LateralFaces, face =>
            Assert.Equal(90.0, face.DihedralAngleToNext, precision: 6));
    }

    // ── Circle ───────────────────────────────────────────────────────────────

    [Fact]
    public void Circle_produces_two_curved_lateral_faces()
    {
        var plan = ParseOk("""
            shapes:
              - id: "cyl"
                type: "circle"
                diameter: 80.0
                depth: 60.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["cyl"]);
        Assert.Equal(2, shape.LateralFaces.Count);
        Assert.All(shape.LateralFaces, face => Assert.True(face.IsCurved));
    }

    [Fact]
    public void Circle_each_arc_has_semicircle_length()
    {
        var plan = ParseOk("""
            shapes:
              - id: "cyl"
                type: "circle"
                diameter: 80.0
                depth: 60.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["cyl"]);
        var expectedArcLength = Math.PI * 40.0; // π × r for semicircle
        Assert.All(shape.LateralFaces, face =>
            Assert.Equal(expectedArcLength, face.EdgeLength, precision: 6));
    }

    // ── Semicircle ───────────────────────────────────────────────────────────

    [Fact]
    public void Semicircle_produces_two_faces_one_straight_one_curved()
    {
        var plan = ParseOk("""
            shapes:
              - id: "half"
                type: "semicircle"
                diameter: 80.0
                depth: 40.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["half"]);
        Assert.Equal(2, shape.LateralFaces.Count);
        Assert.False(shape.LateralFaces[0].IsCurved);  // flat base
        Assert.True(shape.LateralFaces[1].IsCurved);   // arc top
    }

    [Fact]
    public void Semicircle_straight_face_length_equals_diameter()
    {
        var plan = ParseOk("""
            shapes:
              - id: "half"
                type: "semicircle"
                diameter: 80.0
                depth: 40.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["half"]);
        Assert.Equal(80.0, shape.LateralFaces[0].EdgeLength, precision: 6);
    }

    [Fact]
    public void Semicircle_curved_face_length_equals_pi_times_radius()
    {
        var plan = ParseOk("""
            shapes:
              - id: "half"
                type: "semicircle"
                diameter: 80.0
                depth: 40.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["half"]);
        Assert.Equal(Math.PI * 40.0, shape.LateralFaces[1].EdgeLength, precision: 6);
    }

    // ── Quarter-circle ───────────────────────────────────────────────────────

    [Fact]
    public void Quarter_circle_produces_three_faces()
    {
        var plan = ParseOk("""
            shapes:
              - id: "qc"
                type: "quarter-circle"
                radius: 60.0
                depth: 30.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["qc"]);
        Assert.Equal(3, shape.LateralFaces.Count);
        Assert.False(shape.LateralFaces[0].IsCurved);  // bottom line
        Assert.True(shape.LateralFaces[1].IsCurved);   // arc
        Assert.False(shape.LateralFaces[2].IsCurved);  // left line
    }

    [Fact]
    public void Quarter_circle_arc_face_length_equals_quarter_circumference()
    {
        var plan = ParseOk("""
            shapes:
              - id: "qc"
                type: "quarter-circle"
                radius: 60.0
                depth: 30.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["qc"]);
        var expected = Math.PI * 60.0 / 2.0; // quarter of full circumference = 2πr/4 = πr/2
        Assert.Equal(expected, shape.LateralFaces[1].EdgeLength, precision: 6);
    }

    // ── Explicit prism with points ────────────────────────────────────────────

    [Fact]
    public void Prism_with_points_builds_correct_profile()
    {
        var plan = ParseOk("""
            shapes:
              - id: "sq-prism"
                type: "prism"
                depth: 50.0
                points:
                  - [0, 0]
                  - [80, 0]
                  - [80, 80]
                  - [0, 80]
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["sq-prism"]);
        Assert.Equal(new Vec2(0, 0), shape.Profile.StartPoint);
        // 4 points → 4 segments (3 to next vertices + 1 closing back to start)
        Assert.Equal(4, shape.Profile.Segments.Count);
        Assert.Equal(new Vec2(80, 0), shape.Profile.Segments[0].EndPoint);
        Assert.Equal(new Vec2(80, 80), shape.Profile.Segments[1].EndPoint);
        Assert.Equal(new Vec2(0, 80), shape.Profile.Segments[2].EndPoint);
        Assert.Equal(new Vec2(0, 0), shape.Profile.Segments[3].EndPoint);  // close
    }

    [Fact]
    public void Prism_with_four_equal_sides_has_90_degree_dihedrals()
    {
        var plan = ParseOk("""
            shapes:
              - id: "sq-prism"
                type: "prism"
                depth: 50.0
                points:
                  - [0, 0]
                  - [80, 0]
                  - [80, 80]
                  - [0, 80]
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["sq-prism"]);
        Assert.Equal(4, shape.LateralFaces.Count);
        Assert.All(shape.LateralFaces, face =>
            Assert.Equal(90.0, face.DihedralAngleToNext, precision: 6));
    }

    // ── Open lateral face override ────────────────────────────────────────────

    [Fact]
    public void Open_lateral_face_override_sets_type()
    {
        var plan = ParseOk("""
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

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["hex"]);
        Assert.Equal(FaceType.Open, shape.LateralFaces[0].Type);
        Assert.Equal(FaceType.Closed, shape.LateralFaces[1].Type);
        Assert.Equal(FaceType.Open, shape.LateralFaces[3].Type);
    }

    // ── Open cap face ─────────────────────────────────────────────────────────

    [Fact]
    public void Open_front_cap_face_is_resolved()
    {
        var plan = ParseOk("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
                faces:
                  - name: "front"
                    type: "open"
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["hex"]);
        var front = shape.Faces.Single(f => f.Name == FaceName.Front);
        Assert.Equal(FaceType.Open, front.Type);
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Prism_without_depth_or_fit_reports_error()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
            """);

        Assert.Contains(errors, e =>
            e.Message.Contains("depth") && e.Severity == Severity.Error);
    }

    [Fact]
    public void Prism_with_both_depth_and_fit_reports_error()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 60.0
                fit:
                  mode: "cell"
                  clearance: 1.0
            """);

        Assert.Contains(errors, e =>
            e.Message.Contains("depth") && e.Severity == Severity.Error);
    }

    [Fact]
    public void Prism_with_points_and_segments_reports_error()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "bad"
                type: "prism"
                depth: 40.0
                points:
                  - [0, 0]
                  - [50, 0]
                  - [50, 50]
                segments:
                  - line-to: [0, 0]
                  - line-to: [50, 0]
                  - line-to: [50, 50]
            """);

        Assert.Contains(errors, e => e.Severity == Severity.Error);
    }

    // ── Pentagon uses correct side count ─────────────────────────────────────

    [Fact]
    public void Pentagon_produces_five_lateral_faces_with_108_degree_dihedral()
    {
        var plan = ParseOk("""
            shapes:
              - id: "pent"
                type: "pentagon"
                width: 80.0
                depth: 50.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["pent"]);
        Assert.Equal(5, shape.LateralFaces.Count);
        // Interior angle of regular pentagon = (5-2)*180/5 = 108°
        Assert.All(shape.LateralFaces, face =>
            Assert.Equal(108.0, face.DihedralAngleToNext, precision: 1));
    }
}
