using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

internal static class CutoutBuilder
{
    public static Vec2 ResolveCenter(Position? position, double panelU, double panelV, PipelineLogger? logger = null)
    {
        logger?.Log($"[cutout] Resolving center for panelU={panelU}, panelV={panelV}");
        if (position is null) return new Vec2(panelU / 2, panelV / 2);
        var anchor = position.Anchor switch
        {
            Anchor.TopCenter    => new Vec2(panelU / 2, panelV),
            Anchor.BottomCenter => new Vec2(panelU / 2, 0),
            Anchor.LeftCenter   => new Vec2(0, panelV / 2),
            Anchor.RightCenter  => new Vec2(panelU, panelV / 2),
            Anchor.Center       => new Vec2(panelU / 2, panelV / 2),
            _ => new Vec2(panelU / 2, panelV / 2),
        };
        return new Vec2(anchor.X + position.Offset.X, anchor.Y + position.Offset.Y);
    }

    public static CuttablePath Build(CutoutFeature feature, Vec2 center, double kerf, Vec2 translation, PipelineLogger? logger = null)
    {
        logger?.Log($"[cutout] Building cutout {feature.Shape} at center=({center.X},{center.Y})");
        var w = Math.Abs(feature.Width) - kerf;
        var h = Math.Abs(feature.Height) - kerf;
        var cx = center.X + translation.X;
        var cy = center.Y + translation.Y;

        return feature.Shape switch
        {
            CutoutShape.Rectangle  => BuildRectangle(cx, cy, w, h),
            CutoutShape.Circle     => BuildCircle(cx, cy, w / 2),
            CutoutShape.Semicircle => BuildSemicircle(cx, cy, feature.Width >= 0 ? w / 2 : -(w / 2)),
            _ => throw new InvalidOperationException($"Unsupported cutout shape {feature.Shape}"),
        };
    }

    public static CuttablePath BuildSlotRectangle(SlotSpec slot, double kerf, Vec2 translation, PipelineLogger? logger = null)
    {
        logger?.Log($"[cutout] Building slot rectangle at ({slot.U},{slot.V}) size=({slot.Width},{slot.Height})");
        var w = Math.Abs(slot.Width) - kerf;
        var h = Math.Abs(slot.Height) - kerf;
        var cx = slot.U + translation.X;
        var cy = slot.V + translation.Y;
        return BuildRectangle(cx, cy, w, h);
    }

    private static CuttablePath BuildRectangle(double cx, double cy, double w, double h)
    {
        var x0 = cx - w / 2;
        var x1 = cx + w / 2;
        var y0 = cy - h / 2;
        var y1 = cy + h / 2;
        return new CuttablePath
        {
            Start = new Vec2(x0, y0),
            Segments = new List<PathSegment>
            {
                new LineSegment(new Vec2(x1, y0)),
                new LineSegment(new Vec2(x1, y1)),
                new LineSegment(new Vec2(x0, y1)),
                new LineSegment(new Vec2(x0, y0)),
            },
            Closed = true,
        };
    }

    private static CuttablePath BuildCircle(double cx, double cy, double r)
    {
        r = Math.Abs(r);
        return new CuttablePath
        {
            Start = new Vec2(cx + r, cy),
            Segments = new List<PathSegment>
            {
                new ArcSegment(new Vec2(cx - r, cy), r, Clockwise: false, LargeArc: false),
                new ArcSegment(new Vec2(cx + r, cy), r, Clockwise: false, LargeArc: false),
            },
            Closed = true,
        };
    }

    private static CuttablePath BuildSemicircle(double cx, double cy, double r)
    {
        var radius = Math.Abs(r);
        var start = r < 0 ? new Vec2(cx - radius, cy) : new Vec2(cx + radius, cy);
        var end = r < 0 ? new Vec2(cx + radius, cy) : new Vec2(cx - radius, cy);

        return new CuttablePath
        {
            Start = start,
            Segments = new List<PathSegment>
            {
                new ArcSegment(end, radius, Clockwise: false, LargeArc: false),
                new LineSegment(start),
            },
            Closed = true,
        };
    }
}
