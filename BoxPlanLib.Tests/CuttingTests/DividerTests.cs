using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.CuttingTests;

public class DividerTests
{
  private static IReadOnlyList<(double Length, bool PrimaryOwns)> JointBlocks(double length, double fingerJointSize)
  {
    var blocks = new List<(double Length, bool PrimaryOwns)>();
    if (length <= 0)
    {
      return blocks;
    }

    if (length < 3 * fingerJointSize)
    {
      blocks.Add((length, true));
      return blocks;
    }

    var minEndLength = 0.75 * fingerJointSize;
    var interiorCount = (int)Math.Floor(length / fingerJointSize);
    if (interiorCount % 2 == 0)
    {
      interiorCount--;
    }

    while (interiorCount > 1)
    {
      var endLength = (length - interiorCount * fingerJointSize) / 2.0;
      if (endLength >= minEndLength)
      {
        break;
      }

      interiorCount -= 2;
    }

    var balancedEndLength = (length - interiorCount * fingerJointSize) / 2.0;
    blocks.Add((balancedEndLength, true));
    for (var index = 0; index < interiorCount; index++)
    {
      blocks.Add((fingerJointSize, index % 2 != 0));
    }

    blocks.Add((balancedEndLength, true));
    return blocks;
  }

  private static (double Min, double Max) FaceSlotExtents(double length, double thickness, double fingerJointSize, bool dividerOwnsPrimary)
  {
    var edgeInset = thickness * 1.5;
    var innerLength = Math.Max(0, length - 2 * edgeInset);
    var blocks = JointBlocks(innerLength, fingerJointSize);
    var cursor = edgeInset;
    var min = double.PositiveInfinity;
    var max = double.NegativeInfinity;

    foreach (var block in blocks)
    {
      var dividerOwnsBlock = block.PrimaryOwns == dividerOwnsPrimary;
      if (dividerOwnsBlock)
      {
        min = Math.Min(min, cursor);
        max = cursor + block.Length;
      }

      cursor += block.Length;
    }

    if (double.IsPositiveInfinity(min))
    {
      return (0.0, 0.0);
    }

    return (min, max);
  }

    private static BoxPlan ParseOk(string yaml)
    {
        var lib = new BoxPlanLib();
        var result = lib.ParsePlan(yaml);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result.Value!;
    }

    private static BoxPlanSettings Settings(double kerf = 0.0) =>
      new() { Kerf = kerf, MaterialThickness = 3.0, FingerJointSize = 20.0, SheetWidth = 300, SheetHeight = 300 };

    private static IReadOnlyList<Vec2> Points(BoxPlanCuttableShape shape)
    {
      var pts = new List<Vec2> { shape.Outline.Start };
      foreach (var seg in shape.Outline.Segments)
      {
        Assert.IsType<LineSegment>(seg);
        pts.Add(((LineSegment)seg).To);
      }

      return pts;
    }

    private static int FindPointIndex(IReadOnlyList<Vec2> pts, Vec2 target)
    {
      for (var i = 0; i < pts.Count; i++)
      {
        if (Math.Abs(pts[i].X - target.X) < 1e-6 && Math.Abs(pts[i].Y - target.Y) < 1e-6)
        {
          return i;
        }
      }

      throw new Xunit.Sdk.XunitException($"Point {target} not found in outline.");
    }

  private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(CuttablePath path)
  {
    var pts = new List<Vec2> { path.Start };
    foreach (var seg in path.Segments)
    {
      Assert.IsType<LineSegment>(seg);
      pts.Add(((LineSegment)seg).To);
    }

    return (pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
  }


    [Fact]
    public void Drawer_frame_emits_three_divider_panels()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - split: { x: 3, y: 2 }
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());

        var dividers = pieces.Where(p => p.Id.Contains(".divider-")).ToArray();
        Assert.Equal(3, dividers.Length);

