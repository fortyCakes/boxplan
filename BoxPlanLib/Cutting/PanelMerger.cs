using BoxPlanLib.Model;
using Clipper2Lib;

namespace BoxPlanLib.Cutting;

// Groups co-planar PanelShapes (those sharing a world Y value) and unions any
// whose world-coord polygons share an edge. Each connected output polygon
// becomes one BoxPlanCuttableShape; features from member panels are translated
// into the merged shape's local coordinate system.
internal static class PanelMerger
{
    private const int ClipperPrecision = 8;
    private const double PlaneEpsilon = 1e-6;

    public sealed record PanelInfo(
        PanelShape Panel,
        List<Vec2> WorldPolygon,
        Vec2 WorldMin,
        double PanelU,
        double PanelV);

    // Returns merged cuttable shapes plus the set of panel IDs absorbed into a
    // multi-member merge. Singleton groups (panels whose polygons didn't fuse
    // with anything) are emitted via the standard PanelFaceBuilder by the caller.
    public static (List<BoxPlanCuttableShape> Merged, HashSet<string> MergedIds) Build(
        IReadOnlyList<PanelShape> panels,
        BoxPlanSettings settings,
        PipelineLogger? logger)
    {
        var merged = new List<BoxPlanCuttableShape>();
        var mergedIds = new HashSet<string>();
        if (panels.Count == 0) return (merged, mergedIds);

        // Pre-compute world polygon and bbox for each merge candidate.
        var infos = panels.Select(BuildInfo).ToList();

        // Partition by world Y plane.
        var planeGroups = infos
            .GroupBy(i => Math.Round(i.Panel.Location.Y / PlaneEpsilon) * PlaneEpsilon)
            .ToList();

        foreach (var planeGroup in planeGroups)
        {
            var planeMembers = planeGroup.ToList();
            if (planeMembers.Count < 2) continue;

            var subject = new PathsD();
            foreach (var info in planeMembers)
                subject.Add(ToClipperPath(info.WorldPolygon));

            var unioned = Clipper.Union(subject, new PathsD(), FillRule.NonZero, ClipperPrecision);
            if (unioned.Count == 0) continue;

            var connectedCount = unioned.Count(p => Clipper.Area(p) > 1e-9);
            if (connectedCount >= planeMembers.Count)
            {
                // No fusion occurred — every panel is its own connected polygon.
                continue;
            }

            var groupIndex = 0;
            foreach (var outPath in unioned)
            {
                if (outPath.Count < 3 || Clipper.Area(outPath) <= 1e-9) continue;

                var membersInOutput = FindMembers(outPath, planeMembers);
                if (membersInOutput.Count < 2)
                {
                    // Standalone within this plane — let the per-shape path emit it.
                    continue;
                }

                var id = $"merged-panel-{string.Join("+", membersInOutput.Select(m => m.Panel.Id))}";
                merged.Add(BuildMergedShape(id, outPath, membersInOutput, settings, logger));
                foreach (var m in membersInOutput)
                    mergedIds.Add(m.Panel.Id);
                groupIndex++;
            }
        }

        return (merged, mergedIds);
    }

    private static PanelInfo BuildInfo(PanelShape panel)
    {
        var rawPoly = PrismGeometry.DiscretiseProfile(panel.Profile);
        var minX = rawPoly.Min(v => v.X);
        var minZ = rawPoly.Min(v => v.Y);
        var maxX = rawPoly.Max(v => v.X);
        var maxZ = rawPoly.Max(v => v.Y);
        var width = maxX - minX;
        var height = maxZ - minZ;

        double offsetX, offsetZ;
        if (panel.Origin == Origin.Center)
        {
            offsetX = panel.Location.X - width / 2.0 - minX;
            offsetZ = panel.Location.Z - height / 2.0 - minZ;
        }
        else
        {
            offsetX = panel.Location.X - minX;
            offsetZ = panel.Location.Z - minZ;
        }

        var worldPoly = rawPoly.Select(v => new Vec2(v.X + offsetX, v.Y + offsetZ)).ToList();
        var worldMin = new Vec2(offsetX + minX, offsetZ + minZ);
        return new PanelInfo(panel, worldPoly, worldMin, width, height);
    }

    private static List<PanelInfo> FindMembers(PathD outPath, IReadOnlyList<PanelInfo> candidates)
    {
        var pathPoly = outPath.Select(p => new Vec2(p.x, p.y)).ToList();
        var result = new List<PanelInfo>();
        foreach (var info in candidates)
        {
            var center = new Vec2(
                info.WorldMin.X + info.PanelU / 2.0,
                info.WorldMin.Y + info.PanelV / 2.0);
            if (PointInPolygon(center, pathPoly))
                result.Add(info);
        }
        return result;
    }

