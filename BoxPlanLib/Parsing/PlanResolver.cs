using BoxPlanLib.Cutting;
using BoxPlanLib.Model;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BoxPlanLib.Parsing;

public sealed class PlanResolver
{
    private const double SplitCurveEps = 1e-6;

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
            RawPanelShape panel => ResolvePanelShape(panel, path, errors),
            RawPrismShapeBase prism => ResolvePrismShape(prism, path, errors, pendingRefs),
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
        var disjoint = raw.Disjoint ?? raw.Location is null;

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
        var inserts = ResolveInserts(raw.Inserts, dividers, faces, $"{path}.inserts", errors, pendingRefs);
        var features = ResolveFeatures(raw.Features, faces, $"{path}.features", errors);
        ValidateBoxSplitCuts(features, $"{path}.features", errors, raw);
        var scoops = ResolveScoops(raw.Scoops, dimensions, $"{path}.scoops", errors);

        return new BoxShape
        {
            Id = raw.Id ?? string.Empty,
            Origin = origin,
            Location = location,
            Disjoint = disjoint,
            Dimensions = dimensions,
            Fit = fit,
            Faces = faces,
            Dividers = dividers,
            Inserts = inserts,
            Features = features,
            Scoops = scoops,
        };
    }

    // The four faces sharing an edge with `face`. Used to validate that a scoop's
    // anchor edge sits on a face actually adjacent to the host.
    private static IReadOnlyList<FaceName> AdjacentFaces(FaceName face) => face switch
    {
        FaceName.Bottom or FaceName.Top
            => new[] { FaceName.Front, FaceName.Right, FaceName.Back, FaceName.Left },
        FaceName.Front or FaceName.Back
            => new[] { FaceName.Bottom, FaceName.Right, FaceName.Top, FaceName.Left },
        FaceName.Left or FaceName.Right
            => new[] { FaceName.Bottom, FaceName.Back, FaceName.Top, FaceName.Front },
        _ => Array.Empty<FaceName>(),
    };

    // Opposing edge on the host face — used to enforce that opposing scoop insets don't cross.
    private static FaceName Opposite(FaceName face) => face switch
    {
        FaceName.Left => FaceName.Right,
        FaceName.Right => FaceName.Left,
        FaceName.Front => FaceName.Back,
        FaceName.Back => FaceName.Front,
        FaceName.Top => FaceName.Bottom,
        FaceName.Bottom => FaceName.Top,
        _ => face,
    };

    // Length of the host face's axis perpendicular to `edge` — used for inset bounds.
    private static double InsetAxisLength(FaceName host, FaceName edge, Vec3 dims) => (host, edge) switch
    {
        (FaceName.Bottom or FaceName.Top, FaceName.Left or FaceName.Right) => dims.X,
        (FaceName.Bottom or FaceName.Top, FaceName.Front or FaceName.Back) => dims.Z,
        (FaceName.Front or FaceName.Back, FaceName.Left or FaceName.Right) => dims.X,
        (FaceName.Front or FaceName.Back, FaceName.Bottom or FaceName.Top) => dims.Y,
        (FaceName.Left or FaceName.Right, FaceName.Front or FaceName.Back) => dims.Z,
        (FaceName.Left or FaceName.Right, FaceName.Bottom or FaceName.Top) => dims.Y,
        _ => 0,
    };

    // Length of the rise axis — outward-normal of the host face.
    private static double RiseAxisLength(FaceName host, Vec3 dims) => host switch
    {
        FaceName.Bottom or FaceName.Top => dims.Y,
        FaceName.Front or FaceName.Back => dims.Z,
        FaceName.Left or FaceName.Right => dims.X,
        _ => 0,
    };

    private static IReadOnlyList<Scoop> ResolveScoops(
        List<RawScoop>? raw,
        Vec3? dimensions,
        string path,
        List<PlanError> errors)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<Scoop>();
        }

        var seenEdges = new HashSet<(FaceName Face, FaceName Edge)>();
        var byFaceAxis = new Dictionary<(FaceName Face, FaceName InsetAxisEdge), double>();
        var result = new List<Scoop>();

        for (var i = 0; i < raw.Count; i++)
        {
            var rs = raw[i];
            var spath = $"{path}[{i}]";

            var face = ResolveFaceName(rs.Face, $"{spath}.face", errors, rs);
            if (face is null) continue;

            if (rs.Edge is null)
            {
                errors.Add(Err("Scoop requires 'edge'.", $"{spath}.edge", rs));
                continue;
            }

            if (rs.Inset is not { } inset || inset <= 0)
            {
                errors.Add(Err("Scoop 'inset' must be positive.", $"{spath}.inset", rs));
                continue;
            }
            if (rs.Rise is not { } rise || rise <= 0)
            {
                errors.Add(Err("Scoop 'rise' must be positive.", $"{spath}.rise", rs));
                continue;
            }

            var adjacent = AdjacentFaces(face.Value);
            var edges = ExpandEdges(rs.Edge, face.Value, adjacent, spath, errors, rs);
            if (edges is null) continue;

            foreach (var edge in edges)
            {
                if (!adjacent.Contains(edge))
                {
                    errors.Add(Err(
                        $"Edge '{edge.ToString().ToLowerInvariant()}' is not adjacent to face '{face.Value.ToString().ToLowerInvariant()}'.",
                        $"{spath}.edge",
                        rs));
                    continue;
                }
                if (!seenEdges.Add((face.Value, edge)))
                {
                    errors.Add(Err(
                        $"Duplicate scoop on face '{face.Value.ToString().ToLowerInvariant()}' edge '{edge.ToString().ToLowerInvariant()}'.",
                        spath,
                        rs));
                    continue;
                }

                if (dimensions is { } dims)
                {
                    var insetMax = InsetAxisLength(face.Value, edge, dims);
                    var riseMax = RiseAxisLength(face.Value, dims);
                    if (inset >= insetMax)
                    {
                        errors.Add(Err(
                            $"Scoop 'inset' {inset} exceeds host face dimension {insetMax} on the inset axis.",
                            $"{spath}.inset",
                            rs));
                        continue;
                    }
                    if (rise >= riseMax)
                    {
                        errors.Add(Err(
                            $"Scoop 'rise' {rise} exceeds adjacent wall height {riseMax}.",
                            $"{spath}.rise",
                            rs));
                        continue;
                    }

                    // Track combined inset for opposing pairs (same face, opposite edges
                    // share an inset axis).
                    var key = (face.Value, MinEdge(edge, Opposite(edge)));
                    byFaceAxis.TryGetValue(key, out var soFar);
                    var combined = soFar + inset;
                    if (combined > insetMax + 1e-9)
                    {
                        errors.Add(Err(
                            $"Opposing scoops on face '{face.Value.ToString().ToLowerInvariant()}' have combined inset {combined} > axis length {insetMax}.",
                            $"{spath}.inset",
                            rs));
                        continue;
                    }
                    byFaceAxis[key] = combined;
                }

                result.Add(new Scoop(face.Value, edge, inset, rise));
            }
        }

        return result;
    }

    private static FaceName MinEdge(FaceName a, FaceName b) =>
        (int)a < (int)b ? a : b;

    private static IReadOnlyList<FaceName>? ExpandEdges(
        RawScoopEdge raw,
        FaceName face,
        IReadOnlyList<FaceName> adjacent,
        string path,
        List<PlanError> errors,
        IRawLocated at)
    {
        if (raw.IsAllEdges)
        {
            return adjacent;
        }
        if (raw.Names.Count == 0)
        {
            errors.Add(Err("Scoop 'edge' list is empty.", $"{path}.edge", at));
            return null;
        }
        var result = new List<FaceName>(raw.Names.Count);
        foreach (var name in raw.Names)
        {
            var resolved = ResolveFaceName(name, $"{path}.edge", errors, at);
            if (resolved is null) return null;
            result.Add(resolved.Value);
        }
        return result;
    }

    // ── Panel resolution ─────────────────────────────────────────────────────

    private static PanelShape? ResolvePanelShape(
        RawPanelShape raw,
        string path,
        List<PlanError> errors)
    {
        var origin = ResolveOrigin(raw.Origin, $"{path}.origin", errors, raw) ?? Origin.BottomLeftFront;
        var location = ResolveVec3(raw.Location, 3, $"{path}.location", errors, raw) ?? Vec3.Zero;
        var disjoint = raw.Disjoint ?? raw.Location is null;

        if (raw.Profile is null)
        {
            errors.Add(Err("Panel must specify 'profile'.", path, raw));
            return null;
        }
        if (raw.Fit is not null)
        {
            errors.Add(Err("Panel does not support 'fit'.", $"{path}.fit", raw));
        }
        if (raw.Dividers is { Count: > 0 })
        {
            errors.Add(Err("Panel does not support 'dividers'.", $"{path}.dividers", raw));
        }
        if (raw.Inserts is { Count: > 0 })
        {
            errors.Add(Err("Panel does not support 'inserts'.", $"{path}.inserts", raw));
        }

        var profile = BuildProfile(raw.Profile, $"{path}.profile", errors);
        if (profile is null) return null;

        var panelFace = ResolvePanelFace(raw.Faces, $"{path}.faces", errors);
        // Panel features always apply to the single front face; allow omitting the
        // 'face:' field by defaulting it before delegating to the shared resolver.
        if (raw.Features is not null)
        {
            foreach (var feature in raw.Features)
            {
                if (string.IsNullOrEmpty(feature.Face))
                    feature.Face = "front";
            }
        }
        var features = ResolveFeatures(raw.Features, panelFace, $"{path}.features", errors);
        var centroid = PrismGeometry.ComputeProfileCentroid(profile);

        return new PanelShape
        {
            Id = raw.Id ?? string.Empty,
            Origin = origin,
            Location = location,
            Disjoint = disjoint,
            Profile = profile,
            ProfileCentroid = centroid,
            Faces = panelFace,
            Dividers = Array.Empty<DividerSet>(),
            Inserts = Array.Empty<Insert>(),
            Features = features,
        };
    }

    // A panel has a single virtual face named 'front'. Users may explicitly set
    // its type via faces: [{name: front, type: open|closed}], default closed.
    private static IReadOnlyList<Face> ResolvePanelFace(
        List<RawFace>? raw,
        string path,
        List<PlanError> errors)
    {
        var type = FaceType.Closed;
        if (raw is not null)
        {
            for (var i = 0; i < raw.Count; i++)
            {
                var rf = raw[i];
                var facePath = $"{path}[{i}]";
                if (string.IsNullOrEmpty(rf.Name))
                {
                    errors.Add(Err("Face requires a 'name'.", facePath, rf));
                    continue;
                }
                var name = ResolveFaceName(rf.Name, $"{facePath}.name", errors, rf);
                if (name is null) continue;
                if (name != FaceName.Front)
                {
                    errors.Add(Err($"Panel only supports the 'front' face (got '{rf.Name}').", facePath, rf));
                    continue;
                }
                type = ResolveFaceType(rf.Type, $"{facePath}.type", errors, rf) ?? FaceType.Closed;
            }
        }
        return new[] { new Face(FaceName.Front, type) };
    }

    // ── Prism resolution ─────────────────────────────────────────────────────

    private static PrismShape? ResolvePrismShape(
        RawPrismShapeBase raw,
        string path,
        List<PlanError> errors,
        List<(Insert, string, string, IRawLocated?)> pendingRefs)
    {
        var origin = ResolveOrigin(raw.Origin, $"{path}.origin", errors, raw) ?? Origin.BottomLeftFront;
        var location = ResolveVec3(raw.Location, 3, $"{path}.location", errors, raw) ?? Vec3.Zero;
        var disjoint = raw.Disjoint ?? raw.Location is null;

        var profile = BuildProfile(raw, path, errors);
        if (profile is null) return null;

        double? depth = null;
        if (raw.Depth is { } d)
        {
            if (d <= 0)
                errors.Add(Err("Prism 'depth' must be positive.", $"{path}.depth", raw));
            else
                depth = d;
        }

        var fit = raw.Fit is null ? null : ResolveFit(raw.Fit, $"{path}.fit", errors);

        if (depth is null && fit is null)
            errors.Add(Err("Prism must specify either 'depth' or 'fit'.", path, raw));
        if (depth is not null && fit is not null)
            errors.Add(Err("Prism may specify 'depth' or 'fit', not both.", path, raw));

        var backScale = 1.0;
        if (raw.BackSize is { } bs)
        {
            if (bs <= 0)
                errors.Add(Err("Prism 'back-size' must be positive.", $"{path}.back-size", raw));
            else
                backScale = bs;
        }

        var faces = ResolvePrismCapFaces(raw.Faces, $"{path}.faces", errors);
        var centroid = PrismGeometry.ComputeProfileCentroid(profile);
        var lateralFaces = BuildLateralFaces(profile, raw.LateralFaces, $"{path}.lateral-faces", errors, raw);
        var inserts = ResolveInserts(raw.Inserts, Array.Empty<DividerSet>(), faces, $"{path}.inserts", errors, pendingRefs);

        return new PrismShape
        {
            Id = raw.Id ?? string.Empty,
            Origin = origin,
            Location = location,
            Disjoint = disjoint,
            Profile = profile,
            Depth = depth,
            Fit = fit,
            Faces = faces,
            LateralFaces = lateralFaces,
            Dividers = Array.Empty<DividerSet>(),
            Inserts = inserts,
            Features = Array.Empty<Feature>(),
            BackScale = backScale,
            ProfileCentroid = centroid,
        };
    }

    private static PrismProfile? BuildProfile(RawPrismShapeBase raw, string path, List<PlanError> errors)
    {
        return raw switch
        {
            RawPrismShape prism => BuildProfileFromPrism(prism, path, errors),
            RawNamedPolygonShape named => BuildProfileFromNamedPolygon(named, path, errors),
            RawRegularPolygonShape poly => BuildProfileFromRegularPolygon(poly, path, errors),
            RawRectangleShape rect => BuildProfileFromRectangle(rect, path, errors),
            RawCircleShape circle => BuildProfileFromCircle(circle, path, errors),
            RawSemicircleShape semi => BuildProfileFromSemicircle(semi, path, errors),
            RawQuarterCircleShape quarter => BuildProfileFromQuarterCircle(quarter, path, errors),
            _ => Fail<PrismProfile?>(errors, $"Unsupported prism type '{raw.Type}'.", path, raw),
        };
    }

    private static PrismProfile? BuildProfileFromPrism(RawPrismShape raw, string path, List<PlanError> errors)
    {
        if (raw.Points is not null && raw.Segments is not null)
        {
            errors.Add(Err("Prism may specify 'points' or 'segments', not both.", path, raw));
            return null;
        }

        if (raw.Points is { } points)
        {
            if (points.Count < 3)
            {
                errors.Add(Err("Prism 'points' requires at least 3 points.", $"{path}.points", raw));
                return null;
            }

            var start = ParseProfilePoint(points[0], $"{path}.points[0]", errors, raw);
            if (start is null) return null;

            var segs = new List<ProfileSegment>();
            for (var i = 1; i < points.Count; i++)
            {
                var pt = ParseProfilePoint(points[i], $"{path}.points[{i}]", errors, raw);
                if (pt is null) return null;
                segs.Add(new ProfileSegment.Line(pt.Value));
            }
            segs.Add(new ProfileSegment.Line(start.Value));
            return new PrismProfile { StartPoint = start.Value, Segments = segs };
        }

        if (raw.Segments is { } rawSegs)
        {
            if (rawSegs.Count < 3)
            {
                errors.Add(Err("Prism 'segments' requires at least 3 segments.", $"{path}.segments", raw));
                return null;
            }

            var segs = new List<ProfileSegment>();
            for (var i = 0; i < rawSegs.Count; i++)
            {
                var seg = ResolveProfileSegment(rawSegs[i], $"{path}.segments[{i}]", errors);
                if (seg is null) return null;
                segs.Add(seg);
            }
            return new PrismProfile { StartPoint = Vec2.Zero, Segments = segs };
        }

        errors.Add(Err("Prism must specify 'points' or 'segments'.", path, raw));
        return null;
    }

    private static int NamedPolygonSides(string? type) => type?.ToLowerInvariant() switch
    {
        "triangle" => 3,
        "pentagon" => 5,
        "hexagon" => 6,
        _ => 0,
    };

    private static PrismProfile? BuildProfileFromNamedPolygon(RawNamedPolygonShape raw, string path, List<PlanError> errors)
    {
        var sides = NamedPolygonSides(raw.Type);
        if (sides == 0)
        {
            errors.Add(Err($"Unknown named polygon type '{raw.Type}'.", $"{path}.type", raw));
            return null;
        }

        if (raw.Width is not { } w || w <= 0)
        {
            errors.Add(Err($"{raw.Type} requires a positive 'width'.", $"{path}.width", raw));
            return null;
        }

        var h = raw.Height ?? DefaultRegularPolygonHeight(sides, w);
        if (h <= 0)
        {
            errors.Add(Err($"{raw.Type} 'height' must be positive.", $"{path}.height", raw));
            return null;
        }

        return BuildRegularPolygonProfile(sides, w, h);
    }

    private static PrismProfile? BuildProfileFromRegularPolygon(RawRegularPolygonShape raw, string path, List<PlanError> errors)
    {
        if (raw.Sides is not { } sides || sides < 3)
        {
            errors.Add(Err("regular-polygon requires 'sides' >= 3.", $"{path}.sides", raw));
            return null;
        }

        if (raw.Width is not { } w || w <= 0)
        {
            errors.Add(Err("regular-polygon requires a positive 'width'.", $"{path}.width", raw));
            return null;
        }

        var h = raw.Height ?? DefaultRegularPolygonHeight(sides, w);
        if (h <= 0)
        {
            errors.Add(Err("regular-polygon 'height' must be positive.", $"{path}.height", raw));
            return null;
        }

        return BuildRegularPolygonProfile(sides, w, h);
    }

    private static PrismProfile? BuildProfileFromRectangle(RawRectangleShape raw, string path, List<PlanError> errors)
    {
        if (raw.Width is not { } w || w <= 0)
        {
            errors.Add(Err("rectangle requires a positive 'width'.", $"{path}.width", raw));
            return null;
        }
        if (raw.Height is not { } h || h <= 0)
        {
            errors.Add(Err("rectangle requires a positive 'height'.", $"{path}.height", raw));
            return null;
        }

        var start = new Vec2(0, 0);
        return new PrismProfile
        {
            StartPoint = start,
            Segments = new ProfileSegment[]
            {
                new ProfileSegment.Line(new Vec2(w, 0)),
                new ProfileSegment.Line(new Vec2(w, h)),
                new ProfileSegment.Line(new Vec2(0, h)),
                new ProfileSegment.Line(start),
            },
        };
    }

    private static PrismProfile? BuildProfileFromCircle(RawCircleShape raw, string path, List<PlanError> errors)
    {
        if (raw.Diameter is not { } d || d <= 0)
        {
            errors.Add(Err("circle requires a positive 'diameter'.", $"{path}.diameter", raw));
            return null;
        }

        var r = d / 2;
        // Two arcs forming a full circle (start at right, top arc CW, bottom arc CW)
        var right = new Vec2(d, r);
        var left = new Vec2(0, r);
        return new PrismProfile
        {
            StartPoint = right,
            Segments = new[]
            {
                new ProfileSegment.Arc(left, r, Clockwise: false),
                new ProfileSegment.Arc(right, r, Clockwise: false),
            },
        };
    }

    private static PrismProfile? BuildProfileFromSemicircle(RawSemicircleShape raw, string path, List<PlanError> errors)
    {
        if (raw.Diameter is not { } d || d <= 0)
        {
            errors.Add(Err("semicircle requires a positive 'diameter'.", $"{path}.diameter", raw));
            return null;
        }

        var r = d / 2;
        // Flat base at bottom (Z=0), arc on top.
        var leftBase = new Vec2(0, 0);
        var rightBase = new Vec2(d, 0);
        var topRight = new Vec2(d, 0);
        // Arc from (0,0) up over (d/2, r) and back to (d,0):
        // Standard semicircle: start at (0,0), arc CCW to (d,0) passing through (d/2, r).
        return new PrismProfile
        {
            StartPoint = leftBase,
            Segments = new ProfileSegment[]
            {
                new ProfileSegment.Line(rightBase),
                new ProfileSegment.Arc(leftBase, r, Clockwise: false),
            },
        };
    }

    private static PrismProfile? BuildProfileFromQuarterCircle(RawQuarterCircleShape raw, string path, List<PlanError> errors)
    {
        if (raw.Radius is not { } r || r <= 0)
        {
            errors.Add(Err("quarter-circle requires a positive 'radius'.", $"{path}.radius", raw));
            return null;
        }

        // Quarter circle: flat edges on bottom and left, arc from (r, 0) to (0, r).
        var origin = new Vec2(0, 0);
        var right = new Vec2(r, 0);
        var top = new Vec2(0, r);
        return new PrismProfile
        {
            StartPoint = origin,
            Segments = new ProfileSegment[]
            {
                new ProfileSegment.Line(right),
                new ProfileSegment.Arc(top, r, Clockwise: false),
                new ProfileSegment.Line(origin),
            },
        };
    }

    // Default height for a regular N-gon inscribed in a bounding box of given width.
    private static double DefaultRegularPolygonHeight(int n, double width)
    {
        // For a regular N-gon with circumradius R:
        // Bounding box width (X extent) depends on orientation.
        // Use the formula for a polygon in "natural" orientation (pointy-top for odd N,
        // flat-top for even N). Compute the aspect ratio height/width.
        var startAngle = n % 2 == 0 ? Math.PI / n : Math.PI / 2;
        var angles = Enumerable.Range(0, n).Select(i => startAngle + 2 * Math.PI * i / n);
        var xs = angles.Select(Math.Cos).ToArray();
        var zs = angles.Select(Math.Sin).ToArray();
        var xRange = xs.Max() - xs.Min();
        var zRange = zs.Max() - zs.Min();
        return width * zRange / xRange;
    }

    private static PrismProfile BuildRegularPolygonProfile(int n, double width, double height)
    {
        // Pointy-top for odd N, flat-top for even N.
        var startAngle = n % 2 == 0 ? Math.PI / n : Math.PI / 2;

        var angles = Enumerable.Range(0, n).Select(i => startAngle + 2 * Math.PI * i / n).ToArray();
        var xs = angles.Select(Math.Cos).ToArray();
        var zs = angles.Select(Math.Sin).ToArray();
        var xMin = xs.Min();
        var zMin = zs.Min();
        var xRange = xs.Max() - xMin;
        var zRange = zs.Max() - zMin;

        Vec2 Vertex(int i) => new Vec2(
            (xs[i] - xMin) / xRange * width,
            (zs[i] - zMin) / zRange * height);

        var start = Vertex(0);
        var segs = new List<ProfileSegment>();
        for (var i = 1; i < n; i++)
            segs.Add(new ProfileSegment.Line(Vertex(i)));
        segs.Add(new ProfileSegment.Line(start)); // close
        return new PrismProfile { StartPoint = start, Segments = segs };
    }

    private static ProfileSegment? ResolveProfileSegment(RawProfileSegment raw, string path, List<PlanError> errors)
    {
        var lineToSet = raw.LineTo is not null;
        var arcToSet = raw.ArcTo is not null;
        var bezierToSet = raw.BezierTo is not null;
        var count = (lineToSet ? 1 : 0) + (arcToSet ? 1 : 0) + (bezierToSet ? 1 : 0);

        if (count == 0)
        {
            errors.Add(Err("Segment must specify one of 'line-to', 'arc-to', or 'bezier-to'.", path, raw));
            return null;
        }
        if (count > 1)
        {
            errors.Add(Err("Segment must specify exactly one of 'line-to', 'arc-to', or 'bezier-to'.", path, raw));
            return null;
        }

        if (lineToSet)
        {
            var pt = ParseProfilePoint(raw.LineTo!, $"{path}.line-to", errors, raw);
            return pt is null ? null : new ProfileSegment.Line(pt.Value);
        }

        if (arcToSet)
        {
            var pt = ParseProfilePoint(raw.ArcTo!, $"{path}.arc-to", errors, raw);
            if (pt is null) return null;
            if (raw.Radius is not { } radius || radius <= 0)
            {
                errors.Add(Err("Arc segment requires a positive 'radius'.", $"{path}.radius", raw));
                return null;
            }
            return new ProfileSegment.Arc(pt.Value, radius, raw.Clockwise ?? false);
        }

        // bezier
        {
            var pt = ParseProfilePoint(raw.BezierTo!, $"{path}.bezier-to", errors, raw);
            if (pt is null) return null;
            var c1 = raw.Control1 is null ? (Vec2?)null : ParseProfilePoint(raw.Control1, $"{path}.control-1", errors, raw);
            var c2 = raw.Control2 is null ? (Vec2?)null : ParseProfilePoint(raw.Control2, $"{path}.control-2", errors, raw);
            if (c1 is null || c2 is null)
            {
                errors.Add(Err("Bezier segment requires 'control-1' and 'control-2'.", path, raw));
                return null;
            }
            return new ProfileSegment.Bezier(pt.Value, c1.Value, c2.Value);
        }
    }

    private static Vec2? ParseProfilePoint(double[] raw, string path, List<PlanError> errors, IRawLocated? at)
    {
        if (raw.Length != 2)
        {
            errors.Add(Err($"Profile point must have exactly 2 numbers (X, Z), got {raw.Length}.", path, at));
            return null;
        }
        return new Vec2(raw[0], raw[1]);
    }

    private static IReadOnlyList<Face> ResolvePrismCapFaces(List<RawFace>? raw, string path, List<PlanError> errors)
    {
        var byName = new Dictionary<FaceName, FaceType>
        {
            { FaceName.Front, FaceType.Closed },
            { FaceName.Back, FaceType.Closed },
        };

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

                if (name != FaceName.Front && name != FaceName.Back)
                {
                    errors.Add(Err($"Prism cap faces only support 'front' and 'back' (got '{rawFace.Name}').", facePath, rawFace));
                    continue;
                }

                var type = ResolveFaceType(rawFace.Type, $"{facePath}.type", errors, rawFace) ?? FaceType.Closed;
                byName[name.Value] = type;
            }
        }

        return byName.Select(kv => new Face(kv.Key, kv.Value)).ToArray();
    }

    private static IReadOnlyList<PrismLateralFace> BuildLateralFaces(
        PrismProfile profile,
        List<RawLateralFace>? rawOverrides,
        string path,
        List<PlanError> errors,
        IRawLocated? at)
    {
        var n = profile.Segments.Count;
        var vertices = ProfileVertices(profile);
        var overrideByIndex = new Dictionary<int, FaceType>();

        if (rawOverrides is not null)
        {
            for (var i = 0; i < rawOverrides.Count; i++)
            {
                var rl = rawOverrides[i];
                var lpath = $"{path}[{i}]";
                if (rl.Index is not { } idx || idx < 0 || idx >= n)
                {
                    errors.Add(Err($"Lateral face 'index' must be 0..{n - 1}.", lpath, rl));
                    continue;
                }
                var type = ResolveFaceType(rl.Type, $"{lpath}.type", errors, rl) ?? FaceType.Closed;
                overrideByIndex[idx] = type;
            }
        }

        var result = new List<PrismLateralFace>(n);
        for (var i = 0; i < n; i++)
        {
            var seg = profile.Segments[i];
            var isCurved = seg is ProfileSegment.Arc or ProfileSegment.Bezier;
            var edgeStart = i == 0 ? profile.StartPoint : profile.Segments[i - 1].EndPoint;
            var edgeEnd = seg.EndPoint;
            var edgeLength = isCurved ? CurvedSegmentLength(seg, edgeStart) : Distance(edgeStart, edgeEnd);

            // Dihedral angle at the END vertex of this edge (shared with next lateral face).
            // Wrap: after the last face the next edge is face 0, whose end is Segments[0].EndPoint.
            var nextEdgeEnd = i + 1 < n ? profile.Segments[i + 1].EndPoint : profile.Segments[0].EndPoint;
            var dihedral = ComputeDihedralAngle(edgeStart, edgeEnd, nextEdgeEnd);

            result.Add(new PrismLateralFace
            {
                EdgeIndex = i,
                Type = overrideByIndex.TryGetValue(i, out var ft) ? ft : FaceType.Closed,
                DihedralAngleToNext = dihedral,
                IsCurved = isCurved,
                EdgeLength = edgeLength,
            });
        }

        return result;
    }

    // Returns all vertex positions in order: [StartPoint, seg[0].end, seg[1].end, ...]
    private static Vec2[] ProfileVertices(PrismProfile profile)
    {
        var v = new Vec2[profile.Segments.Count + 1];
        v[0] = profile.StartPoint;
        for (var i = 0; i < profile.Segments.Count; i++)
            v[i + 1] = profile.Segments[i].EndPoint;
        return v;
    }

    // Euclidean distance between two 2D points.
    private static double Distance(Vec2 a, Vec2 b)
    {
        var dx = b.X - a.X;
        var dz = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    // Approximate arc or Bezier length for lateral face sizing.
    private static double CurvedSegmentLength(ProfileSegment seg, Vec2 start)
    {
        const int steps = 64;
        if (seg is ProfileSegment.Arc arc)
        {
            // Compute the arc angle subtended and multiply by radius.
            var end = arc.To;
            var dx = (end.X - start.X) / 2;
            var dz = (end.Y - start.Y) / 2;
            var chordHalf = Math.Sqrt(dx * dx + dz * dz);
            if (chordHalf > arc.Radius)
                return Distance(start, end); // degenerate: chord > diameter, use chord
            var angle = 2 * Math.Asin(chordHalf / arc.Radius);
            return arc.Radius * angle;
        }
        if (seg is ProfileSegment.Bezier bezier)
        {
            // Numerically sample the Bezier curve for arc length.
            var length = 0.0;
            var prev = start;
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                var t2 = 1 - t;
                var p = new Vec2(
                    t2 * t2 * t2 * start.X + 3 * t2 * t2 * t * bezier.Control1.X
                        + 3 * t2 * t * t * bezier.Control2.X + t * t * t * bezier.To.X,
                    t2 * t2 * t2 * start.Y + 3 * t2 * t2 * t * bezier.Control1.Y
                        + 3 * t2 * t * t * bezier.Control2.Y + t * t * t * bezier.To.Y);
                length += Distance(prev, p);
                prev = p;
            }
            return length;
        }
        return Distance(start, seg.EndPoint);
    }

    // Interior dihedral angle (degrees) at vertex B, between edges A→B and B→C.
    // For a convex polygon vertex, this is the interior angle of the polygon.
    private static double ComputeDihedralAngle(Vec2 a, Vec2 b, Vec2 c)
    {
        var v1 = new Vec2(b.X - a.X, b.Y - a.Y);
        var v2 = new Vec2(c.X - b.X, c.Y - b.Y);
        var len1 = Math.Sqrt(v1.X * v1.X + v1.Y * v1.Y);
        var len2 = Math.Sqrt(v2.X * v2.X + v2.Y * v2.Y);
        if (len1 < 1e-9 || len2 < 1e-9) return 90.0;

        // cos(exterior angle) = dot(v1, v2) / (|v1| * |v2|)
        var cosExt = (v1.X * v2.X + v1.Y * v2.Y) / (len1 * len2);
        cosExt = Math.Clamp(cosExt, -1.0, 1.0);
        var exteriorAngleDeg = Math.Acos(cosExt) * 180.0 / Math.PI;
        return 180.0 - exteriorAngleDeg;
    }

    // ── End prism resolution ──────────────────────────────────────────────────

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
        IReadOnlyList<Face> faces,
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
                if (string.Equals(ri.Fill, "all-cells", StringComparison.OrdinalIgnoreCase))
                {
                    for (var l = 0; l < layers; l++)
                    for (var r = 0; r < rows; r++)
                    for (var c = 0; c < cols; c++)
                    {
                        var insert = new Insert { Cell = (c, r, l) };
                        result.Add(insert);
                        pendingRefs.Add((insert, ri.Ref!, $"{ipath}.ref", ri));
                    }
                }
                else if (string.Equals(ri.Fill, "entire-face", StringComparison.OrdinalIgnoreCase))
                {
                    var openFace = faces.FirstOrDefault(f => f.Type == FaceType.Open);
                    if (openFace is null)
                    {
                        errors.Add(Err("'entire-face' requires the parent shape to have at least one open face.", $"{ipath}.fill", ri));
                        continue;
                    }
                    var entryAxis = openFace.Name switch
                    {
                        FaceName.Top or FaceName.Bottom => Axis.Y,
                        FaceName.Left or FaceName.Right => Axis.X,
                        _ => Axis.Z,
                    };
                    var insert = new Insert { Cell = (0, 0, 0), EntryAxis = entryAxis };
                    result.Add(insert);
                    pendingRefs.Add((insert, ri.Ref!, $"{ipath}.ref", ri));
                }
                else
                {
                    errors.Add(Err($"Unknown fill mode '{ri.Fill}'.", $"{ipath}.fill", ri));
                    continue;
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
                case RawRasterEngravingFeature rasterEngraving:
                    var rasterEngravingFeature = ResolveRasterEngraving(rasterEngraving, face.Value, position, fpath, errors);
                    if (rasterEngravingFeature is not null) result.Add(rasterEngravingFeature);
                    break;
                case RawSvgEngravingFeature svgEngraving:
                    var svgEngravingFeature = ResolveSvgEngraving(svgEngraving, face.Value, position, fpath, errors);
                    if (svgEngravingFeature is not null) result.Add(svgEngravingFeature);
                    break;
                case RawEngravingGridFeature grid:
                    var gridFeature = ResolveEngravingGrid(grid, face.Value, fpath, errors);
                    if (gridFeature is not null) result.Add(gridFeature);
                    break;
                case RawSplitCutFeature splitCut:
                    var splitCutFeature = ResolveSplitCut(splitCut, face.Value, fpath, errors);
                    if (splitCutFeature is not null) result.Add(splitCutFeature);
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

        if (!Enum.TryParse<CutoutShape>(raw.Shape.Replace("-", ""), ignoreCase: true, out var shape))
        {
            errors.Add(Err($"Unknown cutout shape '{raw.Shape}'.", $"{path}.shape", raw));
            return null;
        }

        if (shape == CutoutShape.EdgeDip)
            return ResolveEdgeDipCutout(raw, face, position, path, errors);

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

        CutoutRepeat? repeat = null;
        if (raw.Repeat is not null)
        {
            var spacing = ResolveVec2(raw.Repeat.Spacing, $"{path}.repeat.spacing", errors, raw.Repeat);
            if (spacing is null)
            {
                errors.Add(Err("Cutout repeat requires 'spacing' as [u, v].", $"{path}.repeat.spacing", raw.Repeat));
                return null;
            }
            if (spacing.Value.X == 0 && spacing.Value.Y == 0)
            {
                errors.Add(Err("Cutout repeat 'spacing' must be non-zero.", $"{path}.repeat.spacing", raw.Repeat));
                return null;
            }
            repeat = new CutoutRepeat(spacing.Value);
        }

        return new CutoutFeature
        {
            Face = face,
            Position = position,
            Shape = shape,
            Width = width,
            Height = height,
            Repeat = repeat,
        };
    }

    private static CutoutFeature? ResolveEdgeDipCutout(
        RawCutoutFeature raw,
        FaceName face,
        Position? position,
        string path,
        List<PlanError> errors)
    {
        if (raw.Diameter is not null)
        {
            errors.Add(Err("EdgeDip cutout does not support 'diameter'.", path, raw));
            return null;
        }
        if (raw.Repeat is not null)
        {
            errors.Add(Err("EdgeDip cutout does not support 'repeat'.", path, raw));
            return null;
        }

        if (raw.Width is not { } width || raw.Height is not { } height)
        {
            errors.Add(Err("EdgeDip cutout requires both 'width' and 'height'.", path, raw));
            return null;
        }
        if (width <= 0)
        {
            errors.Add(Err("EdgeDip 'width' must be positive.", $"{path}.width", raw));
            return null;
        }
        if (height <= 0)
        {
            errors.Add(Err("EdgeDip 'height' must be positive.", $"{path}.height", raw));
            return null;
        }

        var radius = raw.Radius ?? 0.0;
        var innerRadius = raw.InnerRadius ?? 0.0;

        if (radius < 0)
        {
            errors.Add(Err("EdgeDip 'radius' must be non-negative.", $"{path}.radius", raw));
            return null;
        }
        if (innerRadius < 0)
        {
            errors.Add(Err("EdgeDip 'inner-radius' must be non-negative.", $"{path}.inner-radius", raw));
            return null;
        }
        if (width < 2 * radius)
        {
            errors.Add(Err($"EdgeDip 'width' ({width}) must be at least 2 * radius ({2 * radius}).", path, raw));
            return null;
        }
        if (height < radius + innerRadius)
        {
            errors.Add(Err($"EdgeDip 'height' ({height}) must be at least radius + inner-radius ({radius + innerRadius}).", path, raw));
            return null;
        }

        var anchor = position?.Anchor;
        if (anchor is not null &&
            anchor != Anchor.TopCenter &&
            anchor != Anchor.BottomCenter &&
            anchor != Anchor.LeftCenter &&
            anchor != Anchor.RightCenter)
        {
            errors.Add(Err("EdgeDip anchor must be one of: top-center, bottom-center, left-center, right-center.", $"{path}.position.anchor", raw));
            return null;
        }

        return new CutoutFeature
        {
            Face = face,
            Position = position ?? new Position(Anchor.TopCenter, Vec2.Zero),
            Shape = CutoutShape.EdgeDip,
            Width = width,
            Height = height,
            Radius = radius,
            InnerRadius = innerRadius,
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

    private static RasterEngravingFeature? ResolveRasterEngraving(
        RawRasterEngravingFeature raw,
        FaceName face,
        Position? position,
        string path,
        List<PlanError> errors)
    {
        var source = raw.Source;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = raw.Image;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            errors.Add(Err("Raster engraving requires 'source' (or 'image').", $"{path}.source", raw));
            return null;
        }

        if (raw.Width is null && raw.Height is null)
        {
            errors.Add(Err("Raster engraving requires at least one of 'width' or 'height'.", path, raw));
            return null;
        }

        if (raw.Width is { } w && w <= 0)
        {
            errors.Add(Err("Raster engraving 'width' must be positive.", $"{path}.width", raw));
            return null;
        }

        if (raw.Height is { } h && h <= 0)
        {
            errors.Add(Err("Raster engraving 'height' must be positive.", $"{path}.height", raw));
            return null;
        }

        return new RasterEngravingFeature
        {
            Face = face,
            Position = position,
            Source = source,
            Width = raw.Width,
            Height = raw.Height,
        };
    }

    private static SvgEngravingFeature? ResolveSvgEngraving(
        RawSvgEngravingFeature raw,
        FaceName face,
        Position? position,
        string path,
        List<PlanError> errors)
    {
        if (string.IsNullOrWhiteSpace(raw.Source))
        {
            errors.Add(Err("SVG engraving requires 'source'.", $"{path}.source", raw));
            return null;
        }

        if (raw.Width is null && raw.Height is null)
        {
            errors.Add(Err("SVG engraving requires at least one of 'width' or 'height'.", path, raw));
            return null;
        }

        if (raw.Width is { } w && w <= 0)
        {
            errors.Add(Err("SVG engraving 'width' must be positive.", $"{path}.width", raw));
            return null;
        }

        if (raw.Height is { } h && h <= 0)
        {
            errors.Add(Err("SVG engraving 'height' must be positive.", $"{path}.height", raw));
            return null;
        }

        return new SvgEngravingFeature
        {
            Face = face,
            Position = position,
            Source = raw.Source,
            Width = raw.Width,
            Height = raw.Height,
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

    private static SplitCutFeature? ResolveSplitCut(
        RawSplitCutFeature raw,
        FaceName face,
        string path,
        List<PlanError> errors)
    {
        if (raw.Height is not { } height || height <= 0)
        {
            errors.Add(Err("Split cut requires a positive 'height'.", $"{path}.height", raw));
            return null;
        }

        var amplitude = raw.Amplitude ?? 0.0;
        if (amplitude < 0)
        {
            errors.Add(Err("Split cut 'amplitude' must be non-negative.", $"{path}.amplitude", raw));
            return null;
        }

        var normalizedCurve = ResolveSplitCurve(raw.Curve, amplitude, $"{path}.curve", errors);
        if (normalizedCurve is null)
        {
            return null;
        }

        return new SplitCutFeature
        {
            Face = face,
            Height = height,
            Amplitude = amplitude,
            NormalizedCurve = normalizedCurve,
            ValidateSeparation = raw.ValidateSeparation ?? true,
        };
    }

    private static IReadOnlyList<Vec2>? ResolveSplitCurve(
        RawSplitCurve? raw,
        double amplitude,
        string path,
        List<PlanError> errors)
    {
        var levelEndsByRotation = raw?.LevelEnds ?? true;

        if (raw is null || string.IsNullOrWhiteSpace(raw.Type) || raw.Type.Equals("straight", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { new Vec2(0, 0), new Vec2(1, 0) };
        }

        if (raw.Type.Equals("cubic-bezier", StringComparison.OrdinalIgnoreCase))
        {
            var c1 = ResolveVec2(raw.Control1, $"{path}.control-1", errors, raw);
            var c2 = ResolveVec2(raw.Control2, $"{path}.control-2", errors, raw);
            if (c1 is null || c2 is null)
            {
                errors.Add(Err("cubic-bezier requires 'control-1' and 'control-2'.", path, raw));
                return null;
            }

            var samples = Math.Clamp(raw.Samples ?? 64, 8, 1024);
            var points = SampleCubicCurvePoints(c1.Value, c2.Value, samples);
            return BuildNormalizedSplitCurve(points, amplitude, levelEndsByRotation, path, errors, raw);
        }

        if (raw.Type.Equals("polyline", StringComparison.OrdinalIgnoreCase))
        {
            if (raw.Points is null || raw.Points.Count < 2)
            {
                errors.Add(Err("polyline requires at least 2 'points'.", $"{path}.points", raw));
                return null;
            }

            var points = new List<Vec2>(raw.Points.Count);
            for (var i = 0; i < raw.Points.Count; i++)
            {
                var p = ResolveVec2(raw.Points[i], $"{path}.points[{i}]", errors, raw);
                if (p is null) return null;
                points.Add(p.Value);
            }
            return BuildNormalizedSplitCurve(points, amplitude, levelEndsByRotation, path, errors, raw);
        }

        if (raw.Type.Equals("svg-path", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(raw.SvgPathData))
            {
                errors.Add(Err("svg-path requires 'svg-path-data'.", $"{path}.svg-path-data", raw));
                return null;
            }

            var samples = Math.Clamp(raw.Samples ?? 24, 4, 256);
            if (!TrySampleSvgPathCurve(raw.SvgPathData, samples, out var sampled, out var parseError))
            {
                errors.Add(Err($"Invalid split curve svg path: {parseError}", $"{path}.svg-path-data", raw));
                return null;
            }

            return BuildNormalizedSplitCurve(sampled, amplitude, levelEndsByRotation, path, errors, raw);
        }

        errors.Add(Err(
            $"Unknown split curve type '{raw.Type}'. Expected 'straight', 'cubic-bezier', 'polyline', or 'svg-path'.",
            $"{path}.type",
            raw));
        return null;
    }

    private static IReadOnlyList<Vec2>? BuildNormalizedSplitCurve(
        IReadOnlyList<Vec2> points,
        double amplitude,
        bool levelEndsByRotation,
        string path,
        List<PlanError> errors,
        IRawLocated? at)
    {
        if (points.Count < 2)
        {
            errors.Add(Err("Split curve needs at least 2 points.", path, at));
            return null;
        }

        var working = points.ToList();
        if (levelEndsByRotation)
        {
            var start = working[0];
            var end = working[^1];
            var angle = -Math.Atan2(end.Y - start.Y, end.X - start.X);
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);

            // Rotate around the start point so the first/last points become level.
            working = working
                .Select(p =>
                {
                    var relX = p.X - start.X;
                    var relY = p.Y - start.Y;
                    return new Vec2(
                        relX * cos - relY * sin,
                        relX * sin + relY * cos);
                })
                .ToList();
        }

        var minX = working.Min(p => p.X);
        var maxX = working.Max(p => p.X);
        var spanX = maxX - minX;
        if (spanX <= SplitCurveEps)
        {
            errors.Add(Err("Split curve must span a non-zero X range.", path, at));
            return null;
        }

        var sorted = working
            .Select(p => new Vec2((p.X - minX) / spanX, p.Y))
            .OrderBy(p => p.X)
            .ToList();

        var deduped = new List<Vec2>(sorted.Count);
        foreach (var p in sorted)
        {
            if (deduped.Count > 0 && Math.Abs(p.X - deduped[^1].X) <= SplitCurveEps)
            {
                // Average duplicate-X samples to keep a single-valued curve y(x).
                var prev = deduped[^1];
                deduped[^1] = new Vec2(prev.X, (prev.Y + p.Y) / 2.0);
            }
            else
            {
                deduped.Add(p);
            }
        }

        if (deduped.Count < 2)
        {
            errors.Add(Err("Split curve collapsed after X normalization.", path, at));
            return null;
        }

        // Force exact endpoints for predictable edge matching around corners.
        deduped[0] = new Vec2(0, deduped[0].Y);
        deduped[^1] = new Vec2(1, deduped[^1].Y);

        var maxAbs = deduped.Max(p => Math.Abs(p.Y));
        var normalized = maxAbs <= SplitCurveEps
            ? deduped.Select(p => new Vec2(p.X, 0)).ToList()
            : deduped.Select(p => new Vec2(p.X, p.Y / maxAbs)).ToList();

        if (amplitude > 0)
        {
            var peak = normalized.Max(p => Math.Abs(p.Y));
            if (peak <= SplitCurveEps)
            {
                errors.Add(Err("Split curve has no vertical variation; use a non-flat curve or set amplitude to 0.", path, at));
                return null;
            }
        }

        return normalized;
    }

    private static IReadOnlyList<Vec2> SampleCubicCurvePoints(Vec2 c1, Vec2 c2, int samples)
    {
        var points = new List<Vec2>(samples + 1);
        for (var i = 0; i <= samples; i++)
        {
            var t = (double)i / samples;
            var u = 1.0 - t;
            points.Add(new Vec2(
                3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t,
                3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y));
        }
        return points;
    }

    private static bool TrySampleSvgPathCurve(
        string d,
        int samplesPerBezier,
        out IReadOnlyList<Vec2> points,
        out string error)
    {
        points = Array.Empty<Vec2>();
        error = string.Empty;

        var tokens = Regex.Matches(d, @"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")
            .Select(m => m.Value)
            .ToList();

        if (tokens.Count == 0)
        {
            error = "path has no commands.";
            return false;
        }

        var result = new List<Vec2>();
        var index = 0;
        var current = Vec2.Zero;
        var subpathStart = Vec2.Zero;
        var hasCurrent = false;
        var command = '\0';
        var lastWasCubic = false;
        var lastCubicControl2 = Vec2.Zero;

        bool IsCommandToken(string token) => token.Length == 1 && char.IsLetter(token[0]);

        bool TryReadDouble(out double value)
        {
            value = 0;
            if (index >= tokens.Count || IsCommandToken(tokens[index]))
                return false;
            if (!double.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;
            index++;
            return true;
        }

        bool TryReadPoint(bool relative, out Vec2 point)
        {
            point = Vec2.Zero;
            if (!TryReadDouble(out var x) || !TryReadDouble(out var y))
                return false;
            point = relative ? new Vec2(current.X + x, current.Y + y) : new Vec2(x, y);
            return true;
        }

        while (index < tokens.Count)
        {
            if (IsCommandToken(tokens[index]))
            {
                command = tokens[index][0];
                index++;
            }
            else if (command == '\0')
            {
                error = "path starts with numbers before a command.";
                return false;
            }

            var lower = char.ToLowerInvariant(command);
            var relative = char.IsLower(command);

            if (lower == 'z')
            {
                if (!hasCurrent)
                {
                    error = "'Z' used before any point.";
                    return false;
                }
                if (Math.Abs(current.X - subpathStart.X) > SplitCurveEps || Math.Abs(current.Y - subpathStart.Y) > SplitCurveEps)
                {
                    result.Add(subpathStart);
                    current = subpathStart;
                }
                command = '\0';
                lastWasCubic = false;
                continue;
            }

            if (lower == 'm')
            {
                if (!TryReadPoint(relative, out var moveTo))
                {
                    error = "'M' requires an x,y pair.";
                    return false;
                }
                current = moveTo;
                subpathStart = moveTo;
                hasCurrent = true;
                result.Add(moveTo);

                command = relative ? 'l' : 'L';
                lastWasCubic = false;
                continue;
            }

            if (!hasCurrent)
            {
                error = $"'{command}' used before move command.";
                return false;
            }

            if (lower == 'l')
            {
                var consumed = false;
                while (TryReadPoint(relative, out var lineTo))
                {
                    result.Add(lineTo);
                    current = lineTo;
                    consumed = true;
                }
                if (!consumed)
                {
                    error = "'L' requires one or more x,y pairs.";
                    return false;
                }
                lastWasCubic = false;
                continue;
            }

            if (lower == 'c')
            {
                var consumed = false;
                while (true)
                {
                    var save = index;
                    if (!TryReadPoint(relative, out var c1)
                        || !TryReadPoint(relative, out var c2)
                        || !TryReadPoint(relative, out var to))
                    {
                        index = save;
                        break;
                    }

                    consumed = true;
                    for (var i = 1; i <= samplesPerBezier; i++)
                    {
                        var t = (double)i / samplesPerBezier;
                        var u = 1.0 - t;
                        result.Add(new Vec2(
                            u * u * u * current.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * to.X,
                            u * u * u * current.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * to.Y));
                    }
                    current = to;
                            lastCubicControl2 = c2;
                            lastWasCubic = true;
                }

                if (!consumed)
                {
                    error = "'C' requires control-1, control-2, and end point for at least one segment.";
                    return false;
                }
                continue;
            }

            if (lower == 's')
            {
                var consumed = false;
                while (true)
                {
                    var save = index;
                    if (!TryReadPoint(relative, out var c2)
                        || !TryReadPoint(relative, out var to))
                    {
                        index = save;
                        break;
                    }

                    consumed = true;
                    var c1 = lastWasCubic
                        ? new Vec2(2 * current.X - lastCubicControl2.X, 2 * current.Y - lastCubicControl2.Y)
                        : current;

                    for (var i = 1; i <= samplesPerBezier; i++)
                    {
                        var t = (double)i / samplesPerBezier;
                        var u = 1.0 - t;
                        result.Add(new Vec2(
                            u * u * u * current.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * to.X,
                            u * u * u * current.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * to.Y));
                    }
                    current = to;
                    lastCubicControl2 = c2;
                    lastWasCubic = true;
                }

                if (!consumed)
                {
                    error = "'S' requires control-2 and end point for at least one segment.";
                    return false;
                }
                continue;
            }

            if (lower == 'a')
            {
                var consumed = false;
                while (true)
                {
                    var save = index;
                    if (!TryReadDouble(out _)
                        || !TryReadDouble(out _)
                        || !TryReadDouble(out _)
                        || !TryReadDouble(out _)
                        || !TryReadDouble(out _)
                        || !TryReadDouble(out var x)
                        || !TryReadDouble(out var y))
                    {
                        index = save;
                        break;
                    }

                    consumed = true;
                    var end = relative ? new Vec2(current.X + x, current.Y + y) : new Vec2(x, y);
                    // For split-curve extraction we only need a robust 1D profile, so
                    // we approximate elliptical-arc commands as endpoint-connected segments.
                    result.Add(end);
                    current = end;
                    lastWasCubic = false;
                }

                if (!consumed)
                {
                    error = "'A' requires 7 parameters per segment (rx ry rot largeArc sweep x y).";
                    return false;
                }
                continue;
            }

            lastWasCubic = false;

            error = $"unsupported SVG command '{command}' (supported: M, L, C, S, A, Z and lowercase variants).";
            return false;
        }

        if (result.Count < 2)
        {
            error = "path must produce at least two points.";
            return false;
        }

        points = result;
        return true;
    }

    private static void ValidateBoxSplitCuts(
        IReadOnlyList<Feature> features,
        string path,
        List<PlanError> errors,
        IRawLocated? at)
    {
        var cuts = features.OfType<SplitCutFeature>().ToList();
        if (cuts.Count == 0)
            return;

        foreach (var cut in cuts.Where(c => c.Face is FaceName.Top or FaceName.Bottom))
        {
            errors.Add(Err("split-cut is only valid on side faces: left, right, front, back.", path, at));
            _ = cut;
        }

        var validated = cuts.Where(c => c.ValidateSeparation).ToList();
        if (validated.Count == 0)
            return;

        var sideFaces = new[] { FaceName.Front, FaceName.Back, FaceName.Left, FaceName.Right };
        foreach (var face in sideFaces)
        {
            var count = validated.Count(c => c.Face == face);
            if (count == 0)
                errors.Add(Err($"split-cut with validation enabled is missing on face '{face.ToString().ToLowerInvariant()}'.", path, at));
            else if (count > 1)
                errors.Add(Err($"split-cut with validation enabled must be unique per face; '{face.ToString().ToLowerInvariant()}' has {count}.", path, at));
        }

        if (validated.Count != 4)
            return;

        var reference = validated[0];
        foreach (var cut in validated.Skip(1))
        {
            if (Math.Abs(cut.Height - reference.Height) > SplitCurveEps)
            {
                errors.Add(Err("All validated split-cut features must use the same 'height' so the lid/base can separate.", path, at));
                break;
            }
        }

        foreach (var cut in validated.Skip(1))
        {
            if (Math.Abs(cut.Amplitude - reference.Amplitude) > SplitCurveEps)
            {
                errors.Add(Err("All validated split-cut features must use the same 'amplitude' so the lid/base can separate.", path, at));
                break;
            }
        }

        foreach (var cut in validated.Skip(1))
        {
            if (!CurvesEquivalent(reference.NormalizedCurve, cut.NormalizedCurve))
            {
                errors.Add(Err("All validated split-cut features must use the same curve shape so the lid/base can separate.", path, at));
                break;
            }
        }
    }

    private static bool CurvesEquivalent(IReadOnlyList<Vec2> a, IReadOnlyList<Vec2> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i].X - b[i].X) > SplitCurveEps || Math.Abs(a[i].Y - b[i].Y) > SplitCurveEps)
                return false;
        }

        return true;
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

    private static FitDimension ToFitDimension(RawFitDimension? raw)
    {
        if (raw is null) return FitDimension.Auto.Instance;
        var r = raw.Value;
        if (!r.IsAuto) return new FitDimension.Fixed(r.Value);
        if (r.Offset != 0) return new FitDimension.AutoOffset(r.Offset);
        return FitDimension.Auto.Instance;
    }

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
        { "top-left", Anchor.TopLeft },
        { "top-center", Anchor.TopCenter },
        { "top-right", Anchor.TopRight },
        { "bottom-left", Anchor.BottomLeft },
        { "bottom-center", Anchor.BottomCenter },
        { "bottom-right", Anchor.BottomRight },
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