        var xDividers = dividers.Where(p => p.Id.Contains("divider-x")).ToArray();
        Assert.Equal(2, xDividers.Length);
        var yDividers = dividers.Where(p => p.Id.Contains("divider-y")).ToArray();
        Assert.Single(yDividers);
    }

    [Fact]
    public void X_divider_depth_matches_box_side_panel_height()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                faces:
                  - name: "front"
                    type: "open"
                dividers:
                  - split: { x: 3 }
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var xDivider = pieces.First(p => p.Id.Contains("divider-x"));
        var sidePanel = pieces.Single(p => p.Id == "frame.left");

        Assert.Equal(sidePanel.BoundingBoxMax.X, xDivider.BoundingBoxMax.Y, 6);
    }

    [Fact]
    public void Facing_front_x_divider_has_full_depth()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - split: { x: 3 }
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var xDivider = pieces.First(p => p.Id.Contains("divider-x"));

        Assert.Equal(200, xDivider.BoundingBoxMax.X, 6);
        Assert.Equal(150, xDivider.BoundingBoxMax.Y, 6);
        Assert.True(xDivider.Outline.Segments.Count > 4);
    }

    [Fact]
      public void Bottom_face_splits_x_divider_slots_into_finger_openings()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - axis: "x"
                    positions: [100.0, 200.0]
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var bottom = pieces.Single(p => p.Id == "frame.bottom");
          Assert.Equal(6, bottom.InteriorCuts.Count);
    }

    [Fact]
    public void Bottom_face_slots_follow_front_facing_x_divider_edge()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - axis: "x"
                    positions: [150.0]
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var bottom = pieces.Single(p => p.Id == "frame.bottom");
    var back = pieces.Single(p => p.Id == "frame.back");
        var bounds = bottom.InteriorCuts.Select(Bounds).ToArray();
    var backBounds = back.InteriorCuts.Select(Bounds).ToArray();
        var settings = Settings();
        var actualMax = bounds.Max(b => b.MaxY);
        var dividerEdgeSlots = FaceSlotExtents(150.0 - settings.MaterialThickness, settings.MaterialThickness, settings.FingerJointSize, dividerOwnsPrimary: false);
    var backFaceSlots = FaceSlotExtents(200.0, settings.MaterialThickness, settings.FingerJointSize, dividerOwnsPrimary: true);
        var wallEdgeSlots = FaceSlotExtents(150.0, settings.MaterialThickness, settings.FingerJointSize, dividerOwnsPrimary: false);

        Assert.Equal(3, bounds.Length);
      Assert.Equal(settings.MaterialThickness * 1.5, backBounds.Min(b => b.MinY), 6);
    Assert.Equal(backFaceSlots.Max, backBounds.Max(b => b.MaxY), 6);
        Assert.Equal(dividerEdgeSlots.Max, actualMax, 6);
        Assert.True(actualMax < wallEdgeSlots.Max);
    }

    [Fact]
    public void Front_face_has_no_slot_when_facing_is_front()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - axis: "x"
                    positions: [100.0, 200.0]
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var front = pieces.Single(p => p.Id == "frame.front");
        Assert.Empty(front.InteriorCuts);

    var back = pieces.Single(p => p.Id == "frame.back");
        Assert.Equal(10, back.InteriorCuts.Count);
    }

    [Fact]
    public void No_facing_yields_full_panel_size()
  {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - axis: "x"
                    positions: [150.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var xDivider = pieces.First(p => p.Id.Contains("divider-x"));

        Assert.Equal(200, xDivider.BoundingBoxMax.X, 6);
        Assert.Equal(150, xDivider.BoundingBoxMax.Y, 6);
        Assert.True(xDivider.Outline.Segments.Count > 4);
    }

    [Fact]
    public void Vertical_dividers_get_single_long_back_half_slots()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - split: { x: 3, y: 2 }
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var xDividers = pieces.Where(p => p.Id.Contains("divider-x")).ToArray();

        Assert.Equal(2, xDividers.Length);
        foreach (var xDivider in xDividers)
        {
            var slot = Assert.Single(xDivider.InteriorCuts);
            var bounds = Bounds(slot);
            Assert.Equal(98.5, bounds.MinX, 6);
            Assert.Equal(101.5, bounds.MaxX, 6);
            Assert.Equal(73.5, bounds.MinY, 6);
            Assert.Equal(147.0, bounds.MaxY, 6);
        }
    }

    [Fact]
    public void Horizontal_dividers_get_single_long_front_half_slots_per_vertical_divider()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - split: { x: 3, y: 2 }
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var yDivider = pieces.Single(p => p.Id.Contains("divider-y"));

        Assert.Equal(2, yDivider.InteriorCuts.Count);
        var ordered = yDivider.InteriorCuts.Select(Bounds).OrderBy(b => b.MinX).ToArray();

        Assert.Equal(98.5, ordered[0].MinX, 6);
        Assert.Equal(101.5, ordered[0].MaxX, 6);
        Assert.Equal(0.0, ordered[0].MinY, 6);
        Assert.Equal(73.5, ordered[0].MaxY, 6);

        Assert.Equal(198.5, ordered[1].MinX, 6);
        Assert.Equal(201.5, ordered[1].MaxX, 6);
        Assert.Equal(0.0, ordered[1].MinY, 6);
        Assert.Equal(73.5, ordered[1].MaxY, 6);
    }

    [Fact]
    public void Divider_assembly_slots_are_one_kerf_deeper()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - split: { x: 3, y: 2 }
                    facing: "front"
            """);

              var settings = Settings(kerf: 0.2);
              var pieces = new BoxPlanLib().GetCuttableShapes(plan, settings);
        var xDivider = pieces.First(p => p.Id.Contains("divider-x"));
        var yDivider = pieces.Single(p => p.Id.Contains("divider-y"));

        var xBounds = Bounds(Assert.Single(xDivider.InteriorCuts));
              Assert.Equal(147.0 / 2.0 + settings.Kerf, xBounds.MaxY - xBounds.MinY, 6);
              Assert.Equal(xDivider.BoundingBoxMax.Y - settings.Kerf / 2.0, xBounds.MaxY, 6);

        var yBounds = yDivider.InteriorCuts.Select(Bounds).OrderBy(b => b.MinX).ToArray();
              Assert.Equal(settings.Kerf / 2.0, yBounds[0].MinY, 6);
              Assert.Equal(147.0 / 2.0 + settings.Kerf, yBounds[0].MaxY - yBounds[0].MinY, 6);
              Assert.Equal(settings.Kerf / 2.0, yBounds[1].MinY, 6);
              Assert.Equal(147.0 / 2.0 + settings.Kerf, yBounds[1].MaxY - yBounds[1].MinY, 6);
    }

    [Fact]
    public void Divider_corner_keeps_material_thickness_clearance_before_next_joined_edge()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - split: { x: 3 }
                    facing: "front"
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var xDivider = pieces.First(p => p.Id.Contains("divider-x"));
        var pts = Points(xDivider);
        var maxX = xDivider.BoundingBoxMax.X;
        var topRight = pts.Where(p => Math.Abs(p.X - maxX) < 1e-6).MaxBy(p => p.Y)!;
        var cornerIndex = FindPointIndex(pts, topRight);
        var next = pts[cornerIndex + 1];

        Assert.Equal(topRight.Y, next.Y, 6);
        Assert.True(next.X < topRight.X - 1e-6, $"Expected smooth clearance after {topRight}, got {next}.");
    }

    [Fact]
    public void Divider_corner_keeps_material_on_one_adjacent_edge_after_clearance_strip()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                dividers:
                  - axis: "x"
                    positions: [100.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var xDivider = pieces.First(p => p.Id.Contains("divider-x"));
        var pts = Points(xDivider);
        var maxX = xDivider.BoundingBoxMax.X;
        var topRight = pts.Where(p => Math.Abs(p.X - maxX) < 1e-6).MaxBy(p => p.Y)!;
        var cornerIndex = FindPointIndex(pts, topRight);
        var afterClearance = pts[cornerIndex + 1];
        var secondAfterCorner = pts[cornerIndex + 2];

        var continuesAlongTop = Math.Abs(afterClearance.Y - topRight.Y) < 1e-6 && afterClearance.X < topRight.X - 1e-6;
        var continuesAlongRight = Math.Abs(afterClearance.X - topRight.X) < 1e-6 && afterClearance.Y < topRight.Y - 1e-6;

        Assert.True(continuesAlongTop || continuesAlongRight,
          $"Expected corner clearance to preserve material along one adjacent edge, got {afterClearance} then {secondAfterCorner}.");
    }

    [Fact]
    public void Divider_join_to_open_face_does_not_create_corner_spike()
    {
        var plan = ParseOk("""
            shapes:
              - id: "frame"
                type: "box"
                dimensions: [300.0, 200.0, 150.0]
                faces:
                  - name: "top"
                    type: "open"
                dividers:
                  - axis: "x"
                    positions: [100.0]
            """);

        var pieces = new BoxPlanLib().GetCuttableShapes(plan, Settings());
        var xDivider = pieces.First(p => p.Id.Contains("divider-x"));
        var pts = Points(xDivider);
        var maxX = xDivider.BoundingBoxMax.X;
        var topRight = pts.Where(p => Math.Abs(p.X - maxX) < 1e-6).MaxBy(p => p.Y)!;
        var cornerIndex = FindPointIndex(pts, topRight);
        var next = pts[cornerIndex + 1];

        Assert.Equal(topRight.Y, next.Y, 6);
        Assert.True(next.X < topRight.X - 1e-6, $"Expected no spike after open-face corner {topRight}, got {next}.");
    }
}
