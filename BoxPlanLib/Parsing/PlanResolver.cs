using BoxPlanLib.Model;

namespace BoxPlanLib.Parsing;

public sealed class PlanResolver
{
    public ParseResult<BoxPlan> Resolve(RawPlan raw)
    {
        var errors = new List<PlanError>();

        var rawById = new Dictionary<string, RawShape>();
        for (var i = 0; i < raw.Shapes.Count; i++)
        {
            var rs = raw.Shapes[i];
            var path = $"shapes[{i}]";
            if (string.IsNullOrEmpty(rs.Id))
            {
                errors.Add(Err($"Shape requires an 'id'.", path, rs));
                continue;
            }
            if (!rawById.TryAdd(rs.Id, rs))
            {
                errors.Add(Err($"Duplicate shape id '{rs.Id}'.", path, rs));
            }
        }

        var byId = new Dictionary<string, Shape>();
        var shapes = new List<Shape>();
        var pendingInsertRefs = new List<(Insert Insert, string RefId, string Path, IRawLocated? At)>();

        for (var i = 0; i < raw.Shapes.Count; i++)
        {
            var rs = raw.Shapes[i];
            if (string.IsNullOrEmpty(rs.Id) || !rawById.TryGetValue(rs.Id, out var canonical) || canonical != rs)
            {
                continue;
            }

            var path = $"shapes[{i}]";
            var resolved = ResolveShape(rs, path, errors, pendingInsertRefs);
            if (resolved is not null)
            {
                byId[rs.Id] = resolved;
                shapes.Add(resolved);
            }
        }

        foreach (var (insert, refId, path, at) in pendingInsertRefs)
        {
            if (byId.TryGetValue(refId, out var target))
            {
                insert.Target = target;
            }
            else
            {
                errors.Add(Err($"Insert references unknown shape '{refId}'.", path, at));
            }
        }

        if (errors.Any(e => e.Severity == Severity.Error))
        {
            return ParseResult<BoxPlan>.Fail(errors);
        }

        var plan = new BoxPlan { Shapes = shapes, ShapesById = byId };
        return ParseResult<BoxPlan>.Ok(plan, errors);
    }

    private static Shape? ResolveShape(
        RawShape raw,
        string path,
        List<PlanError> errors,
        List<(Insert, string, string, IRawLocated?)> pendingRefs)
    {
        return raw switch
        {
            RawBoxShape box => ResolveBoxShape(box, path, errors, pendingRefs),
            _ => Fail(errors, $"Unsupported shape type '{raw.Type}'.", path, raw),
        };
    }

    private static BoxShape? ResolveBoxShape(
        RawBoxShape raw,
        string path,
        List<PlanError> errors,
        List<(Insert, string, string, IRawLocated?)> pendingRefs)
    {
        var origin = ResolveOrigin(raw.Origin, $"{path}.origin", errors, raw) ?? Origin.BottomLeftFront;
        var location = ResolveVec3(raw.Location, 3, $"{path}.location", errors, raw) ?? Vec3.Zero;

        Vec3? dimensions = null;
        if (raw.Dimensions is not null)
        {
            dimensions = ResolveVec3(raw.Dimensions, 3, $"{path}.dimensions", errors, raw);
            if (dimensions is { } d && (d.X <= 0 || d.Y <= 0 || d.Z <= 0))
            {
                errors.Add(Err("Dimensions must be positive.", $"{path}.dimensions", raw));
            }
        }

        var fit = raw.Fit is null ? null : ResolveFit(raw.Fit, $"{path}.fit", errors);

        if (dimensions is null && fit is null)
        {
            errors.Add(Err("Box must specify either 'dimensions' or 'fit'.", path, raw));
        }
        if (dimensions is not null && fit is not null)
        {
            errors.Add(Err("Box may specify 'dimensions' or 'fit', not both.", path, raw));
        }

        var faces = ResolveFaces(raw.Faces, $"{path}.faces", errors);
        var dividers = ResolveDividers(raw.Dividers, dimensions, $"{path}.dividers", errors);
        var inserts = ResolveInserts(raw.Inserts, dividers, $"{path}.inserts", errors, pendingRefs);
        var features = ResolveFeatures(raw.Features, faces, $"{path}.features", errors);

        return new BoxShape
        {
            Id = raw.Id ?? string.Empty,
            Origin = origin,
            Location = location,
            Dimensions = dimensions,
            Fit = fit,
            Faces = faces,
            Dividers = dividers,
            Inserts = inserts,
            Features = features,
        };
    }

