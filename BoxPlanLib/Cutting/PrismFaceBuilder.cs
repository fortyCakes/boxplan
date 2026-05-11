using BoxPlanLib.Model;
using Clipper2Lib;

namespace BoxPlanLib.Cutting;

// Generates BoxPlanCuttableShape outputs for a PrismShape:
//   - One rectangular panel per lateral face (straight or curved)
//   - One polygon panel per closed cap face (front/back)
internal static class PrismFaceBuilder
{
    public static IEnumerable<BoxPlanCuttableShape> Build(
        string shapeId,
        PrismShape prism,
        double depth,
        BoxPlanSettings settings,
        PipelineLogger? logger)
    {
        var n = prism.LateralFaces.Count;

        for (var i = 0; i < n; i++)
        {
            var lf = prism.LateralFaces[i];
            if (lf.Type != FaceType.Closed) continue;

            var id = $"{shapeId}.lateral-{i}";
            if (lf.IsCurved)
                yield return BuildCurvedLateralFace(id, i, prism, depth, settings, logger);
            else
                yield return BuildStraightLateralFace(id, i, prism, depth, settings, logger);
        }

        foreach (var face in prism.Faces)
        {
            if (face.Type != FaceType.Closed) continue;
            var isFront = face.Name == FaceName.Front;
            yield return BuildCapFace(
                $"{shapeId}.{(isFront ? "front" : "back")}",
                prism, depth, isFront, settings, logger);
        }
    }

    // ── Straight lateral face ─────────────────────────────────────────────────

    private static BoxPlanCuttableShape BuildStraightLateralFace(
        string id,
        int faceIndex,
        PrismShape prism,
        double depth,
        BoxPlanSettings settings,
        PipelineLogger? logger)
    {
        var t = settings.MaterialThickness;
        var s = settings.FingerJointSize;
        var n = prism.LateralFaces.Count;
        var face = prism.LateralFaces[faceIndex];
        var w = face.EdgeLength;
        var h = depth;

        var prevIndex = (faceIndex + n - 1) % n;
        var nextIndex = (faceIndex + 1) % n;
        var prevFace = prism.LateralFaces[prevIndex];
        var nextFace = prism.LateralFaces[nextIndex];

        var frontClosed = IsFaceClosed(prism, FaceName.Front);
        var backClosed = IsFaceClosed(prism, FaceName.Back);

        logger?.Log($"[prism] Building lateral face {id} w={w:F3} h={h:F3}");

        var builder = new PanelShapeBuilder(w, h, logger);

        // All lateral face corners get notches (cap faces are always lower priority).
        for (var ci = 0; ci < 4; ci++)
            SubtractCornerNotch(builder, ci, w, h, t);

        // Bottom edge (edge 0, meets back cap — lowest priority 0)
        if (backClosed)
        {
            var blocks = FingerJointPattern.Build(w - 2 * t, s, t);
            var cursor = t;
            foreach (var block in blocks)
            {
                // Back cap owns primary blocks → lateral gets slots there
                if (block.PrimaryOwns)
                    builder.SubtractEdgeNotch(0, cursor, block.Length, t);
                cursor += block.Length;
            }
        }

        // Top edge (edge 2, meets front cap — priority 1)
        // Edge 2 runs right→left; position 0 = top-right corner. Pattern is palindromic, so
        // the same cursor logic applies measured from the right.
        if (frontClosed)
        {
            var blocks = FingerJointPattern.Build(w - 2 * t, s, t);
            var cursor = t;
            foreach (var block in blocks)
            {
                if (block.PrimaryOwns)
                    builder.SubtractEdgeNotch(2, cursor, block.Length, t);
                cursor += block.Length;
            }
        }

        // Left edge (edge 3, meets prev lateral face)
        if (prevFace.Type == FaceType.Closed)
        {
            // prevFace.DihedralAngleToNext is the angle at the LEFT vertex of this face.
            var slotDepthLeft = SlotDepth(prevFace.DihedralAngleToNext, t);
            var currentIsPrimaryOnLeft = IsLowerPriority(faceIndex, prevIndex);
            var blocks = FingerJointPattern.Build(h - 2 * t, s, t);
            var cursor = t;
            foreach (var block in blocks)
            {
                if (currentIsPrimaryOnLeft)
                {
                    // Current face owns primary → tabs; subtract secondary (prevFace's tabs) at t
                    if (!block.PrimaryOwns)
                        builder.SubtractEdgeNotch(3, cursor, block.Length, t);
                }
                else
                {
                    // Current face is secondary → subtract primary (prevFace's tabs) at deep slot
                    if (block.PrimaryOwns)
                        builder.SubtractEdgeNotch(3, cursor, block.Length, slotDepthLeft);
                }
                cursor += block.Length;
            }
        }

        // Right edge (edge 1, meets next lateral face)
        if (nextFace.Type == FaceType.Closed)
        {
            // face.DihedralAngleToNext is the angle at the RIGHT vertex of this face (shared with next).
            var slotDepthRight = SlotDepth(face.DihedralAngleToNext, t);
            var currentIsPrimaryOnRight = IsLowerPriority(faceIndex, nextIndex);
            var blocks = FingerJointPattern.Build(h - 2 * t, s, t);
            var cursor = t;
            foreach (var block in blocks)
            {
                if (currentIsPrimaryOnRight)
                {
                    // Current face owns primary → tabs; subtract secondary at t
                    if (!block.PrimaryOwns)
                        builder.SubtractEdgeNotch(1, cursor, block.Length, t);
                }
                else
                {
                    // Current face is secondary → subtract primary at deep slot
                    if (block.PrimaryOwns)
                        builder.SubtractEdgeNotch(1, cursor, block.Length, slotDepthRight);
                }
                cursor += block.Length;
            }
        }

        var polygon = builder.Build();
        var (path, bbMin, bbMax, _) = KerfOffset.OffsetOutwardAndTranslate(polygon, settings.Kerf, logger);

        return new BoxPlanCuttableShape
        {
            Id = id,
            BoundingBoxMin = bbMin,
            BoundingBoxMax = bbMax,
            Outline = path,
            InteriorCuts = Array.Empty<CuttablePath>(),
            Engravings = Array.Empty<CuttablePath>(),
            TextEngravings = Array.Empty<TextEngraving>(),
        };
    }

