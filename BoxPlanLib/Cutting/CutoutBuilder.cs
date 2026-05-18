using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

internal static class CutoutBuilder
{
    private readonly record struct LocalBounds(double MinX, double MaxX, double MinY, double MaxY);

    public static Vec2 ResolveCenter(Position? position, double panelU, double panelV, PipelineLogger? logger = null)
    {
        logger?.Log($"[cutout] Resolving center for panelU={panelU}, panelV={panelV}");
        if (position is null) return new Vec2(panelU / 2, panelV / 2);
        var anchor = position.Anchor switch
        {
            Anchor.TopLeft      => new Vec2(0, panelV),
            Anchor.TopCenter    => new Vec2(panelU / 2, panelV),
            Anchor.TopRight     => new Vec2(panelU, panelV),
            Anchor.BottomLeft   => new Vec2(0, 0),
            Anchor.BottomCenter => new Vec2(panelU / 2, 0),
            Anchor.BottomRight  => new Vec2(panelU, 0),
            Anchor.LeftCenter   => new Vec2(0, panelV / 2),
            Anchor.RightCenter  => new Vec2(panelU, panelV / 2),
            Anchor.Center       => new Vec2(panelU / 2, panelV / 2),
            _ => new Vec2(panelU / 2, panelV / 2),
        };
        return new Vec2(anchor.X + position.Offset.X, anchor.Y + position.Offset.Y);
    }

    public static Vec2 ResolvePlacementCenter(
        Position? position,
        CutoutShape shape,
        double width,
        double height,
        double panelU,
        double panelV,
        double kerf = 0,
        PipelineLogger? logger = null)
    {
        if (position is null)
        {
            return new Vec2(panelU / 2, panelV / 2);
        }

        // EdgeDip: the anchor point IS the center (mouth center on the edge).
        if (shape == CutoutShape.EdgeDip)
            return ResolveCenter(position, panelU, panelV, logger);

        var anchorPoint = ResolveCenter(position, panelU, panelV, logger);
        var bounds = GetLocalBounds(shape, width, height, kerf);
        var featureAnchor = GetFeatureAnchorPoint(bounds, position.Anchor);
        return new Vec2(anchorPoint.X - featureAnchor.X, anchorPoint.Y - featureAnchor.Y);
    }

    public static CuttablePath Build(CutoutFeature feature, Vec2 center, double kerf, Vec2 translation, PipelineLogger? logger = null)
    {
        if (feature.Shape == CutoutShape.EdgeDip)
        {
            var anchor = feature.Position?.Anchor ?? Anchor.TopCenter;
            return BuildEdgeDip(center, feature.Width, feature.Height, feature.Radius, feature.InnerRadius, anchor, kerf, translation, logger);
        }
        return Build(feature.Shape, feature.Width, feature.Height, center, kerf, translation, logger);
    }

    public readonly record struct SafeZone(double UMin, double UMax, double VMin, double VMax);

    public static IEnumerable<Vec2> ExpandCenters(
        CutoutFeature feature,
        Vec2 seed,
        SafeZone zone,
        double kerf = 0)
    {
        if (feature.Repeat is null)
        {
            yield return seed;
            yield break;
        }

        var spacing = feature.Repeat.Spacing;
        var bounds = GetLocalBounds(feature.Shape, feature.Width, feature.Height, kerf);

        bool Fits(Vec2 c) =>
            c.X + bounds.MinX >= zone.UMin &&
            c.X + bounds.MaxX <= zone.UMax &&
            c.Y + bounds.MinY >= zone.VMin &&
            c.Y + bounds.MaxY <= zone.VMax;

        if (Fits(seed)) yield return seed;

        for (var n = 1; ; n++)
        {
            var c = new Vec2(seed.X + n * spacing.X, seed.Y + n * spacing.Y);
            if (!Fits(c)) break;
            yield return c;
        }
        for (var n = 1; ; n++)
        {
            var c = new Vec2(seed.X - n * spacing.X, seed.Y - n * spacing.Y);
            if (!Fits(c)) break;
            yield return c;
        }
    }

