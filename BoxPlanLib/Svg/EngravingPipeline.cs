using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BoxPlanLib.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BoxPlanLib.Svg;

internal static class EngravingPipeline
{
    // ── Phase 1: called inside GetCuttableShapes ─────────────────────────────

    internal static BoxPlanCuttableShape[] NormalizeEngravingSources(
        BoxPlanCuttableShape[] pieces, string sourcePath)
    {
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))
            ?? Directory.GetCurrentDirectory();

        pieces = NormalizeRasterSources(pieces, sourceDirectory);
        pieces = NormalizeSvgSources(pieces, sourceDirectory);
        return pieces;
    }

    private static BoxPlanCuttableShape[] NormalizeRasterSources(
        BoxPlanCuttableShape[] pieces, string sourceDirectory)
    {
        var remap = BuildHrefRemap(
            pieces.SelectMany(p => p.RasterEngravings).Select(e => e.Href),
            sourceDirectory);

        return remap.Count == 0 ? pieces : CloneWithRasterHrefMap(pieces, remap);
    }

    private static BoxPlanCuttableShape[] NormalizeSvgSources(
        BoxPlanCuttableShape[] pieces, string sourceDirectory)
    {
        var remap = BuildHrefRemap(
            pieces.SelectMany(p => p.SvgEngravings).Select(e => e.Href),
            sourceDirectory);

        return remap.Count == 0 ? pieces : CloneWithSvgHrefMap(pieces, remap);
    }

    private static Dictionary<string, string> BuildHrefRemap(
        IEnumerable<string> hrefs, string sourceDirectory)
    {
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var href in hrefs.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.Ordinal))
        {
            if (LooksLikeDataUri(href))
            {
                remap[href] = href;
                continue;
            }
            remap[href] = Path.IsPathRooted(href)
                ? Path.GetFullPath(href)
                : Path.GetFullPath(Path.Combine(sourceDirectory, href));
        }
        return remap;
    }

    internal static BoxPlanCuttableShape[] ResolveEngravingDimensions(BoxPlanCuttableShape[] pieces)
    {
        pieces = ResolveRasterDimensions(pieces);
        pieces = ResolveSvgDimensions(pieces);
        return pieces;
    }

    private static BoxPlanCuttableShape[] ResolveRasterDimensions(BoxPlanCuttableShape[] pieces)
    {
        bool NeedsResolution(RasterEngraving e) => e.Width is null || e.Height is null;
        if (!pieces.Any(p => p.RasterEngravings.Any(NeedsResolution)))
            return pieces;

        var aspectByPath = new Dictionary<string, double?>(StringComparer.Ordinal);

        return pieces.Select(piece =>
        {
            if (!piece.RasterEngravings.Any(NeedsResolution))
                return piece;

            var resolved = piece.RasterEngravings.Select(e =>
            {
                if (!NeedsResolution(e)) return e;
                if (!aspectByPath.TryGetValue(e.Href, out var aspect))
                    aspectByPath[e.Href] = aspect = TryGetRasterAspectRatio(e.Href);

                var width = e.Width;
                var height = e.Height;
                if (aspect is { } ar && ar > 0)
                {
                    if (width is null && height is not null) width = height.Value * ar;
                    else if (height is null && width is not null) height = width.Value / ar;
                }
                if (width == e.Width && height == e.Height) return e;
                return new RasterEngraving
                {
                    Href = e.Href, X = e.X, Y = e.Y, Anchor = e.Anchor,
                    Width = width, Height = height,
                };
            }).ToArray();

            return new BoxPlanCuttableShape
            {
                Id = piece.Id, BoundingBoxMin = piece.BoundingBoxMin, BoundingBoxMax = piece.BoundingBoxMax,
                Outline = piece.Outline, InteriorCuts = piece.InteriorCuts, Engravings = piece.Engravings,
                TextEngravings = piece.TextEngravings, RasterEngravings = resolved, SvgEngravings = piece.SvgEngravings,
            };
        }).ToArray();
    }

    private static BoxPlanCuttableShape[] ResolveSvgDimensions(BoxPlanCuttableShape[] pieces)
    {
        bool NeedsResolution(SvgEngraving e) => e.Width is null || e.Height is null;
        if (!pieces.Any(p => p.SvgEngravings.Any(NeedsResolution)))
            return pieces;

        var aspectByPath = new Dictionary<string, double?>(StringComparer.Ordinal);

        return pieces.Select(piece =>
        {
            if (!piece.SvgEngravings.Any(NeedsResolution))
                return piece;

            var resolved = piece.SvgEngravings.Select(e =>
            {
                if (!NeedsResolution(e)) return e;
                if (!aspectByPath.TryGetValue(e.Href, out var aspect))
                    aspectByPath[e.Href] = aspect = TryGetSvgAspectRatio(e.Href);

                var width = e.Width;
                var height = e.Height;
                if (aspect is { } ar && ar > 0)
                {
                    if (width is null && height is not null) width = height.Value * ar;
                    else if (height is null && width is not null) height = width.Value / ar;
                }
                if (width == e.Width && height == e.Height) return e;
                return new SvgEngraving
                {
                    Href = e.Href, X = e.X, Y = e.Y, Anchor = e.Anchor,
                    Width = width, Height = height,
                    InlinedContent = e.InlinedContent,
                    InlinedViewBoxWidth = e.InlinedViewBoxWidth,
                    InlinedViewBoxHeight = e.InlinedViewBoxHeight,
                };
            }).ToArray();

            return new BoxPlanCuttableShape
            {
                Id = piece.Id, BoundingBoxMin = piece.BoundingBoxMin, BoundingBoxMax = piece.BoundingBoxMax,
                Outline = piece.Outline, InteriorCuts = piece.InteriorCuts, Engravings = piece.Engravings,
                TextEngravings = piece.TextEngravings, RasterEngravings = piece.RasterEngravings, SvgEngravings = resolved,
            };
        }).ToArray();
    }

    internal static BoxPlanCuttableShape[] VectorizeRasterEngravings(BoxPlanCuttableShape[] pieces)
    {
        var sources = pieces
            .SelectMany(p => p.RasterEngravings)
            .Where(e => e.InlinedPaths is null && !string.IsNullOrWhiteSpace(e.Href) && !LooksLikeDataUri(e.Href))
            .Select(e => e.Href)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (sources.Length == 0) return pieces;

        var vectorizer = new MarchingSquaresVectorizer();
        var inlineMap = new Dictionary<string, (string Paths, int W, int H)>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            if (!File.Exists(source)) continue;
            try
            {
                using var image = Image.Load<Rgba32>(source);
                var w = image.Width;
                var h = image.Height;
                var dark = new bool[h, w];
                image.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < h; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (var x = 0; x < w; x++)
                        {
                            var p = row[x];
                            var luminance = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                            dark[y, x] = p.A >= 128 && luminance < 128.0;
                        }
                    }
                });
                var paths = vectorizer.Vectorize(dark, w, h);
                if (!string.IsNullOrEmpty(paths))
                    inlineMap[source] = (paths, w, h);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: could not vectorize raster engraving '{source}': {ex.Message}");
            }
        }

        if (inlineMap.Count == 0) return pieces;

        return pieces.Select(piece =>
        {
            if (!piece.RasterEngravings.Any(e => e.InlinedPaths is null && inlineMap.ContainsKey(e.Href)))
                return piece;

            return new BoxPlanCuttableShape
            {
                Id = piece.Id, BoundingBoxMin = piece.BoundingBoxMin, BoundingBoxMax = piece.BoundingBoxMax,
                Outline = piece.Outline, InteriorCuts = piece.InteriorCuts, Engravings = piece.Engravings,
                TextEngravings = piece.TextEngravings, SvgEngravings = piece.SvgEngravings,
                RasterEngravings = piece.RasterEngravings.Select(e =>
                {
                    if (e.InlinedPaths is not null || !inlineMap.TryGetValue(e.Href, out var inline))
                        return e;
                    return new RasterEngraving
                    {
                        Href = e.Href, X = e.X, Y = e.Y, Anchor = e.Anchor,
                        Width = e.Width, Height = e.Height,
                        InlinedPaths = inline.Paths, PixelWidth = inline.W, PixelHeight = inline.H,
                    };
                }).ToArray(),
            };
        }).ToArray();
    }

    internal static BoxPlanCuttableShape[] InlineSvgEngravings(BoxPlanCuttableShape[] pieces)
    {
        var sources = pieces
            .SelectMany(p => p.SvgEngravings)
            .Select(e => e.Href)
            .Where(h => !string.IsNullOrWhiteSpace(h) && !LooksLikeDataUri(h))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (sources.Length == 0) return pieces;

        var inlineMap = new Dictionary<string, (string Content, double VbW, double VbH)>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (!File.Exists(source)) continue;
            try
            {
                var doc = XDocument.Load(source);
                var root = doc.Root;
                if (root is null) continue;

                double vbW = 0, vbH = 0;
                var viewBox = root.Attribute("viewBox")?.Value;
                if (viewBox is not null)
                {
                    var parts = viewBox.Trim().Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 4
                        && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
                        && double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h)
                        && w > 0 && h > 0)
                    {
                        vbW = w; vbH = h;
                    }
                }

                if (vbW == 0 || vbH == 0)
                {
                    var wAttr = root.Attribute("width")?.Value;
                    var hAttr = root.Attribute("height")?.Value;
                    if (wAttr is not null && hAttr is not null)
                    {
                        var wm = Regex.Match(wAttr, @"^[\d.]+");
                        var hm = Regex.Match(hAttr, @"^[\d.]+");
                        if (wm.Success && hm.Success
                            && double.TryParse(wm.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w2)
                            && double.TryParse(hm.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h2)
                            && w2 > 0 && h2 > 0)
                        {
                            vbW = w2; vbH = h2;
                        }
                    }
                }

                if (vbW == 0 || vbH == 0) continue;
                var innerXml = ExtractSvgInnerContent(root, "purple");
                if (string.IsNullOrEmpty(innerXml)) continue;
                inlineMap[source] = (innerXml, vbW, vbH);
            }
            catch
            {
                // SVG could not be parsed — will fall back to <image>
            }
        }

        if (inlineMap.Count == 0) return pieces;

        return pieces.Select(piece =>
        {
            if (!piece.SvgEngravings.Any(e => inlineMap.ContainsKey(e.Href)))
                return piece;

            return new BoxPlanCuttableShape
            {
                Id = piece.Id, BoundingBoxMin = piece.BoundingBoxMin, BoundingBoxMax = piece.BoundingBoxMax,
                Outline = piece.Outline, InteriorCuts = piece.InteriorCuts, Engravings = piece.Engravings,
                TextEngravings = piece.TextEngravings, RasterEngravings = piece.RasterEngravings,
                SvgEngravings = piece.SvgEngravings.Select(e =>
                {
                    if (!inlineMap.TryGetValue(e.Href, out var inline)) return e;
                    return new SvgEngraving
                    {
                        Href = e.Href, X = e.X, Y = e.Y, Anchor = e.Anchor,
                        Width = e.Width, Height = e.Height,
                        InlinedContent = inline.Content,
                        InlinedViewBoxWidth = inline.VbW,
                        InlinedViewBoxHeight = inline.VbH,
                    };
                }).ToArray(),
            };
        }).ToArray();
    }

    // ── Phase 2: called inside GeneratePagedSVGPages ──────────────────────────

    internal static BoxPlanCuttableShape[] PrepareEngravingAssets(
        BoxPlanCuttableShape[] pieces, BoxPlanSettings settings, string outputDirectory)
    {
        pieces = PrepareRasterAssets(pieces, settings, outputDirectory);
        pieces = PrepareSvgAssets(pieces, settings, outputDirectory);
        return pieces;
    }

    private static BoxPlanCuttableShape[] PrepareRasterAssets(
        BoxPlanCuttableShape[] pieces, BoxPlanSettings settings, string outputDirectory)
    {
        var sources = pieces
            .SelectMany(p => p.RasterEngravings)
            .Where(e => e.InlinedPaths is null)
            .Select(e => e.Href)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (sources.Length == 0) return pieces;

        var remap = new Dictionary<string, string>(StringComparer.Ordinal);

        if (settings.EmbedRasterEngravings)
        {
            foreach (var source in sources)
            {
                if (LooksLikeDataUri(source)) { remap[source] = source; continue; }
                if (!File.Exists(source))
                    throw new FileNotFoundException($"Raster engraving source not found: {source}");
                var mimeType = RasterMimeTypeFromPath(source);
                var bytes = File.ReadAllBytes(source);
                remap[source] = $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
            }
            return CloneWithRasterHrefMap(pieces, remap);
        }

        var assetDirectory = Path.Combine(outputDirectory, ResolveAssetFolderName(settings));
        Directory.CreateDirectory(assetDirectory);
        var folderName = ResolveAssetFolderName(settings);

        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (LooksLikeDataUri(source)) { remap[source] = source; continue; }
            if (!File.Exists(source))
                throw new FileNotFoundException($"Raster engraving source not found: {source}");
            var fileName = Path.GetFileName(source);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "raster-engraving.bin";
            var uniqueFileName = AllocateUniqueFileName(fileName, usedFileNames);
            File.Copy(source, Path.Combine(assetDirectory, uniqueFileName), overwrite: true);
            remap[source] = $"{folderName.Replace('\\', '/')}/{uniqueFileName}";
        }
        return CloneWithRasterHrefMap(pieces, remap);
    }

    private static BoxPlanCuttableShape[] PrepareSvgAssets(
        BoxPlanCuttableShape[] pieces, BoxPlanSettings settings, string outputDirectory)
    {
        var sources = pieces
            .SelectMany(p => p.SvgEngravings)
            .Where(e => e.InlinedContent is null)
            .Select(e => e.Href)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (sources.Length == 0) return pieces;

        var remap = new Dictionary<string, string>(StringComparer.Ordinal);

        if (settings.EmbedRasterEngravings)
        {
            foreach (var source in sources)
            {
                if (LooksLikeDataUri(source)) { remap[source] = source; continue; }
                if (!File.Exists(source))
                    throw new FileNotFoundException($"SVG engraving source not found: {source}");
                var bytes = File.ReadAllBytes(source);
                remap[source] = $"data:image/svg+xml;base64,{Convert.ToBase64String(bytes)}";
            }
            return CloneWithSvgHrefMap(pieces, remap);
        }

        var folderName = ResolveAssetFolderName(settings);
        var assetDirectory = Path.Combine(outputDirectory, folderName);
        Directory.CreateDirectory(assetDirectory);

        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (LooksLikeDataUri(source)) { remap[source] = source; continue; }
            if (!File.Exists(source))
                throw new FileNotFoundException($"SVG engraving source not found: {source}");
            var fileName = Path.GetFileName(source);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "svg-engraving.svg";
            var uniqueFileName = AllocateUniqueFileName(fileName, usedFileNames);
            File.Copy(source, Path.Combine(assetDirectory, uniqueFileName), overwrite: true);
            remap[source] = $"{folderName.Replace('\\', '/')}/{uniqueFileName}";
        }
        return CloneWithSvgHrefMap(pieces, remap);
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string ResolveAssetFolderName(BoxPlanSettings settings)
    {
        var name = settings.RasterEngravingAssetFolder?.Trim() ?? string.Empty;
        name = name.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(name) ? "assets" : name;
    }

    internal static bool LooksLikeDataUri(string value) =>
        value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static string RasterMimeTypeFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "application/octet-stream",
        };

    private static string AllocateUniqueFileName(string fileName, HashSet<string> usedFileNames)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = fileName;
        var counter = 2;
        while (!usedFileNames.Add(candidate))
        {
            candidate = $"{baseName}-{counter}{extension}";
            counter++;
        }
        return candidate;
    }

    private static BoxPlanCuttableShape[] CloneWithRasterHrefMap(
        BoxPlanCuttableShape[] pieces, IReadOnlyDictionary<string, string> hrefMap)
    {
        return pieces.Select(piece => new BoxPlanCuttableShape
        {
            Id = piece.Id, BoundingBoxMin = piece.BoundingBoxMin, BoundingBoxMax = piece.BoundingBoxMax,
            Outline = piece.Outline, InteriorCuts = piece.InteriorCuts, Engravings = piece.Engravings,
            TextEngravings = piece.TextEngravings,
            RasterEngravings = piece.RasterEngravings.Select(e => new RasterEngraving
            {
                Href = hrefMap.TryGetValue(e.Href, out var mapped) ? mapped : e.Href,
                X = e.X, Y = e.Y, Anchor = e.Anchor, Width = e.Width, Height = e.Height,
                InlinedPaths = e.InlinedPaths, PixelWidth = e.PixelWidth, PixelHeight = e.PixelHeight,
            }).ToArray(),
            SvgEngravings = piece.SvgEngravings,
        }).ToArray();
    }

    private static BoxPlanCuttableShape[] CloneWithSvgHrefMap(
        BoxPlanCuttableShape[] pieces, IReadOnlyDictionary<string, string> hrefMap)
    {
        return pieces.Select(piece => new BoxPlanCuttableShape
        {
            Id = piece.Id, BoundingBoxMin = piece.BoundingBoxMin, BoundingBoxMax = piece.BoundingBoxMax,
            Outline = piece.Outline, InteriorCuts = piece.InteriorCuts, Engravings = piece.Engravings,
            TextEngravings = piece.TextEngravings, RasterEngravings = piece.RasterEngravings,
            SvgEngravings = piece.SvgEngravings.Select(e => new SvgEngraving
            {
                Href = hrefMap.TryGetValue(e.Href, out var mapped) ? mapped : e.Href,
                X = e.X, Y = e.Y, Anchor = e.Anchor, Width = e.Width, Height = e.Height,
                InlinedContent = e.InlinedContent,
                InlinedViewBoxWidth = e.InlinedViewBoxWidth,
                InlinedViewBoxHeight = e.InlinedViewBoxHeight,
            }).ToArray(),
        }).ToArray();
    }

    private static string ExtractSvgInnerContent(XElement root, string engravingColor)
    {
        var svgNs = XNamespace.Get("http://www.w3.org/2000/svg");

        static XAttribute RecolorAttr(XAttribute a, string color) =>
            new(a.Name.LocalName,
                a.Value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? "none" : color);

        static string RewriteStyleColors(string style, string color)
        {
            style = Regex.Replace(style, @"(?<=\bfill\s*:\s*)(?!none\b)[^;]+", color);
            style = Regex.Replace(style, @"(?<=\bstroke\s*:\s*)(?!none\b)[^;]+", color);
            return style;
        }

        XElement? CleanElement(XElement elem)
        {
            if (elem.Name.Namespace != svgNs && elem.Name.Namespace != XNamespace.None)
                return null;
            var cleanedAttrs = elem.Attributes()
                .Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None)
                .Select(a => a.Name.LocalName switch
                {
                    "fill" => RecolorAttr(a, engravingColor),
                    "stroke" => RecolorAttr(a, engravingColor),
                    "style" => new XAttribute("style", RewriteStyleColors(a.Value, engravingColor)),
                    _ => new XAttribute(a.Name.LocalName, a.Value),
                });
            var cleanedChildren = elem.Nodes()
                .Select<XNode, XNode?>(n => n is XElement child ? CleanElement(child) : n is XText t ? t : null)
                .Where(n => n is not null)
                .Cast<XNode>();
            return new XElement(elem.Name.LocalName, cleanedAttrs, cleanedChildren);
        }

        var sb = new StringBuilder();
        foreach (var node in root.Nodes())
        {
            if (node is XElement elem)
            {
                var cleaned = CleanElement(elem);
                if (cleaned is not null)
                    sb.Append(cleaned.ToString(SaveOptions.DisableFormatting));
            }
        }
        return sb.ToString();
    }

    private static double? TryGetRasterAspectRatio(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            // PNG: width at offset 16, height at offset 20
            if (bytes.Length >= 24
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                var w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                var h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                if (w > 0 && h > 0) return (double)w / h;
            }
            // JPEG: scan for SOF markers
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                var i = 2;
                while (i + 8 < bytes.Length)
                {
                    if (bytes[i] != 0xFF) break;
                    var marker = bytes[i + 1];
                    var segLen = (bytes[i + 2] << 8) | bytes[i + 3];
                    if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8)
                    {
                        var h = (bytes[i + 5] << 8) | bytes[i + 6];
                        var w = (bytes[i + 7] << 8) | bytes[i + 8];
                        if (w > 0 && h > 0) return (double)w / h;
                    }
                    i += 2 + segLen;
                }
            }
        }
        catch { }
        return null;
    }

    private static double? TryGetSvgAspectRatio(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null) return null;

            var viewBox = root.Attribute("viewBox")?.Value;
            if (viewBox is not null)
            {
                var parts = viewBox.Trim().Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4
                    && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vbW)
                    && double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vbH)
                    && vbW > 0 && vbH > 0)
                {
                    return vbW / vbH;
                }
            }

            var wAttr = root.Attribute("width")?.Value;
            var hAttr = root.Attribute("height")?.Value;
            if (wAttr is not null && hAttr is not null)
            {
                var wNum = Regex.Match(wAttr, @"^[\d.]+");
                var hNum = Regex.Match(hAttr, @"^[\d.]+");
                if (wNum.Success && hNum.Success
                    && double.TryParse(wNum.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var attrW)
                    && double.TryParse(hNum.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var attrH)
                    && attrW > 0 && attrH > 0)
                {
                    return attrW / attrH;
                }
            }
        }
        catch { }
        return null;
    }
}
