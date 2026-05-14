using System.Diagnostics;
using BoxPlanLib.Cutting;
using BoxPlanLib.Model;
using Clipper2Lib;
using Google.OrTools.Sat;

namespace BoxPlanLib.Svg;

internal sealed record AdvancedLayoutPlacement(BoxPlanCuttableShape Shape, int PageIndex, double X, double Y, int Rotation);

internal sealed record AdvancedLayoutResult(IReadOnlyList<AdvancedLayoutPlacement> Items, int PageCount);

internal sealed class AdvancedLayoutOptimizer
{
    private const double Epsilon = 1e-6;
    private const int ClipperPrecision = 6;

    private readonly BoxPlanSettings _settings;
    private readonly double _usableWidth;
    private readonly double _usableHeight;
    private readonly Random _random;
    private readonly PipelineLogger _logger;

    public AdvancedLayoutOptimizer(BoxPlanSettings settings, PipelineLogger? logger = null)
    {
        _settings = settings;
        _usableWidth = settings.SheetWidth - (2 * settings.Margin);
        _usableHeight = settings.SheetHeight - (2 * settings.Margin);
        _random = new Random(settings.LayoutRandomSeed);
        _logger = logger ?? new PipelineLogger(false);
    }

    public AdvancedLayoutResult? Optimize(BoxPlanCuttableShape[] shapes)
    {
        if (shapes.Length == 0)
        {
            return new AdvancedLayoutResult(Array.Empty<AdvancedLayoutPlacement>(), 0);
        }

        _logger.Log($"[layout] optimizing {shapes.Length} shape(s), sheet {_usableWidth:F1}x{_usableHeight:F1}mm usable");

        var pieces = BuildPieces(shapes);
        EnsureAllPiecesFitPage(pieces);

        var orderings = BuildOrderings(pieces);
        _logger.Log($"[layout] evaluating {orderings.Count} ordering(s)");
        var candidates = new List<LayoutCandidate>();
        var sw = Stopwatch.StartNew();
        var bestPages = int.MaxValue;

        for (var i = 0; i < orderings.Count; i++)
        {
            var ordering = orderings[i];
            var bestDisplay = bestPages == int.MaxValue ? "-" : $"{bestPages}p";
            _logger.Progress($"[layout] ordering {i + 1}/{orderings.Count}  {sw.Elapsed.TotalSeconds:F1}s  best: {bestDisplay}");

            var prevCount = candidates.Count;
            void SubProgress(string suffix)
            {
                var bd = bestPages == int.MaxValue ? "-" : $"{bestPages}p";
                _logger.Progress($"[layout] ordering {i + 1}/{orderings.Count}  {sw.Elapsed.TotalSeconds:F1}s  best: {bd} | {suffix}");
            }
            TryAddCandidate(candidates, BuildLayout(pieces, ordering, useConvexHull: false, useCompaction: false, useVoidFill: false, "outline-blf", SubProgress));
            TryAddCandidate(candidates, BuildLayout(pieces, ordering, useConvexHull: true, useCompaction: false, useVoidFill: false, "hull-blf", SubProgress));
            TryAddCandidate(candidates, BuildLayout(pieces, ordering, useConvexHull: true, useCompaction: true, useVoidFill: false, "hull-blf-compact", SubProgress));
            TryAddCandidate(candidates, BuildLayout(pieces, ordering, useConvexHull: true, useCompaction: true, useVoidFill: true, "hull-blf-compact-voidfill", SubProgress));

            for (var j = prevCount; j < candidates.Count; j++)
                if (candidates[j].PageCount < bestPages)
                    bestPages = candidates[j].PageCount;
        }

        _logger.EndProgress();

        _logger.Log($"[layout] {candidates.Count} valid candidate(s) produced");

        if (candidates.Count == 0)
        {
            _logger.Warn("layout optimizer produced no valid candidates");
            return null;
        }

        var best = candidates
            .OrderBy(c => c.PageCount)
            .ThenBy(c => c.BoundingAreaWithMargins)
            .ThenBy(c => c.AggregateUsedArea)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .First();

        _logger.Log($"[layout] best strategy '{best.Name}': {best.PageCount} page(s), bounding area {best.BoundingAreaWithMargins:F0}mm²");

        var placements = new List<AdvancedLayoutPlacement>();
        for (var pageIndex = 0; pageIndex < best.Pages.Count; pageIndex++)
        {
            var page = best.Pages[pageIndex];
            foreach (var item in page.Items)
            {
                placements.Add(new AdvancedLayoutPlacement(item.Piece.Shape, pageIndex, item.X, item.Y, item.Geometry.Rotation));
            }
        }

        return new AdvancedLayoutResult(placements, best.Pages.Count);
    }

    private static void TryAddCandidate(List<LayoutCandidate> candidates, LayoutCandidate candidate)
    {
        if (candidate.Pages.Count == 0)
        {
            return;
        }

        if (candidate.Pages.Any(p => p.Items.Count == 0))
        {
            return;
        }

        candidates.Add(candidate);
    }

