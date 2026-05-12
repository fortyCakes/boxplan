using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class PanelShapeTests
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

    [Fact]
    public void Rectangular_panel_emits_single_shape()
    {
        var shapes = Cut("""
            shapes:
              - id: "plate"
                type: "panel"
                profile:
                  type: "prism"
                  points:
                    - [0, 0]
                    - [100, 0]
                    - [100, 50]
                    - [0, 50]
            """);

        Assert.Single(shapes);
        Assert.Equal("plate", shapes[0].Id);
    }

    [Fact]
    public void Panel_outline_bounding_box_matches_profile_extents()
    {
        var shapes = Cut("""
            shapes:
              - id: "plate"
                type: "panel"
                profile:
                  type: "prism"
                  points:
                    - [0, 0]
                    - [100, 0]
                    - [100, 50]
                    - [0, 50]
            """);

        var shape = shapes[0];
        Assert.Equal(0.0, shape.BoundingBoxMin.X, 6);
        Assert.Equal(0.0, shape.BoundingBoxMin.Y, 6);
        Assert.Equal(100.0, shape.BoundingBoxMax.X, 6);
        Assert.Equal(50.0, shape.BoundingBoxMax.Y, 6);
    }

    [Fact]
    public void Hexagon_panel_emits_single_shape()
    {
        var shapes = Cut("""
            shapes:
              - id: "hex-plate"
                type: "panel"
                profile:
                  type: "hexagon"
                  width: 100.0
            """);

        Assert.Single(shapes);
    }

    [Fact]
    public void Circle_panel_emits_single_shape()
    {
        var shapes = Cut("""
            shapes:
              - id: "disc"
                type: "panel"
                profile:
                  type: "circle"
                  diameter: 80.0
            """);

        Assert.Single(shapes);
    }

    [Fact]
    public void Panel_has_no_finger_joint_notches()
    {
        // A simple rectangular panel should have exactly 4 outline corners
        // (no finger-joint notches subtracted from any edge).
        var shapes = Cut("""
            shapes:
              - id: "plate"
                type: "panel"
                profile:
                  type: "prism"
                  points:
                    - [0, 0]
                    - [100, 0]
                    - [100, 50]
                    - [0, 50]
            """);

        var outline = shapes[0].Outline;
        var pointCount = 1 + outline.Segments.Count;
        // 4 unique corners (start + 3 line segments back to start would be 4 segments).
        // Accept up to 5 to account for potential closing duplicate vertex.
        Assert.True(pointCount <= 5,
            $"Expected rectangular panel to have ≤5 outline vertices, got {pointCount}");
        Assert.Empty(shapes[0].InteriorCuts);
    }

    [Fact]
    public void Panel_supports_cutout_feature_with_default_face()
    {
        var shapes = Cut("""
            shapes:
              - id: "plate"
                type: "panel"
                profile:
                  type: "prism"
                  points:
                    - [0, 0]
                    - [100, 0]
                    - [100, 50]
                    - [0, 50]
                features:
                  - type: "cutout"
                    shape: "circle"
                    diameter: 20
            """);

        Assert.Single(shapes);
        Assert.Single(shapes[0].InteriorCuts);
    }

    [Fact]
    public void Panel_supports_text_engraving()
    {
        var shapes = Cut("""
            shapes:
              - id: "label"
                type: "panel"
                profile:
                  type: "prism"
                  points:
                    - [0, 0]
                    - [80, 0]
                    - [80, 30]
                    - [0, 30]
                features:
                  - type: "engraving"
                    text: "Hello"
                    size: 10
            """);

        Assert.Single(shapes);
        Assert.Single(shapes[0].TextEngravings);
        Assert.Equal("Hello", shapes[0].TextEngravings[0].Text);
    }

    [Fact]
    public void Panel_open_face_emits_nothing()
    {
        var shapes = Cut("""
            shapes:
              - id: "ghost"
                type: "panel"
                profile:
                  type: "circle"
                  diameter: 50
                faces:
                  - name: "front"
                    type: "open"
            """);

        Assert.Empty(shapes);
    }

    [Fact]
    public void Panel_without_profile_fails()
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan("""
            shapes:
              - id: "p"
                type: "panel"
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("profile"));
    }

    [Fact]
    public void Two_edge_touching_panels_merge_into_single_outline()
    {
        // Panel 1: 68x98 at (0,0,0). Panel 2: 3x3 at (68,0,95). They share a
        // 3mm edge at X=68, Z∈[95,98] — should merge into one stepped outline.
        var shapes = Cut("""
            shapes:
              - id: "main"
                type: "panel"
                location: [0, 0, 0]
                profile:
                  type: "rectangle"
                  width: 68
                  height: 98
              - id: "tab"
                type: "panel"
                location: [68, 0, 95]
                profile:
                  type: "rectangle"
                  width: 3
                  height: 3
            """);

        Assert.Single(shapes);
        Assert.Contains("merged-panel", shapes[0].Id);
        // Merged outline has 8 vertices (rectangle + 1 tab).
        var vertexCount = 1 + shapes[0].Outline.Segments.Count;
        Assert.True(vertexCount >= 7 && vertexCount <= 9,
            $"Expected merged outline to have ~8 vertices, got {vertexCount}");
    }

    [Fact]
    public void Corner_touching_panels_do_not_merge()
    {
        // Panel 1: 68x98 at (0,0,0). Panel 2: 3x3 at (68,0,98) — shares only
        // the single corner (68,98) with panel 1, no real edge overlap.
        var shapes = Cut("""
            shapes:
              - id: "main"
                type: "panel"
                location: [0, 0, 0]
                profile:
                  type: "rectangle"
                  width: 68
                  height: 98
              - id: "corner"
                type: "panel"
                location: [68, 0, 98]
                profile:
                  type: "rectangle"
                  width: 3
                  height: 3
            """);

        Assert.Equal(2, shapes.Length);
        Assert.DoesNotContain(shapes, s => s.Id.Contains("merged-panel"));
    }

    [Fact]
    public void Panels_in_different_y_planes_do_not_merge()
    {
        // Same X-Z coords but different Y planes — must remain disjoint.
        var shapes = Cut("""
            shapes:
              - id: "front"
                type: "panel"
                location: [0, 0, 0]
                profile:
                  type: "rectangle"
                  width: 50
                  height: 50
              - id: "back"
                type: "panel"
                location: [50, 10, 0]
                profile:
                  type: "rectangle"
                  width: 50
                  height: 50
            """);

        Assert.Equal(2, shapes.Length);
    }

    [Fact]
    public void Disjoint_panel_skips_merging()
    {
        var shapes = Cut("""
            shapes:
              - id: "main"
                type: "panel"
                location: [0, 0, 0]
                profile:
                  type: "rectangle"
                  width: 50
                  height: 50
              - id: "loner"
                type: "panel"
                location: [50, 0, 0]
                disjoint: true
                profile:
                  type: "rectangle"
                  width: 10
                  height: 10
            """);

        Assert.Equal(2, shapes.Length);
        Assert.DoesNotContain(shapes, s => s.Id.Contains("merged-panel"));
    }

    [Fact]
    public void Merged_panel_carries_member_features()
    {
        var shapes = Cut("""
            shapes:
              - id: "main"
                type: "panel"
                location: [0, 0, 0]
                profile:
                  type: "rectangle"
                  width: 68
                  height: 98
                features:
                  - type: "cutout"
                    shape: "circle"
                    diameter: 10
              - id: "tab"
                type: "panel"
                location: [68, 0, 95]
                profile:
                  type: "rectangle"
                  width: 3
                  height: 3
            """);

        Assert.Single(shapes);
        Assert.Single(shapes[0].InteriorCuts);
    }

    [Fact]
    public void Panel_rejects_non_front_face()
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan("""
            shapes:
              - id: "p"
                type: "panel"
                profile:
                  type: "circle"
                  diameter: 30
                faces:
                  - name: "back"
                    type: "open"
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("front"));
    }
}
