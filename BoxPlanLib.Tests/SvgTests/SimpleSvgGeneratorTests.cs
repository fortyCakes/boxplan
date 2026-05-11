using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BoxPlanLib;
using BoxPlanLib.Model;
using Clipper2Lib;

namespace BoxPlanLib.Tests.SvgTests;

public class SimpleSvgGeneratorTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static BoxPlanSettings Settings(double spacing = 5.0) => new()
    {
        SheetWidth = 300,
        SheetHeight = 300,
        Margin = 0,
        Kerf = 0,
        MaterialThickness = 3,
        FingerJointSize = 5,
        Spacing = spacing,
    };

    private static BoxPlanSettings Settings(double sheetWidth, double sheetHeight, double spacing, double margin = 0) => new()
    {
        SheetWidth = sheetWidth,
        SheetHeight = sheetHeight,
        Margin = margin,
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
        IReadOnlyList<CuttablePath>? engravings = null)
        => Rectangle(id, size, size, interior, engravings);

    private static BoxPlanCuttableShape Rectangle(string id, double width, double height,
        IReadOnlyList<CuttablePath>? interior = null,
        IReadOnlyList<CuttablePath>? engravings = null) => new()
    {
        Id = id,
        BoundingBoxMin = new Vec2(0, 0),
        BoundingBoxMax = new Vec2(width, height),
        Outline = ClosedRect(new Vec2(0, 0), width, height),
        InteriorCuts = interior ?? Array.Empty<CuttablePath>(),
        Engravings = engravings ?? Array.Empty<CuttablePath>(),
        TextEngravings = Array.Empty<TextEngraving>(),
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
            TextEngravings = Array.Empty<TextEngraving>(),
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
                TextEngravings = Array.Empty<TextEngraving>(),
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
            TextEngravings = Array.Empty<TextEngraving>(),
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

    [Fact]
    public void Paged_svg_outlines_each_sheet_in_green()
    {
        var lib = new BoxPlanLib();
        var shapes = new[] { Square("a", 10), Square("b", 10), Square("c", 10) };
        var svg = lib.GeneratePagedSVG(shapes, Settings(sheetWidth: 25, sheetHeight: 15, spacing: 5));

        var doc = XDocument.Parse(svg);
        var rects = doc.Descendants(Svg + "rect").ToArray();

        Assert.Equal(2, rects.Length);
        Assert.All(rects, rect =>
        {
            Assert.Equal("green", rect.Attribute("stroke")!.Value);
            Assert.Equal("25.000", rect.Attribute("width")!.Value);
            Assert.Equal("15.000", rect.Attribute("height")!.Value);
        });
    }

    [Fact]
    public void Paged_svg_packs_shapes_tightly_within_each_page()
    {
        var lib = new BoxPlanLib();
        var shapes = new[] { Square("small", 10), Square("large", 20), Square("third", 10) };
        var svg = lib.GeneratePagedSVG(shapes, Settings(sheetWidth: 40, sheetHeight: 25, spacing: 5));

        var doc = XDocument.Parse(svg);
        var pageGroups = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id") is not null && g.Attribute("id")!.Value.StartsWith("page-"))
            .ToDictionary(g => g.Attribute("id")!.Value, g => g.Attribute("transform")!.Value);
        var shapeGroups = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id") is not null && !g.Attribute("id")!.Value.StartsWith("page-"))
            .ToDictionary(g => g.Attribute("id")!.Value, g => g.Attribute("transform")!.Value);

        Assert.Equal("translate(0.000 0)", pageGroups["page-0"]);
        Assert.Equal("translate(45.000 0)", pageGroups["page-1"]);
        Assert.Equal("translate(0.000 0.000)", shapeGroups["large"]);
        Assert.Equal("translate(25.000 0.000)", shapeGroups["small"]);
        Assert.Equal("translate(0.000 0.000)", shapeGroups["third"]);
    }

    [Fact]
    public void Paged_svg_offsets_shapes_by_margin_within_page()
    {
        var lib = new BoxPlanLib();
        var svg = lib.GeneratePagedSVG(
            new[] { Square("a", 10), Square("b", 10) },
            Settings(sheetWidth: 30, sheetHeight: 20, spacing: 5, margin: 2));

        var doc = XDocument.Parse(svg);
        var shapeGroups = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id") is not null && !g.Attribute("id")!.Value.StartsWith("page-"))
            .ToDictionary(g => g.Attribute("id")!.Value, g => g.Attribute("transform")!.Value);

        Assert.Equal("translate(2.000 2.000)", shapeGroups["a"]);
        Assert.Equal("translate(17.000 2.000)", shapeGroups["b"]);
    }

    [Fact]
    public void Paged_svg_rotates_piece_when_rotation_is_required_to_fit_page()
    {
        var lib = new BoxPlanLib();
        var svg = lib.GeneratePagedSVG(
            new[] { Rectangle("rotated", 25, 10) },
            Settings(sheetWidth: 14, sheetHeight: 30, spacing: 5, margin: 2));

        var doc = XDocument.Parse(svg);
        var pageGroups = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id") is not null && g.Attribute("id")!.Value.StartsWith("page-"))
            .ToArray();
        var rotatedTransform = doc.Descendants(Svg + "g")
            .Single(g => g.Attribute("id")?.Value == "rotated")
            .Attribute("transform")!
            .Value;

        Assert.Single(pageGroups);
        Assert.Equal("translate(2.000 2.000) translate(10.000 0) rotate(90)", rotatedTransform);
    }

    [Fact]
    public void Paged_svg_rotates_piece_to_fit_existing_shelf()
    {
        var lib = new BoxPlanLib();
        var svg = lib.GeneratePagedSVG(
            new[] { Rectangle("wide", 20, 15), Rectangle("tall", 15, 20) },
            Settings(sheetWidth: 35, sheetHeight: 30, spacing: 5));

        var doc = XDocument.Parse(svg);
        var pageGroups = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id") is not null && g.Attribute("id")!.Value.StartsWith("page-"))
            .ToArray();
        var shapeGroups = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id") is not null && !g.Attribute("id")!.Value.StartsWith("page-"))
            .ToDictionary(g => g.Attribute("id")!.Value, g => g.Attribute("transform")!.Value);

        Assert.Single(pageGroups);
        Assert.Equal("translate(0.000 0.000)", shapeGroups["tall"]);
        Assert.Equal("translate(20.000 0.000) translate(15.000 0) rotate(90)", shapeGroups["wide"]);
    }

    [Fact]
    public void Advanced_layout_optimizer_does_not_regress_corner_staircase_used_area()
    {
        var lib = new BoxPlanLib();
        var parsed = lib.ParsePlan("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [71.2, 12.7, 25.4]
                location: [0.0, 0.0, 0.0]

              - id: "middle"
                type: "box"
                dimensions: [50.8, 12.7, 25.4]
                location: [0.0, 12.7, 0.0]

              - id: "upper"
                type: "box"
                dimensions: [25.4, 12.7, 25.4]
                location: [0.0, 25.4, 0.0]

              - id: "side1"
                type: "box"
                dimensions: [25.4, 50.8, 25.4]
                location: [0.0, 0.0, 25.4]

              - id: "side2"
                type: "box"
                dimensions: [25.4, 63.5, 25.4]
                location: [0.0, 0.0, 50.8]
            """);
        Assert.True(parsed.Success, string.Join("; ", parsed.Errors));
        var plan = parsed.Value!;

        var legacySettings = new BoxPlanSettings
        {
            SheetWidth = 400,
            SheetHeight = 400,
            Margin = 5.0,
            Kerf = 0.1,
            MaterialThickness = 3.0,
            FingerJointSize = 5.0,
            Spacing = 1.0,
            UseAdvancedLayoutOptimizer = false,
        };

        var advancedSettings = new BoxPlanSettings
        {
            SheetWidth = 400,
            SheetHeight = 400,
            Margin = 5.0,
            Kerf = 0.1,
            MaterialThickness = 3.0,
            FingerJointSize = 5.0,
            Spacing = 1.0,
            UseAdvancedLayoutOptimizer = true,
            UseOrToolsSequenceOptimization = true,
            LayoutSearchIterations = 20,
            LayoutRandomSeed = 1337,
        };

        var pieces = lib.GetCuttableShapes(plan, legacySettings);
        var legacySvg = lib.GeneratePagedSVG(pieces, legacySettings);
        var advancedSvg = lib.GeneratePagedSVG(pieces, advancedSettings);

        var legacyDoc = XDocument.Parse(legacySvg);
        var advancedDoc = XDocument.Parse(advancedSvg);

        var legacyPages = legacyDoc.Descendants(Svg + "g")
            .Count(g => g.Attribute("id")?.Value.StartsWith("page-") == true);
        var advancedPages = advancedDoc.Descendants(Svg + "g")
            .Count(g => g.Attribute("id")?.Value.StartsWith("page-") == true);

        var advancedShapes = advancedDoc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id") is not null && !g.Attribute("id")!.Value.StartsWith("page-"))
            .ToArray();

        Assert.True(advancedPages <= legacyPages);
        Assert.Equal(pieces.Length, advancedShapes.Length);
        Assert.True(UsedBoundingArea(advancedSvg, margin: advancedSettings.Margin) > 0.0);
    }

    [Fact]
    public void Advanced_layout_respects_spacing_parameter()
    {
        var lib = new BoxPlanLib();
        var pieces = new[]
        {
            Rectangle("a", 20, 10),
            Rectangle("b", 20, 10),
            Rectangle("c", 8, 8),
        };

        var settings = new BoxPlanSettings
        {
            SheetWidth = 120,
            SheetHeight = 60,
            Margin = 0,
            Kerf = 0,
            MaterialThickness = 3,
            FingerJointSize = 5,
            Spacing = 7,
            UseAdvancedLayoutOptimizer = true,
            UseOrToolsSequenceOptimization = true,
            LayoutSearchIterations = 20,
            LayoutRandomSeed = 1337,
        };

        var svg = lib.GeneratePagedSVG(pieces, settings);
        var bounds = ShapeBounds(svg);

        Assert.Equal(3, bounds.Count);

        var pairGaps = new[]
        {
            BoundsGap(bounds["a"], bounds["b"]),
            BoundsGap(bounds["a"], bounds["c"]),
            BoundsGap(bounds["b"], bounds["c"]),
        };

        Assert.All(pairGaps, gap =>
            Assert.True(gap >= settings.Spacing - 1e-3, $"Expected gap >= {settings.Spacing:0.###}mm but got {gap:0.###}mm"));
    }

    [Fact]
    public void Advanced_layout_corner_staircase_has_no_overlap_and_respects_spacing_clearance()
    {
        var lib = new BoxPlanLib();
        var parsed = lib.ParsePlan("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [71.2, 12.7, 25.4]
                location: [0.0, 0.0, 0.0]

              - id: "middle"
                type: "box"
                dimensions: [50.8, 12.7, 25.4]
                location: [0.0, 12.7, 0.0]

              - id: "upper"
                type: "box"
                dimensions: [25.4, 12.7, 25.4]
                location: [0.0, 25.4, 0.0]

              - id: "side1"
                type: "box"
                dimensions: [25.4, 50.8, 25.4]
                location: [0.0, 0.0, 25.4]

              - id: "side2"
                type: "box"
                dimensions: [25.4, 63.5, 25.4]
                location: [0.0, 0.0, 50.8]
            """);
        Assert.True(parsed.Success, string.Join("; ", parsed.Errors));

        var settings = new BoxPlanSettings
        {
            SheetWidth = 400,
            SheetHeight = 400,
            Margin = 5.0,
            Kerf = 0.1,
            MaterialThickness = 3.0,
            FingerJointSize = 5.0,
            Spacing = 1.0,
            UseAdvancedLayoutOptimizer = true,
            UseOrToolsSequenceOptimization = true,
            LayoutSearchIterations = 24,
            LayoutRandomSeed = 1337,
        };

        var pieces = lib.GetCuttableShapes(parsed.Value!, settings);
        var svg = lib.GeneratePagedSVG(pieces, settings);
        var polygons = ShapePolygons(svg);

        Assert.Equal(pieces.Length, polygons.Count);

        var ids = polygons.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            for (var j = i + 1; j < ids.Length; j++)
            {
                var a = polygons[ids[i]];
                var b = polygons[ids[j]];

                var overlap = Clipper.Intersect(new PathsD { a }, new PathsD { b }, FillRule.NonZero);
                var overlapArea = overlap.Sum(path => Math.Abs(Clipper.Area(path)));
                Assert.True(overlapArea <= 1e-4, $"Expected no polygon overlap between '{ids[i]}' and '{ids[j]}', but area={overlapArea:0.######}");

                var clearanceDelta = settings.Spacing * 0.5;
                var aClearance = Clipper.InflatePaths(new PathsD { a }, clearanceDelta, JoinType.Round, EndType.Polygon);
                var bClearance = Clipper.InflatePaths(new PathsD { b }, clearanceDelta, JoinType.Round, EndType.Polygon);
                var clearanceOverlap = Clipper.Intersect(aClearance, bClearance, FillRule.NonZero);
                var clearanceArea = clearanceOverlap.Sum(path => Math.Abs(Clipper.Area(path)));

                Assert.True(clearanceArea <= 1e-4,
                    $"Expected spacing clearance between '{ids[i]}' and '{ids[j]}' with spacing={settings.Spacing:0.###}, but overlap area={clearanceArea:0.######}");
            }
        }
    }

    [Fact]
    public void Staircase_layout_middle_riser_is_3mm_wider_than_bottom_riser_in_svg_x_dimension()
    {
        var lib = new BoxPlanLib();
        var plan = lib.ParsePlan("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [71.2, 12.7, 25.4]
                location: [0.0, 0.0, 0.0]

              - id: "middle"
                type: "box"
                dimensions: [50.8, 12.7, 25.4]
                location: [0.0, 12.7, 0.0]

              - id: "upper"
                type: "box"
                dimensions: [25.4, 12.7, 25.4]
                location: [0.0, 25.4, 0.0]
            """).Value!;

        var settings = new BoxPlanSettings
        {
            SheetWidth = 400,
            SheetHeight = 400,
            Margin = 5.0,
            Kerf = 0.1,
            MaterialThickness = 3.0,
            FingerJointSize = 5.0,
            Spacing = 1.0,
        };

        var pieces = lib.GetCuttableShapes(plan, settings);
        var svg = lib.GeneratePagedSVG(pieces, settings);
        var doc = XDocument.Parse(svg);

        var middleRiser = doc.Descendants(Svg + "g")
            .Single(g => g.Attribute("id")?.Value.StartsWith("merged-0.x+@50.8") == true);
        var bottomRiser = doc.Descendants(Svg + "g")
            .Single(g => g.Attribute("id")?.Value.StartsWith("merged-0.x+@71.2") == true);

        var middleWidth = LayoutXWidth(middleRiser);
        var bottomWidth = LayoutXWidth(bottomRiser);

        Assert.Equal(3.0, middleWidth - bottomWidth, 6);
    }

    [Fact]
    public void Staircase_layout_all_three_risers_have_20_points()
    {
        var lib = new BoxPlanLib();
                var parsed = lib.ParsePlan(
                        "shapes:\n" +
                        "  - id: \"lower\"\n" +
                        "    type: \"box\"\n" +
                        "    dimensions: [71.2, 15.0, 25.4]\n" +
                        "    location: [0.0, 0.0, 0.0]\n" +
                        "\n" +
                        "  - id: \"middle\"\n" +
                        "    type: \"box\"\n" +
                        "    dimensions: [50.8, 15.0, 25.4]\n" +
                        "    location: [0.0, 15.0, 0.0]\n" +
                        "\n" +
                        "  - id: \"upper\"\n" +
                        "    type: \"box\"\n" +
                        "    dimensions: [25.4, 15.0, 25.4]\n" +
                        "    location: [0.0, 30.0, 0.0]\n");
                Assert.True(parsed.Success, string.Join("; ", parsed.Errors));
                var plan = parsed.Value!;

        var settings = new BoxPlanSettings
        {
            SheetWidth = 400,
            SheetHeight = 400,
            Margin = 5.0,
            Kerf = 0.1,
            MaterialThickness = 3.0,
            FingerJointSize = 5.0,
            Spacing = 1.0,
        };

        var pieces = lib.GetCuttableShapes(plan, settings);
        var svg = lib.GeneratePagedSVG(pieces, settings);
        var doc = XDocument.Parse(svg);

        var risers = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id")?.Value.StartsWith("merged-0.x+@") == true)
            .OrderBy(g => g.Attribute("id")!.Value)
            .ToArray();

        Assert.Equal(3, risers.Length);

        var distinctCounts = risers
            .Select(riser => riser.Descendants(Svg + "path").First().Attribute("d")!.Value)
            .Select(d => ParsePathPoints(d)
                .GroupBy(p => (Math.Round(p.X, 3), Math.Round(p.Y, 3)))
                .Count())
            .ToArray();

        Assert.Equal(new[] { 20, 20, 20 }, distinctCounts);
    }

    [Fact]
    public void Staircase_layout_middle_riser_bottom_tab_is_centered_in_grown_face()
    {
        var lib = new BoxPlanLib();
        var plan = lib.ParsePlan("""
            shapes:
              - id: "lower"
                type: "box"
                dimensions: [71.2, 12.7, 25.4]
                location: [0.0, 0.0, 0.0]

              - id: "middle"
                type: "box"
                dimensions: [50.8, 12.7, 25.4]
                location: [0.0, 12.7, 0.0]

              - id: "upper"
                type: "box"
                dimensions: [25.4, 12.7, 25.4]
                location: [0.0, 25.4, 0.0]
            """).Value!;

        var settings = new BoxPlanSettings
        {
            SheetWidth = 400,
            SheetHeight = 400,
            Margin = 5.0,
            Kerf = 0.1,
            MaterialThickness = 3.0,
            FingerJointSize = 5.0,
            Spacing = 1.0,
        };

        var pieces = lib.GetCuttableShapes(plan, settings);
        var svg = lib.GeneratePagedSVG(pieces, settings);
        var doc = XDocument.Parse(svg);

        var middleRiser = doc.Descendants(Svg + "g")
            .Single(g => g.Attribute("id")?.Value.StartsWith("merged-0.x+@50.8") == true);

        var d = middleRiser.Descendants(Svg + "path").First().Attribute("d")!.Value;
        var points = ParsePathPoints(d);
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);

        var bottomXs = points
            .Where(p => Math.Abs(p.Y - minY) < 1e-6)
            .Select(p => Math.Round(p.X, 3))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(2, bottomXs.Length);

        var tabCenter = (bottomXs[0] + bottomXs[1]) / 2.0;
        var faceCenter = (minX + maxX) / 2.0;

        Assert.Equal(faceCenter, tabCenter, 6);
    }

    private static double LayoutXWidth(XElement pieceGroup)
    {
        var pageGroup = pieceGroup.Parent;
        Assert.NotNull(pageGroup);

        var pageTransform = pageGroup!.Attribute("transform")?.Value ?? string.Empty;
        var pieceTransform = pieceGroup.Attribute("transform")?.Value ?? string.Empty;

        var path = pieceGroup.Descendants(Svg + "path").First();
        var d = path.Attribute("d")!.Value;
        var points = ParsePathPoints(d);

        var transformedX = points
            .Select(p => ApplyTransform(ApplyTransform(p, pieceTransform), pageTransform).X)
            .ToArray();

        return transformedX.Max() - transformedX.Min();
    }

    private static IReadOnlyList<Vec2> ParsePathPoints(string d)
    {
        var matches = Regex.Matches(d, @"(?:M|L)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)");
        return matches
            .Select(m => new Vec2(
                double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();
    }

    private static Vec2 ApplyTransform(Vec2 p, string transform)
    {
        var result = p;
        var opMatches = Regex.Matches(transform, @"(translate|rotate)\(([^)]*)\)");
        for (var index = opMatches.Count - 1; index >= 0; index--)
        {
            var m = opMatches[index];
            var op = m.Groups[1].Value;
            var values = m.Groups[2].Value
                .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(v => double.Parse(v, CultureInfo.InvariantCulture))
                .ToArray();

            if (op == "translate")
            {
                var tx = values.Length > 0 ? values[0] : 0.0;
                var ty = values.Length > 1 ? values[1] : 0.0;
                result = new Vec2(result.X + tx, result.Y + ty);
            }
            else if (op == "rotate")
            {
                var deg = values.Length > 0 ? values[0] : 0.0;
                var rad = deg * Math.PI / 180.0;
                var c = Math.Cos(rad);
                var s = Math.Sin(rad);
                result = new Vec2(result.X * c - result.Y * s, result.X * s + result.Y * c);
            }
        }

        return result;
    }

    private static double UsedBoundingArea(string svg, double margin = 0.0)
    {
        var doc = XDocument.Parse(svg);

        var points = new List<Vec2>();
        var pageGroups = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id")?.Value.StartsWith("page-") == true)
            .ToArray();

        foreach (var page in pageGroups)
        {
            var pageTransform = page.Attribute("transform")?.Value ?? string.Empty;
            var pieces = page.Elements(Svg + "g")
                .Where(g => g.Attribute("id") is not null && !g.Attribute("id")!.Value.StartsWith("page-"));

            foreach (var piece in pieces)
            {
                var pieceTransform = piece.Attribute("transform")?.Value ?? string.Empty;
                var path = piece.Descendants(Svg + "path").FirstOrDefault();
                if (path is null)
                {
                    continue;
                }

                var d = path.Attribute("d")?.Value;
                if (string.IsNullOrWhiteSpace(d))
                {
                    continue;
                }

                foreach (var p in ParsePathPoints(d))
                {
                    var transformed = ApplyTransform(ApplyTransform(p, pieceTransform), pageTransform);
                    points.Add(transformed);
                }
            }
        }

        Assert.NotEmpty(points);
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        var width = maxX - minX;
        var height = maxY - minY;
        return (width + (2 * margin)) * (height + (2 * margin));
    }

    private static Dictionary<string, (double MinX, double MinY, double MaxX, double MaxY)> ShapeBounds(string svg)
    {
        var doc = XDocument.Parse(svg);
        var result = new Dictionary<string, (double MinX, double MinY, double MaxX, double MaxY)>();

        var pageGroups = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id")?.Value.StartsWith("page-") == true)
            .ToArray();

        foreach (var page in pageGroups)
        {
            var pageTransform = page.Attribute("transform")?.Value ?? string.Empty;
            var pieces = page.Elements(Svg + "g")
                .Where(g => g.Attribute("id") is not null && !g.Attribute("id")!.Value.StartsWith("page-"));

            foreach (var piece in pieces)
            {
                var id = piece.Attribute("id")!.Value;
                var pieceTransform = piece.Attribute("transform")?.Value ?? string.Empty;
                var path = piece.Descendants(Svg + "path").FirstOrDefault();
                if (path is null)
                {
                    continue;
                }

                var d = path.Attribute("d")?.Value;
                if (string.IsNullOrWhiteSpace(d))
                {
                    continue;
                }

                var points = ParsePathPoints(d)
                    .Select(p => ApplyTransform(ApplyTransform(p, pieceTransform), pageTransform))
                    .ToArray();
                if (points.Length == 0)
                {
                    continue;
                }

                result[id] = (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
            }
        }

        return result;
    }

    private static double BoundsGap(
        (double MinX, double MinY, double MaxX, double MaxY) a,
        (double MinX, double MinY, double MaxX, double MaxY) b)
    {
        var dx = Math.Max(0.0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
        var dy = Math.Max(0.0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static Dictionary<string, PathD> ShapePolygons(string svg)
    {
        var doc = XDocument.Parse(svg);
        var result = new Dictionary<string, PathD>();

        var pageGroups = doc.Descendants(Svg + "g")
            .Where(g => g.Attribute("id")?.Value.StartsWith("page-") == true)
            .ToArray();

        foreach (var page in pageGroups)
        {
            var pageTransform = page.Attribute("transform")?.Value ?? string.Empty;
            var pieces = page.Elements(Svg + "g")
                .Where(g => g.Attribute("id") is not null && !g.Attribute("id")!.Value.StartsWith("page-"));

            foreach (var piece in pieces)
            {
                var id = piece.Attribute("id")!.Value;
                var pieceTransform = piece.Attribute("transform")?.Value ?? string.Empty;
                var path = piece.Descendants(Svg + "path").FirstOrDefault();
                if (path is null)
                {
                    continue;
                }

                var d = path.Attribute("d")?.Value;
                if (string.IsNullOrWhiteSpace(d))
                {
                    continue;
                }

                var points = ParsePathPoints(d)
                    .Select(p => ApplyTransform(ApplyTransform(p, pieceTransform), pageTransform))
                    .ToList();

                if (points.Count > 1)
                {
                    var first = points[0];
                    var last = points[^1];
                    if (Math.Abs(first.X - last.X) < 1e-6 && Math.Abs(first.Y - last.Y) < 1e-6)
                    {
                        points.RemoveAt(points.Count - 1);
                    }
                }

                if (points.Count < 3)
                {
                    continue;
                }

                var polygon = points.Select(p => new PointD(p.X, p.Y)).ToList();
                result[id] = new PathD(polygon);
            }
        }

        return result;
    }
}
