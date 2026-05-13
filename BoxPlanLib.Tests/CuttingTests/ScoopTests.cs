using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class ScoopTests
{
    private static BoxPlan ParseOk(string yaml)
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan(yaml);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.Value!;
    }

    private static IReadOnlyList<PlanError> ParseErrors(string yaml)
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan(yaml);
        return result.Errors;
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

    // ── Resolver: model + desugaring ──────────────────────────────────────────

    [Fact]
    public void Single_scoop_parses_into_one_scoop_record()
    {
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 25
                    rise: 30
            """);

        var box = (BoxShape)plan.Shapes.Single();
        Assert.Single(box.Scoops);
        Assert.Equal(FaceName.Bottom, box.Scoops[0].Face);
        Assert.Equal(FaceName.Left, box.Scoops[0].Edge);
        Assert.Equal(25, box.Scoops[0].Inset);
        Assert.Equal(30, box.Scoops[0].Rise);
    }

    [Fact]
    public void Edge_list_desugars_to_multiple_scoops()
    {
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: [left, right]
                    inset: 25
                    rise: 30
            """);

        var box = (BoxShape)plan.Shapes.Single();
        Assert.Equal(2, box.Scoops.Count);
        Assert.Equal(new[] { FaceName.Left, FaceName.Right }, box.Scoops.Select(s => s.Edge).ToArray());
        Assert.All(box.Scoops, s => Assert.Equal(25, s.Inset));
        Assert.All(box.Scoops, s => Assert.Equal(30, s.Rise));
    }

    [Fact]
    public void All_edges_desugars_to_four_scoops()
    {
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: all-edges
                    inset: 15
                    rise: 20
            """);

        var box = (BoxShape)plan.Shapes.Single();
        Assert.Equal(4, box.Scoops.Count);
        var edges = box.Scoops.Select(s => s.Edge).ToHashSet();
        Assert.Equal(new HashSet<FaceName> { FaceName.Front, FaceName.Right, FaceName.Back, FaceName.Left }, edges);
    }

    [Fact]
    public void Asymmetric_pair_keeps_independent_values()
    {
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 25
                    rise: 30
                  - face: bottom
                    edge: right
                    inset: 40
                    rise: 50
            """);

        var box = (BoxShape)plan.Shapes.Single();
        Assert.Equal(2, box.Scoops.Count);
        var left = box.Scoops.Single(s => s.Edge == FaceName.Left);
        var right = box.Scoops.Single(s => s.Edge == FaceName.Right);
        Assert.Equal(25, left.Inset);
        Assert.Equal(30, left.Rise);
        Assert.Equal(40, right.Inset);
        Assert.Equal(50, right.Rise);
    }

    // ── Resolver: validation ──────────────────────────────────────────────────

    [Fact]
    public void Non_adjacent_edge_is_rejected()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: top
                    inset: 25
                    rise: 30
            """);
        Assert.Contains(errors, e => e.Message.Contains("not adjacent"));
    }

    [Fact]
    public void Non_positive_inset_is_rejected()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 0
                    rise: 30
            """);
        Assert.Contains(errors, e => e.Message.Contains("inset"));
    }

    [Fact]
    public void Non_positive_rise_is_rejected()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 25
                    rise: -1
            """);
        Assert.Contains(errors, e => e.Message.Contains("rise"));
    }

    [Fact]
    public void Inset_exceeding_face_dimension_is_rejected()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [100, 50, 60]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 150
                    rise: 20
            """);
        Assert.Contains(errors, e => e.Message.Contains("inset"));
    }

    [Fact]
    public void Rise_exceeding_wall_height_is_rejected()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 25
                    rise: 200
            """);
        Assert.Contains(errors, e => e.Message.Contains("rise"));
    }

    [Fact]
    public void Duplicate_scoop_on_same_edge_is_rejected()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 25
                    rise: 30
                  - face: bottom
                    edge: left
                    inset: 40
                    rise: 35
            """);
        Assert.Contains(errors, e => e.Message.Contains("Duplicate"));
    }

    [Fact]
    public void Opposing_insets_exceeding_axis_length_are_rejected()
    {
        var errors = ParseErrors("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [100, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 60
                    rise: 20
                  - face: bottom
                    edge: right
                    inset: 60
                    rise: 20
            """);
        Assert.Contains(errors, e => e.Message.Contains("combined inset"));
    }

    // ── Phase 1 cutting ───────────────────────────────────────────────────────

    [Fact]
    public void Box_with_left_scoop_emits_a_scoop_panel()
    {
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 25
                    rise: 30
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        // 6 box faces + 1 scoop panel.
        Assert.Equal(7, pieces.Length);
        Assert.Contains(pieces, p => p.Id == "trough.scoop-bottom-left");
    }

    [Fact]
    public void Scoop_panel_is_slant_by_edge_axis()
    {
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 30
                    rise: 40
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings(kerf: 0));
        var scoop = pieces.Single(p => p.Id == "trough.scoop-bottom-left");
        var slant = Math.Sqrt(30.0 * 30.0 + 40.0 * 40.0);
        var w = scoop.BoundingBoxMax.X - scoop.BoundingBoxMin.X;
        var h = scoop.BoundingBoxMax.Y - scoop.BoundingBoxMin.Y;
        // For a Bottom face, FaceLayout panel size is (dims.X, dims.Z) — width × depth.
        // Left scoop's edge axis runs along the bottom face's V axis = dims.Z = 80.
        Assert.Equal(slant, w, 4);
        Assert.Equal(80, h, 4);
    }

    [Fact]
    public void Bottom_panel_is_narrowed_by_inset()
    {
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: [left, right]
                    inset: 25
                    rise: 30
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings(kerf: 0));
        var bottom = pieces.Single(p => p.Id == "trough.bottom");
        var w = bottom.BoundingBoxMax.X - bottom.BoundingBoxMin.X;
        Assert.Equal(150, w, 4); // 200 - 25 - 25
    }

    [Fact]
    public void Toes_meeting_throws_not_implemented()
    {
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [100, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 50
                    rise: 20
                  - face: bottom
                    edge: right
                    inset: 50
                    rise: 20
            """);

        Assert.Throws<NotImplementedException>(() =>
            new BoxPlanLib().GetCuttableShapes(plan, Settings()));
    }

    [Fact]
    public void Strip_thinner_than_thickness_throws_invalid_op()
    {
        // 100 - 49 - 49 = 2 mm strip, which is < t=3.
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [100, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 49
                    rise: 20
                  - face: bottom
                    edge: right
                    inset: 49
                    rise: 20
            """);

        Assert.Throws<InvalidOperationException>(() =>
            new BoxPlanLib().GetCuttableShapes(plan, Settings(t: 3)));
    }

    [Fact]
    public void Scoop_on_non_bottom_host_throws_not_implemented()
    {
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: top
                    edge: left
                    inset: 25
                    rise: 30
            """);

        Assert.Throws<NotImplementedException>(() =>
            new BoxPlanLib().GetCuttableShapes(plan, Settings()));
    }

    // ── Phase 2 joinery ───────────────────────────────────────────────────────

    [Fact]
    public void Scoop_panel_has_edge_notches()
    {
        // A plain rectangle has 4 vertices; a notched panel has more.
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 25
                    rise: 30
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings(kerf: 0));
        var scoop = pieces.Single(p => p.Id == "trough.scoop-bottom-left");
        Assert.True(scoop.Outline.Segments.Count > 4, "Scoop panel should have edge notches (more than 4 outline vertices)");
    }

    [Fact]
    public void Anchor_wall_has_heel_interior_cuts()
    {
        // Left face is the anchor wall for a Bottom+Left scoop; it should get interior
        // slot cuts at V=rise where the scoop heel tabs pass through.
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 25
                    rise: 30
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings(kerf: 0));
        var left = pieces.Single(p => p.Id == "trough.left");
        Assert.NotEmpty(left.InteriorCuts);
    }

    [Fact]
    public void Host_face_has_toe_interior_cuts()
    {
        // The narrowed bottom panel should have interior slot cuts at the toe position.
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 25
                    rise: 30
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings(kerf: 0));
        var bottom = pieces.Single(p => p.Id == "trough.bottom");
        Assert.NotEmpty(bottom.InteriorCuts);
    }

    [Fact]
    public void Cap_faces_have_oblique_interior_cuts()
    {
        // Front and Back are the cap faces for a Bottom+Left scoop; both should get
        // oblique slot cuts where the scoop panel passes through.
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 25
                    rise: 30
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings(kerf: 0));
        var front = pieces.Single(p => p.Id == "trough.front");
        var back = pieces.Single(p => p.Id == "trough.back");
        Assert.NotEmpty(front.InteriorCuts);
        Assert.NotEmpty(back.InteriorCuts);
    }

    [Fact]
    public void Two_scoops_produce_cuts_on_both_anchor_walls()
    {
        var plan = ParseOk("""
            shapes:
              - id: "trough"
                type: "box"
                dimensions: [200, 100, 80]
                scoops:
                  - face: bottom
                    edge: left
                    inset: 25
                    rise: 30
                  - face: bottom
                    edge: right
                    inset: 25
                    rise: 30
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings(kerf: 0));
        var left = pieces.Single(p => p.Id == "trough.left");
        var right = pieces.Single(p => p.Id == "trough.right");
        Assert.NotEmpty(left.InteriorCuts);
        Assert.NotEmpty(right.InteriorCuts);
    }
}