    public static CuttablePath Build(CutoutShape shape, double width, double height, Vec2 center, double kerf, Vec2 translation, PipelineLogger? logger = null)
    {
        logger?.Log($"[cutout] Building shape {shape} at center=({center.X},{center.Y})");
        var w = Math.Abs(width) - kerf;
        var h = Math.Abs(height) - kerf;
        var cx = center.X + translation.X;
        var cy = center.Y + translation.Y;

        return shape switch
        {
            CutoutShape.Rectangle  => BuildRectangle(cx, cy, w, h),
            CutoutShape.Circle     => BuildCircle(cx, cy, w / 2),
            CutoutShape.Semicircle => BuildSemicircle(cx, cy, width >= 0 ? w / 2 : -(w / 2)),
            _ => throw new InvalidOperationException($"Unsupported shape {shape}"),
        };
    }

    private static LocalBounds GetLocalBounds(CutoutShape shape, double width, double height, double kerf)
    {
        var w = Math.Max(0, Math.Abs(width) - kerf);
        var h = Math.Max(0, Math.Abs(height) - kerf);

        return shape switch
        {
            CutoutShape.Rectangle => new LocalBounds(-w / 2.0, w / 2.0, -h / 2.0, h / 2.0),
            CutoutShape.Circle => new LocalBounds(-w / 2.0, w / 2.0, -w / 2.0, w / 2.0),
            CutoutShape.Semicircle => width >= 0
                ? new LocalBounds(-w / 2.0, w / 2.0, 0, w / 2.0)
                : new LocalBounds(-w / 2.0, w / 2.0, -w / 2.0, 0),
            CutoutShape.EdgeDip => new LocalBounds(-w / 2.0, w / 2.0, -h, 0),
            _ => throw new InvalidOperationException($"Unsupported shape {shape}"),
        };
    }

    private static Vec2 GetFeatureAnchorPoint(LocalBounds bounds, Anchor anchor)
    {
        var midX = (bounds.MinX + bounds.MaxX) / 2.0;
        var midY = (bounds.MinY + bounds.MaxY) / 2.0;

        return anchor switch
        {
            Anchor.TopLeft => new Vec2(bounds.MinX, bounds.MaxY),
            Anchor.TopCenter => new Vec2(midX, bounds.MaxY),
            Anchor.TopRight => new Vec2(bounds.MaxX, bounds.MaxY),
            Anchor.LeftCenter => new Vec2(bounds.MinX, midY),
            Anchor.Center => new Vec2(midX, midY),
            Anchor.RightCenter => new Vec2(bounds.MaxX, midY),
            Anchor.BottomLeft => new Vec2(bounds.MinX, bounds.MinY),
            Anchor.BottomCenter => new Vec2(midX, bounds.MinY),
            Anchor.BottomRight => new Vec2(bounds.MaxX, bounds.MinY),
            _ => new Vec2(midX, midY),
        };
    }

    public static CuttablePath BuildSlotRectangle(SlotSpec slot, double kerf, Vec2 translation, PipelineLogger? logger = null)
    {
        logger?.Log($"[cutout] Building slot rectangle at ({slot.U},{slot.V}) size=({slot.Width},{slot.Height})");
        // Kerf applies to the slot's length (finger-span) direction only.
        // The depth dimension is the material thickness t, which is uncut in that
        // direction and must be left at the exact specified value.
        var rawW = Math.Abs(slot.Width);
        var rawH = Math.Abs(slot.Height);
        double w, h;
        if (rawW >= rawH) { w = rawW - kerf; h = rawH; }
        else              { w = rawW;        h = rawH - kerf; }
        var cx = slot.U + translation.X;
        var cy = slot.V + translation.Y;
        return BuildRectangle(cx, cy, w, h);
    }