    // ── Curved lateral face ───────────────────────────────────────────────────

    private static BoxPlanCuttableShape BuildCurvedLateralFace(
        string id,
        int faceIndex,
        PrismShape prism,
        double depth,
        BoxPlanSettings settings,
        PipelineLogger? logger)
    {
        var t = settings.MaterialThickness;
        var s = settings.FingerJointSize;
        var n = prism.LateralFaces.Count;
        var face = prism.LateralFaces[faceIndex];

        // Arc length, compensated for flex cut shortening. Guard against zero/unset factor.
        var compensation = settings.FlexLengthCompensationFactor > 0 ? settings.FlexLengthCompensationFactor : 1.0;
        var arcLen = face.EdgeLength * compensation;
        var w = arcLen;
        var h = depth;

        logger?.Log($"[prism] Building curved lateral face {id} arcLen={arcLen:F3} depth={h:F3}");

        var prevIndex = (faceIndex + n - 1) % n;
        var nextIndex = (faceIndex + 1) % n;
        var prevFace = prism.LateralFaces[prevIndex];
        var nextFace = prism.LateralFaces[nextIndex];

        var frontClosed = IsFaceClosed(prism, FaceName.Front);
        var backClosed = IsFaceClosed(prism, FaceName.Back);

        var builder = new PanelShapeBuilder(w, h, logger);

        for (var ci = 0; ci < 4; ci++)
            SubtractCornerNotch(builder, ci, w, h, t);

        if (backClosed)
        {
            var blocks = FingerJointPattern.Build(w - 2 * t, s, t);
            var cursor = t;
            foreach (var block in blocks)
            {
                if (block.PrimaryOwns)
                    builder.SubtractEdgeNotch(0, cursor, block.Length, t);
                cursor += block.Length;
            }
        }

        if (frontClosed)
        {
            var blocks = FingerJointPattern.Build(w - 2 * t, s, t);
            var cursor = t;
            foreach (var block in blocks)
            {
                if (block.PrimaryOwns)
                    builder.SubtractEdgeNotch(2, cursor, block.Length, t);
                cursor += block.Length;
            }
        }

        if (prevFace.Type == FaceType.Closed)
        {
            var slotDepth = SlotDepth(prevFace.DihedralAngleToNext, t);
            var isPrimary = IsLowerPriority(faceIndex, prevIndex);
            var blocks = FingerJointPattern.Build(h - 2 * t, s, t);
            var cursor = t;
            foreach (var block in blocks)
            {
                if (isPrimary ? !block.PrimaryOwns : block.PrimaryOwns)
                    builder.SubtractEdgeNotch(3, cursor, block.Length, isPrimary ? t : slotDepth);
                cursor += block.Length;
            }
        }

        if (nextFace.Type == FaceType.Closed)
        {
            var slotDepth = SlotDepth(face.DihedralAngleToNext, t);
            var isPrimary = IsLowerPriority(faceIndex, nextIndex);
            var blocks = FingerJointPattern.Build(h - 2 * t, s, t);
            var cursor = t;
            foreach (var block in blocks)
            {
                if (isPrimary ? !block.PrimaryOwns : block.PrimaryOwns)
                    builder.SubtractEdgeNotch(1, cursor, block.Length, isPrimary ? t : slotDepth);
                cursor += block.Length;
            }
        }

        var polygon = builder.Build();
        var (path, bbMin, bbMax, translation) = KerfOffset.OffsetOutwardAndTranslate(polygon, settings.Kerf, logger);

        var rawFlexCuts = FlexPatternBuilder.Build(w, h, settings, logger);
        var flexCuts = ClipFlexCuts(rawFlexCuts, polygon, w, h, t, translation);

        return new BoxPlanCuttableShape
        {
            Id = id,
            BoundingBoxMin = bbMin,
            BoundingBoxMax = bbMax,
            Outline = path,
            InteriorCuts = flexCuts,
            Engravings = Array.Empty<CuttablePath>(),
            TextEngravings = Array.Empty<TextEngraving>(),
        };
    }

