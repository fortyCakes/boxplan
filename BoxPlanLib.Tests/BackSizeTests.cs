using BoxPlanLib;
using BoxPlanLib.Cutting;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests;

public class BackSizeTests
{
    private static BoxPlanSettings Settings(double t = 3.0, double s = 20.0) => new()
    {
        Kerf = 0.0,
        MaterialThickness = t,
        FingerJointSize = s,
        SheetWidth = 1000,
        SheetHeight = 1000,
    };

    private static BoxPlan ParseOk(string yaml, BoxPlanSettings? settings = null)
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan(yaml, settings ?? Settings());
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.Value!;
    }

    private static BoxPlanCuttableShape[] Cut(string yaml, BoxPlanSettings? settings = null)
    {
        var lib = new BoxPlanLib();
        var plan = ParseOk(yaml, settings);
        return lib.GetCuttableShapes(plan, settings ?? Settings());
    }

    // ── Resolver: parsing and defaults ────────────────────────────────────────

    [Fact]
    public void BackSize_defaults_to_one_when_omitted()
    {
        var plan = ParseOk("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 50.0
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["hex"]);
        Assert.Equal(1.0, shape.BackScale);
    }

    [Fact]
    public void BackSize_is_parsed_when_supplied()
    {
        var plan = ParseOk("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 50.0
                back-size: 0.7
            """);

        var shape = Assert.IsType<PrismShape>(plan.ShapesById["hex"]);
        Assert.Equal(0.7, shape.BackScale, precision: 9);
    }

    [Fact]
    public void Negative_or_zero_back_size_is_rejected()
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 50.0
                back-size: 0
            """, Settings());
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("back-size"));
    }

    // ── Geometry: centroid + scaling ──────────────────────────────────────────

    [Fact]
    public void Profile_centroid_of_axis_aligned_square_is_its_center()
    {
        var profile = new PrismProfile
        {
            StartPoint = new Vec2(0, 0),
            Segments = new ProfileSegment[]
            {
                new ProfileSegment.Line(new Vec2(10, 0)),
                new ProfileSegment.Line(new Vec2(10, 10)),
                new ProfileSegment.Line(new Vec2(0, 10)),
                new ProfileSegment.Line(new Vec2(0, 0)),
            },
        };
        var c = PrismGeometry.ComputeProfileCentroid(profile);
        Assert.Equal(5.0, c.X, precision: 6);
        Assert.Equal(5.0, c.Y, precision: 6);
    }

    // ── 3D dihedrals collapse to 2D when scale == 1 ──────────────────────────

    [Fact]
    public void Dihedral3D_matches_2D_interior_angle_when_scale_is_one()
    {
        // Three consecutive vertices of a regular hexagon → interior angle = 120°.
        var a = new Vec2(0, 0);
        var b = new Vec2(10, 0);
        var c = new Vec2(15, 10 * Math.Sqrt(3) / 2);
        var centroid = new Vec2(5, 5 * Math.Sqrt(3) / 2);
        var angle = PrismGeometry.Dihedral3D(a, b, c, centroid, backScale: 1.0, depth: 40);
        Assert.Equal(120.0, angle, precision: 4);
    }

    [Fact]
    public void CapToLateralAngle_is_90_when_scale_is_one()
    {
        var a = new Vec2(0, 0);
        var b = new Vec2(10, 0);
        var centroid = new Vec2(5, 5);
        Assert.Equal(90.0,
            PrismGeometry.CapToLateralAngle(a, b, centroid, 1.0, 40, isFront: true),
            precision: 4);
        Assert.Equal(90.0,
            PrismGeometry.CapToLateralAngle(a, b, centroid, 1.0, 40, isFront: false),
            precision: 4);
    }

    [Fact]
    public void CapToLateralAngle_is_acute_at_front_and_obtuse_at_back_when_scale_lt_one()
    {
        // Hexagon bottom edge with back-scale < 1: the lateral wall leans inward,
        // so the cap-to-wall interior dihedral is acute at the front cap and
        // obtuse at the back cap.
        var a = new Vec2(0, 0);
        var b = new Vec2(10, 0);
        var centroid = new Vec2(5, 5);
        var front = PrismGeometry.CapToLateralAngle(a, b, centroid, 0.5, 40, isFront: true);
        var back = PrismGeometry.CapToLateralAngle(a, b, centroid, 0.5, 40, isFront: false);
        Assert.True(front < 90.0, $"Front angle should be < 90°, got {front}");
        Assert.True(back > 90.0, $"Back angle should be > 90°, got {back}");
        // Symmetric about 90°.
        Assert.Equal(180.0, front + back, precision: 4);
    }

    // ── Trapezoid layout ──────────────────────────────────────────────────────

    [Fact]
    public void TrapezoidLayout_collapses_to_rectangle_when_scale_is_one()
    {
        var a = new Vec2(0, 0);
        var b = new Vec2(10, 0);
        var centroid = new Vec2(5, 5);
        var (poly, frontLen, backLen, leftSlant, rightSlant) =
            PrismGeometry.TrapezoidLayout(a, b, centroid, 1.0, 40);
        Assert.Equal(10.0, frontLen, precision: 6);
        Assert.Equal(10.0, backLen, precision: 6);
        Assert.Equal(40.0, leftSlant, precision: 6);
        Assert.Equal(40.0, rightSlant, precision: 6);
        // Back at bottom: [0]=(0,0), [1]=(backLen,0)=(10,0); front at top.
        Assert.Equal(new Vec2(0, 0), poly[0]);
        Assert.Equal(new Vec2(10, 0), poly[1]);
        Assert.Equal(10.0, poly[2].X, precision: 6);
        Assert.Equal(40.0, poly[2].Y, precision: 6);
        Assert.Equal(0.0, poly[3].X, precision: 6);
        Assert.Equal(40.0, poly[3].Y, precision: 6);
    }

    [Fact]
    public void TrapezoidLayout_produces_smaller_bottom_when_back_scale_lt_one()
    {
        // Symmetric edge: endpoints equidistant from centroid (regular polygon).
        var a = new Vec2(0, 0);
        var b = new Vec2(10, 0);
        var centroid = new Vec2(5, 8);
        var (poly, frontLen, backLen, leftSlant, rightSlant) =
            PrismGeometry.TrapezoidLayout(a, b, centroid, 0.5, 40);
        Assert.Equal(10.0, frontLen, precision: 6);
        Assert.Equal(5.0, backLen, precision: 6);
        Assert.Equal(leftSlant, rightSlant, precision: 6); // isosceles
        // Back edge (bottom) length 5, front edge (top) length 10, centred above.
        // Front-left at x = -(frontLen - backLen)/2 = -2.5, front-right at +7.5.
        Assert.Equal(-2.5, poly[3].X, precision: 6);
        Assert.Equal(7.5, poly[2].X, precision: 6);
        Assert.Equal(poly[2].Y, poly[3].Y, precision: 9);
    }

    // ── Cutting pipeline: shape inventory ─────────────────────────────────────

    [Fact]
    public void Frustum_hexagon_produces_one_front_six_lateral_one_back()
    {
        var shapes = Cut("""
            shapes:
              - id: "frustum"
                type: "hexagon"
                width: 100.0
                depth: 50.0
                back-size: 0.6
            """);
        Assert.Contains(shapes, sh => sh.Id == "frustum.front");
        Assert.Contains(shapes, sh => sh.Id == "frustum.back");
        for (var i = 0; i < 6; i++)
            Assert.Contains(shapes, sh => sh.Id == $"frustum.lateral-{i}");
        Assert.Equal(8, shapes.Length);
    }

    [Fact]
    public void Frustum_back_cap_has_smaller_bounding_box_than_front_cap()
    {
        var shapes = Cut("""
            shapes:
              - id: "frustum"
                type: "hexagon"
                width: 120.0
                depth: 50.0
                back-size: 0.5
            """);
        var front = shapes.Single(sh => sh.Id == "frustum.front");
        var back = shapes.Single(sh => sh.Id == "frustum.back");
        var frontW = front.BoundingBoxMax.X - front.BoundingBoxMin.X;
        var backW = back.BoundingBoxMax.X - back.BoundingBoxMin.X;
        Assert.True(backW < frontW, $"back width {backW} should be smaller than front width {frontW}");
        // Roughly half the size (with some allowance for finger-joint reservations).
        Assert.InRange(backW / frontW, 0.45, 0.55);
    }

    [Fact]
    public void Frustum_lateral_panel_is_a_trapezoid_with_correct_dimensions()
    {
        var shapes = Cut("""
            shapes:
              - id: "frustum"
                type: "hexagon"
                width: 100.0
                depth: 50.0
                back-size: 0.5
                faces:
                  - name: "front"
                    type: "open"
                  - name: "back"
                    type: "open"
                lateral-faces:
                  - index: 1
                    type: "open"
                  - index: 5
                    type: "open"
            """);
        // All caps & two lateral faces are open: lateral 0 has no neighbours' finger
        // joints on the slant edges, so the panel polygon is a clean trapezoid.
        var panel = shapes.Single(sh => sh.Id == "frustum.lateral-0");
        var pts = new List<Vec2> { panel.Outline.Start };
        pts.AddRange(panel.Outline.Segments.OfType<LineSegment>().Select(l => l.To));
        // Outline polygon is closed; remove duplicate closing vertex if present.
        if (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
        Assert.Equal(4, pts.Count);
        // Verify edge lengths regardless of which vertex Clipper picks as polygon start.
        // Regular hexagon with width 100 has edge length 100/√3 ≈ 57.735.
        var lengths = new double[4];
        for (var i = 0; i < 4; i++)
            lengths[i] = Vec2Distance(pts[i], pts[(i + 1) % 4]);
        Array.Sort(lengths);
        var expectedFront = 100.0 / Math.Sqrt(3);
        // Two parallel edges: back (short) and front (long); two slants between.
        Assert.Equal(expectedFront * 0.5, lengths[0], precision: 3); // shortest = back
        Assert.Equal(expectedFront, lengths[3], precision: 3);       // longest = front
    }

    private static double Vec2Distance(Vec2 a, Vec2 b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ── Regression: explicit back-size: 1.0 matches omitted ───────────────────

    [Fact]
    public void Explicit_back_size_one_matches_omitted_back_size()
    {
        var withoutBs = Cut("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 50.0
            """);
        var withBs = Cut("""
            shapes:
              - id: "hex"
                type: "hexagon"
                width: 100.0
                depth: 50.0
                back-size: 1.0
            """);

        Assert.Equal(withoutBs.Length, withBs.Length);
        for (var i = 0; i < withoutBs.Length; i++)
        {
            Assert.Equal(withoutBs[i].Id, withBs[i].Id);
            Assert.Equal(withoutBs[i].BoundingBoxMin.X, withBs[i].BoundingBoxMin.X, precision: 6);
            Assert.Equal(withoutBs[i].BoundingBoxMin.Y, withBs[i].BoundingBoxMin.Y, precision: 6);
            Assert.Equal(withoutBs[i].BoundingBoxMax.X, withBs[i].BoundingBoxMax.X, precision: 6);
            Assert.Equal(withoutBs[i].BoundingBoxMax.Y, withBs[i].BoundingBoxMax.Y, precision: 6);
        }
    }

    [Fact]
    public void Frustum_slant_edges_carry_finger_joint_slots()
    {
        // Regression: Clipper2 was returning slant-edge slots as separate negative-area
        // holes (because their outer edge sat exactly on the polygon boundary) instead
        // of carving them into the panel outline. PolygonPanelShapeBuilder's largest-area
        // selector then discarded the holes, leaving the slants visually un-slotted.
        var shapes = Cut("""
            shapes:
              - id: "frustum"
                type: "hexagon"
                width: 150.0
                depth: 30.0
                back-size: 0.7
            """);
        var panel = shapes.Single(sh => sh.Id == "frustum.lateral-0");
        var pts = new List<Vec2> { panel.Outline.Start };
        pts.AddRange(panel.Outline.Segments.OfType<LineSegment>().Select(l => l.To));
        if (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
        // A bare trapezoid is 4 vertices. Every finger-joint slot adds 4 vertices.
        // With back closed (≥3 bottom slots) and both slants notched (≥3 each), expect
        // well over 20 vertices in the outline.
        Assert.True(pts.Count > 20,
            $"Lateral panel outline has only {pts.Count} vertices — slant slots likely dropped");
    }

    [Fact]
    public void Prism_type_with_explicit_polygon_also_supports_back_size()
    {
        var shapes = Cut("""
            shapes:
              - id: "tri-prism"
                type: "prism"
                depth: 40.0
                back-size: 0.5
                points:
                  - [0, 0]
                  - [60, 0]
                  - [30, 50]
            """);
        // Three lateral faces, plus front and back caps.
        Assert.Equal(5, shapes.Length);
        var front = shapes.Single(sh => sh.Id == "tri-prism.front");
        var back = shapes.Single(sh => sh.Id == "tri-prism.back");
        var frontArea = (front.BoundingBoxMax.X - front.BoundingBoxMin.X)
                      * (front.BoundingBoxMax.Y - front.BoundingBoxMin.Y);
        var backArea = (back.BoundingBoxMax.X - back.BoundingBoxMin.X)
                     * (back.BoundingBoxMax.Y - back.BoundingBoxMin.Y);
        // Back cap area should be ~0.5² of front cap area.
        Assert.InRange(backArea / frontArea, 0.20, 0.30);
    }
}