    private List<LayoutPiece> BuildPieces(BoxPlanCuttableShape[] shapes)
    {
        var result = new List<LayoutPiece>(shapes.Length);
        for (var index = 0; index < shapes.Length; index++)
        {
            var shape = shapes[index];
            var outline = BuildLocalOutlinePolygon(shape);
            if (outline.Count < 3)
            {
                throw new InvalidOperationException($"Shape '{shape.Id}' cannot be laid out because its outline is degenerate.");
            }

            var normalizedOutline = NormalizePolygon(outline);
            var normalizedHull = ConvexHull(normalizedOutline);

            var orientation0 = BuildOrientedGeometry(normalizedOutline, normalizedHull, rotation: 0);
            var orientations = new List<OrientedGeometry> { orientation0 };
            var seenOutlines = new HashSet<string> { OutlineKey(orientation0.Outline) };

            void TryAddOrientation(IReadOnlyList<Vec2> outline, int rotation)
            {
                var hull = ConvexHull(outline);
                var o = BuildOrientedGeometry(outline, hull, rotation);
                if (seenOutlines.Add(OutlineKey(o.Outline)))
                    orientations.Add(o);
            }

            TryAddOrientation(Rotate90AndNormalize(normalizedOutline, orientation0.Height), rotation: 90);
            TryAddOrientation(Rotate180AndNormalize(normalizedOutline, orientation0.Width, orientation0.Height), rotation: 180);
            TryAddOrientation(Rotate90AndNormalize(Rotate180AndNormalize(normalizedOutline, orientation0.Width, orientation0.Height), orientation0.Height), rotation: 270);

            var area = Math.Abs(SignedArea(normalizedOutline));
            var hullArea = Math.Abs(SignedArea(normalizedHull));
            var concavity = hullArea <= Epsilon ? 0.0 : (hullArea - area) / hullArea;

            result.Add(new LayoutPiece(index, shape, area, concavity, orientations));
        }

        return result;
    }

    private static OrientedGeometry BuildOrientedGeometry(IReadOnlyList<Vec2> outline, IReadOnlyList<Vec2> hull, int rotation)
    {
        var (minX, minY, maxX, maxY) = Bounds(outline);
        var normalizedOutline = TranslatePolygon(outline, -minX, -minY);
        var normalizedHull = TranslatePolygon(hull, -minX, -minY);

        return new OrientedGeometry(
            rotation,
            maxX - minX,
            maxY - minY,
            normalizedOutline,
            normalizedHull);
    }