    // ── Cap face (polygon panel) ──────────────────────────────────────────────

    private static BoxPlanCuttableShape BuildCapFace(
        string id,
        PrismShape prism,
        double depth,
        bool isFront,
        BoxPlanSettings settings,
        PipelineLogger? logger)
    {
        var t = settings.MaterialThickness;
        var s = settings.FingerJointSize;
        var n = prism.LateralFaces.Count;
        var profile = prism.Profile;

        logger?.Log($"[prism] Building cap face {id}");

        // Subject polygon: arcs are discretised into polyline segments so Clipper gets a valid area.
        var rawPoly = BuildCapPolygon(profile, n);
        var minX = rawPoly.Min(v => v.X);
        var minZ = rawPoly.Min(v => v.Y);
        var poly = rawPoly.Select(v => new Vec2(v.X - minX, v.Y - minZ)).ToList();

        var subject = new PathsD { ToClipperPath(poly) };
        var clips = new PathsD();

        for (var i = 0; i < n; i++)
        {
            var lf = prism.LateralFaces[i];
            if (lf.Type != FaceType.Closed) continue;

            var edgeLen = lf.EdgeLength;
            var innerLen = edgeLen - 2 * t;
            if (innerLen <= 0) continue;

            var seg = profile.Segments[i];
            // Chord endpoints in translated coords (needed by OrientedNotch / ArcGeometry).
            var rawStart = i == 0 ? profile.StartPoint : profile.Segments[i - 1].EndPoint;
            var edgeStart = new Vec2(rawStart.X - minX, rawStart.Y - minZ);
            var edgeEnd   = new Vec2(seg.EndPoint.X - minX, seg.EndPoint.Y - minZ);

            var blocks = FingerJointPattern.Build(innerLen, s, t);
            var cursor = t;

            if (lf.IsCurved && seg is ProfileSegment.Arc arc)
            {
                var translatedArc = new ProfileSegment.Arc(edgeEnd, arc.Radius, arc.Clockwise);
                var (arcCenter, thetaStart, thetaTotal) = ArcGeometry(edgeStart, translatedArc);
                foreach (var block in blocks)
                {
                    if (!block.PrimaryOwns)
                        clips.Add(ArcNotch(arcCenter, arc.Radius, thetaStart, thetaTotal, edgeLen, cursor, block.Length, t));
                    cursor += block.Length;
                }
            }
            else
            {
                // Straight edge (Bezier falls back to chord-direction approximation).
                foreach (var block in blocks)
                {
                    if (!block.PrimaryOwns)
                        clips.Add(OrientedNotch(edgeStart, edgeEnd, cursor, block.Length, t, poly));
                    cursor += block.Length;
                }
            }
        }

        const int precision = 8;
        var solution = Clipper.Difference(subject, clips, FillRule.NonZero, precision);
        var result = PickLargestArea(solution);
        if (result is null)
        {
            logger?.Warn($"[prism] Cap face {id} produced empty polygon after notching");
            return EmptyShape(id);
        }

        var polyVecs = result.Select(p => new Vec2(p.x, p.y)).ToList();
        var (path, bbMin, bbMax, _) = KerfOffset.OffsetOutwardAndTranslate(polyVecs, settings.Kerf, logger);

        return new BoxPlanCuttableShape
        {
            Id = id,
            BoundingBoxMin = bbMin,
            BoundingBoxMax = bbMax,
            Outline = path,
            InteriorCuts = Array.Empty<CuttablePath>(),
            Engravings = Array.Empty<CuttablePath>(),
            TextEngravings = Array.Empty<TextEngraving>(),
        };
    }

