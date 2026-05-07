using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class MergedShapeTests
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
            else throw new InvalidOperationException("Expected polyline outline.");
        }
        return pts;
    }

    [Fact]
    public void Two_disjoint_cubes_remain_independent()
    {
        var plan = ParseOk("""
            shapes:
              - id: "a"
                type: "box"
                dimensions: [30.0, 30.0, 30.0]
                location: [0.0, 0.0, 0.0]
              - id: "b"
                type: "box"
                dimensions: [30.0, 30.0, 30.0]
                location: [100.0, 0.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings());

        var ids = pieces.Select(p => p.Id).ToHashSet();
        // 6 per cube, totally separate.
        Assert.Equal(12, pieces.Length);
        Assert.Contains("a.top", ids);
        Assert.Contains("b.top", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("merged-"));
    }

    [Fact]
    public void Two_stacked_equal_cubes_emit_six_merged_faces()
    {
        var plan = ParseOk("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [30.0, 30.0, 30.0]
                location: [0.0, 0.0, 0.0]
              - id: "upper"
                type: "box"
                dimensions: [30.0, 30.0, 30.0]
                location: [0.0, 30.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings());

        // Two boxes touching on their y=30 plane fully cancel that contact area.
        // The merged solid is one 30 x 60 x 30 prism: 6 outward face panels.
        Assert.Equal(6, pieces.Length);
        Assert.All(pieces, p => Assert.StartsWith("merged-0.", p.Id));
    }

    [Fact]
    public void Two_side_by_side_cubes_emit_six_merged_faces()
    {
        var plan = ParseOk("""
            shapes:
              - id: "left"
                type: "box"
                dimensions: [30.0, 30.0, 30.0]
                location: [0.0, 0.0, 0.0]
              - id: "right"
                type: "box"
                dimensions: [30.0, 30.0, 30.0]
                location: [30.0, 0.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings());

        Assert.Equal(6, pieces.Length);
        Assert.All(pieces, p => Assert.StartsWith("merged-0.", p.Id));
    }

    [Fact]
    public void Staircase_emits_expected_face_count_and_outline_shape()
    {
        var plan = ParseOk("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [30.0, 10.0, 10.0]
                location: [0.0, 0.0, 0.0]
              - id: "middle"
                type: "box"
                dimensions: [20.0, 10.0, 10.0]
                location: [0.0, 10.0, 0.0]
              - id: "upper"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                location: [0.0, 20.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings(kerf: 0.0, t: 3.0, s: 5.0));

        // Expected merged faces:
        //   1 bottom (30 x 10)
        //   3 top treads at y=10, 20, 30 (each 10 x 10)
        //   1 front (staircase polygon at z=0)
        //   1 back  (staircase polygon at z=10)
        //   1 left  (10 x 30 rectangle at x=0)
        //   3 right faces (10 x 10 each at x=10, 20, 30)
        Assert.Equal(10, pieces.Length);

        var ids = pieces.Select(p => p.Id).ToList();
        Assert.Single(ids, id => id.StartsWith("merged-0.y-@"));
        Assert.Equal(3, ids.Count(id => id.StartsWith("merged-0.y+@")));
        Assert.Single(ids, id => id.StartsWith("merged-0.z-@"));
        Assert.Single(ids, id => id.StartsWith("merged-0.z+@"));
        Assert.Single(ids, id => id.StartsWith("merged-0.x-@"));
        Assert.Equal(3, ids.Count(id => id.StartsWith("merged-0.x+@")));
    }

    [Fact]
    public void Staircase_front_face_has_reentrant_polygon_with_eight_distinct_vertices()
    {
        var plan = ParseOk("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [30.0, 10.0, 10.0]
                location: [0.0, 0.0, 0.0]
              - id: "middle"
                type: "box"
                dimensions: [20.0, 10.0, 10.0]
                location: [0.0, 10.0, 0.0]
              - id: "upper"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                location: [0.0, 20.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        // Set s very large so finger jointing degenerates to the smooth edge case
        // and we can inspect the raw staircase polygon corners.
        var pieces = lib.GetCuttableShapes(plan, Settings(kerf: 0.0, t: 3.0, s: 1000.0));

        // The two staircase polygons (front, back) have 8 90-degree corners.
        // Pick the front (-Z) panel.
        var front = pieces.Single(p => p.Id.StartsWith("merged-0.z-@"));
        var pts = Points(front);

        // Closed polyline: first point repeats at end. Distinct vertex positions:
        var distinct = pts.GroupBy(p => (Math.Round(p.X, 3), Math.Round(p.Y, 3))).Count();
        // The staircase shape has 8 corner positions when joints/notches are
        // suppressed by an oversized finger size and zero kerf.
        Assert.True(distinct >= 8, $"expected at least 8 distinct corners, got {distinct}");
    }

    [Fact]
    public void Bottom_face_of_two_stacked_cubes_matches_box_footprint()
    {
        var plan = ParseOk("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [30.0, 30.0, 30.0]
                location: [0.0, 0.0, 0.0]
              - id: "upper"
                type: "box"
                dimensions: [30.0, 30.0, 30.0]
                location: [0.0, 30.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings(kerf: 0.0, t: 3.0, s: 1000.0));

        var bottom = pieces.Single(p => p.Id.StartsWith("merged-0.y-@"));
        Assert.Equal(30, bottom.BoundingBoxMax.X, 6);
        Assert.Equal(30, bottom.BoundingBoxMax.Y, 6);
    }

    [Fact]
    public void Side_face_of_two_stacked_cubes_spans_doubled_dimension()
    {
        var plan = ParseOk("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                location: [0.0, 0.0, 0.0]
              - id: "upper"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                location: [0.0, 100.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings(kerf: 0.0, t: 3.0, s: 20.0));

        // The +X (right) face of the merged 100x200x100 solid: with proper
        // finger-jointing the panel outline reaches the full 200mm extent on
        // its longer axis (the merged Y dimension).
        var rightFace = pieces.Single(p => p.Id.StartsWith("merged-0.x+@"));
        var width = rightFace.BoundingBoxMax.X - rightFace.BoundingBoxMin.X;
        var height = rightFace.BoundingBoxMax.Y - rightFace.BoundingBoxMin.Y;
        var longer = Math.Max(width, height);
        var shorter = Math.Min(width, height);
        Assert.Equal(200, longer, 0);
        Assert.Equal(100, shorter, 0);
    }

    [Fact]
    public void Staircase_front_left_edge_has_continuous_finger_pattern()
    {
        // Three stacked boxes meet on x=0 across the full 30mm height of the
        // front face. Clipper preserves collinear vertices in unioned polygons
        // by default, so without merging these the left edge would be three
        // 10mm sub-segments — each with t-reservation eating most of the
        // edge, producing a degenerate single-block joint with no notches on
        // the front face. With collinear-merge we expect a continuous
        // multi-block finger pattern: the front (lower-priority) face must
        // surface alternating inward jogs along its left edge.
        var plan = ParseOk("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [30.0, 10.0, 10.0]
                location: [0.0, 0.0, 0.0]
              - id: "middle"
                type: "box"
                dimensions: [20.0, 10.0, 10.0]
                location: [0.0, 10.0, 0.0]
              - id: "upper"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                location: [0.0, 20.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings(kerf: 0.0, t: 3.0, s: 5.0));
        var front = pieces.Single(p => p.Id.StartsWith("merged-0.z-@"));
        var pts = Points(front);

        // Inward-jog vertices along the left edge (x=0): with proper merging
        // the multi-block finger pattern produces at least 4 such vertices
        // (a pair per inward notch). A degenerate single-segment-per-box
        // path would have zero — the edge would run straight from (0,0) up
        // to (0,30).
        var leftJogVertices = pts.Count(p => p.X > 1e-6 && p.X < 5 && p.Y > 5 && p.Y < 25);
        Assert.True(leftJogVertices >= 4,
            $"expected merged staircase front-face left edge to carry ≥4 inward-jog vertices, got {leftJogVertices}");
    }

    [Fact]
    public void Staircase_front_finger_notch_extends_to_reflex_corner()
    {
        // At the staircase's reflex 2D corners (20,10) and (10,20) on the
        // front face, no corner cube exists for the perpendicular faces to
        // share. The previous bug reserved t at every shared-segment end
        // unconditionally, leaving an unfinished 3mm gap of unjointed edge
        // adjacent to each reflex corner. The finger pattern should run all
        // the way to the corner.
        var plan = ParseOk("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [30.0, 10.0, 10.0]
                location: [0.0, 0.0, 0.0]
              - id: "middle"
                type: "box"
                dimensions: [20.0, 10.0, 10.0]
                location: [0.0, 10.0, 0.0]
              - id: "upper"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                location: [0.0, 20.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings(kerf: 0.0, t: 3.0, s: 5.0));
        var front = pieces.Single(p => p.Id.StartsWith("merged-0.z-@"));
        var pts = Points(front);

        // Outline should include a vertex at exactly the reflex 2D corner
        // x=20, y=7 (i.e. the inner corner of the notch reaching from
        // x=27 down to x=20 on the y=10 edge). With the bug the inner
        // corner stopped at x=23, leaving 3mm of un-notched edge.
        Assert.Contains(pts, p => Math.Abs(p.X - 20) < 1e-6 && Math.Abs(p.Y - 7) < 1e-6);
        // Same on the y=20 edge between middle and upper steps: notch
        // reaches the reflex corner at (10, 20).
        Assert.Contains(pts, p => Math.Abs(p.X - 10) < 1e-6 && Math.Abs(p.Y - 17) < 1e-6);
    }

    [Fact]
    public void Staircase_tread_has_inner_corner_notches_where_front_back_face_is_reflex()
    {
        // At the inner step corners of the staircase the front and back faces
        // have REFLEX vertices. Their material wraps around the corner and
        // physically fills the t×t×t cube that sits at that vertex. Both
        // the tread and the adjacent step-side panel must therefore subtract
        // a t×t notch from that corner, regardless of face-priority order.
        //
        // Staircase geometry (t=3, s=1000 suppresses finger jointing):
        //   lower  30×10×10 at y=0
        //   middle 20×10×10 at y=10
        //   upper  10×10×10 at y=20
        //
        // The first tread sits at y=10 (x:[20,30], z:[0,10]) and the second
        // at y=20 (x:[10,20], z:[0,10]). Each has one inner edge (at x=20
        // and x=10 respectively) with two corners where a reflex face (front
        // or back) neighbour fills the corner cube. Without the fix those
        // treads are emitted as plain 4-vertex rectangles. With the fix each
        // inner corner gains a t×t L-cut, adding 2 extra vertices per corner
        // (4 extra total) → 8 vertices per tread.
        //
        // The upper tread (x:[0,10], y=30) has no inner step corner; the
        // front and back faces are convex at all four of its corners, so the
        // standard priority rule applies and Top owns — it stays a plain
        // 4-vertex rectangle.
        var plan = ParseOk("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [30.0, 10.0, 10.0]
                location: [0.0, 0.0, 0.0]
              - id: "middle"
                type: "box"
                dimensions: [20.0, 10.0, 10.0]
                location: [0.0, 10.0, 0.0]
              - id: "upper"
                type: "box"
                dimensions: [10.0, 10.0, 10.0]
                location: [0.0, 20.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        // Large s suppresses finger joints, leaving only corner notches visible.
        var pieces = lib.GetCuttableShapes(plan, Settings(kerf: 0.0, t: 3.0, s: 1000.0));

        var treads = pieces.Where(p => p.Id.StartsWith("merged-0.y+@")).ToList();
        Assert.Equal(3, treads.Count);

        var uniqueVertexCounts = treads
            .Select(t => Points(t).GroupBy(p => (Math.Round(p.X, 3), Math.Round(p.Y, 3))).Count())
            .OrderBy(c => c)
            .ToList();

        // Exactly one tread is a plain rectangle (4 vertices) — the uppermost,
        // whose front/back neighbours are convex at all its corners.
        // The other two each carry two t×t inner-corner notches (8 vertices).
        Assert.Equal([4, 8, 8], uniqueVertexCounts);
    }

    [Fact]
    public void Mergeable_check_skips_box_with_open_face()
    {
        // Two stacked boxes but the lower has an open top face.
        // Without merge eligibility, both boxes go through the per-box pipeline.
        var plan = ParseOk("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [30.0, 30.0, 30.0]
                location: [0.0, 0.0, 0.0]
                faces:
                  - name: "top"
                    type: "open"
              - id: "upper"
                type: "box"
                dimensions: [30.0, 30.0, 30.0]
                location: [0.0, 30.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings());
        var ids = pieces.Select(p => p.Id).ToHashSet();
        Assert.DoesNotContain(ids, id => id.StartsWith("merged-"));
        Assert.Contains("lower.front", ids);
        Assert.Contains("upper.bottom", ids);
    }
}
