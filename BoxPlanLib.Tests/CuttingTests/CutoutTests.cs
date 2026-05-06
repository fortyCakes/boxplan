using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class CutoutTests
{
    private static BoxPlan ParseOk(string yaml)
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan(yaml);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.Value!;
    }

    private static BoxPlanSettings Settings(double kerf = 0.0) =>
        new() { Kerf = kerf, MaterialThickness = 3.0, FingerJointSize = 20.0, SheetWidth = 300, SheetHeight = 300 };

    private static IReadOnlyList<Vec2> Points(CuttablePath path)
    {
      var points = new List<Vec2> { path.Start };
      foreach (var segment in path.Segments)
      {
        points.Add(segment switch
        {
          LineSegment line => line.To,
          ArcSegment arc => arc.To,
          _ => throw new InvalidOperationException($"Unsupported segment {segment.GetType().Name}"),
        });
      }

      return points;
    }

  private static IEnumerable<Vec2> SamplePoints(CuttablePath path)
  {
    var current = path.Start;
    yield return current;

    foreach (var segment in path.Segments)
    {
      switch (segment)
      {
        case LineSegment line:
          yield return new Vec2((current.X + line.To.X) / 2.0, (current.Y + line.To.Y) / 2.0);
          current = line.To;
          yield return current;
          break;

        case ArcSegment arc:
          yield return ArcMidpoint(current, arc);
          current = arc.To;
          yield return current;
          break;

        default:
          throw new InvalidOperationException($"Unsupported segment {segment.GetType().Name}");
      }
    }
  }

  private static Vec2 ArcMidpoint(Vec2 start, ArcSegment arc)
  {
    var dx = arc.To.X - start.X;
    var dy = arc.To.Y - start.Y;
    var chordLength = Math.Sqrt((dx * dx) + (dy * dy));
    var midpoint = new Vec2((start.X + arc.To.X) / 2.0, (start.Y + arc.To.Y) / 2.0);
    var height = Math.Sqrt(Math.Max(0.0, (arc.Radius * arc.Radius) - ((chordLength * chordLength) / 4.0)));
    var perp = new Vec2(-dy / chordLength, dx / chordLength);

    foreach (var center in new[]
         {
           new Vec2(midpoint.X + (perp.X * height), midpoint.Y + (perp.Y * height)),
           new Vec2(midpoint.X - (perp.X * height), midpoint.Y - (perp.Y * height)),
         })
    {
      var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
      var endAngle = Math.Atan2(arc.To.Y - center.Y, arc.To.X - center.X);
      var ccwSweep = NormalizePositive(endAngle - startAngle);
      var sweepAngle = arc.Clockwise ? -((Math.PI * 2.0) - ccwSweep) : ccwSweep;
      var isLargeArc = Math.Abs(sweepAngle) > Math.PI + 1e-6;
      if (isLargeArc == arc.LargeArc || Math.Abs(Math.Abs(sweepAngle) - Math.PI) <= 1e-6)
      {
        var angle = startAngle + (sweepAngle / 2.0);
        return new Vec2(center.X + (arc.Radius * Math.Cos(angle)), center.Y + (arc.Radius * Math.Sin(angle)));
      }
    }

    throw new InvalidOperationException("Unable to resolve arc midpoint.");
  }

  private static double NormalizePositive(double angle)
  {
    var fullTurn = Math.PI * 2.0;
    var normalized = angle % fullTurn;
    return normalized < 0 ? normalized + fullTurn : normalized;
  }

    [Fact]
    public void Rectangle_cutout_emits_four_line_segments_in_interior_cuts()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "rectangle"
                    width: 30.0
                    height: 20.0
                    position:
                      anchor: "center"
                      offset: [0.0, 0.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var cut = Assert.Single(front.InteriorCuts);
        Assert.True(cut.Closed);
        Assert.Equal(4, cut.Segments.Count);
        Assert.All(cut.Segments, s => Assert.IsType<LineSegment>(s));
    }

    [Fact]
    public void Circle_cutout_uses_two_arc_segments()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "circle"
                    diameter: 40.0
                    position:
                      anchor: "center"
                      offset: [0.0, 0.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var cut = Assert.Single(front.InteriorCuts);
        Assert.Equal(2, cut.Segments.Count);
        Assert.All(cut.Segments, s => Assert.IsType<ArcSegment>(s));
    }

    [Fact]
    public void Semicircle_cutout_emits_arc_plus_line()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "semicircle"
                    diameter: 50.0
                    position:
                      anchor: "center"
                      offset: [0.0, 0.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var cut = Assert.Single(front.InteriorCuts);
        Assert.Equal(2, cut.Segments.Count);
        Assert.IsType<ArcSegment>(cut.Segments[0]);
        Assert.IsType<LineSegment>(cut.Segments[1]);
    }

    [Fact]
    public void Circle_cutout_clips_arc_segments_to_open_face_outline()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                faces:
                  - name: "top"
                    type: "open"
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "circle"
                    diameter: 40.0
                    position:
                      anchor: "top-center"
                      offset: [0.0, 0.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var cut = Assert.Single(front.InteriorCuts);
        Assert.False(cut.Closed);
        var arc = Assert.IsType<ArcSegment>(Assert.Single(cut.Segments));
        Assert.False(arc.LargeArc);
        Assert.All(SamplePoints(cut), point => Assert.True(point.Y <= front.BoundingBoxMax.Y + 1e-6,
            $"arc sample point {point} extends above outline max Y {front.BoundingBoxMax.Y}"));
    }

    [Fact]
    public void Cutout_translates_with_outline_when_kerf_offsets_panel()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "rectangle"
                    width: 10.0
                    height: 10.0
                    position:
                      anchor: "center"
                      offset: [0.0, 0.0]
            """);

        var lib = new BoxPlanLib();
        var noKerf = lib.GetCuttableShapes(plan, Settings(kerf: 0.0))
                        .Single(p => p.Id == "box.front").InteriorCuts[0].Start;
        var withKerf = lib.GetCuttableShapes(plan, Settings(kerf: 0.2))
                          .Single(p => p.Id == "box.front").InteriorCuts[0].Start;

        Assert.Equal(noKerf.X + 0.2, withKerf.X, 6);
        Assert.Equal(noKerf.Y + 0.2, withKerf.Y, 6);
    }

    [Fact]
    public void Closed_cube_with_no_features_has_no_interior_cuts()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
            """);
        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        Assert.All(pieces, p => Assert.Empty(p.InteriorCuts));
    }

    [Fact]
    public void Rectangle_cutout_removes_segments_outside_open_face_outline()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                faces:
                  - name: "top"
                    type: "open"
                features:
                  - face: "front"
                    type: "cutout"
                    shape: "rectangle"
                    width: 30.0
                    height: 20.0
                    position:
                      anchor: "top-center"
                      offset: [0.0, 0.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        Assert.NotEmpty(front.InteriorCuts);
        var lineSegments = front.InteriorCuts.SelectMany(path => path.Segments).OfType<LineSegment>().ToArray();
        Assert.NotEmpty(lineSegments);
        Assert.True(lineSegments.Length < 4, $"expected clipped cutout segments, got {lineSegments.Length}");

        var allPoints = front.InteriorCuts.SelectMany(SamplePoints).ToArray();
        Assert.All(allPoints, point => Assert.True(point.Y <= front.BoundingBoxMax.Y + 1e-6,
            $"cutout point {point} extends above outline max Y {front.BoundingBoxMax.Y}"));
    }

    [Fact]
    public void Negative_semicircle_diameter_flips_arc_into_panel_and_survives_kerf_clipping()
    {
        var plan = ParseOk("""
            shapes:

              - id: "frame"
                type: "box"
                dimensions: [30.0, 30.0, 40.0]

                faces:
                  - name: "back"
                    type: "open"
                  - name: "front"
                    type: "closed"
                  - name: "top"
                    type: "open"
                  - name: "bottom"
                    type: "open"
                  - name: "left"
                    type: "open"
                  - name: "right"
                    type: "open"

                features:
                  - face: "front"
                    type: "cutout"
                    shape: "semicircle"
                    diameter: -20.0
                    position:
                      anchor: "top-center"
                      offset: [0.0, 5.0]
            """);

        var front = new BoxPlanLib().GetCuttableShapes(plan, Settings(kerf: 0.1)).Single(p => p.Id == "frame.front");

        var cut = Assert.Single(front.InteriorCuts);
        Assert.False(cut.Closed);
        var arc = Assert.IsType<ArcSegment>(cut.Segments[0]);
        var midpoint = ArcMidpoint(cut.Start, arc);

        Assert.True(midpoint.Y < cut.Start.Y, $"expected negative diameter semicircle to arc into the panel, midpoint={midpoint}, start={cut.Start}");
        Assert.All(SamplePoints(cut), point =>
        {
            Assert.True(point.X >= front.BoundingBoxMin.X - 1e-6 && point.X <= front.BoundingBoxMax.X + 1e-6,
                $"cutout point {point} extends outside outline X range [{front.BoundingBoxMin.X}, {front.BoundingBoxMax.X}]");
            Assert.True(point.Y >= front.BoundingBoxMin.Y - 1e-6 && point.Y <= front.BoundingBoxMax.Y + 1e-6,
                $"cutout point {point} extends outside outline Y range [{front.BoundingBoxMin.Y}, {front.BoundingBoxMax.Y}]");
        });
    }
}
