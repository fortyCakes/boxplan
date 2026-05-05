using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests;

public class SamplePlanTests
{
    private static string LoadSample(string filename) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sample-plans", filename));

    [Fact]
    public void Simple_cube_round_trips_through_full_pipeline()
    {
        var yaml = LoadSample("simple-cube.yml");
        var lib = new BoxPlanLib();

        var result = lib.ParsePlan(yaml);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var box = Assert.IsType<BoxShape>(Assert.Single(result.Value!.Shapes));
        Assert.Equal("sample-box", box.Id);
        Assert.Equal(new Vec3(50.0, 50.0, 50.0), box.Dimensions);
        Assert.Equal(Origin.BottomLeftFront, box.Origin);
        Assert.Equal(FaceType.Closed, box.Faces.Single(f => f.Name == FaceName.Top).Type);
    }

    [Fact]
    public void Drawer_frame_3x2_round_trips_through_full_pipeline()
    {
        var yaml = LoadSample("drawer-frame-3x2.yml");
        var lib = new BoxPlanLib();

        var result = lib.ParsePlan(yaml);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var plan = result.Value!;
        Assert.Equal(2, plan.Shapes.Count);

        var frame = Assert.IsType<BoxShape>(plan.ShapesById["frame"]);
        Assert.Equal(new Vec3(300.0, 200.0, 150.0), frame.Dimensions);
        Assert.Equal(FaceType.Open, frame.Faces.Single(f => f.Name == FaceName.Front).Type);

        Assert.Equal(2, frame.Dividers.Count);
        var xDiv = frame.Dividers.Single(d => d.Axis == Axis.X);
        Assert.Equal(new[] { 100.0, 200.0 }, xDiv.Positions);
        Assert.Equal(FaceName.Front, xDiv.Facing);
        var yDiv = frame.Dividers.Single(d => d.Axis == Axis.Y);
        Assert.Equal(new[] { 100.0 }, yDiv.Positions);

        Assert.Equal(6, frame.Inserts.Count);
        var drawer = plan.ShapesById["drawer"];
        Assert.All(frame.Inserts, i => Assert.Same(drawer, i.Target));
        Assert.All(frame.Inserts, i => Assert.Equal(new Vec3(98, 98, 148), i.ResolvedDimensions));
        var byCell = frame.Inserts.ToDictionary(i => i.Cell!.Value, i => i.ResolvedLocation!.Value);
        Assert.Equal(new Vec3(1, 1, 1), byCell[(0, 0, 0)]);
        Assert.Equal(new Vec3(201, 101, 1), byCell[(2, 1, 0)]);

        var drawerBox = Assert.IsType<BoxShape>(drawer);
        Assert.Null(drawerBox.Dimensions);
        Assert.NotNull(drawerBox.Fit);
        Assert.IsType<FitDimension.Auto>(drawerBox.Fit!.Depth);
        Assert.Equal(1.0, drawerBox.Fit!.Clearance);
        Assert.Equal(FaceType.Open, drawerBox.Faces.Single(f => f.Name == FaceName.Top).Type);

        var cutout = Assert.IsType<CutoutFeature>(Assert.Single(drawerBox.Features));
        Assert.Equal(CutoutShape.Semicircle, cutout.Shape);
        Assert.Equal(50.0, cutout.Width);
        Assert.Equal(FaceName.Front, cutout.Face);
        Assert.Equal(Anchor.TopCenter, cutout.Position!.Anchor);
        Assert.Equal(new Vec2(0.0, 15.0), cutout.Position!.Offset);
    }
}
