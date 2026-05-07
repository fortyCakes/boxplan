using BoxPlanLib.Model;

namespace BoxPlanLib.Parsing;

public sealed class FitResolver
{
    public ParseResult<BoxPlan> Resolve(BoxPlan plan, double materialThickness = 0)
    {
        var errors = new List<PlanError>();
        var usedAsCellTarget = new HashSet<string>(StringComparer.Ordinal);

        for (var pi = 0; pi < plan.Shapes.Count; pi++)
        {
            var parent = plan.Shapes[pi];
            if (parent.Inserts.Count == 0)
            {
                continue;
            }

            var parentPath = $"shapes[{pi}]";
            var parentDims = (parent as BoxShape)?.Dimensions;

            if (parentDims is null)
            {
                errors.Add(new PlanError(
                    Severity.Error,
                    $"Shape '{parent.Id}' has inserts but no resolved dimensions; nested fit:cell parents are not supported.",
                    parentPath));
                continue;
            }

            for (var ii = 0; ii < parent.Inserts.Count; ii++)
            {
                var insert = parent.Inserts[ii];
                var insertPath = $"{parentPath}.inserts[{ii}]";

                if (insert.Cell is not { } cell)
                {
                    errors.Add(new PlanError(
                        Severity.Error,
                        "Insert without a cell address cannot be resolved.",
                        insertPath));
                    continue;
                }

                if (insert.Target is not null)
                {
                    usedAsCellTarget.Add(insert.Target.Id);
                }

                var (cellOrigin, cellSize) = CellBounds(parentDims.Value, parent.Dividers, cell);
                ResolveInsert(insert, cellOrigin, cellSize, materialThickness, insertPath, errors);
            }
        }

        foreach (var shape in plan.Shapes)
        {
            if (shape.Fit is not null && !usedAsCellTarget.Contains(shape.Id))
            {
                errors.Add(new PlanError(
                    Severity.Error,
                    $"Shape '{shape.Id}' uses 'fit' but is never placed by an insert with a cell.",
                    null));
            }
        }

        if (errors.Any(e => e.Severity == Severity.Error))
        {
            return ParseResult<BoxPlan>.Fail(errors);
        }
        return ParseResult<BoxPlan>.Ok(plan, errors);
    }

    private static void ResolveInsert(
        Insert insert,
        Vec3 cellOrigin,
        Vec3 cellSize,
        double materialThickness,
        string path,
        List<PlanError> errors)
    {
        var target = insert.Target;
        var box = target as BoxShape;

        if (box?.Dimensions is { } fixedDims)
        {
            insert.ResolvedDimensions = fixedDims;
            insert.ResolvedLocation = cellOrigin;
            return;
        }

        if (target.Fit is not { } fit)
        {
            errors.Add(new PlanError(
                Severity.Error,
                $"Insert target '{target.Id}' has neither dimensions nor fit.",
                path));
            return;
        }

        var clearance = fit.Clearance;
        var (rx, ox, okX) = ResolveAxis(fit.Width, cellSize.X, cellOrigin.X, clearance, materialThickness, "width", path, errors);
        var (ry, oy, okY) = ResolveAxis(fit.Height, cellSize.Y, cellOrigin.Y, clearance, materialThickness, "height", path, errors);
        var (rz, oz, okZ) = ResolveAxis(fit.Depth, cellSize.Z, cellOrigin.Z, clearance, materialThickness, "depth", path, errors);

        if (!okX || !okY || !okZ)
        {
            return;
        }

        insert.ResolvedDimensions = new Vec3(rx, ry, rz);
        insert.ResolvedLocation = new Vec3(ox, oy, oz);
    }

    private static (double Size, double Origin, bool Ok) ResolveAxis(
        FitDimension dim,
        double cellSize,
        double cellOrigin,
        double clearance,
        double materialThickness,
        string label,
        string path,
        List<PlanError> errors)
    {
        switch (dim)
        {
            case FitDimension.Fixed f:
                return (f.Value, cellOrigin, true);
            case FitDimension.Auto:
                var size = cellSize - 2 * clearance - materialThickness;
                if (size <= 0)
                {
                    errors.Add(new PlanError(
                        Severity.Error,
                        $"Fit clearance {clearance} and material thickness {materialThickness} leave no room on {label} (cell size {cellSize}).",
                        path));
                    return (0, 0, false);
                }
                return (size, cellOrigin + clearance + (materialThickness / 2), true);
            default:
                errors.Add(new PlanError(
                    Severity.Error,
                    $"Unsupported fit dimension on {label}.",
                    path));
                return (0, 0, false);
        }
    }

    private static (Vec3 Origin, Vec3 Size) CellBounds(
        Vec3 parentDims,
        IReadOnlyList<DividerSet> dividers,
        (int Col, int Row, int Layer) cell)
    {
        var (x0, x1) = AxisRange(Axis.X, parentDims.X, dividers, cell.Col);
        var (y0, y1) = AxisRange(Axis.Y, parentDims.Y, dividers, cell.Row);
        var (z0, z1) = AxisRange(Axis.Z, parentDims.Z, dividers, cell.Layer);
        return (new Vec3(x0, y0, z0), new Vec3(x1 - x0, y1 - y0, z1 - z0));
    }

    private static (double Lo, double Hi) AxisRange(
        Axis axis,
        double axisLength,
        IReadOnlyList<DividerSet> dividers,
        int index)
    {
        var positions = dividers.FirstOrDefault(d => d.Axis == axis)?.Positions;
        if (positions is null || positions.Count == 0)
        {
            return (0, axisLength);
        }
        var lo = index == 0 ? 0 : positions[index - 1];
        var hi = index >= positions.Count ? axisLength : positions[index];
        return (lo, hi);
    }
}
