using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class SplitCutTests
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

    private static List<Vec2> ToPoints(CuttablePath path)
    {
        var points = new List<Vec2> { path.Start };
        points.AddRange(path.Segments.OfType<LineSegment>().Select(s => s.To));
        return points;
    }

    [Fact]
    public void Split_cut_keeps_face_as_single_piece_and_adds_internal_cut()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 80.0, 60.0]
                features:
                  - face: "front"
                    type: "split-cut"
                    height: 25.0
                  - face: "back"
                    type: "split-cut"
                    height: 25.0
                  - face: "left"
                    type: "split-cut"
                    height: 25.0
                  - face: "right"
                    type: "split-cut"
                    height: 25.0
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());

        Assert.Equal(6, pieces.Length);

        foreach (var face in new[] { "front", "back", "left", "right" })
        {
            var piece = pieces.Single(p => p.Id == $"box.{face}");
            var cut = Assert.Single(piece.InteriorCuts);
            Assert.False(cut.Closed);
            Assert.Single(cut.Segments);
            Assert.IsType<LineSegment>(cut.Segments[0]);
        }
    }

    [Fact]
    public void Split_cut_supports_svg_curve_and_emits_polyline_cut()
    {
        var curve = "M 0 0 C 25 10 75 -10 100 0";
        var plan = ParseOk($"""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 80.0, 60.0]
                features:
                  - face: "front"
                    type: "split-cut"
                    height: 25.0
                    amplitude: 10.0
                    curve:
                      type: "svg-path"
                      samples: 16
                      svg-path-data: "{curve}"
                  - face: "back"
                    type: "split-cut"
                    height: 25.0
                    amplitude: 10.0
                    curve:
                      type: "svg-path"
                      samples: 16
                      svg-path-data: "{curve}"
                  - face: "left"
                    type: "split-cut"
                    height: 25.0
                    amplitude: 10.0
                    curve:
                      type: "svg-path"
                      samples: 16
                      svg-path-data: "{curve}"
                  - face: "right"
                    type: "split-cut"
                    height: 25.0
                    amplitude: 10.0
                    curve:
                      type: "svg-path"
                      samples: 16
                      svg-path-data: "{curve}"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");
        var cut = Assert.Single(front.InteriorCuts);

        Assert.False(cut.Closed);
        Assert.True(cut.Segments.Count > 2);

        var points = new List<Vec2> { cut.Start };
        points.AddRange(cut.Segments.OfType<LineSegment>().Select(s => s.To));
        var ySpan = points.Max(p => p.Y) - points.Min(p => p.Y);
        Assert.True(ySpan > 0.1, $"Expected curved split cut with non-zero Y span, got {ySpan}");
    }

    [Fact]
    public void Split_cut_autoscales_to_each_side_width()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 80.0, 60.0]
                features:
                  - face: "front"
                    type: "split-cut"
                    height: 25.0
                    validate-separation: false
                  - face: "back"
                    type: "split-cut"
                    height: 25.0
                    validate-separation: false
                  - face: "left"
                    type: "split-cut"
                    height: 25.0
                    validate-separation: false
                  - face: "right"
                    type: "split-cut"
                    height: 25.0
                    validate-separation: false
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());

        var front = pieces.Single(p => p.Id == "box.front");
        var back = pieces.Single(p => p.Id == "box.back");
        var left = pieces.Single(p => p.Id == "box.left");
        var right = pieces.Single(p => p.Id == "box.right");

        var frontCut = Assert.Single(front.InteriorCuts);
        var backCut = Assert.Single(back.InteriorCuts);
        var leftCut = Assert.Single(left.InteriorCuts);
        var rightCut = Assert.Single(right.InteriorCuts);

        var frontEnd = Assert.IsType<LineSegment>(Assert.Single(frontCut.Segments)).To;
        var backEnd = Assert.IsType<LineSegment>(Assert.Single(backCut.Segments)).To;
        var leftEnd = Assert.IsType<LineSegment>(Assert.Single(leftCut.Segments)).To;
        var rightEnd = Assert.IsType<LineSegment>(Assert.Single(rightCut.Segments)).To;

        var frontWidth = Math.Abs(frontEnd.X - frontCut.Start.X);
        var backWidth = Math.Abs(backEnd.X - backCut.Start.X);
        var leftWidth = Math.Abs(leftEnd.X - leftCut.Start.X);
        var rightWidth = Math.Abs(rightEnd.X - rightCut.Start.X);

        Assert.True(Math.Abs(frontWidth - backWidth) < 1e-6, $"Expected front/back split widths to match, got {frontWidth} vs {backWidth}");
        Assert.True(Math.Abs(leftWidth - rightWidth) < 1e-6, $"Expected left/right split widths to match, got {leftWidth} vs {rightWidth}");
        Assert.True(Math.Abs(frontWidth - leftWidth) > 1.0, $"Expected split widths to differ across side sets, got {frontWidth} vs {leftWidth}");
    }

    [Fact]
    public void Split_cut_levels_curve_ends_by_rotation_by_default()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 80.0, 60.0]
                faces:
                  - name: "left"
                    type: "open"
                  - name: "right"
                    type: "open"
                features:
                  - face: "front"
                    type: "split-cut"
                    height: 25.0
                    amplitude: 10.0
                    validate-separation: false
                    curve:
                      type: "polyline"
                      points:
                        - [0.0, 0.0]
                        - [50.0, 30.0]
                        - [100.0, 10.0]
            """);

        var front = new BoxPlanLib().GetCuttableShapes(plan, Settings()).Single(p => p.Id == "box.front");
        var cut = Assert.Single(front.InteriorCuts);
        var points = ToPoints(cut);

        var delta = Math.Abs(points[0].Y - points[^1].Y);
        Assert.True(delta < 1e-6, $"Expected leveled split-cut endpoints, got delta {delta}");
    }

    [Fact]
    public void Split_cut_can_disable_curve_end_leveling()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 80.0, 60.0]
                faces:
                  - name: "left"
                    type: "open"
                  - name: "right"
                    type: "open"
                features:
                  - face: "front"
                    type: "split-cut"
                    height: 25.0
                    amplitude: 10.0
                    validate-separation: false
                    curve:
                      type: "polyline"
                      level-ends: false
                      points:
                        - [0.0, 0.0]
                        - [50.0, 30.0]
                        - [100.0, 10.0]
            """);

        var front = new BoxPlanLib().GetCuttableShapes(plan, Settings()).Single(p => p.Id == "box.front");
        var cut = Assert.Single(front.InteriorCuts);
        var points = ToPoints(cut);

        var delta = Math.Abs(points[0].Y - points[^1].Y);
        Assert.True(delta > 0.1, $"Expected non-leveled split-cut endpoints, got delta {delta}");
    }

    [Fact]
    public void Split_cut_on_tabbed_side_edges_uses_inner_width_and_snaps_endpoints()
    {
        const double requestedHeight = 26.37;

        var tabbedPlan = ParseOk($"""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 80.0, 60.0]
                features:
                  - face: "front"
                    type: "split-cut"
                    height: {requestedHeight}
                    validate-separation: false
            """);

        var smoothPlan = ParseOk($"""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 80.0, 60.0]
                faces:
                  - name: "left"
                    type: "open"
                  - name: "right"
                    type: "open"
                features:
                  - face: "front"
                    type: "split-cut"
                    height: {requestedHeight}
                    validate-separation: false
            """);

        var tabbedFront = new BoxPlanLib().GetCuttableShapes(tabbedPlan, Settings()).Single(p => p.Id == "box.front");
        var smoothFront = new BoxPlanLib().GetCuttableShapes(smoothPlan, Settings()).Single(p => p.Id == "box.front");

        var tabbedCut = Assert.Single(tabbedFront.InteriorCuts);
        var smoothCut = Assert.Single(smoothFront.InteriorCuts);

        var tabbedEnd = Assert.IsType<LineSegment>(Assert.Single(tabbedCut.Segments)).To;
        var smoothEnd = Assert.IsType<LineSegment>(Assert.Single(smoothCut.Segments)).To;

        var tabbedWidth = Math.Abs(tabbedEnd.X - tabbedCut.Start.X);
        var smoothWidth = Math.Abs(smoothEnd.X - smoothCut.Start.X);
        Assert.True(
            smoothWidth - tabbedWidth > 2.0 * Settings().MaterialThickness - 0.5,
            $"Expected tabbed split width to be reduced by edge strips; smooth={smoothWidth}, tabbed={tabbedWidth}");

        var tabbedLeftDelta = Math.Abs(tabbedCut.Start.Y - requestedHeight);
        var tabbedRightDelta = Math.Abs(tabbedEnd.Y - requestedHeight);
        Assert.True(
            tabbedLeftDelta > 0.01 || tabbedRightDelta > 0.01,
            $"Expected at least one tabbed endpoint to snap from requested height {requestedHeight}");

        Assert.InRange(Math.Abs(smoothCut.Start.Y - requestedHeight), 0.0, 1e-6);
        Assert.InRange(Math.Abs(smoothEnd.Y - requestedHeight), 0.0, 1e-6);
    }

    [Fact]
    public void Split_cut_tabbed_endpoint_snap_translates_curve_without_warping()
    {
        const double requestedHeight = 26.37;
        const string curve = "M 0 0 C 25 10 75 -10 100 0";

        var tabbedPlan = ParseOk($"""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 80.0, 60.0]
                features:
                  - face: "front"
                    type: "split-cut"
                    height: {requestedHeight}
                    amplitude: 10.0
                    validate-separation: false
                    curve:
                      type: "svg-path"
                      samples: 16
                      svg-path-data: "{curve}"
            """);

        var smoothPlan = ParseOk($"""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 80.0, 60.0]
                faces:
                  - name: "left"
                    type: "open"
                  - name: "right"
                    type: "open"
                features:
                  - face: "front"
                    type: "split-cut"
                    height: {requestedHeight}
                    amplitude: 10.0
                    validate-separation: false
                    curve:
                      type: "svg-path"
                      samples: 16
                      svg-path-data: "{curve}"
            """);

        var tabbedCut = Assert.Single(new BoxPlanLib().GetCuttableShapes(tabbedPlan, Settings()).Single(p => p.Id == "box.front").InteriorCuts);
        var smoothCut = Assert.Single(new BoxPlanLib().GetCuttableShapes(smoothPlan, Settings()).Single(p => p.Id == "box.front").InteriorCuts);

        var tabbedPoints = ToPoints(tabbedCut);
        var smoothPoints = ToPoints(smoothCut);

        Assert.Equal(smoothPoints.Count, tabbedPoints.Count);

        var baselineDelta = tabbedPoints[0].Y - smoothPoints[0].Y;
        for (var i = 1; i < tabbedPoints.Count; i++)
        {
            var delta = tabbedPoints[i].Y - smoothPoints[i].Y;
            Assert.True(Math.Abs(delta - baselineDelta) < 1e-6,
                $"Expected pure vertical translation; point {i} delta {delta} differs from baseline {baselineDelta}");
        }
    }
}
