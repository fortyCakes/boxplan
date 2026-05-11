using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class LineEngravingTests
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
    public void Rectangle_line_engraving_produces_four_line_segments_in_engravings()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "line-engraving"
                    shape: "rectangle"
                    width: 40.0
                    height: 30.0
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");

        var engraving = Assert.Single(front.Engravings);
        Assert.True(engraving.Closed);
        Assert.Equal(4, engraving.Segments.Count);
        Assert.All(engraving.Segments, s => Assert.IsType<LineSegment>(s));
    }

    [Fact]
    public void Line_engraving_defaults_to_center_of_face()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "line-engraving"
                    shape: "rectangle"
                    width: 20.0
                    height: 10.0
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");
        var engraving = Assert.Single(front.Engravings);

        // Rectangle centred in face: midpoint of bounding box should equal face centre.
        var xs = engraving.Segments.OfType<LineSegment>().Select(s => s.To.X).Append(engraving.Start.X).ToArray();
        var ys = engraving.Segments.OfType<LineSegment>().Select(s => s.To.Y).Append(engraving.Start.Y).ToArray();
        var midX = (xs.Min() + xs.Max()) / 2.0;
        var midY = (ys.Min() + ys.Max()) / 2.0;
        var faceCx = (front.BoundingBoxMin.X + front.BoundingBoxMax.X) / 2.0;
        var faceCy = (front.BoundingBoxMin.Y + front.BoundingBoxMax.Y) / 2.0;
        Assert.Equal(faceCx, midX, precision: 3);
        Assert.Equal(faceCy, midY, precision: 3);
    }

    [Fact]
    public void Line_engraving_does_not_apply_kerf()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "line-engraving"
                    shape: "rectangle"
                    width: 20.0
                    height: 10.0
            """);

        var kerfSettings = Settings();
        kerfSettings.Kerf = 2.0;
        var pieces = new BoxPlanLib().GetCuttableShapes(plan, kerfSettings);
        var front = pieces.Single(p => p.Id == "box.front");
        var engraving = Assert.Single(front.Engravings);

        var xs = engraving.Segments.OfType<LineSegment>().Select(s => s.To.X).Append(engraving.Start.X).ToArray();
        var ys = engraving.Segments.OfType<LineSegment>().Select(s => s.To.Y).Append(engraving.Start.Y).ToArray();
        Assert.Equal(20.0, xs.Max() - xs.Min(), precision: 3);
        Assert.Equal(10.0, ys.Max() - ys.Min(), precision: 3);
    }

    [Fact]
    public void Line_engraving_does_not_affect_interior_cuts()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "line-engraving"
                    shape: "rectangle"
                    width: 20.0
                    height: 10.0
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "box.front");
        Assert.Empty(front.InteriorCuts);
    }

    [Fact]
    public void Line_engraving_without_shape_is_invalid()
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "line-engraving"
                    width: 20.0
                    height: 10.0
            """);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("shape"));
    }

    [Fact]
    public void Line_engraving_svg_uses_black_stroke()
    {
        var plan = ParseOk("""
            shapes:
              - id: "box"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
                features:
                  - face: "front"
                    type: "line-engraving"
                    shape: "rectangle"
                    width: 20.0
                    height: 10.0
            """);

        var lib = new BoxPlanLib();
        var pieces = lib.GetCuttableShapes(plan, Settings());
        var svg = lib.GenerateSimpleSVG(pieces, Settings());

        // The engraving path should use black stroke (not blue, not purple).
        Assert.Contains("stroke=\"black\"", svg);
    }
}