    private static IReadOnlyList<Face> ResolveFaces(
        List<RawFace>? raw,
        string path,
        List<PlanError> errors)
    {
        var byName = Enum.GetValues<FaceName>().ToDictionary(n => n, n => FaceType.Closed);

        if (raw is not null)
        {
            for (var i = 0; i < raw.Count; i++)
            {
                var rawFace = raw[i];
                var facePath = $"{path}[{i}]";
                if (string.IsNullOrEmpty(rawFace.Name))
                {
                    errors.Add(Err("Face requires a 'name'.", facePath, rawFace));
                    continue;
                }
                var name = ResolveFaceName(rawFace.Name, $"{facePath}.name", errors, rawFace);
                if (name is null) continue;

                var type = ResolveFaceType(rawFace.Type, $"{facePath}.type", errors, rawFace) ?? FaceType.Closed;
                byName[name.Value] = type;
            }
        }

        return Enum.GetValues<FaceName>()
            .Select(n => new Face(n, byName[n]))
            .ToArray();
    }

    private static IReadOnlyList<DividerSet> ResolveDividers(
        List<RawDividerSet>? raw,
        Vec3? dimensions,
        string path,
        List<PlanError> errors)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<DividerSet>();
        }

        var result = new List<DividerSet>();
        for (var i = 0; i < raw.Count; i++)
        {
            var rd = raw[i];
            var dpath = $"{path}[{i}]";
            var facing = rd.Facing is null ? null : ResolveFaceName(rd.Facing, $"{dpath}.facing", errors, rd);

            var hasSplit = rd.Split is not null;
            var hasAxis = rd.Axis is not null || rd.Positions is not null;

            if (hasSplit && hasAxis)
            {
                errors.Add(Err("Divider may use 'split' or 'axis'/'positions', not both.", dpath, rd));
                continue;
            }
            if (!hasSplit && !hasAxis)
            {
                errors.Add(Err("Divider must specify 'split' or 'axis'+'positions'.", dpath, rd));
                continue;
            }

            if (hasSplit)
            {
                if (dimensions is null)
                {
                    errors.Add(Err("'split' is only allowed on shapes with explicit 'dimensions'.", dpath, rd));
                    continue;
                }
                ExpandSplit(rd.Split!, dimensions.Value, facing, result, dpath, errors, rd);
            }
            else
            {
                var axis = ResolveAxis(rd.Axis, $"{dpath}.axis", errors, rd);
                if (axis is null) continue;
                if (rd.Positions is null || rd.Positions.Length == 0)
                {
                    errors.Add(Err("Divider 'positions' must contain at least one value.", $"{dpath}.positions", rd));
                    continue;
                }
                if (dimensions is { } dims)
                {
                    var bound = AxisLength(axis.Value, dims);
                    foreach (var p in rd.Positions)
                    {
                        if (p <= 0 || p >= bound)
                        {
                            errors.Add(Err(
                                $"Divider position {p} is outside interior 0..{bound} on axis {axis}.",
                                $"{dpath}.positions",
                                rd));
                        }
                    }
                }
                result.Add(new DividerSet
                {
                    Axis = axis.Value,
                    Positions = rd.Positions.OrderBy(x => x).ToArray(),
                    Facing = facing,
                });
            }
        }
        return result;
    }

    private static void ExpandSplit(
        RawSplit split,
        Vec3 dims,
        FaceName? facing,
        List<DividerSet> sink,
        string path,
        List<PlanError> errors,
        IRawLocated? at)
    {
        AddSplit(Axis.X, split.X, dims.X);
        AddSplit(Axis.Y, split.Y, dims.Y);
        AddSplit(Axis.Z, split.Z, dims.Z);
        return;

        void AddSplit(Axis axis, int? count, double length)
        {
            if (count is null) return;
            if (count.Value < 2)
            {
                errors.Add(Err($"split.{axis.ToString().ToLowerInvariant()} must be 2 or more.", path, at));
                return;
            }
            var positions = new double[count.Value - 1];
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i] = length * (i + 1) / count.Value;
            }
            sink.Add(new DividerSet
            {
                Axis = axis,
                Positions = positions,
                Facing = facing,
            });
        }
    }

    private static IReadOnlyList<Insert> ResolveInserts(
        List<RawInsert>? raw,
        IReadOnlyList<DividerSet> dividers,
        string path,
        List<PlanError> errors,
        List<(Insert, string, string, IRawLocated?)> pendingRefs)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<Insert>();
        }

        var (cols, rows, layers) = CellGrid(dividers);
        var result = new List<Insert>();

        for (var i = 0; i < raw.Count; i++)
        {
            var ri = raw[i];
            var ipath = $"{path}[{i}]";

            if (ri.Inline is not null)
            {
                errors.Add(Err("Inline insert shapes are not yet supported.", $"{ipath}.inline", ri));
                continue;
            }
            if (string.IsNullOrEmpty(ri.Ref))
            {
                errors.Add(Err("Insert must specify 'ref'.", ipath, ri));
                continue;
            }

            if (ri.Fill is not null)
            {
                if (!string.Equals(ri.Fill, "all-cells", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(Err($"Unknown fill mode '{ri.Fill}'.", $"{ipath}.fill", ri));
                    continue;
                }
                for (var l = 0; l < layers; l++)
                for (var r = 0; r < rows; r++)
                for (var c = 0; c < cols; c++)
                {
                    var insert = new Insert { Cell = (c, r, l) };
                    result.Add(insert);
                    pendingRefs.Add((insert, ri.Ref!, $"{ipath}.ref", ri));
                }
            }
            else if (ri.Cell is not null)
            {
                var cell = ParseCell(ri.Cell, ipath, errors, ri);
                if (cell is null) continue;
                if (cell.Value.Col < 0 || cell.Value.Col >= cols ||
                    cell.Value.Row < 0 || cell.Value.Row >= rows ||
                    cell.Value.Layer < 0 || cell.Value.Layer >= layers)
                {
                    errors.Add(Err(
                        $"Cell {cell.Value} is outside grid {cols}x{rows}x{layers}.",
                        $"{ipath}.cell",
                        ri));
                    continue;
                }
                var insert = new Insert { Cell = cell };
                result.Add(insert);
                pendingRefs.Add((insert, ri.Ref!, $"{ipath}.ref", ri));
            }
            else
            {
                errors.Add(Err("Insert must specify 'fill' or 'cell'.", ipath, ri));
            }
        }

        return result;
    }

    private static (int Col, int Row, int Layer)? ParseCell(int[] cell, string path, List<PlanError> errors, IRawLocated? at)
    {
        return cell.Length switch
        {
            2 => (cell[0], cell[1], 0),
            3 => (cell[0], cell[1], cell[2]),
            _ => Fail<(int, int, int)?>(errors, "Cell must have 2 or 3 integers.", $"{path}.cell", at),
        };
    }

    private static (int Cols, int Rows, int Layers) CellGrid(IReadOnlyList<DividerSet> dividers)
    {
        var cols = 1;
        var rows = 1;
        var layers = 1;
        foreach (var d in dividers)
        {
            switch (d.Axis)
            {
                case Axis.X: cols = d.Positions.Count + 1; break;
                case Axis.Y: rows = d.Positions.Count + 1; break;
                case Axis.Z: layers = d.Positions.Count + 1; break;
            }
        }
        return (cols, rows, layers);
    }

    private static IReadOnlyList<Feature> ResolveFeatures(
        List<RawFeature>? raw,
        IReadOnlyList<Face> faces,
        string path,
        List<PlanError> errors)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<Feature>();
        }

        var legal = faces.Select(f => f.Name).ToHashSet();
        var result = new List<Feature>();
        for (var i = 0; i < raw.Count; i++)
        {
            var rf = raw[i];
            var fpath = $"{path}[{i}]";
            var face = ResolveFaceName(rf.Face, $"{fpath}.face", errors, rf);
            if (face is null) continue;
            if (!legal.Contains(face.Value))
            {
                errors.Add(Err($"Face '{face}' is not a valid face for this shape.", $"{fpath}.face", rf));
                continue;
            }
            var position = rf.Position is null ? null : ResolvePosition(rf.Position, $"{fpath}.position", errors);

            switch (rf)
            {
                case RawCutoutFeature cutout:
                    var feature = ResolveCutout(cutout, face.Value, position, fpath, errors);
                    if (feature is not null) result.Add(feature);
                    break;
                case RawEngravingFeature engraving:
                    var engravingFeature = ResolveEngraving(engraving, face.Value, position, fpath, errors);
                    if (engravingFeature is not null) result.Add(engravingFeature);
                    break;
                case RawLineEngravingFeature lineEngraving:
                    var lineEngravingFeature = ResolveLineEngraving(lineEngraving, face.Value, position, fpath, errors);
                    if (lineEngravingFeature is not null) result.Add(lineEngravingFeature);
                    break;
                case RawEngravingGridFeature grid:
                    var gridFeature = ResolveEngravingGrid(grid, face.Value, fpath, errors);
                    if (gridFeature is not null) result.Add(gridFeature);
                    break;
                default:
                    errors.Add(Err($"Unsupported feature type '{rf.Type}'.", fpath, rf));
                    break;
            }
        }
        return result;
    }

    private static CutoutFeature? ResolveCutout(
        RawCutoutFeature raw,
        FaceName face,
        Position? position,
        string path,
        List<PlanError> errors)
    {
        if (string.IsNullOrEmpty(raw.Shape))
        {
            errors.Add(Err("Cutout requires 'shape'.", $"{path}.shape", raw));
            return null;
        }

        if (!Enum.TryParse<CutoutShape>(raw.Shape, ignoreCase: true, out var shape))
        {
            errors.Add(Err($"Unknown cutout shape '{raw.Shape}'.", $"{path}.shape", raw));
            return null;
        }

        double width;
        double height;
        if (raw.Diameter is { } d)
        {
            if (raw.Width is not null || raw.Height is not null)
            {
                errors.Add(Err("Cutout may specify 'diameter' or 'width'+'height', not both.", path, raw));
                return null;
            }
            width = d;
            height = d;
        }
        else if (raw.Width is { } w && raw.Height is { } h)
        {
            width = w;
            height = h;
        }
        else
        {
            errors.Add(Err("Cutout requires 'diameter' or both 'width' and 'height'.", path, raw));
            return null;
        }

        return new CutoutFeature
        {
            Face = face,
            Position = position,
            Shape = shape,
            Width = width,
            Height = height,
        };
    }

    private static EngravingTextFeature? ResolveEngraving(
        RawEngravingFeature raw,
        FaceName face,
        Position? position,
        string path,
        List<PlanError> errors)
    {
        if (string.IsNullOrEmpty(raw.Text))
        {
            errors.Add(Err("Engraving requires 'text'.", $"{path}.text", raw));
            return null;
        }

        if (raw.Size is not { } size || size <= 0)
        {
            errors.Add(Err("Engraving requires a positive 'size'.", $"{path}.size", raw));
            return null;
        }

        var bold = false;
        var italic = false;
        if (!string.IsNullOrEmpty(raw.Style))
        {
            var styles = raw.Style.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var s in styles)
            {
                if (s.Equals("bold", StringComparison.OrdinalIgnoreCase)) bold = true;
                else if (s.Equals("italic", StringComparison.OrdinalIgnoreCase)) italic = true;
                else errors.Add(Err($"Unknown style value '{s}'.", $"{path}.style", raw));
            }
        }

        return new EngravingTextFeature
        {
            Face = face,
            Position = position,
            Text = raw.Text,
            Size = size,
            Font = string.IsNullOrEmpty(raw.Font) ? "sans-serif" : raw.Font,
            Bold = bold,
            Italic = italic,
        };
    }

    private static LineEngravingFeature? ResolveLineEngraving(
        RawLineEngravingFeature raw,
        FaceName face,
        Position? position,
        string path,
        List<PlanError> errors)
    {
        if (string.IsNullOrEmpty(raw.Shape))
        {
            errors.Add(Err("Line engraving requires 'shape'.", $"{path}.shape", raw));
            return null;
        }

        if (!Enum.TryParse<CutoutShape>(raw.Shape, ignoreCase: true, out var shape))
        {
            errors.Add(Err($"Unknown shape '{raw.Shape}'.", $"{path}.shape", raw));
            return null;
        }

        double width;
        double height;
        if (raw.Diameter is { } d)
        {
            if (raw.Width is not null || raw.Height is not null)
            {
                errors.Add(Err("Line engraving may specify 'diameter' or 'width'+'height', not both.", path, raw));
                return null;
            }
            width = d;
            height = d;
        }
        else if (raw.Width is { } w && raw.Height is { } h)
        {
            width = w;
            height = h;
        }
        else
        {
            errors.Add(Err("Line engraving requires 'diameter' or both 'width' and 'height'.", path, raw));
            return null;
        }

        return new LineEngravingFeature
        {
            Face = face,
            Position = position,
            Shape = shape,
            Width = width,
            Height = height,
        };
    }

    private static EngravingGridFeature? ResolveEngravingGrid(
        RawEngravingGridFeature raw,
        FaceName face,
        string path,
        List<PlanError> errors)
    {
        if (raw.CellSize is not { } cellSize || cellSize <= 0)
        {
            errors.Add(Err("Engraving grid requires a positive 'cell-size'.", $"{path}.cell-size", raw));
            return null;
        }

        var center = GridCenter.Space;
        if (!string.IsNullOrEmpty(raw.Center))
        {
            if (!Enum.TryParse<GridCenter>(raw.Center, ignoreCase: true, out center))
            {
                errors.Add(Err($"Unknown grid center '{raw.Center}'. Expected 'space', 'corner', or 'maximize'.", $"{path}.center", raw));
                return null;
            }
        }

        return new EngravingGridFeature
        {
            Face = face,
            CellSize = cellSize,
            Center = center,
        };
    }

    private static Position? ResolvePosition(RawPosition raw, string path, List<PlanError> errors)
    {
        var anchor = ResolveAnchor(raw.Anchor, $"{path}.anchor", errors, raw);
        if (anchor is null) return null;
        var offset = raw.Offset is null
            ? Vec2.Zero
            : ResolveVec2(raw.Offset, $"{path}.offset", errors, raw) ?? Vec2.Zero;
        return new Position(anchor.Value, offset);
    }

    private static Fit? ResolveFit(RawFit raw, string path, List<PlanError> errors)
    {
        if (string.IsNullOrEmpty(raw.Mode))
        {
            errors.Add(Err("Fit requires 'mode'.", $"{path}.mode", raw));
            return null;
        }
        if (!Enum.TryParse<FitMode>(raw.Mode, ignoreCase: true, out var mode))
        {
            errors.Add(Err($"Unknown fit mode '{raw.Mode}'.", $"{path}.mode", raw));
            return null;
        }

        return new Fit
        {
            Mode = mode,
            Clearance = raw.Clearance ?? 0.0,
            Width = ToFitDimension(raw.Width),
            Height = ToFitDimension(raw.Height),
            Depth = ToFitDimension(raw.Depth),
        };
    }

    private static FitDimension ToFitDimension(RawFitDimension? raw) =>
        raw is null || raw.Value.IsAuto
            ? FitDimension.Auto.Instance
            : new FitDimension.Fixed(raw.Value.Value);

    private static double AxisLength(Axis axis, Vec3 dims) => axis switch
    {
        Axis.X => dims.X,
        Axis.Y => dims.Y,
        Axis.Z => dims.Z,
        _ => 0,
    };

    private static Vec3? ResolveVec3(double[]? raw, int expected, string path, List<PlanError> errors, IRawLocated? at)
    {
        if (raw is null) return null;
        if (raw.Length != expected)
        {
            errors.Add(Err($"Expected {expected} numbers, got {raw.Length}.", path, at));
            return null;
        }
        return new Vec3(raw[0], raw[1], raw[2]);
    }

    private static Vec2? ResolveVec2(double[]? raw, string path, List<PlanError> errors, IRawLocated? at)
    {
        if (raw is null) return null;
        if (raw.Length != 2)
        {
            errors.Add(Err($"Expected 2 numbers, got {raw.Length}.", path, at));
            return null;
        }
        return new Vec2(raw[0], raw[1]);
    }

    private static readonly Dictionary<string, Origin> _origins = new(StringComparer.OrdinalIgnoreCase)
    {
        { "bottom-left-front", Origin.BottomLeftFront },
        { "center", Origin.Center },
    };

    private static readonly Dictionary<string, FaceName> _faces = new(StringComparer.OrdinalIgnoreCase)
    {
        { "top", FaceName.Top },
        { "bottom", FaceName.Bottom },
        { "left", FaceName.Left },
        { "right", FaceName.Right },
        { "front", FaceName.Front },
        { "back", FaceName.Back },
    };

    private static readonly Dictionary<string, FaceType> _faceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "open", FaceType.Open },
        { "closed", FaceType.Closed },
    };

    private static readonly Dictionary<string, Axis> _axes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "x", Axis.X },
        { "y", Axis.Y },
        { "z", Axis.Z },
    };

    private static readonly Dictionary<string, Anchor> _anchors = new(StringComparer.OrdinalIgnoreCase)
    {
        { "top-center", Anchor.TopCenter },
        { "bottom-center", Anchor.BottomCenter },
        { "left-center", Anchor.LeftCenter },
        { "right-center", Anchor.RightCenter },
        { "center", Anchor.Center },
    };

    private static Origin? ResolveOrigin(string? raw, string path, List<PlanError> errors, IRawLocated? at) =>
        ResolveEnum(raw, _origins, "origin", path, errors, at);

    private static FaceName? ResolveFaceName(string? raw, string path, List<PlanError> errors, IRawLocated? at) =>
        ResolveEnum(raw, _faces, "face name", path, errors, at);

    private static FaceType? ResolveFaceType(string? raw, string path, List<PlanError> errors, IRawLocated? at) =>
        ResolveEnum(raw, _faceTypes, "face type", path, errors, at);

    private static Axis? ResolveAxis(string? raw, string path, List<PlanError> errors, IRawLocated? at) =>
        ResolveEnum(raw, _axes, "axis", path, errors, at);

    private static Anchor? ResolveAnchor(string? raw, string path, List<PlanError> errors, IRawLocated? at) =>
        ResolveEnum(raw, _anchors, "anchor", path, errors, at);

    private static T? ResolveEnum<T>(string? raw, Dictionary<string, T> table, string label, string path, List<PlanError> errors, IRawLocated? at)
        where T : struct
    {
        if (raw is null) return null;
        if (table.TryGetValue(raw, out var value)) return value;
        errors.Add(Err($"Unknown {label} '{raw}'.", path, at));
        return null;
    }

    private static PlanError Err(string message, string path, IRawLocated? at = null) =>
        new(Severity.Error, message, path, at?.SourceLocation?.Line, at?.SourceLocation?.Column);

    private static T? Fail<T>(List<PlanError> errors, string message, string path, IRawLocated? at = null)
    {
        errors.Add(Err(message, path, at));
        return default;
    }

    private static Shape? Fail(List<PlanError> errors, string message, string path, IRawLocated? at = null)
    {
        errors.Add(Err(message, path, at));
        return null;
    }
}

