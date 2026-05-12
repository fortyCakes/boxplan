using BoxPlanLib.Model;

namespace BoxPlanLib.Cutting;

// Emits a single flat cuttable face for a PanelShape. No finger joints — the
// outline is the profile polygon translated to origin (0,0), with cutouts,
// engravings and grids applied in the panel's local coordinate system.
internal static class PanelFaceBuilder
{
    public static BoxPlanCuttableShape? Build(
        string shapeId,
        PanelShape panel,
        BoxPlanSettings settings,
        PipelineLogger? logger)
    {
        var faceClosed = panel.Faces.Any(f => f.Name == FaceName.Front && f.Type == FaceType.Closed);
        if (!faceClosed)
        {
            logger?.Log($"[panel] Skipping panel {shapeId} — front face is open");
            return null;
        }

        var rawPoly = PrismGeometry.DiscretiseProfile(panel.Profile);
        if (rawPoly.Count < 3)
        {
            logger?.Warn($"[panel] Panel {shapeId} discretised to fewer than 3 vertices");
            return null;
        }

        var minX = rawPoly.Min(v => v.X);
        var minZ = rawPoly.Min(v => v.Y);
        var maxX = rawPoly.Max(v => v.X);
        var maxZ = rawPoly.Max(v => v.Y);
        var poly = rawPoly.Select(v => new Vec2(v.X - minX, v.Y - minZ)).ToList();
        var panelU = maxX - minX;
        var panelV = maxZ - minZ;

        logger?.Log($"[panel] Building panel {shapeId} with bbox {panelU:F3}x{panelV:F3}");

        var (path, bbMin, bbMax, translation) = KerfOffset.OffsetOutwardAndTranslate(poly, settings.Kerf, logger);

        var interiorCuts = new List<CuttablePath>();
        var engravings = new List<CuttablePath>();
        var textEngravings = new List<TextEngraving>();

        // No finger-joint margins — the entire panel interior is fair game.
        var zone = new CutoutBuilder.SafeZone(UMin: 0, UMax: panelU, VMin: 0, VMax: panelV);
        foreach (var feature in panel.Features)
        {
            switch (feature)
            {
                case CutoutFeature cutout:
                    var seed = CutoutBuilder.ResolvePlacementCenter(cutout.Position, cutout.Shape, cutout.Width, cutout.Height, panelU, panelV, settings.Kerf, logger);
                    foreach (var center in CutoutBuilder.ExpandCenters(cutout, seed, zone, settings.Kerf))
                    {
                        var cutPath = CutoutBuilder.Build(cutout, center, settings.Kerf, translation, logger);
                        interiorCuts.AddRange(CutoutClipper.ClipToOutline(cutPath, path, logger));
                    }
                    break;
                case LineEngravingFeature lineEngraving:
                    var lineCenter = CutoutBuilder.ResolvePlacementCenter(lineEngraving.Position, lineEngraving.Shape, lineEngraving.Width, lineEngraving.Height, panelU, panelV, kerf: 0, logger);
                    var engravingPath = CutoutBuilder.Build(
                        lineEngraving.Shape, lineEngraving.Width, lineEngraving.Height,
                        lineCenter, kerf: 0, translation, logger);
                    engravings.AddRange(CutoutClipper.ClipToOutline(engravingPath, path, logger));
                    break;
                case EngravingGridFeature grid:
                    foreach (var gridLine in EngravingBuilder.BuildGrid(panelU, panelV, grid.CellSize, grid.Center, translation))
                        engravings.AddRange(CutoutClipper.ClipToOutline(gridLine, path, logger));
                    break;
                case EngravingTextFeature text:
                    var textCenter = CutoutBuilder.ResolveCenter(text.Position, panelU, panelV, logger);
                    var anchorPoint = new Vec2(textCenter.X + translation.X, textCenter.Y + translation.Y);
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

        return new BoxPlanCuttableShape
        {
            Id = shapeId,
            BoundingBoxMin = bbMin,
            BoundingBoxMax = bbMax,
            Outline = path,
            InteriorCuts = interiorCuts,
            Engravings = engravings,
            TextEngravings = textEngravings,
        };
    }
}
