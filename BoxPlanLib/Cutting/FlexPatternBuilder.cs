using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

// Generates the staggered flex-cut pattern for a panel that will be bent.
// Each column repeats cut-gap-cut from alternating sides, where gap = FlexLineSpacing:
//   even columns (from bottom): [0,L]  [L+G, 2L+G]  ...
//   odd  columns (from top):    [H-L,H]  [H-2L-G, H-L-G]  ...
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

        var cutLen = panelHeight * fraction;
        var colCount = (int)Math.Floor(panelWidth / spacing);
        if (colCount <= 0) return Array.Empty<CuttablePath>();

        logger?.Log($"[flex] Building flex pattern width={panelWidth:F3} height={panelHeight:F3} spacing={spacing:F3}");

        var totalWidth = colCount * spacing;
        var xStart = (panelWidth - totalWidth) / 2.0 + spacing / 2.0;
        var pitch = cutLen + spacing;

        var cuts = new List<CuttablePath>();
        for (var col = 0; col < colCount; col++)
        {
            var x = xStart + col * spacing;

            if (col % 2 == 0)
            {
                for (var k = 0; ; k++)
                {
                    var y0 = k * pitch;
                    if (y0 >= panelHeight) break;
                    var y1 = Math.Min(y0 + cutLen, panelHeight);
                    cuts.Add(VerticalCut(x, y0, y1));
                }
            }
            else
            {
                for (var k = 0; ; k++)
                {
                    var y1 = panelHeight - k * pitch;
                    if (y1 <= 0) break;
                    var y0 = Math.Max(y1 - cutLen, 0.0);
                    cuts.Add(VerticalCut(x, y0, y1));
                }
            }
        }

        return cuts;
    }

    private static CuttablePath VerticalCut(double x, double y0, double y1) => new()
    {
        Start = new Vec2(x, y0),
        Segments = new[] { new LineSegment(new Vec2(x, y1)) },
        Closed = false,
    };
}
