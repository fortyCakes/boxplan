using System.Globalization;
using System.Xml.Linq;
using BoxPlanLib;
using BoxPlanLib.Model;

namespace BoxPlanLib.Tests.SvgTests;

public class SimpleSvgGeneratorTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static BoxPlanSettings Settings(double spacing = 5.0) => new()
    {
        SheetWidth = 300,
        SheetHeight = 300,
        Kerf = 0,
        MaterialThickness = 3,
        FingerJointSize = 5,
        Spacing = spacing,
    };

    private static CuttablePath ClosedRect(Vec2 origin, double w, double h) => new()
    {
        Start = origin,
        Closed = true,
        Segments = new PathSegment[]
        {
            new LineSegment(new Vec2(origin.X + w, origin.Y)),
            new LineSegment(new Vec2(origin.X + w, origin.Y + h)),
            new LineSegment(new Vec2(origin.X, origin.Y + h)),
            new LineSegment(origin),
        },
    };

    private static BoxPlanCuttableShape Square(string id, double size,
        IReadOnlyList<CuttablePath>? interior = null,
        IReadOnlyList<CuttablePath>? engravings = null) => new()
    {
        Id = id,
        BoundingBoxMin = new Vec2(0, 0),
        BoundingBoxMax = new Vec2(size, size),
        Outline = ClosedRect(new Vec2(0, 0), size, size),
        InteriorCuts = interior ?? Array.Empty<CuttablePath>(),
        Engravings = engravings ?? Array.Empty<CuttablePath>(),
    };

    [Fact]
    public void Empty_input_returns_minimal_valid_svg()
    {
        var lib = new BoxPlanLib();
        var svg = lib.GenerateSimpleSVG(Array.Empty<BoxPlanCuttableShape>(), Settings());

        var doc = XDocument.Parse(svg);
        Assert.Equal(Svg + "svg", doc.Root!.Name);
        Assert.Equal("0 0 0 0", doc.Root!.Attribute("viewBox")!.Value);
        Assert.Equal("0mm", doc.Root!.Attribute("width")!.Value);
        Assert.Equal("0mm", doc.Root!.Attribute("height")!.Value);
    }

    [Fact]
    public void Single_square_outline_emits_one_red_closed_path()
    {
        var lib = new BoxPlanLib();
        var svg = lib.GenerateSimpleSVG(new[] { Square("piece-0", 10) }, Settings());

        var doc = XDocument.Parse(svg);
        var paths = doc.Descendants(Svg + "path").ToArray();
        Assert.Single(paths);
        Assert.Equal("red", paths[0].Attribute("stroke")!.Value);
        Assert.Equal("none", paths[0].Attribute("fill")!.Value);
        var d = paths[0].Attribute("d")!.Value;
        Assert.StartsWith("M ", d);
        Assert.Contains(" L ", d);
        Assert.EndsWith(" Z", d);
    }

    [Fact]
    public void Two_shapes_are_spaced_horizontally()
    {
        var lib = new BoxPlanLib();
        var shapes = new[] { Square("a", 1), Square("b", 1) };
        var svg = lib.GenerateSimpleSVG(shapes, Settings(spacing: 5));

        var doc = XDocument.Parse(svg);
        Assert.Equal("7.000mm", doc.Root!.Attribute("width")!.Value);

        var shapeGroups = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id") != null)
            .ToArray();
        Assert.Equal(2, shapeGroups.Length);
        Assert.Equal("translate(0.000 0)", shapeGroups[0].Attribute("transform")!.Value);
        Assert.Equal("translate(6.000 0)", shapeGroups[1].Attribute("transform")!.Value);
    }

    [Fact]
    public void ArcSegment_emits_A_command_with_correct_flags()
    {
        var arcPath = new CuttablePath
        {
            Start = new Vec2(0, 0),
            Closed = false,
            Segments = new PathSegment[]
            {
                new ArcSegment(new Vec2(10, 10), Radius: 10, Clockwise: true, LargeArc: false),
            },
        };
        var shape = new BoxPlanCuttableShape
        {
            Id = "arc",
            BoundingBoxMin = new Vec2(0, 0),
            BoundingBoxMax = new Vec2(10, 10),
            Outline = arcPath,
            InteriorCuts = Array.Empty<CuttablePath>(),
            Engravings = Array.Empty<CuttablePath>(),
        };

        var lib = new BoxPlanLib();
        var svg = lib.GenerateSimpleSVG(new[] { shape }, Settings());

        var doc = XDocument.Parse(svg);
        var d = doc.Descendants(Svg + "path").Single().Attribute("d")!.Value;
        Assert.Contains("A 10.000 10.000 0 0 0 10.000 10.000", d);
    }

    [Fact]
    public void Numbers_format_with_invariant_culture_under_de_locale()
    {
        var prevCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var shape = new BoxPlanCuttableShape
            {
                Id = "x",
                BoundingBoxMin = new Vec2(0, 0),
                BoundingBoxMax = new Vec2(1.5, 1.5),
                Outline = ClosedRect(new Vec2(0, 0), 1.5, 1.5),
                InteriorCuts = Array.Empty<CuttablePath>(),
                Engravings = Array.Empty<CuttablePath>(),
            };
            var lib = new BoxPlanLib();
            var svg = lib.GenerateSimpleSVG(new[] { shape }, Settings());

            Assert.Contains("1.500", svg);
            Assert.DoesNotContain("1,500", svg);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prevCulture;
        }
    }

    [Fact]
    public void Outline_interior_engraving_get_distinct_stroke_colors()
    {
        var interior = new CuttablePath
        {
            Start = new Vec2(2, 2),
            Closed = true,
            Segments = new PathSegment[]
            {
                new LineSegment(new Vec2(4, 2)),
                new LineSegment(new Vec2(4, 4)),
                new LineSegment(new Vec2(2, 4)),
                new LineSegment(new Vec2(2, 2)),
            },
        };
        var engraving = new CuttablePath
        {
            Start = new Vec2(1, 1),
            Closed = false,
            Segments = new PathSegment[] { new LineSegment(new Vec2(9, 9)) },
        };
        var shape = Square("s", 10, new[] { interior }, new[] { engraving });

        var lib = new BoxPlanLib();
        var svg = lib.GenerateSimpleSVG(new[] { shape }, Settings());

        var doc = XDocument.Parse(svg);
        var paths = doc.Descendants(Svg + "path").ToArray();
        Assert.Equal(3, paths.Length);
        Assert.Equal("red", paths[0].Attribute("stroke")!.Value);
        Assert.Equal("blue", paths[1].Attribute("stroke")!.Value);
        Assert.Equal("black", paths[2].Attribute("stroke")!.Value);
    }

    [Fact]
    public void Root_group_applies_y_flip_transform()
    {
        var lib = new BoxPlanLib();
        var svg = lib.GenerateSimpleSVG(new[] { Square("a", 10) }, Settings());

        var doc = XDocument.Parse(svg);
        var rootGroup = doc.Root!.Elements(Svg + "g").First();
        Assert.Equal("translate(0 10.000) scale(1 -1)", rootGroup.Attribute("transform")!.Value);
    }

    [Fact]
    public void BoundingBox_offset_normalizes_negative_local_coords()
    {
        var origin = new Vec2(-10, -10);
        var shape = new BoxPlanCuttableShape
        {
            Id = "neg",
            BoundingBoxMin = origin,
            BoundingBoxMax = new Vec2(0, 0),
            Outline = ClosedRect(origin, 10, 10),
            InteriorCuts = Array.Empty<CuttablePath>(),
            Engravings = Array.Empty<CuttablePath>(),
        };

        var lib = new BoxPlanLib();
        var svg = lib.GenerateSimpleSVG(new[] { shape }, Settings());

        var doc = XDocument.Parse(svg);
        var d = doc.Descendants(Svg + "path").Single().Attribute("d")!.Value;
        Assert.StartsWith("M 0.000 0.000", d);
    }

    [Fact]
    public void Real_cube_pipeline_produces_six_shape_groups_in_a_row()
    {
        var lib = new BoxPlanLib();
        var plan = lib.ParsePlan("""
            shapes:
              - id: "cube"
                type: "box"
                dimensions: [100.0, 100.0, 100.0]
            """).Value!;
        var settings = Settings();
        var pieces = lib.GetCuttableShapes(plan, settings);

        var svg = lib.GenerateSimpleSVG(pieces, settings);
        var doc = XDocument.Parse(svg);
        var shapeGroups = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id") != null)
            .ToArray();
        Assert.Equal(6, shapeGroups.Length);
    }
}