    // Build a polygon approximating the profile, sampling arcs/beziers into line segments.
    private static List<Vec2> BuildCapPolygon(PrismProfile profile, int n, int arcSamples = 32)
    {
        var result = new List<Vec2> { profile.StartPoint };
        var prev = profile.StartPoint;
        for (var i = 0; i < n; i++)
        {
            var seg = profile.Segments[i];
            if (seg is ProfileSegment.Arc arc)
                result.AddRange(SampleArc(prev, arc, arcSamples));
            else if (seg is ProfileSegment.Bezier bez)
                result.AddRange(SampleBezier(prev, bez, arcSamples));
            else
                result.Add(seg.EndPoint);
            prev = seg.EndPoint;
        }
        // Remove duplicate closing vertex.
        if (result.Count > 1)
        {
            var last = result[^1];
            if (Math.Abs(last.X - result[0].X) < 1e-9 && Math.Abs(last.Y - result[0].Y) < 1e-9)
                result.RemoveAt(result.Count - 1);
        }
        return result;
    }

    private static IEnumerable<Vec2> SampleArc(Vec2 start, ProfileSegment.Arc arc, int n)
    {
        var (center, thetaStart, thetaTotal) = ArcGeometry(start, arc);
        for (var i = 1; i <= n; i++)
        {
            var theta = thetaStart + thetaTotal * i / n;
            yield return new Vec2(center.X + arc.Radius * Math.Cos(theta), center.Y + arc.Radius * Math.Sin(theta));
        }
    }

    private static IEnumerable<Vec2> SampleBezier(Vec2 start, ProfileSegment.Bezier bez, int n)
    {
        for (var i = 1; i <= n; i++)
        {
            var u = (double)i / n;
            var u2 = 1 - u;
            yield return new Vec2(
                u2*u2*u2 * start.X + 3*u2*u2*u * bez.Control1.X + 3*u2*u*u * bez.Control2.X + u*u*u * bez.To.X,
                u2*u2*u2 * start.Y + 3*u2*u2*u * bez.Control1.Y + 3*u2*u*u * bez.Control2.Y + u*u*u * bez.To.Y);
        }
    }

    // Returns (center, thetaStart, thetaTotal) for an arc defined by start→arc.To with given radius.
    // thetaTotal is positive for CCW, negative for CW.
    private static (Vec2 center, double thetaStart, double thetaTotal) ArcGeometry(
        Vec2 start, ProfileSegment.Arc arc)
    {
        var p2 = arc.To;
        var cx = p2.X - start.X;
        var cz = p2.Y - start.Y;
        var chordLen = Math.Sqrt(cx * cx + cz * cz);
        if (chordLen < 1e-9)
            return (start, 0, 0);
        var halfChord = chordLen / 2;
        var h = halfChord < arc.Radius ? Math.Sqrt(arc.Radius * arc.Radius - halfChord * halfChord) : 0.0;
        // For CCW arc the centre is to the LEFT of chord P1→P2; CW → right.
        double perpX, perpZ;
        if (!arc.Clockwise) { perpX = -cz / chordLen; perpZ =  cx / chordLen; }
        else                { perpX =  cz / chordLen; perpZ = -cx / chordLen; }
        var center = new Vec2((start.X + p2.X) / 2 + h * perpX, (start.Y + p2.Y) / 2 + h * perpZ);
        var thetaStart = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var thetaEnd   = Math.Atan2(p2.Y   - center.Y, p2.X   - center.X);
        double thetaTotal;
        if (!arc.Clockwise)
        {
            thetaTotal = thetaEnd - thetaStart;
            if (thetaTotal <= 0) thetaTotal += 2 * Math.PI;
        }
        else
        {
            thetaTotal = thetaEnd - thetaStart;
            if (thetaTotal >= 0) thetaTotal -= 2 * Math.PI;
        }
        return (center, thetaStart, thetaTotal);
    }

