using System.Globalization;
using System.Security;
using System.Text;
using BoxPlanLib.Model;

namespace BoxPlanLib.Svg;

internal sealed class SvgGenerator
{
    private const double StrokeWidthMm = 0.1;
    private const string OutlineColor = "red";
    private const string InteriorColor = "blue";
    private const string LineEngravingColor = "black";
    private const string TextEngravingColor = "purple";
    private const string LabelColor = "green";
    private const string PageOutlineColor = "green";

    public string GenerateSimpleSvg(BoxPlanCuttableShape[] shapes, BoxPlanSettings settings)
    {
        if (shapes.Length == 0)
        {
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"0mm\" height=\"0mm\" viewBox=\"0 0 0 0\"/>";
        }

        var widths = new double[shapes.Length];
        var heights = new double[shapes.Length];
        for (int i = 0; i < shapes.Length; i++)
        {
            widths[i] = shapes[i].BoundingBoxMax.X - shapes[i].BoundingBoxMin.X;
            heights[i] = shapes[i].BoundingBoxMax.Y - shapes[i].BoundingBoxMin.Y;
        }

        double totalW = 0;
        for (int i = 0; i < widths.Length; i++) totalW += widths[i];
        totalW += settings.Spacing * (shapes.Length - 1);

        double totalH = 0;
        for (int i = 0; i < heights.Length; i++) if (heights[i] > totalH) totalH = heights[i];

        var sb = new StringBuilder(256 + shapes.Length * 128);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" ");
        sb.Append("width=\"").Append(F(totalW)).Append("mm\" ");
        sb.Append("height=\"").Append(F(totalH)).Append("mm\" ");
        sb.Append("viewBox=\"0 0 ").Append(F(totalW)).Append(' ').Append(F(totalH)).Append("\">");
        EmitSvgEngravingDefsIfNeeded(sb, shapes);
        sb.Append("<g transform=\"translate(0 ").Append(F(totalH)).Append(") scale(1 -1)\">");

        double xCursor = 0;
        for (int i = 0; i < shapes.Length; i++)
        {
            var s = shapes[i];
            sb.Append("<g id=\"").Append(Escape(s.Id)).Append("\" transform=\"translate(")
              .Append(F(xCursor)).Append(" 0)\">");
            EmitPath(sb, s.Outline, s.BoundingBoxMin, OutlineColor);
            foreach (var p in s.InteriorCuts) EmitPath(sb, p, s.BoundingBoxMin, InteriorColor);
            foreach (var p in s.Engravings) EmitPath(sb, p, s.BoundingBoxMin, LineEngravingColor);
            foreach (var t in s.TextEngravings) EmitTextEngraving(sb, t, s.BoundingBoxMin);
            foreach (var r in s.RasterEngravings) EmitRasterEngraving(sb, r, s.BoundingBoxMin);
            foreach (var v in s.SvgEngravings) EmitSvgEngraving(sb, v, s.BoundingBoxMin);
            if (settings.Labels) EmitLabel(sb, s);
            sb.Append("</g>");
            xCursor += widths[i] + settings.Spacing;
        }