    private static IReadOnlyList<Vec2> BuildLocalOutlinePolygon(BoxPlanCuttableShape shape)
    {
        var origin = shape.BoundingBoxMin;
        var points = new List<Vec2>();
        var current = new Vec2(shape.Outline.Start.X - origin.X, shape.Outline.Start.Y - origin.Y);
        points.Add(current);

        foreach (var segment in shape.Outline.Segments)
        {
            switch (segment)
            {
                case LineSegment line:
                {
                    current = new Vec2(line.To.X - origin.X, line.To.Y - origin.Y);
                    points.Add(current);
                    break;
                }
                case ArcSegment arc:
                {
                    var arcEnd = new Vec2(arc.To.X - origin.X, arc.To.Y - origin.Y);
                    AppendArcPolyline(points, current, arcEnd, arc);
                    current = arcEnd;
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unsupported segment type '{segment.GetType().Name}'.");
            }
        }

        if (points.Count > 1 && NearlyEqual(points[0], points[^1]))
        {
            points.RemoveAt(points.Count - 1);
        }

        return RemoveCollinear(points);
    }

    private static void AppendArcPolyline(List<Vec2> points, Vec2 arcStart, Vec2 arcEnd, ArcSegment arc)
    {
        var geometry = ArcGeometry.Create(arcStart, arcEnd, arc);
        var sweep = Math.Abs(geometry.SweepAngle);

        var segments = Math.Max(4, (int)Math.Ceiling(sweep / (Math.PI / 12.0)));
        for (var i = 1; i <= segments; i++)
        {
            var t = i / (double)segments;
            points.Add(geometry.PointAt(t));
        }
    }

    private void EnsureAllPiecesFitPage(IReadOnlyList<LayoutPiece> pieces)
    {
        foreach (var piece in pieces)
        {
            if (!piece.Orientations.Any(o => o.Width <= _usableWidth + Epsilon && o.Height <= _usableHeight + Epsilon))
            {
                throw new InvalidOperationException($"Shape '{piece.Shape.Id}' does not fit within the configured sheet size.");
            }
        }
    }

    private List<int[]> BuildOrderings(IReadOnlyList<LayoutPiece> pieces)
    {
        var orderings = new List<int[]>();

        void AddOrder(IEnumerable<LayoutPiece> ordered)
        {
            var order = ordered.Select(p => p.Index).ToArray();
            if (order.Length == 0)
            {
                return;
            }

            if (!orderings.Any(existing => existing.SequenceEqual(order)))
            {
                orderings.Add(order);
            }
        }

        AddOrder(pieces
            .OrderByDescending(p => p.Area)
            .ThenByDescending(p => p.MaxSpan)
            .ThenBy(p => p.Index));

        AddOrder(pieces
            .OrderByDescending(p => p.MaxSpan)
            .ThenByDescending(p => p.MinSpan)
            .ThenBy(p => p.Index));

        AddOrder(pieces
            .OrderByDescending(p => p.Concavity)
            .ThenByDescending(p => p.Area)
            .ThenBy(p => p.Index));

        var shuffleIterations = Math.Max(0, _settings.LayoutSearchIterations);
        if (shuffleIterations > 0)
        {
            var baseline = pieces.OrderByDescending(p => p.Area).ThenBy(p => p.Index).Select(p => p.Index).ToArray();
            for (var iteration = 0; iteration < shuffleIterations; iteration++)
            {
                var shuffled = baseline.ToArray();
                FisherYates(shuffled);
                if (!orderings.Any(existing => existing.SequenceEqual(shuffled)))
                {
                    orderings.Add(shuffled);
                }
            }
        }

        if (_settings.UseOrToolsSequenceOptimization)
        {
            var orToolsOrder = TryBuildOrToolsOrdering(pieces);
            if (orToolsOrder is not null && !orderings.Any(existing => existing.SequenceEqual(orToolsOrder)))
            {
                orderings.Add(orToolsOrder);
            }
        }

        return orderings;
    }

    private int[]? TryBuildOrToolsOrdering(IReadOnlyList<LayoutPiece> pieces)
    {
        // The OR-Tools sequence model explores adjacency compatibility and can
        // discover better placement orders than simple sorting heuristics.
        if (pieces.Count < 4 || pieces.Count > 28)
        {
            _logger.Log($"[layout] OR-Tools ordering skipped (piece count {pieces.Count} outside supported range 4–28)");
            return null;
        }

        _logger.Log($"[layout] running OR-Tools sequence optimization for {pieces.Count} piece(s)");

        try
        {
            var n = pieces.Count;
            var model = new CpModel();
            var x = new BoolVar[n, n];

            for (var i = 0; i < n; i++)
            {
                for (var p = 0; p < n; p++)
                {
                    x[i, p] = model.NewBoolVar($"x_{i}_{p}");
                }
            }

            for (var i = 0; i < n; i++)
            {
                var row = new List<ILiteral>(n);
                for (var p = 0; p < n; p++)
                {
                    row.Add(x[i, p]);
                }

                model.AddExactlyOne(row);
            }

            for (var p = 0; p < n; p++)
            {
                var col = new List<ILiteral>(n);
                for (var i = 0; i < n; i++)
                {
                    col.Add(x[i, p]);
                }

                model.AddExactlyOne(col);
            }

            var objectiveTerms = new List<LinearExpr>();
            for (var p = 0; p < n - 1; p++)
            {
                for (var i = 0; i < n; i++)
                {
                    for (var j = 0; j < n; j++)
                    {
                        if (i == j)
                        {
                            continue;
                        }

                        var adjacency = model.NewBoolVar($"adj_{i}_{j}_{p}");
                        model.Add(adjacency <= x[i, p]);
                        model.Add(adjacency <= x[j, p + 1]);
                        model.Add(adjacency >= x[i, p] + x[j, p + 1] - 1);

                        var weight = CompatibilityScore(pieces[i], pieces[j]);
                        if (weight != 0)
                        {
                            objectiveTerms.Add(weight * adjacency);
                        }
                    }
                }
            }

            if (objectiveTerms.Count == 0)
            {
                return null;
            }

            model.Maximize(LinearExpr.Sum(objectiveTerms));

            var solver = new CpSolver
            {
                StringParameters = "max_time_in_seconds:2.0,num_search_workers:8"
            };

            var status = solver.Solve(model);
            if (status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible))
            {
                return null;
            }

            var byPosition = new int[n];
            for (var p = 0; p < n; p++)
            {
                for (var i = 0; i < n; i++)
                {
                    if (solver.Value(x[i, p]) == 1)
                    {
                        byPosition[p] = pieces[i].Index;
                        break;
                    }
                }
            }

            return byPosition;
        }
        catch
        {
            return null;
        }
    }

    private static int CompatibilityScore(LayoutPiece a, LayoutPiece b)
    {
        var widthDelta = Math.Abs(a.MaxSpan - b.MaxSpan);
        var heightDelta = Math.Abs(a.MinSpan - b.MinSpan);
        var areaDelta = Math.Abs(a.Area - b.Area) / 50.0;
        var concavityDelta = Math.Abs(a.Concavity - b.Concavity) * 10.0;

        // Higher score means piece pairs are likely to form compact frontiers.
        var score = 200.0 - (widthDelta + heightDelta + areaDelta + concavityDelta);
        return (int)Math.Round(score);
    }

