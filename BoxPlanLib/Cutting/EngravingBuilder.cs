using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

internal static class EngravingBuilder
{
    public static IEnumerable<CuttablePath> BuildGrid(double panelU, double panelV, double cellSize, GridCenter center, Vec2 translation)
    {
        foreach (var x in GridLinePositions(panelU, cellSize, center))
        {
            var tx = x + translation.X;
            yield return new CuttablePath
            {
                Start = new Vec2(tx, translation.Y),
                Segments = [new LineSegment(new Vec2(tx, panelV + translation.Y))],
                Closed = false,
            };
        }

        foreach (var y in GridLinePositions(panelV, cellSize, center))
        {
            var ty = y + translation.Y;
            yield return new CuttablePath
            {
                Start = new Vec2(translation.X, ty),
                Segments = [new LineSegment(new Vec2(panelU + translation.X, ty))],
                Closed = false,
            };
        }
    }

    // Returns positions (in panel-local coords) of all grid lines for the given axis length.
    private static IEnumerable<double> GridLinePositions(double length, double cellSize, GridCenter center)
    {
        if (center == GridCenter.Maximize)
        {
            // Round to nearest cell count so that cells straddling the edge are included
            // as long as more than half the cell is on the face.
            var n = (int)Math.Round(length / cellSize, MidpointRounding.AwayFromZero);
            if (n < 1) n = 1;
            var start = (length - n * cellSize) / 2.0;
            for (var k = 0; k <= n; k++)
            {
                var x = start + k * cellSize;
                if (x >= -1e-9 && x <= length + 1e-9)
                    yield return Math.Clamp(x, 0.0, length);
            }
            yield break;
        }

        var offset = center == GridCenter.Corner ? 0.0 : SpaceCenteredOffset(length, cellSize);
        for (var x = offset; x <= length + 1e-9; x += cellSize)
            yield return x;
    }

    // Compute the first grid line position >= 0 such that a cell centre falls at length/2.
    private static double SpaceCenteredOffset(double length, double cellSize)
    {
        var firstLineFromCenter = (length / 2.0) - cellSize / 2.0;
        var offset = firstLineFromCenter % cellSize;
        if (offset < -1e-9) offset += cellSize;
        return offset < 1e-9 ? 0.0 : offset;
    }

    public static Vec2 AnchorPointFromCenter(Vec2 center, Anchor anchor, double width, double height)
    {
        var halfW = width * 0.5;
        var halfH = height * 0.5;
        var (dx, dy) = anchor switch
        {
            Anchor.TopLeft => (-halfW, halfH),
            Anchor.TopCenter => (0.0, halfH),
            Anchor.TopRight => (halfW, halfH),
            Anchor.LeftCenter => (-halfW, 0.0),
            Anchor.Center => (0.0, 0.0),
            Anchor.RightCenter => (halfW, 0.0),
            Anchor.BottomLeft => (-halfW, -halfH),
            Anchor.BottomCenter => (0.0, -halfH),
            Anchor.BottomRight => (halfW, -halfH),
            _ => (0.0, 0.0),
        };
        return new Vec2(center.X + dx, center.Y + dy);
    }
}