        sb.Append("</g></svg>");
        return sb.ToString();
    }

    public string GeneratePagedSvg(BoxPlanCuttableShape[] shapes, BoxPlanSettings settings)
    {
        if (shapes.Length == 0)
        {
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"0mm\" height=\"0mm\" viewBox=\"0 0 0 0\"/>";
        }

        ValidatePagedSettings(settings);

        var placements = LayoutPages(shapes, settings);
        double totalWidth = placements.PageCount * settings.SheetWidth + Math.Max(0, placements.PageCount - 1) * settings.Spacing;
        double totalHeight = settings.SheetHeight;

        var sb = new StringBuilder(256 + shapes.Length * 192 + placements.PageCount * 128);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" ");
        sb.Append("width=\"").Append(F(totalWidth)).Append("mm\" ");
        sb.Append("height=\"").Append(F(totalHeight)).Append("mm\" ");
        sb.Append("viewBox=\"0 0 ").Append(F(totalWidth)).Append(' ').Append(F(totalHeight)).Append("\">");
        EmitSvgEngravingDefsIfNeeded(sb, shapes);
        sb.Append("<g transform=\"translate(0 ").Append(F(totalHeight)).Append(") scale(1 -1)\">");

        for (int pageIndex = 0; pageIndex < placements.PageCount; pageIndex++)
        {
            double pageX = pageIndex * (settings.SheetWidth + settings.Spacing);
            sb.Append("<g id=\"page-").Append(pageIndex).Append("\" transform=\"translate(")
              .Append(F(pageX)).Append(" 0)\">");
            EmitPageOutline(sb, settings.SheetWidth, settings.SheetHeight);

            foreach (var placement in placements.Items.Where(p => p.PageIndex == pageIndex))
            {
                sb.Append("<g id=\"").Append(Escape(placement.Shape.Id)).Append("\" transform=\"translate(")
                  .Append(F(settings.Margin + placement.X)).Append(' ').Append(F(settings.Margin + placement.Y)).Append(')');
                AppendRotationTransform(sb, placement.Rotation, placement.Shape);
                sb.Append("\">");
                EmitPath(sb, placement.Shape.Outline, placement.Shape.BoundingBoxMin, OutlineColor);
                foreach (var path in placement.Shape.InteriorCuts) EmitPath(sb, path, placement.Shape.BoundingBoxMin, InteriorColor);
                foreach (var path in placement.Shape.Engravings) EmitPath(sb, path, placement.Shape.BoundingBoxMin, LineEngravingColor);
                foreach (var t in placement.Shape.TextEngravings) EmitTextEngraving(sb, t, placement.Shape.BoundingBoxMin);
                foreach (var r in placement.Shape.RasterEngravings) EmitRasterEngraving(sb, r, placement.Shape.BoundingBoxMin);
                foreach (var v in placement.Shape.SvgEngravings) EmitSvgEngraving(sb, v, placement.Shape.BoundingBoxMin);
                if (settings.Labels) EmitLabel(sb, placement.Shape);
                sb.Append("</g>");
            }

            sb.Append("</g>");
        }

        sb.Append("</g></svg>");
        return sb.ToString();
    }

    public IReadOnlyList<string> GeneratePagedSvgPages(BoxPlanCuttableShape[] shapes, BoxPlanSettings settings)
    {
        if (shapes.Length == 0)
        {
            return Array.Empty<string>();
        }

        ValidatePagedSettings(settings);

        var placements = LayoutPages(shapes, settings);
        var pages = new List<string>(placements.PageCount);

        for (int pageIndex = 0; pageIndex < placements.PageCount; pageIndex++)
        {
            pages.Add(BuildSinglePageSvg(placements, pageIndex, settings, shapes.Length));
        }

        return pages;
    }

    private static string BuildSinglePageSvg(LayoutResult placements, int pageIndex, BoxPlanSettings settings, int shapeCount)
    {
        var sb = new StringBuilder(256 + shapeCount * 192);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" ");
        sb.Append("width=\"").Append(F(settings.SheetWidth)).Append("mm\" ");
        sb.Append("height=\"").Append(F(settings.SheetHeight)).Append("mm\" ");
        sb.Append("viewBox=\"0 0 ").Append(F(settings.SheetWidth)).Append(' ').Append(F(settings.SheetHeight)).Append("\">");
        if (placements.Items.Any(p => p.PageIndex == pageIndex && p.Shape.SvgEngravings.Count > 0))
        {
            EmitSvgEngravingDefs(sb);
        }
        sb.Append("<g transform=\"translate(0 ").Append(F(settings.SheetHeight)).Append(") scale(1 -1)\">");

        foreach (var placement in placements.Items.Where(p => p.PageIndex == pageIndex))
        {
            sb.Append("<g id=\"").Append(Escape(placement.Shape.Id)).Append("\" transform=\"translate(")
                .Append(F(settings.Margin + placement.X)).Append(' ').Append(F(settings.Margin + placement.Y)).Append(')');
            AppendRotationTransform(sb, placement.Rotation, placement.Shape);
            sb.Append("\">");
            EmitPath(sb, placement.Shape.Outline, placement.Shape.BoundingBoxMin, OutlineColor);
            foreach (var path in placement.Shape.InteriorCuts) EmitPath(sb, path, placement.Shape.BoundingBoxMin, InteriorColor);
            foreach (var path in placement.Shape.Engravings) EmitPath(sb, path, placement.Shape.BoundingBoxMin, LineEngravingColor);
            foreach (var t in placement.Shape.TextEngravings) EmitTextEngraving(sb, t, placement.Shape.BoundingBoxMin);
            foreach (var r in placement.Shape.RasterEngravings) EmitRasterEngraving(sb, r, placement.Shape.BoundingBoxMin);
            foreach (var v in placement.Shape.SvgEngravings) EmitSvgEngraving(sb, v, placement.Shape.BoundingBoxMin);
            if (settings.Labels) EmitLabel(sb, placement.Shape);
            sb.Append("</g>");
        }

        sb.Append("</g></svg>");
        return sb.ToString();
    }

    private static void ValidatePagedSettings(BoxPlanSettings settings)
    {
        if (settings.SheetWidth <= 0 || settings.SheetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Sheet dimensions must be positive.");
        }

        if (settings.Margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Margin cannot be negative.");
        }

        double usableWidth = settings.SheetWidth - (2 * settings.Margin);
        double usableHeight = settings.SheetHeight - (2 * settings.Margin);
        if (usableWidth <= 0 || usableHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Margin leaves no usable sheet area.");
        }
    }

    private static LayoutResult LayoutPages(BoxPlanCuttableShape[] shapes, BoxPlanSettings settings)
    {
        var legacy = LayoutPagesLegacy(shapes, settings);

        if (settings.UseAdvancedLayoutOptimizer)
        {
            var optimizer = new AdvancedLayoutOptimizer(settings, new Cutting.PipelineLogger(settings.Debug));
            var optimized = optimizer.Optimize(shapes);
            if (optimized is not null)
            {
                var advanced = new LayoutResult(
                    optimized.Items
                    .Select(item => new ShapePlacement(item.Shape, item.PageIndex, item.X, item.Y, item.Rotation))
                    .ToList(),
                    optimized.PageCount);

                if (IsLayoutBetter(advanced, legacy, settings))
                {
                    return advanced;
                }
            }
        }

        return legacy;
    }

    private static bool IsLayoutBetter(LayoutResult candidate, LayoutResult baseline, BoxPlanSettings settings)
    {
        var candidateScore = ScoreLayout(candidate, settings);
        var baselineScore = ScoreLayout(baseline, settings);

        if (candidateScore.PageCount != baselineScore.PageCount)
        {
            return candidateScore.PageCount < baselineScore.PageCount;
        }

        if (Math.Abs(candidateScore.BoundingAreaWithMargins - baselineScore.BoundingAreaWithMargins) > 1e-6)
        {
            return candidateScore.BoundingAreaWithMargins < baselineScore.BoundingAreaWithMargins;
        }

        return candidateScore.AggregateUsedArea <= baselineScore.AggregateUsedArea + 1e-6;
    }

    private static (int PageCount, double BoundingAreaWithMargins, double AggregateUsedArea) ScoreLayout(LayoutResult layout, BoxPlanSettings settings)
    {
        if (layout.PageCount == 0)
        {
            return (0, 0.0, 0.0);
        }

        var pageWidths = new double[layout.PageCount];
        var pageHeights = new double[layout.PageCount];

        foreach (var placement in layout.Items)
        {
            var width = placement.Rotation is 90 or 270 ? Height(placement.Shape) : Width(placement.Shape);
            var height = placement.Rotation is 90 or 270 ? Width(placement.Shape) : Height(placement.Shape);

            var right = placement.X + width;
            var top = placement.Y + height;
            if (right > pageWidths[placement.PageIndex])
            {
                pageWidths[placement.PageIndex] = right;
            }

            if (top > pageHeights[placement.PageIndex])
            {
                pageHeights[placement.PageIndex] = top;
            }
        }

        var totalWidth = pageWidths.Sum(w => w + (2 * settings.Margin));
        if (layout.PageCount > 1)
        {
            totalWidth += settings.Spacing * (layout.PageCount - 1);
        }

        var maxHeight = pageHeights.Max(h => h + (2 * settings.Margin));
        var boundingAreaWithMargins = totalWidth * maxHeight;
        var aggregateUsedArea = 0.0;
        for (var i = 0; i < layout.PageCount; i++)
        {
            aggregateUsedArea += pageWidths[i] * pageHeights[i];
        }

        return (layout.PageCount, boundingAreaWithMargins, aggregateUsedArea);
    }

    private static LayoutResult LayoutPagesLegacy(BoxPlanCuttableShape[] shapes, BoxPlanSettings settings)
    {
        double usableWidth = settings.SheetWidth - (2 * settings.Margin);
        double usableHeight = settings.SheetHeight - (2 * settings.Margin);
        var orderedShapes = shapes
            .Select((shape, index) => new IndexedShape(shape, index, Width(shape), Height(shape)))
            .OrderByDescending(item => Math.Max(item.Width, item.Height))
            .ThenByDescending(item => Math.Min(item.Width, item.Height))
            .ThenByDescending(item => item.Height)
            .ThenByDescending(item => item.Width)
            .ThenBy(item => item.Index)
            .ToArray();

        var pages = new List<PageLayout>();
        var placements = new List<ShapePlacement>(shapes.Length);

        foreach (var item in orderedShapes)
        {
            var pageFit = ChoosePageFit(item, usableWidth, usableHeight);
            if (pageFit is null)
            {
                throw new InvalidOperationException($"Shape '{item.Shape.Id}' does not fit within the configured sheet size.");
            }

            if (!TryPlaceOnExistingPage(item, settings, usableWidth, usableHeight, pages, placements))
            {
                var page = new PageLayout();
                pages.Add(page);
                var shelf = CreateShelf(page, pageFit.Height, settings.Spacing);
                PlaceOnShelf(item, page, shelf, pages.Count - 1, settings.Spacing, pageFit, placements);
            }
        }

        return new LayoutResult(placements, pages.Count);
    }

    private static bool TryPlaceOnExistingPage(
        IndexedShape item,
        BoxPlanSettings settings,
        double usableWidth,
        double usableHeight,
        List<PageLayout> pages,
        List<ShapePlacement> placements)
    {
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var page = pages[pageIndex];
            foreach (var shelf in page.Shelves)
            {
                var shelfFit = ChooseShelfFit(item, shelf, usableWidth, settings.Spacing);
                if (shelfFit is not null)
                {
                    PlaceOnShelf(item, page, shelf, pageIndex, settings.Spacing, shelfFit, placements);
                    return true;
                }
            }

            var newShelfFit = ChooseNewShelfFit(item, page, usableHeight, settings.Spacing);
            if (newShelfFit is not null)
            {
                var shelf = CreateShelf(page, newShelfFit.Height, settings.Spacing);
                PlaceOnShelf(item, page, shelf, pageIndex, settings.Spacing, newShelfFit, placements);
                return true;
            }
        }

        return false;
    }

    private static OrientationFit? ChoosePageFit(IndexedShape item, double usableWidth, double usableHeight)
    {
        return EnumerateFits(item)
            .Where(fit => fit.Width <= usableWidth + 1e-6 && fit.Height <= usableHeight + 1e-6)
            .OrderBy(fit => fit.Width)
            .ThenByDescending(fit => fit.Height)
            .ThenBy(fit => fit.Rotation)
            .FirstOrDefault();
    }

    private static OrientationFit? ChooseShelfFit(IndexedShape item, ShelfLayout shelf, double usableWidth, double spacing)
    {
        return EnumerateFits(item)
            .Where(fit => fit.Height <= shelf.Height + 1e-6 && FitsOnShelf(fit.Width, shelf, usableWidth, spacing))
            .OrderBy(fit => fit.Width)
            .ThenBy(fit => fit.Height)
            .ThenBy(fit => fit.Rotation)
            .FirstOrDefault();
    }

    private static OrientationFit? ChooseNewShelfFit(IndexedShape item, PageLayout page, double usableHeight, double spacing)
    {
        return EnumerateFits(item)
            .Where(fit => CanCreateShelf(page, fit.Height, usableHeight, spacing))
            .OrderBy(fit => fit.Width)
            .ThenByDescending(fit => fit.Height)
            .ThenBy(fit => fit.Rotation)
            .FirstOrDefault();
    }

    private static IEnumerable<OrientationFit> EnumerateFits(IndexedShape item)
    {
        yield return new OrientationFit(item.Width, item.Height, Rotation: 0);

        if (Math.Abs(item.Width - item.Height) > 1e-6)
        {
            yield return new OrientationFit(item.Height, item.Width, Rotation: 90);
        }
    }

    private static bool FitsOnShelf(double width, ShelfLayout shelf, double sheetWidth, double spacing)
    {
        double x = shelf.NextX;
        if (shelf.ItemCount > 0)
        {
            x += spacing;
        }
        return x + width <= sheetWidth + 1e-6;
    }

    private static bool CanCreateShelf(PageLayout page, double height, double sheetHeight, double spacing)
    {
        double y = page.NextShelfY;
        if (page.Shelves.Count > 0)
        {
            y += spacing;
        }
        return y + height <= sheetHeight + 1e-6;
    }

    private static ShelfLayout CreateShelf(PageLayout page, double height, double spacing)
    {
        double y = page.NextShelfY;
        if (page.Shelves.Count > 0)
        {
            y += spacing;
        }

        var shelf = new ShelfLayout(y, height);
        page.Shelves.Add(shelf);
        page.NextShelfY = y + height;
        return shelf;
    }

    private static void PlaceOnShelf(
        IndexedShape item,
        PageLayout page,
        ShelfLayout shelf,
        int pageIndex,
        double spacing,
        OrientationFit fit,
        List<ShapePlacement> placements)
    {
        double x = shelf.NextX;
        if (shelf.ItemCount > 0)
        {
            x += spacing;
        }

        placements.Add(new ShapePlacement(item.Shape, pageIndex, x, shelf.Y, fit.Rotation));
        shelf.NextX = x + fit.Width;
        shelf.ItemCount++;
        page.UsedWidth = Math.Max(page.UsedWidth, shelf.NextX);
    }

    private static void EmitPageOutline(StringBuilder sb, double width, double height)
    {
        sb.Append("<rect x=\"0\" y=\"0\" width=\"").Append(F(width))
          .Append("\" height=\"").Append(F(height))
          .Append("\" stroke=\"").Append(PageOutlineColor)
          .Append("\" stroke-width=\"").Append(F(StrokeWidthMm))
          .Append("\" fill=\"none\"/>");
    }

    private static double Width(BoxPlanCuttableShape shape) => shape.BoundingBoxMax.X - shape.BoundingBoxMin.X;

    private static double Height(BoxPlanCuttableShape shape) => shape.BoundingBoxMax.Y - shape.BoundingBoxMin.Y;

    private static void EmitLabel(StringBuilder sb, BoxPlanCuttableShape shape)
    {
        var width = Width(shape);
        var height = Height(shape);
        if (width <= 1e-6 || height <= 1e-6)
        {
            return;
        }

        var label = shape.Id;
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        var fontSize = ComputeLabelFontSize(width, height, label);
        if (fontSize < 1.0)
        {
            return;
        }

        var cx = width * 0.5;
        var cy = height * 0.5;
        sb.Append("<g transform=\"translate(")
          .Append(F(cx)).Append(' ').Append(F(cy))
          .Append(") scale(1 -1)\">");
        sb.Append("<text x=\"0\" y=\"0\" fill=\"").Append(LabelColor)
          .Append("\" text-anchor=\"middle\" dominant-baseline=\"middle\" font-size=\"")
          .Append(F(fontSize)).Append("\" font-family=\"sans-serif\">")
          .Append(Escape(label)).Append("</text>");
        sb.Append("</g>");
    }

    private static void EmitTextEngraving(StringBuilder sb, TextEngraving engraving, Vec2 origin)
    {
        var x = engraving.X - origin.X;
        var y = engraving.Y - origin.Y;
        var fontWeight = engraving.Bold ? "bold" : "normal";
        var fontStyle = engraving.Italic ? "italic" : "normal";
                var textAnchor = engraving.Anchor switch
        {
            Anchor.TopLeft or Anchor.LeftCenter or Anchor.BottomLeft => "start",
            Anchor.TopRight or Anchor.RightCenter or Anchor.BottomRight => "end",
            _ => "middle",
        };
        // dominant-baseline is the correct attribute for <text> elements (alignment-baseline
        // applies to inline tspan children). Laser software rejects alignment-baseline on text
        // elements and falls back to alphabetic, which shifts top-anchored text up by its height.
        var dominantBaseline = engraving.Anchor switch
        {
            Anchor.TopLeft or Anchor.TopCenter or Anchor.TopRight => "hanging",
            Anchor.BottomLeft or Anchor.BottomCenter or Anchor.BottomRight => "alphabetic",
            _ => "middle",
        };
        sb.Append("<g transform=\"translate(")
          .Append(F(x)).Append(' ').Append(F(y))
          .Append(") scale(1 -1)\">");
        sb.Append("<text x=\"0\" y=\"0\" fill=\"").Append(TextEngravingColor)
          .Append("\" text-anchor=\"").Append(textAnchor)
          .Append("\" dominant-baseline=\"").Append(dominantBaseline).Append("\"")
          .Append(" font-size=\"").Append(F(engraving.Size)).Append("\"")
          .Append(" font-family=\"").Append(Escape(engraving.Font)).Append("\"")
          .Append(" font-weight=\"").Append(fontWeight).Append("\"")
          .Append(" font-style=\"").Append(fontStyle).Append("\">")
          .Append(Escape(engraving.Text)).Append("</text>");
        sb.Append("</g>");
    }

    private static void EmitRasterEngraving(StringBuilder sb, RasterEngraving engraving, Vec2 origin)
    {
        var x = engraving.X - origin.X;
        var y = engraving.Y - origin.Y;
        var width = engraving.Width.GetValueOrDefault();
        var height = engraving.Height.GetValueOrDefault();
        var (offsetX, offsetY) = AnchorOffset(engraving.Anchor, width, height);

        sb.Append("<g transform=\"translate(")
          .Append(F(x)).Append(' ').Append(F(y))
          .Append(") scale(1 -1)\">");

        if (engraving.InlinedPaths is { } paths && engraving.PixelWidth > 0 && engraving.PixelHeight > 0)
        {
            var scaleX = width / engraving.PixelWidth;
            var scaleY = height / engraving.PixelHeight;
            sb.Append("<g fill=\"").Append(TextEngravingColor).Append("\" filter=\"url(#svg-eng)\" transform=\"translate(")
              .Append(F(offsetX)).Append(' ').Append(F(offsetY))
              .Append(") scale(").Append(F(scaleX)).Append(' ').Append(F(scaleY))
              .Append(")\">");
            sb.Append(paths);
            sb.Append("</g>");
        }
        else
        {
            sb.Append("<image x=\"").Append(F(offsetX))
              .Append("\" y=\"").Append(F(offsetY))
              .Append("\" width=\"").Append(F(width))
              .Append("\" height=\"").Append(F(height))
              .Append("\" preserveAspectRatio=\"none\" href=\"").Append(Escape(engraving.Href))
              .Append("\"/>");
        }

        sb.Append("</g>");
    }

    private static void EmitSvgEngravingDefsIfNeeded(StringBuilder sb, BoxPlanCuttableShape[] shapes)
    {
        if (shapes.Any(s => s.SvgEngravings.Count > 0
                         || s.RasterEngravings.Any(e => e.InlinedPaths is not null)))
            EmitSvgEngravingDefs(sb);
    }

    private static void EmitSvgEngravingDefs(StringBuilder sb)
    {
        sb.Append("<defs><filter id=\"svg-eng\">")
          .Append("<feFlood flood-color=\"").Append(TextEngravingColor).Append("\" result=\"c\"/>")
          .Append("<feComposite in=\"c\" in2=\"SourceGraphic\" operator=\"in\"/>")
          .Append("</filter></defs>");
    }

    private static void EmitSvgEngraving(StringBuilder sb, SvgEngraving engraving, Vec2 origin)
    {
        var x = engraving.X - origin.X;
        var y = engraving.Y - origin.Y;
        var width = engraving.Width.GetValueOrDefault();
        var height = engraving.Height.GetValueOrDefault();
        var (offsetX, offsetY) = AnchorOffset(engraving.Anchor, width, height);

        sb.Append("<g transform=\"translate(")
          .Append(F(x)).Append(' ').Append(F(y))
          .Append(") scale(1 -1)\">");

        if (engraving.InlinedContent is { } content
            && engraving.InlinedViewBoxWidth > 0
            && engraving.InlinedViewBoxHeight > 0)
        {
            var scaleX = width / engraving.InlinedViewBoxWidth;
            var scaleY = height / engraving.InlinedViewBoxHeight;
            sb.Append("<g filter=\"url(#svg-eng)\" transform=\"translate(")
              .Append(F(offsetX)).Append(' ').Append(F(offsetY))
              .Append(") scale(").Append(F(scaleX)).Append(' ').Append(F(scaleY))
              .Append(")\">");
            sb.Append(content);
            sb.Append("</g>");
        }
        else
        {
            sb.Append("<image x=\"").Append(F(offsetX))
              .Append("\" y=\"").Append(F(offsetY))
              .Append("\" width=\"").Append(F(width))
              .Append("\" height=\"").Append(F(height))
              .Append("\" preserveAspectRatio=\"none\" href=\"").Append(Escape(engraving.Href))
              .Append("\" filter=\"url(#svg-eng)\"/>");
        }

        sb.Append("</g>");
    }

        private static (double X, double Y) AnchorOffset(Anchor anchor, double width, double height)
        {
                return anchor switch
                {
                        Anchor.TopLeft => (0.0, 0.0),
                        Anchor.TopCenter => (-width * 0.5, 0.0),
                        Anchor.TopRight => (-width, 0.0),
                        Anchor.LeftCenter => (0.0, -height * 0.5),
                        Anchor.Center => (-width * 0.5, -height * 0.5),
                        Anchor.RightCenter => (-width, -height * 0.5),
                        Anchor.BottomLeft => (0.0, -height),
                        Anchor.BottomCenter => (-width * 0.5, -height),
                        Anchor.BottomRight => (-width, -height),
                        _ => (-width * 0.5, -height * 0.5),
                };
        }

    private static double ComputeLabelFontSize(double width, double height, string label)
    {
        var count = Math.Max(1, label.Length);
        var widthLimit = width * 0.85;
        var heightLimit = height * 0.45;
        var byWidth = widthLimit / (0.6 * count);
        return Math.Min(byWidth, heightLimit);
    }

    private static void EmitPath(StringBuilder sb, CuttablePath path, Vec2 origin, string color)
    {
        var (start, segments) = CollapseReversals(path.Start, path.Segments);

        sb.Append("<path d=\"M ").Append(F(start.X - origin.X)).Append(' ').Append(F(start.Y - origin.Y));
        foreach (var seg in segments)
        {
            switch (seg)
            {
                case LineSegment ls:
                    sb.Append(" L ").Append(F(ls.To.X - origin.X)).Append(' ').Append(F(ls.To.Y - origin.Y));
                    break;
                case ArcSegment a:
                    int large = a.LargeArc ? 1 : 0;
                    int sweep = a.Clockwise ? 0 : 1;
                    sb.Append(" A ").Append(F(a.Radius)).Append(' ').Append(F(a.Radius))
                      .Append(" 0 ").Append(large).Append(' ').Append(sweep).Append(' ')
                      .Append(F(a.To.X - origin.X)).Append(' ').Append(F(a.To.Y - origin.Y));
                    break;
            }
        }
        if (path.Closed) sb.Append(" Z");
        sb.Append("\" stroke=\"").Append(color).Append("\" stroke-width=\"").Append(F(StrokeWidthMm))
          .Append("\" fill=\"none\"/>");
    }

    // Collapse runs where a line segment is immediately followed by its exact reverse —
    // both segments cancel out. Stack-based so chains like A→B→C→B→A reduce all the
    // way to A. Only line/line cancellations are removed; arcs are treated as opaque
    // and never collapse.
    private static (Vec2 Start, IReadOnlyList<PathSegment> Segments) CollapseReversals(
        Vec2 start, IReadOnlyList<PathSegment> segments)
    {
        var points = new List<Vec2> { start };
        var segs = new List<PathSegment?> { null };

        foreach (var seg in segments)
        {
            var to = seg switch
            {
                LineSegment ls => ls.To,
                ArcSegment a => a.To,
                _ => throw new InvalidOperationException($"Unsupported segment {seg.GetType().Name}"),
            };

            if (seg is LineSegment
                && segs[^1] is LineSegment
                && points.Count >= 2
                && PointsEqual(points[^2], to))
            {
                points.RemoveAt(points.Count - 1);
                segs.RemoveAt(segs.Count - 1);
            }
            else
            {
                points.Add(to);
                segs.Add(seg);
            }
        }

        var result = new List<PathSegment>(segs.Count - 1);
        for (int i = 1; i < segs.Count; i++) result.Add(segs[i]!);
        return (points[0], result);
    }

    private static bool PointsEqual(Vec2 a, Vec2 b) =>
        Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    private static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private static string Escape(string s) => SecurityElement.Escape(s) ?? s;

    private static void AppendRotationTransform(StringBuilder sb, int rotation, BoxPlanCuttableShape shape)
    {
        switch (rotation)
        {
            case 90:
                sb.Append(" translate(").Append(F(Height(shape))).Append(" 0) rotate(90)");
                break;
            case 180:
                sb.Append(" translate(").Append(F(Width(shape))).Append(' ').Append(F(Height(shape))).Append(") rotate(180)");
                break;
            case 270:
                sb.Append(" translate(0 ").Append(F(Width(shape))).Append(") rotate(270)");
                break;
        }
    }

    private sealed record IndexedShape(BoxPlanCuttableShape Shape, int Index, double Width, double Height);

    private sealed record ShapePlacement(BoxPlanCuttableShape Shape, int PageIndex, double X, double Y, int Rotation);

    private sealed record OrientationFit(double Width, double Height, int Rotation);

    private sealed record LayoutResult(IReadOnlyList<ShapePlacement> Items, int PageCount);

    private sealed class PageLayout
    {
        public List<ShelfLayout> Shelves { get; } = new();

        public double NextShelfY { get; set; }

        public double UsedWidth { get; set; }
    }

    private sealed class ShelfLayout
    {
        public ShelfLayout(double y, double height)
        {
            Y = y;
            Height = height;
        }

        public double Y { get; }

        public double Height { get; }

        public double NextX { get; set; }

        public int ItemCount { get; set; }
    }
}