    private LayoutCandidate BuildLayout(
        IReadOnlyList<LayoutPiece> pieces,
        IReadOnlyList<int> ordering,
        bool useConvexHull,
        bool useCompaction,
        bool useVoidFill,
        string strategyName,
        Action<string> subProgress)
    {
        var pages = new List<PageState>();
        var unplaced = new HashSet<int>(ordering);

        while (unplaced.Count > 0)
        {
            subProgress($"{strategyName}: page {pages.Count + 1}  {unplaced.Count}/{pieces.Count} pieces");
            var page = new PageState(pages.Count);
            var orderForPage = ordering.Where(unplaced.Contains).Select(index => pieces[index]).ToList();

            if (useVoidFill && orderForPage.Count >= 4)
            {
                var largeCount = Math.Max(1, (int)Math.Ceiling(orderForPage.Count * 0.65));
                var primary = orderForPage
                    .OrderByDescending(p => p.Area)
                    .ThenByDescending(p => p.MaxSpan)
                    .Take(largeCount)
                    .ToList();
                var deferred = orderForPage
                    .Except(primary)
                    .OrderBy(p => p.Area)
                    .ThenBy(p => p.Index)
                    .ToList();

                PlaceSequence(page, primary, useConvexHull, envelopeLimit: null);
                TryFillVoids(page, deferred, useConvexHull);
                PlaceSequence(page, deferred, useConvexHull, envelopeLimit: null);
            }
            else
            {
                PlaceSequence(page, orderForPage, useConvexHull, envelopeLimit: null);
            }

            if (page.Items.Count == 0)
            {
                // A defensive fallback: force at least one shape to avoid an infinite loop.
                var first = orderForPage[0];
                if (!TryPlacePiece(page, first, useConvexHull, envelopeLimit: null, seedCandidates: null, out _))
                {
                    throw new InvalidOperationException($"Unable to place shape '{first.Shape.Id}' on an empty page.");
                }
            }

            if (useCompaction)
            {
                CompactPage(page, useConvexHull);
            }

            foreach (var placed in page.Items)
            {
                unplaced.Remove(placed.Piece.Index);
            }

            pages.Add(page);
        }

        var boundingAreaWithMargins = ComputeBoundingAreaWithMargins(pages);
        var aggregateUsedArea = pages.Sum(p => p.UsedWidth * p.UsedHeight);

        return new LayoutCandidate(
            strategyName,
            pages,
            boundingAreaWithMargins,
            aggregateUsedArea);
    }

    private void PlaceSequence(
        PageState page,
        IReadOnlyList<LayoutPiece> sequence,
        bool useConvexHull,
        EnvelopeLimit? envelopeLimit)
    {
        foreach (var piece in sequence)
        {
            if (page.Items.Any(p => p.Piece.Index == piece.Index))
            {
                continue;
            }

            TryPlacePiece(page, piece, useConvexHull, envelopeLimit, seedCandidates: null, out _);
        }
    }

    private void TryFillVoids(PageState page, List<LayoutPiece> deferred, bool useConvexHull)
    {
        if (page.Items.Count == 0 || deferred.Count == 0)
        {
            return;
        }

        var progress = true;
        while (progress && deferred.Count > 0)
        {
            progress = false;
            var envelope = new EnvelopeLimit(page.UsedWidth, page.UsedHeight);
            var freeRegions = BuildFreeRegions(page, envelope.Width, envelope.Height);
            if (freeRegions.Count == 0)
            {
                return;
            }

            for (var i = 0; i < deferred.Count; i++)
            {
                var piece = deferred[i];
                var candidatePoints = BuildVoidCandidatePoints(freeRegions);
                if (TryPlacePiece(page, piece, useConvexHull, envelope, candidatePoints, out _))
                {
                    deferred.RemoveAt(i);
                    i--;
                    progress = true;
                }
            }
        }
    }

    private static List<(double X, double Y)> BuildVoidCandidatePoints(IReadOnlyList<IReadOnlyList<Vec2>> freeRegions)
    {
        var result = new List<(double X, double Y)>();
        foreach (var region in freeRegions)
        {
            var (minX, minY, maxX, maxY) = Bounds(region);
            result.Add((minX, minY));
            result.Add((minX, maxY));
            result.Add((maxX, minY));

            foreach (var v in region)
            {
                result.Add((v.X, v.Y));
            }
        }

        return result;
    }

    private List<IReadOnlyList<Vec2>> BuildFreeRegions(PageState page, double envelopeWidth, double envelopeHeight)
    {
        if (envelopeWidth <= Epsilon || envelopeHeight <= Epsilon)
        {
            return new List<IReadOnlyList<Vec2>>();
        }

        var occupied = new PathsD();
        foreach (var item in page.Items)
        {
            occupied.Add(ToPathD(item.CollisionPolygonWorld));
        }

        var envelope = BuildRectanglePath(0.0, 0.0, envelopeWidth, envelopeHeight);
        var mergedOccupied = Clipper.Union(occupied, new PathsD(), FillRule.NonZero, ClipperPrecision);
        var free = Clipper.Difference(new PathsD { envelope }, mergedOccupied, FillRule.NonZero, ClipperPrecision);

        var regions = new List<IReadOnlyList<Vec2>>();
        foreach (var region in free)
        {
            if (Math.Abs(Clipper.Area(region)) < 1e-4)
            {
                continue;
            }

            regions.Add(ToVecList(region));
        }

        return regions;
    }