    public static CuttablePath BuildObliqueSlotRectangle(ObliqueSlotSpec obl, double kerf, Vec2 translation)
    {
        // Kerf applies to the span-length direction; the width (material thickness) is exact.
        var halfLen = Math.Max(0, (obl.Length - kerf) / 2.0);
        var halfWidth = obl.Width / 2.0;
        var cx = obl.Center.X + translation.X;
        var cy = obl.Center.Y + translation.Y;

        var p1 = new Vec2(cx - halfLen * obl.Dir.X - halfWidth * obl.Perp.X,
                          cy - halfLen * obl.Dir.Y - halfWidth * obl.Perp.Y);
        var p2 = new Vec2(cx + halfLen * obl.Dir.X - halfWidth * obl.Perp.X,
                          cy + halfLen * obl.Dir.Y - halfWidth * obl.Perp.Y);
        var p3 = new Vec2(cx + halfLen * obl.Dir.X + halfWidth * obl.Perp.X,
                          cy + halfLen * obl.Dir.Y + halfWidth * obl.Perp.Y);
        var p4 = new Vec2(cx - halfLen * obl.Dir.X + halfWidth * obl.Perp.X,
                          cy - halfLen * obl.Dir.Y + halfWidth * obl.Perp.Y);

        return new CuttablePath
        {
            Start = p1,
            Segments = new List<PathSegment>
            {
                new LineSegment(p2),
                new LineSegment(p3),
                new LineSegment(p4),
                new LineSegment(p1),
            },
            Closed = true,
        };
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

    private static CuttablePath BuildEdgeDip(
        Vec2 center,
        double width,
        double height,
        double radius,
        double innerRadius,
        Anchor anchor,
        double kerf,
        Vec2 translation,
        PipelineLogger? logger = null)
    {
        logger?.Log($"[cutout] Building edge-dip at center=({center.X},{center.Y}) anchor={anchor}");
        var halfW = Math.Max(0, width - kerf) / 2.0;
        var H = Math.Max(0, height - kerf);
        var R = radius;
        var r = innerRadius;
        const double Overshoot = 2.0;

        var cx = center.X + translation.X;
        var cy = center.Y + translation.Y;

        Vec2 T(double x, double y) => anchor switch
        {
            Anchor.BottomCenter => new Vec2(cx + x, cy - y),
            Anchor.LeftCenter   => new Vec2(cx - y, cy + x),
            Anchor.RightCenter  => new Vec2(cx + y, cy + x),
            _                   => new Vec2(cx + x, cy + y), // TopCenter
        };

        // bottom-center and right-center have det=-1 (orientation-reversing), so flip arc direction
        var cw = anchor is not (Anchor.BottomCenter or Anchor.RightCenter);

        var segs = new List<PathSegment>();

        // Overshoot past edge so clipper trims cleanly
        segs.Add(new LineSegment(T(-halfW - R, Overshoot)));
        segs.Add(new LineSegment(T(+halfW + R, Overshoot)));
        segs.Add(new LineSegment(T(+halfW + R, 0)));

        if (R > 0)
            segs.Add(new ArcSegment(T(+halfW, -R), R, !cw, LargeArc: false));

        segs.Add(new LineSegment(T(+halfW, -(H - r))));

        if (r > 0)
            segs.Add(new ArcSegment(T(+(halfW - r), -H), r, cw, LargeArc: false));

        segs.Add(new LineSegment(T(-(halfW - r), -H)));

        if (r > 0)
            segs.Add(new ArcSegment(T(-halfW, -(H - r)), r, cw, LargeArc: false));

        segs.Add(new LineSegment(T(-halfW, -R)));

        if (R > 0)
            segs.Add(new ArcSegment(T(-halfW - R, 0), R, !cw, LargeArc: false));

        return new CuttablePath
        {
            Start = T(-halfW - R, 0),
            Segments = segs,
            Closed = true,
        };
    }
}