    // Clip polygon for one finger-joint notch along a circular arc edge.
    // Notch spans arc-length [posStart, posStart+posLen] and cuts radially inward by depth.
    private static PathD ArcNotch(
        Vec2 center, double radius, double thetaStart, double thetaTotal,
        double arcLength, double posStart, double posLen, double depth)
    {
        var theta0 = thetaStart + thetaTotal * posStart / arcLength;
        var theta1 = thetaStart + thetaTotal * (posStart + posLen) / arcLength;
        var c0 = Math.Cos(theta0); var s0 = Math.Sin(theta0);
        var c1 = Math.Cos(theta1); var s1 = Math.Sin(theta1);
        var ri = radius - depth;
        return new PathD
        {
            new PointD(center.X + radius * c0, center.Y + radius * s0),
            new PointD(center.X + radius * c1, center.Y + radius * s1),
            new PointD(center.X + ri     * c1, center.Y + ri     * s1),
            new PointD(center.X + ri     * c0, center.Y + ri     * s0),
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns n+1 vertices: StartPoint, seg[0].end, ..., seg[n-1].end
    // (last vertex equals StartPoint for a properly-closed profile)
    internal static Vec2[] GetProfileVertices(PrismProfile profile)
    {
        var v = new Vec2[profile.Segments.Count + 1];
        v[0] = profile.StartPoint;
        for (var i = 0; i < profile.Segments.Count; i++)
            v[i + 1] = profile.Segments[i].EndPoint;
        return v;
    }

    private static bool IsFaceClosed(PrismShape prism, FaceName name)
        => prism.Faces.Any(f => f.Name == name && f.Type == FaceType.Closed);

    // Lateral face priority = 2 + index; returns true if faceIndex has lower priority than otherIndex.
    private static bool IsLowerPriority(int faceIndex, int otherIndex)
        => faceIndex < otherIndex;

    // Slot depth for a lateral-to-lateral joint at interior dihedral angle θ (degrees).
    internal static double SlotDepth(double dihedralDeg, double t)
    {
        var sinTheta = Math.Sin(dihedralDeg * Math.PI / 180.0);
        return sinTheta < 1e-6 ? t : Math.Max(t, t / sinTheta);
    }

    private static void SubtractCornerNotch(PanelShapeBuilder builder, int cornerIndex, double w, double h, double t)
    {
        double[] edgeLengths = { w, h, w, h };
        var prevI = (cornerIndex + 3) % 4;
        builder.SubtractEdgeNotch(prevI, edgeLengths[prevI] - t, t, t);
        builder.SubtractEdgeNotch(cornerIndex, 0, t, t);
    }

    // Builds a clip polygon for a notch along an edge, properly oriented along the edge direction.
    // The notch is cut inward (into the polygon interior) perpendicular to the edge.
    // For a CCW polygon, the inward normal is the left-perpendicular of the edge direction.
    private static PathD OrientedNotch(
        Vec2 edgeStart,
        Vec2 edgeEnd,
        double pos,
        double len,
        double depth,
        IReadOnlyList<Vec2> polygon)
    {
        var dx = edgeEnd.X - edgeStart.X;
        var dz = edgeEnd.Y - edgeStart.Y;
        var magnitude = Math.Sqrt(dx * dx + dz * dz);
        if (magnitude < 1e-9) return new PathD();

        // Unit along edge and inward normal (left-perp for CCW polygon)
        var ux = dx / magnitude;
        var uz = dz / magnitude;
        var nx = -uz; // inward normal X
        var nz = ux;  // inward normal Z

        var ax = edgeStart.X + ux * pos;
        var az = edgeStart.Y + uz * pos;
        var bx = edgeStart.X + ux * (pos + len);
        var bz = edgeStart.Y + uz * (pos + len);

        // Pick the notch normal that actually points into the polygon. This avoids
        // losing notches on sloped edges when the profile winding does not match the
        // fixed left-perpendicular assumption.
        var mid = new Vec2((ax + bx) / 2.0, (az + bz) / 2.0);
        var probe = new Vec2(mid.X + nx * (depth / 2.0), mid.Y + nz * (depth / 2.0));
        if (!PointInPolygon(probe, polygon))
        {
            nx = -nx;
            nz = -nz;
        }

        var p0 = new PointD(ax, az);
        var p1 = new PointD(bx, bz);
        var p2 = new PointD(bx + nx * depth, bz + nz * depth);
        var p3 = new PointD(ax + nx * depth, az + nz * depth);

        var clip = new PathD { p0, p1, p2, p3 };
        if (Clipper.Area(clip) < 0)
            clip.Reverse();

        return clip;
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
            if (intersects)
                inside = !inside;
        }

        return inside;
    }

    private static PathD ToClipperPath(Vec2[] verts, int count)
    {
        var path = new PathD(count);
        for (var i = 0; i < count; i++)
            path.Add(new PointD(verts[i].X, verts[i].Y));
        return path;
    }

    private static PathD ToClipperPath(List<Vec2> verts)
    {
        var path = new PathD(verts.Count);
        foreach (var v in verts)
            path.Add(new PointD(v.X, v.Y));
        return path;
    }

    private static PathD? PickLargestArea(PathsD paths)
    {
        PathD? best = null;
        var bestArea = double.NegativeInfinity;
        foreach (var p in paths)
        {
            var a = Clipper.Area(p);
            if (a > bestArea) { bestArea = a; best = p; }
        }
        return bestArea > 1e-9 ? best : null;
    }

    // Clips open flex-cut paths against the safe interior of the panel, then applies translation.
    // The safe interior is: panel polygon ∩ [t, W-t]×[t, H-t].
    // This excludes both slot areas (missing from the polygon) and tab areas (within t of any edge).
    private static List<CuttablePath> ClipFlexCuts(
        IReadOnlyList<CuttablePath> cuts,
        List<Vec2> clipPolygon,
        double panelWidth,
        double panelHeight,
        double margin,
        Vec2 translation)
    {
        if (cuts.Count == 0) return new List<CuttablePath>();

        const int precision = 8;

        // Inset rectangle strips the t-wide border where all tabs live.
        var innerRect = new PathsD
        {
            new PathD
            {
                new PointD(margin, margin),
                new PointD(panelWidth - margin, margin),
                new PointD(panelWidth - margin, panelHeight - margin),
                new PointD(margin, panelHeight - margin),
            }
        };

        // Clip zone = panel polygon ∩ inner rectangle.
        var clipZone = Clipper.Intersect(
            new PathsD { ToClipperPath(clipPolygon) },
            innerRect,
            FillRule.NonZero,
            precision);

        if (clipZone.Count == 0) return new List<CuttablePath>();

        var clipper = new ClipperD(precision);
        foreach (var cut in cuts)
        {
            var pt0 = new PointD(cut.Start.X, cut.Start.Y);
            var seg = (LineSegment)cut.Segments[0];
            var pt1 = new PointD(seg.To.X, seg.To.Y);
            clipper.AddOpenSubject(new PathD { pt0, pt1 });
        }
        foreach (var zone in clipZone)
            clipper.AddClip(zone);

        var closedSolution = new PathsD();
        var openSolution = new PathsD();
        clipper.Execute(ClipType.Intersection, FillRule.NonZero, closedSolution, openSolution);

        var result = new List<CuttablePath>(openSolution.Count);
        foreach (var openPath in openSolution)
        {
            if (openPath.Count < 2) continue;
            for (var i = 0; i + 1 < openPath.Count; i++)
            {
                result.Add(new CuttablePath
                {
                    Start = new Vec2(openPath[i].x + translation.X, openPath[i].y + translation.Y),
                    Segments = new PathSegment[]
                    {
                        new LineSegment(new Vec2(openPath[i + 1].x + translation.X, openPath[i + 1].y + translation.Y))
                    },
                    Closed = false,
                });
            }
        }
        return result;
    }

    private static BoxPlanCuttableShape EmptyShape(string id) => new()
    {
        Id = id,
        BoundingBoxMin = Vec2.Zero,
        BoundingBoxMax = Vec2.Zero,
        Outline = new CuttablePath { Start = Vec2.Zero, Segments = Array.Empty<PathSegment>(), Closed = true },
        InteriorCuts = Array.Empty<CuttablePath>(),
        Engravings = Array.Empty<CuttablePath>(),
        TextEngravings = Array.Empty<TextEngraving>(),
    };
}