    private bool TryPlacePiece(
        PageState page,
        LayoutPiece piece,
        bool useConvexHull,
        EnvelopeLimit? envelopeLimit,
        IReadOnlyList<(double X, double Y)>? seedCandidates,
        out PlacedPiece placed)
    {
        placed = default!;

        var seenOutlines = new HashSet<string>();
        var orientations = piece.Orientations
            .OrderBy(o => o.Height)
            .ThenBy(o => o.Width)
            .ThenBy(o => o.Rotation)
            .Where(o => seenOutlines.Add(OutlineKey(o.Outline)))
            .ToArray();

        var candidates = BuildCandidatePositions(page, seedCandidates);
        PlacedPiece? best = null;

        foreach (var geometry in orientations)
        {
            foreach (var candidate in candidates)
            {
                // Candidates are sorted by (Y, X). Once Y exceeds best.Y, no further candidate
                // can satisfy IsBetterPlacement (requires Y < best.Y, or same Y with lower X/height).
                if (best is not null && candidate.Y > best.Y + Epsilon)
                    break;

                if (!FitsBounds(candidate.X, candidate.Y, geometry, envelopeLimit))
                {
                    continue;
                }

                var rawCollision = useConvexHull ? geometry.ConvexHull : geometry.Outline;
                var localCollision = BuildClearancePolygon(rawCollision);
                var worldCollision = TranslatePolygon(localCollision, candidate.X, candidate.Y);
                if (!IsPlacementFeasible(page, worldCollision, candidate.X, candidate.Y, geometry.Width, geometry.Height))
                {
                    continue;
                }

                var current = new PlacedPiece(piece, geometry, localCollision, candidate.X, candidate.Y, worldCollision);
                if (best is null || IsBetterPlacement(current, best))
                {
                    best = current;
                }
            }
        }

        if (best is null)
        {
            return false;
        }

        page.Items.Add(best);
        placed = best;
        return true;
    }

    private static bool IsBetterPlacement(PlacedPiece current, PlacedPiece best)
    {
        if (current.Y < best.Y - Epsilon)
        {
            return true;
        }

        if (Math.Abs(current.Y - best.Y) <= Epsilon && current.X < best.X - Epsilon)
        {
            return true;
        }

        if (Math.Abs(current.Y - best.Y) <= Epsilon
            && Math.Abs(current.X - best.X) <= Epsilon
            && current.Geometry.Height < best.Geometry.Height - Epsilon)
        {
            return true;
        }

        return false;
    }

    private bool FitsBounds(double x, double y, OrientedGeometry geometry, EnvelopeLimit? envelopeLimit)
    {
        if (x < -Epsilon || y < -Epsilon)
        {
            return false;
        }

        if (x + geometry.Width > _usableWidth + Epsilon || y + geometry.Height > _usableHeight + Epsilon)
        {
            return false;
        }

        if (envelopeLimit is null)
        {
            return true;
        }

        return x + geometry.Width <= envelopeLimit.Width + Epsilon
            && y + geometry.Height <= envelopeLimit.Height + Epsilon;
    }

