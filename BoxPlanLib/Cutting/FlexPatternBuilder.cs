using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

// Generates the staggered flex-cut pattern for a panel that will be bent.
// Each row repeats cut-gap-cut from alternating sides, where gap = FlexLineSpacing:
//   even rows (from left):  [0,L]  [L+G, 2L+G]  ...
//   odd  rows (from right): [W-L,W]  [W-2L-G, W-L-G]  ...
// Cuts are returned in panel space [0,panelWidth]×[0,panelHeight]; the caller
// is responsible for clipping them against the actual panel outline.
internal static class FlexPatternBuilder
{
    public static IReadOnlyList<CuttablePath> Build(
        double panelWidth,
        double panelHeight,
        BoxPlanSettings settings,
        PipelineLogger? logger)
    {
        var spacing = settings.FlexLineSpacing;
        var fraction = settings.FlexLineLengthFraction;

        if (spacing <= 0 || fraction <= 0) return Array.Empty<CuttablePath>();

        var cutLen = panelWidth * fraction;
        var rowCount = (int)Math.Floor(panelHeight / spacing);
        if (rowCount <= 0) return Array.Empty<CuttablePath>();

        logger?.Log($"[flex] Building flex pattern width={panelWidth:F3} height={panelHeight:F3} spacing={spacing:F3}");

        var totalHeight = rowCount * spacing;
        var yStart = (panelHeight - totalHeight) / 2.0 + spacing / 2.0;
        var pitch = cutLen + spacing; // length of one cut + one gap

        var cuts = new List<CuttablePath>();
        for (var row = 0; row < rowCount; row++)
        {
            var y = yStart + row * spacing;

            if (row % 2 == 0)
            {
                for (var k = 0; ; k++)
                {
                    var x0 = k * pitch;
                    if (x0 >= panelWidth) break;
                    var x1 = Math.Min(x0 + cutLen, panelWidth);
                    cuts.Add(HorizontalCut(x0, x1, y));
                }
            }
            else
            {
                for (var k = 0; ; k++)
                {
                    var x1 = panelWidth - k * pitch;
                    if (x1 <= 0) break;
                    var x0 = Math.Max(x1 - cutLen, 0.0);
                    cuts.Add(HorizontalCut(x0, x1, y));
                }
            }
        }

        return cuts;
    }

    private static CuttablePath HorizontalCut(double x0, double x1, double y) => new()
    {
        Start = new Vec2(x0, y),
        Segments = new[] { new LineSegment(new Vec2(x1, y)) },
        Closed = false,
    };
}