    private static BoxPlanCuttableShape BuildMergedShape(
        string id,
        PathD worldPath,
        IReadOnlyList<PanelInfo> members,
        BoxPlanSettings settings,
        PipelineLogger? logger)
    {
        var minX = worldPath.Min(p => p.x);
        var minZ = worldPath.Min(p => p.y);
        var localPoly = worldPath.Select(p => new Vec2(p.x - minX, p.y - minZ)).ToList();

        logger?.Log($"[panel-merge] Building merged panel {id} with {members.Count} member(s)");

        var (path, bbMin, bbMax, translation) = KerfOffset.OffsetOutwardAndTranslate(localPoly, settings.Kerf, logger);

        var interiorCuts = new List<CuttablePath>();
        var engravings = new List<CuttablePath>();
        var textEngravings = new List<TextEngraving>();

        foreach (var member in members)
        {
            var memberOffset = new Vec2(member.WorldMin.X - minX, member.WorldMin.Y - minZ);
            var adj = new Vec2(translation.X + memberOffset.X, translation.Y + memberOffset.Y);
            var zone = new CutoutBuilder.SafeZone(UMin: 0, UMax: member.PanelU, VMin: 0, VMax: member.PanelV);

            foreach (var feature in member.Panel.Features)
            {
                switch (feature)
                {
                    case CutoutFeature cutout:
                        var seed = CutoutBuilder.ResolveCenter(cutout.Position, member.PanelU, member.PanelV, logger);
                        foreach (var center in CutoutBuilder.ExpandCenters(cutout, seed, zone))
                        {
                            var cutPath = CutoutBuilder.Build(cutout, center, settings.Kerf, adj, logger);
                            interiorCuts.AddRange(CutoutClipper.ClipToOutline(cutPath, path, logger));
                        }
                        break;
                    case LineEngravingFeature lineEngraving:
                        var lineCenter = CutoutBuilder.ResolveCenter(lineEngraving.Position, member.PanelU, member.PanelV, logger);
                        var engravingPath = CutoutBuilder.Build(
                            lineEngraving.Shape, lineEngraving.Width, lineEngraving.Height,
                            lineCenter, kerf: 0, adj, logger);
                        engravings.AddRange(CutoutClipper.ClipToOutline(engravingPath, path, logger));
                        break;
                    case EngravingGridFeature grid:
                        foreach (var gridLine in EngravingBuilder.BuildGrid(member.PanelU, member.PanelV, grid.CellSize, grid.Center, adj))
                            engravings.AddRange(CutoutClipper.ClipToOutline(gridLine, path, logger));
                        break;
                    case EngravingTextFeature text:
                        var textCenter = CutoutBuilder.ResolveCenter(text.Position, member.PanelU, member.PanelV, logger);
                        var anchorPoint = new Vec2(textCenter.X + adj.X, textCenter.Y + adj.Y);
                        if (CutoutClipper.IsInsideOutline(anchorPoint, path))
                        {
                            textEngravings.Add(new TextEngraving
                            {
                                Text = text.Text,
                                X = anchorPoint.X,
                                Y = anchorPoint.Y,
                                Anchor = text.Position?.Anchor ?? Anchor.Center,
                                Size = text.Size,
                                Font = text.Font,
                                Bold = text.Bold,
                                Italic = text.Italic,
                            });
                        }
                        break;
                }
            }
        }

        return new BoxPlanCuttableShape
        {
            Id = id,
            BoundingBoxMin = bbMin,
            BoundingBoxMax = bbMax,
            Outline = path,
            InteriorCuts = interiorCuts,
            Engravings = engravings,
            TextEngravings = textEngravings,
        };
    }

    private static PathD ToClipperPath(IReadOnlyList<Vec2> polygon)
    {
        var path = new PathD(polygon.Count);
        foreach (var v in polygon) path.Add(new PointD(v.X, v.Y));
        return path;
    }

    private static bool PointInPolygon(Vec2 point, IReadOnlyList<Vec2> polygon)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var intersects = ((a.Y > point.Y) != (b.Y > point.Y)) &&
                (point.X < (b.X - a.X) * (point.Y - a.Y) / ((b.Y - a.Y) + 1e-12) + a.X);
            if (intersects) inside = !inside;
        }
        return inside;
    }
}
