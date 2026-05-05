using System.Globalization;
using System.Security;
using System.Text;
using BoxPlanLib.Model;

namespace BoxPlanLib.Svg;

internal sealed class SimpleSvgGenerator
{
    private const double StrokeWidthMm = 0.1;
    private const string OutlineColor = "red";
    private const string InteriorColor = "blue";
    private const string EngravingColor = "black";

    public string Generate(BoxPlanCuttableShape[] shapes, BoxPlanSettings settings)
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
        sb.Append("<g transform=\"translate(0 ").Append(F(totalH)).Append(") scale(1 -1)\">");

        double xCursor = 0;
        for (int i = 0; i < shapes.Length; i++)
        {
            var s = shapes[i];
            sb.Append("<g id=\"").Append(Escape(s.Id)).Append("\" transform=\"translate(")
              .Append(F(xCursor)).Append(" 0)\">");
            EmitPath(sb, s.Outline, s.BoundingBoxMin, OutlineColor);
            foreach (var p in s.InteriorCuts) EmitPath(sb, p, s.BoundingBoxMin, InteriorColor);
            foreach (var p in s.Engravings) EmitPath(sb, p, s.BoundingBoxMin, EngravingColor);
            sb.Append("</g>");
            xCursor += widths[i] + settings.Spacing;
        }

        sb.Append("</g></svg>");
        return sb.ToString();
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
}