    private bool IsPlacementFeasible(
        PageState page,
        IReadOnlyList<Vec2> candidateCollisionPolygon,
        double x,
        double y,
        double width,
        double height)
    {
        var spacingHalf = _settings.Spacing * 0.5;
        var candidateX = x - spacingHalf;
        var candidateY = y - spacingHalf;
        var candidateW = width + _settings.Spacing;
        var candidateH = height + _settings.Spacing;

        foreach (var existing in page.Items)
        {
            var existingX = existing.X - spacingHalf;
            var existingY = existing.Y - spacingHalf;
            var existingW = existing.Geometry.Width + _settings.Spacing;
            var existingH = existing.Geometry.Height + _settings.Spacing;

            if (!AabbOverlap(candidateX, candidateY, candidateW, candidateH, existingX, existingY, existingW, existingH))
            {
                continue;
            }

            if (PolygonsOverlap(candidateCollisionPolygon, existing.CollisionPolygonWorld))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AabbOverlap(
        double ax,
        double ay,
        double aw,
        double ah,
        double bx,
        double by,
        double bw,
        double bh)
    {
        return ax < bx + bw - Epsilon
            && ax + aw > bx + Epsilon
            && ay < by + bh - Epsilon
            && ay + ah > by + Epsilon;
    }

    private static bool PolygonsOverlap(IReadOnlyList<Vec2> a, IReadOnlyList<Vec2> b)
    {
        var intersection = Clipper.Intersect(
            new PathsD { ToPathD(a) },
            new PathsD { ToPathD(b) },
            FillRule.NonZero,
            ClipperPrecision);

        foreach (var path in intersection)
        {
            if (Math.Abs(Clipper.Area(path)) > 1e-6)
            {
                return true;
            }
        }

        return false;
    }

    private List<(double X, double Y)> BuildCandidatePositions(PageState page, IReadOnlyList<(double X, double Y)>? seedCandidates)
    {
        var candidates = new List<(double X, double Y)> { (0.0, 0.0) };

        if (seedCandidates is not null)
        {
            candidates.AddRange(seedCandidates);
        }

        var xAnchors = new HashSet<double>(new ApproxDoubleComparer()) { 0.0 };
        var yAnchors = new HashSet<double>(new ApproxDoubleComparer()) { 0.0 };

        foreach (var item in page.Items)
        {
            xAnchors.Add(item.X);
            xAnchors.Add(item.Right + _settings.Spacing);
            xAnchors.Add(item.Right);

            yAnchors.Add(item.Y);
            yAnchors.Add(item.Top + _settings.Spacing);
            yAnchors.Add(item.Top);

            candidates.Add((item.X, item.Top + _settings.Spacing));
            candidates.Add((item.Right + _settings.Spacing, item.Y));
            candidates.Add((item.Right + _settings.Spacing, item.Top + _settings.Spacing));
        }

        var productLimit = 5000;
        var crossCount = 0;
        var xList = xAnchors.OrderBy(v => v).ToList();
        var yList = yAnchors.OrderBy(v => v).ToList();
        foreach (var x in xList)
        {
            foreach (var y in yList)
            {
                candidates.Add((x, y));
                crossCount++;
                if (crossCount >= productLimit)
                {
                    break;
                }
            }

            if (crossCount >= productLimit)
            {
                break;
            }
        }

        return candidates
            .Where(c => c.X >= -Epsilon && c.Y >= -Epsilon)
            .DistinctBy(c => (Math.Round(c.X, 4), Math.Round(c.Y, 4)))
            .OrderBy(c => c.Y)
            .ThenBy(c => c.X)
            .ToList();
    }

    private void CompactPage(PageState page, bool useConvexHull)
    {
        if (page.Items.Count < 2)
        {
            return;
        }

        for (var pass = 0; pass < 6; pass++)
        {
            var movedAny = false;
            foreach (var item in page.Items.OrderBy(p => p.Y).ThenBy(p => p.X).ToList())
            {
                movedAny |= TryShift(item, page, useConvexHull, shiftX: true);
                movedAny |= TryShift(item, page, useConvexHull, shiftX: false);
            }

            if (!movedAny)
            {
                break;
            }
        }
    }

    private bool TryShift(PlacedPiece item, PageState page, bool useConvexHull, bool shiftX)
    {
        var currentValue = shiftX ? item.X : item.Y;
        if (currentValue <= Epsilon)
        {
            return false;
        }

        var low = 0.0;
        var high = currentValue;
        var best = currentValue;

        for (var iteration = 0; iteration < 18; iteration++)
        {
            var probe = (low + high) / 2.0;
            var testX = shiftX ? probe : item.X;
            var testY = shiftX ? item.Y : probe;
            if (CanMoveTo(item, testX, testY, page))
            {
                best = probe;
                high = probe;
            }
            else
            {
                low = probe;
            }
        }

        if (currentValue - best <= 1e-4)
        {
            return false;
        }

        item.UpdatePosition(shiftX ? best : item.X, shiftX ? item.Y : best);
        return true;
    }

    private bool CanMoveTo(PlacedPiece target, double x, double y, PageState page)
    {
        if (x < -Epsilon || y < -Epsilon || x + target.Geometry.Width > _usableWidth + Epsilon || y + target.Geometry.Height > _usableHeight + Epsilon)
        {
            return false;
        }

        var candidateWorld = TranslatePolygon(target.CollisionPolygonLocal, x, y);
        var spacingHalf = _settings.Spacing * 0.5;
        var candidateX = x - spacingHalf;
        var candidateY = y - spacingHalf;
        var candidateW = target.Geometry.Width + _settings.Spacing;
        var candidateH = target.Geometry.Height + _settings.Spacing;

        foreach (var other in page.Items)
        {
            if (ReferenceEquals(other, target))
            {
                continue;
            }

            var otherX = other.X - spacingHalf;
            var otherY = other.Y - spacingHalf;
            var otherW = other.Geometry.Width + _settings.Spacing;
            var otherH = other.Geometry.Height + _settings.Spacing;

            if (!AabbOverlap(candidateX, candidateY, candidateW, candidateH, otherX, otherY, otherW, otherH))
            {
                continue;
            }

            if (PolygonsOverlap(candidateWorld, other.CollisionPolygonWorld))
            {
                return false;
            }
        }

        return true;
    }

    private double ComputeBoundingAreaWithMargins(IReadOnlyList<PageState> pages)
    {
        if (pages.Count == 0)
        {
            return 0.0;
        }

        var widths = pages.Select(p => p.UsedWidth + (2 * _settings.Margin)).ToArray();
        var heights = pages.Select(p => p.UsedHeight + (2 * _settings.Margin)).ToArray();

        var totalWidth = widths.Sum();
        if (pages.Count > 1)
        {
            totalWidth += _settings.Spacing * (pages.Count - 1);
        }

        var maxHeight = heights.Max();
        return totalWidth * maxHeight;
    }

    private static IReadOnlyList<Vec2> NormalizePolygon(IReadOnlyList<Vec2> polygon)
    {
        var (minX, minY, _, _) = Bounds(polygon);
        return TranslatePolygon(polygon, -minX, -minY);
    }

    private static IReadOnlyList<Vec2> Rotate90AndNormalize(IReadOnlyList<Vec2> polygon, double originalHeight)
    {
        var rotated = new List<Vec2>(polygon.Count);
        foreach (var point in polygon)
        {
            rotated.Add(new Vec2(originalHeight - point.Y, point.X));
        }

        return NormalizePolygon(rotated);
    }

    private static IReadOnlyList<Vec2> Rotate180AndNormalize(IReadOnlyList<Vec2> polygon, double originalWidth, double originalHeight)
    {
        var rotated = new List<Vec2>(polygon.Count);
        foreach (var point in polygon)
        {
            rotated.Add(new Vec2(originalWidth - point.X, originalHeight - point.Y));
        }

        return NormalizePolygon(rotated);
    }

    private static string OutlineKey(IReadOnlyList<Vec2> outline) =>
        string.Join(";", outline
            .Select(v => ((long)Math.Round(v.X * 1e4), (long)Math.Round(v.Y * 1e4)))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2)
            .Select(t => $"{t.Item1},{t.Item2}"));

    private static IReadOnlyList<Vec2> TranslatePolygon(IReadOnlyList<Vec2> polygon, double dx, double dy)
    {
        var translated = new List<Vec2>(polygon.Count);
        foreach (var point in polygon)
        {
            translated.Add(new Vec2(point.X + dx, point.Y + dy));
        }

        return translated;
    }

    private IReadOnlyList<Vec2> BuildClearancePolygon(IReadOnlyList<Vec2> polygon)
    {
        if (_settings.Spacing <= Epsilon)
        {
            return polygon;
        }

        var delta = _settings.Spacing * 0.5;
        var inflated = Clipper.InflatePaths(
            new PathsD { ToPathD(polygon) },
            delta,
            JoinType.Round,
            EndType.Polygon,
            ClipperPrecision);

        if (inflated.Count == 0)
        {
            return polygon;
        }

        var largest = inflated
            .OrderByDescending(path => Math.Abs(Clipper.Area(path)))
            .First();
        return ToVecList(largest);
    }

    private static PathD BuildRectanglePath(double minX, double minY, double maxX, double maxY)
    {
        return new PathD(new[]
        {
            new PointD(minX, minY),
            new PointD(maxX, minY),
            new PointD(maxX, maxY),
            new PointD(minX, maxY),
        });
    }

    private static PathD ToPathD(IReadOnlyList<Vec2> polygon)
    {
        return new PathD(polygon.Select(p => new PointD(p.X, p.Y)));
    }

    private static IReadOnlyList<Vec2> ToVecList(PathD path)
    {
        return path.Select(point => new Vec2(point.x, point.y)).ToList();
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<Vec2> polygon)
    {
        var minX = polygon[0].X;
        var minY = polygon[0].Y;
        var maxX = polygon[0].X;
        var maxY = polygon[0].Y;

        for (var i = 1; i < polygon.Count; i++)
        {
            var p = polygon[i];
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        return (minX, minY, maxX, maxY);
    }

    private static double SignedArea(IReadOnlyList<Vec2> polygon)
    {
        var areaTwice = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            areaTwice += (a.X * b.Y) - (a.Y * b.X);
        }

        return areaTwice / 2.0;
    }

    private static IReadOnlyList<Vec2> ConvexHull(IReadOnlyList<Vec2> points)
    {
        var ordered = points
            .DistinctBy(p => (Math.Round(p.X, 7), Math.Round(p.Y, 7)))
            .OrderBy(p => p.X)
            .ThenBy(p => p.Y)
            .ToList();

        if (ordered.Count <= 3)
        {
            return ordered;
        }

        var lower = new List<Vec2>();
        foreach (var point in ordered)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= Epsilon)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        var upper = new List<Vec2>();
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var point = ordered[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= Epsilon)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);

        return lower.Concat(upper).ToList();
    }

    private static IReadOnlyList<Vec2> RemoveCollinear(IReadOnlyList<Vec2> polygon)
    {
        if (polygon.Count <= 3)
        {
            return polygon;
        }

        var result = new List<Vec2>(polygon.Count);
        var n = polygon.Count;
        for (var i = 0; i < n; i++)
        {
            var prev = polygon[(i + n - 1) % n];
            var curr = polygon[i];
            var next = polygon[(i + 1) % n];

            var a = new Vec2(curr.X - prev.X, curr.Y - prev.Y);
            var b = new Vec2(next.X - curr.X, next.Y - curr.Y);
            var cross = (a.X * b.Y) - (a.Y * b.X);
            var dot = (a.X * b.X) + (a.Y * b.Y);

            if (Math.Abs(cross) <= 1e-8 && dot > 0)
            {
                continue;
            }

            result.Add(curr);
        }

        return result;
    }

    private static double Cross(Vec2 a, Vec2 b, Vec2 c)
    {
        return ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
    }

    private void FisherYates(int[] array)
    {
        for (var i = array.Length - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }

    private static bool NearlyEqual(Vec2 a, Vec2 b)
    {
        return Math.Abs(a.X - b.X) <= Epsilon && Math.Abs(a.Y - b.Y) <= Epsilon;
    }

    private sealed record LayoutPiece(
        int Index,
        BoxPlanCuttableShape Shape,
        double Area,
        double Concavity,
        IReadOnlyList<OrientedGeometry> Orientations)
    {
        public double MaxSpan => Orientations.Max(o => Math.Max(o.Width, o.Height));
        public double MinSpan => Orientations.Min(o => Math.Min(o.Width, o.Height));
    }

    private sealed record OrientedGeometry(
        int Rotation,
        double Width,
        double Height,
        IReadOnlyList<Vec2> Outline,
        IReadOnlyList<Vec2> ConvexHull);

    private sealed class PlacedPiece
    {
        public PlacedPiece(
            LayoutPiece piece,
            OrientedGeometry geometry,
            IReadOnlyList<Vec2> collisionPolygonLocal,
            double x,
            double y,
            IReadOnlyList<Vec2> collisionPolygonWorld)
        {
            Piece = piece;
            Geometry = geometry;
            X = x;
            Y = y;
            CollisionPolygonLocal = collisionPolygonLocal;
            CollisionPolygonWorld = collisionPolygonWorld;
        }

        public LayoutPiece Piece { get; }
        public OrientedGeometry Geometry { get; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public IReadOnlyList<Vec2> CollisionPolygonLocal { get; }
        public IReadOnlyList<Vec2> CollisionPolygonWorld { get; private set; }
        public double Right => X + Geometry.Width;
        public double Top => Y + Geometry.Height;

        public void UpdatePosition(double x, double y)
        {
            X = x;
            Y = y;
            CollisionPolygonWorld = TranslatePolygon(CollisionPolygonLocal, x, y);
        }
    }

    private sealed class PageState
    {
        public PageState(int pageIndex)
        {
            PageIndex = pageIndex;
        }

        public int PageIndex { get; }
        public List<PlacedPiece> Items { get; } = new();

        public double UsedWidth => Items.Count == 0 ? 0.0 : Items.Max(i => i.Right);
        public double UsedHeight => Items.Count == 0 ? 0.0 : Items.Max(i => i.Top);
    }

    private sealed record LayoutCandidate(
        string Name,
        IReadOnlyList<PageState> Pages,
        double BoundingAreaWithMargins,
        double AggregateUsedArea)
    {
        public int PageCount => Pages.Count;
    }

    private sealed record EnvelopeLimit(double Width, double Height);

    private sealed class ApproxDoubleComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= 1e-6;

        public int GetHashCode(double obj) => Math.Round(obj, 6).GetHashCode();
    }

    private sealed class ArcGeometry
    {
        private ArcGeometry(Vec2 center, double radius, double startAngle, double sweepAngle)
        {
            Center = center;
            Radius = radius;
            StartAngle = startAngle;
            SweepAngle = sweepAngle;
        }

        public Vec2 Center { get; }
        public double Radius { get; }
        public double StartAngle { get; }
        public double SweepAngle { get; }

        public static ArcGeometry Create(Vec2 start, Vec2 end, ArcSegment arc)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var chordLength = Math.Sqrt((dx * dx) + (dy * dy));
            if (chordLength <= Epsilon)
            {
                throw new InvalidOperationException("Degenerate arc segment is not supported.");
            }

            var halfChord = chordLength / 2.0;
            var heightSquared = (arc.Radius * arc.Radius) - (halfChord * halfChord);
            if (heightSquared < -Epsilon)
            {
                throw new InvalidOperationException("Arc radius is too small for the requested segment.");
            }

            var midpoint = new Vec2((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0);
            var height = Math.Sqrt(Math.Max(0.0, heightSquared));
            var perp = new Vec2(-dy / chordLength, dx / chordLength);
            var centers = new[]
            {
                new Vec2(midpoint.X + (perp.X * height), midpoint.Y + (perp.Y * height)),
                new Vec2(midpoint.X - (perp.X * height), midpoint.Y - (perp.Y * height)),
            };

            foreach (var center in centers)
            {
                var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
                var endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);
                var ccwSweep = NormalizePositiveSweep(endAngle - startAngle);
                var sweepAngle = arc.Clockwise
                    ? -(Math.Abs(ccwSweep) <= Epsilon ? Math.PI * 2.0 : (Math.PI * 2.0) - ccwSweep)
                    : (Math.Abs(ccwSweep) <= Epsilon ? Math.PI * 2.0 : ccwSweep);

                var isLargeArc = Math.Abs(sweepAngle) > Math.PI + Epsilon;
                if (isLargeArc == arc.LargeArc || Math.Abs(Math.Abs(sweepAngle) - Math.PI) <= Epsilon)
                {
                    return new ArcGeometry(center, arc.Radius, startAngle, sweepAngle);
                }
            }

            throw new InvalidOperationException("Unable to resolve arc geometry.");
        }

        public Vec2 PointAt(double t)
        {
            var angle = StartAngle + (SweepAngle * t);
            return new Vec2(Center.X + (Radius * Math.Cos(angle)), Center.Y + (Radius * Math.Sin(angle)));
        }

        private static double NormalizeAngle(double angle)
        {
            var fullTurn = Math.PI * 2.0;
            var normalized = angle % fullTurn;
            return normalized < 0 ? normalized + fullTurn : normalized;
        }

        private static double NormalizePositiveSweep(double angle)
        {
            return NormalizeAngle(angle + (Math.PI * 2.0));
        }
    }
}
