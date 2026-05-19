using BoxPlanLib;
using BoxPlanLib.Cli;
using BoxPlanLib.Model;
using System.Text;
using System.Xml.Linq;

var lib = new BoxPlanLib.BoxPlanLib();

CliSettings.Resolved resolved;
try
{
    resolved = CliSettings.Resolve(args, Directory.GetCurrentDirectory());
}
catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException or FileNotFoundException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine("Use --help for usage.");
    return 2;
}

if (resolved.HelpRequested)
{
    Console.WriteLine(CliSettings.BuildHelpText());
    return 0;
}

if (resolved.LoadedSettingsPath is not null)
{
    Console.WriteLine($"Loaded settings from {resolved.LoadedSettingsPath}");
}

if (resolved.SaveSettingsRequested)
{
    CliSettings.WriteSettingsFile(resolved.Settings, resolved.SaveSettingsPath);
    Console.WriteLine($"Saved settings to {resolved.SaveSettingsPath}");
}

var settings = resolved.Settings;
var positional = resolved.Positional;

if (resolved.InputDirectory is not null && positional.Count > 1)
{
    Console.Error.WriteLine("When --input-dir is used, provide at most one positional argument for output directory.");
    return 2;
}

if (resolved.InputDirectory is not null)
{
    var targetOutputDirectory = positional.Count == 1
        ? positional[0]
        : Path.Combine(ResolveDefaultOutputDirectory(), BuildOutputFolderName(resolved.InputDirectory));

    return ProcessPlanDirectory(lib, settings, resolved.InputDirectory, targetOutputDirectory) ? 0 : 1;
}

if (positional.Count == 0)
{
    var sampleDir = ResolveSamplePlansDirectory();
    var outputDir = ResolveDefaultOutputDirectory();
    if (!Directory.Exists(sampleDir))
    {
        Console.Error.WriteLine($"Sample plans directory not found: {sampleDir}");
        return 1;
    }

    Directory.CreateDirectory(outputDir);

    var inputs = Directory.GetFiles(sampleDir, "*.yml").OrderBy(p => p).ToArray();
    if (inputs.Length == 0)
    {
        Console.Error.WriteLine($"No .yml plans found in {sampleDir}");
        return 1;
    }

    var anyFailed = false;
    foreach (var input in inputs)
    {
        var perPlanOutputDirectory = Path.Combine(outputDir, BuildOutputFolderName(input));
        if (!ProcessPlan(lib, settings, input, perPlanOutputDirectory)) anyFailed = true;
    }
    return anyFailed ? 1 : 0;
}

var inputPath = positional[0];

if (Directory.Exists(inputPath))
{
    var targetOutputDirectory = positional.Count > 1
        ? positional[1]
        : Path.Combine(ResolveDefaultOutputDirectory(), BuildOutputFolderName(inputPath));
    return ProcessPlanDirectory(lib, settings, inputPath, targetOutputDirectory) ? 0 : 1;
}

var outputDirectory = positional.Count > 1
    ? positional[1]
    : Path.Combine(ResolveDefaultOutputDirectory(), BuildOutputFolderName(inputPath));

return ProcessPlan(lib, settings, inputPath, outputDirectory) ? 0 : 1;

static bool ProcessPlan(BoxPlanLib.BoxPlanLib lib, BoxPlanSettings settings, string inputPath, string outputDirectory)
{
    var pieces = ReadPlanPieces(lib, settings, inputPath);
    if (pieces is null)
    {
        return false;
    }

    var planName = Path.GetFileNameWithoutExtension(inputPath);
    var pageCount = WritePagedSvgFiles(lib, settings, pieces, outputDirectory, planName);
    Console.WriteLine($"Wrote {pageCount} page SVG file(s) for {pieces.Length} pieces to {Path.GetFullPath(outputDirectory)}");
    return pageCount > 0;
}

static bool ProcessPlanDirectory(BoxPlanLib.BoxPlanLib lib, BoxPlanSettings settings, string inputDirectory, string outputDirectory)
{
    if (!Directory.Exists(inputDirectory))
    {
        Console.Error.WriteLine($"Input directory not found: {inputDirectory}");
        return false;
    }

    var inputs = Directory
        .GetFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
        .Where(path => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path)
        .ToArray();

    if (inputs.Length == 0)
    {
        Console.Error.WriteLine($"No .yml/.yaml plans found in {inputDirectory}");
        return false;
    }

    var combinedPieces = new List<BoxPlanCuttableShape>();
    var successfulPlans = 0;
    var failedPlans = 0;

    foreach (var input in inputs)
    {
        var pieces = ReadPlanPieces(lib, settings, input);
        if (pieces is null)
        {
            failedPlans++;
            continue;
        }

        successfulPlans++;
        combinedPieces.AddRange(pieces);
        Console.WriteLine($"Loaded {pieces.Length} pieces from {Path.GetFileName(input)}");
    }

    if (combinedPieces.Count == 0)
    {
        Console.Error.WriteLine($"No cuttable pieces were produced from plans in {inputDirectory}");
        return false;
    }

    var planName = BuildOutputFolderName(inputDirectory);
    var pageCount = WritePagedSvgFiles(lib, settings, combinedPieces.ToArray(), outputDirectory, planName);
    Console.WriteLine(
        $"Wrote {pageCount} page SVG file(s) for {combinedPieces.Count} combined pieces from {successfulPlans} plan(s) to {Path.GetFullPath(outputDirectory)}");

    if (failedPlans > 0)
    {
        Console.Error.WriteLine($"Skipped {failedPlans} plan(s) due to parse/build errors.");
    }

    return failedPlans == 0;
}

static int WritePagedSvgFiles(
    BoxPlanLib.BoxPlanLib lib,
    BoxPlanSettings settings,
    BoxPlanCuttableShape[] pieces,
    string outputDirectory,
    string planName)
{
    Directory.CreateDirectory(outputDirectory);

    BoxPlanCuttableShape[] preparedPieces;
    try
    {
        preparedPieces = PrepareRasterEngravingAssets(pieces, settings, outputDirectory);
        preparedPieces = PrepareSvgEngravingAssets(preparedPieces, settings, outputDirectory);
    }
    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
    {
        Console.Error.WriteLine($"Failed to prepare raster engravings: {ex.Message}");
        return 0;
    }

    var pageSvgs = lib.GeneratePagedSVGPages(preparedPieces, settings);

    foreach (var existingSvg in Directory.GetFiles(outputDirectory, "*.svg", SearchOption.TopDirectoryOnly))
    {
        File.Delete(existingSvg);
    }

    var safePlanName = string.IsNullOrWhiteSpace(planName)
        ? "plan"
        : planName;

    for (var i = 0; i < pageSvgs.Count; i++)
    {
        var fileName = $"{safePlanName}-page-{i + 1}.svg";
        var filePath = Path.Combine(outputDirectory, fileName);
        File.WriteAllText(filePath, pageSvgs[i]);
    }

    return pageSvgs.Count;
}

static BoxPlanCuttableShape[]? ReadPlanPieces(BoxPlanLib.BoxPlanLib lib, BoxPlanSettings settings, string inputPath)
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Input file not found: {inputPath}");
        return null;
    }

    var yaml = File.ReadAllText(inputPath);
    var parsed = lib.ParsePlan(yaml, settings);
    if (!parsed.Success || parsed.Value is null)
    {
        Console.Error.WriteLine($"Failed to parse {inputPath}:");
        foreach (var err in parsed.Errors) Console.Error.WriteLine($"  {err}");
        return null;
    }

    var pieces = lib.GetCuttableShapes(parsed.Value, settings);
    pieces = NormalizeRasterEngravingSources(pieces, inputPath);
    pieces = NormalizeSvgEngravingSources(pieces, inputPath);
    pieces = ResolveRasterEngravingDimensions(pieces);
    pieces = ResolveSvgEngravingDimensions(pieces);
    return InlineSvgEngravingContent(pieces);
}

static BoxPlanCuttableShape[] NormalizeRasterEngravingSources(BoxPlanCuttableShape[] pieces, string inputPath)
{
    var inputDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? Directory.GetCurrentDirectory();
    var remap = new Dictionary<string, string>(StringComparer.Ordinal);

    foreach (var source in pieces
        .SelectMany(piece => piece.RasterEngravings)
        .Select(engraving => engraving.Href)
        .Where(href => !string.IsNullOrWhiteSpace(href))
        .Distinct(StringComparer.Ordinal))
    {
        if (LooksLikeDataUri(source))
        {
            remap[source] = source;
            continue;
        }

        var absolutePath = Path.IsPathRooted(source)
            ? Path.GetFullPath(source)
            : Path.GetFullPath(Path.Combine(inputDirectory, source));

        remap[source] = absolutePath;
    }

    return remap.Count == 0 ? pieces : CloneWithRasterHrefMap(pieces, remap);
}

static BoxPlanCuttableShape[] PrepareRasterEngravingAssets(
    BoxPlanCuttableShape[] pieces,
    BoxPlanSettings settings,
    string outputDirectory)
{
    var rasterSources = pieces
        .SelectMany(piece => piece.RasterEngravings)
        .Select(engraving => engraving.Href)
        .Where(href => !string.IsNullOrWhiteSpace(href))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    if (rasterSources.Length == 0)
    {
        return pieces;
    }

    var remap = new Dictionary<string, string>(StringComparer.Ordinal);

    if (settings.EmbedRasterEngravings)
    {
        foreach (var source in rasterSources)
        {
            if (LooksLikeDataUri(source))
            {
                remap[source] = source;
                continue;
            }

            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"Raster engraving source not found: {source}");
            }

            var mimeType = RasterMimeTypeFromPath(source);
            var bytes = File.ReadAllBytes(source);
            remap[source] = $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
        }

        return CloneWithRasterHrefMap(pieces, remap);
    }

    var folderName = settings.RasterEngravingAssetFolder?.Trim() ?? string.Empty;
    folderName = folderName.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (string.IsNullOrWhiteSpace(folderName))
    {
        folderName = "assets";
    }

    var assetDirectory = Path.Combine(outputDirectory, folderName);
    Directory.CreateDirectory(assetDirectory);

    var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var source in rasterSources)
    {
        if (LooksLikeDataUri(source))
        {
            remap[source] = source;
            continue;
        }

        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Raster engraving source not found: {source}");
        }

        var fileName = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "raster-engraving.bin";
        }

        var uniqueFileName = AllocateUniqueFileName(fileName, usedFileNames);
        var destinationPath = Path.Combine(assetDirectory, uniqueFileName);
        File.Copy(source, destinationPath, overwrite: true);

        remap[source] = $"{folderName.Replace('\\', '/')}/{uniqueFileName}";
    }

    return CloneWithRasterHrefMap(pieces, remap);
}

static BoxPlanCuttableShape[] CloneWithRasterHrefMap(
    BoxPlanCuttableShape[] pieces,
    IReadOnlyDictionary<string, string> hrefMap)
{
    return pieces.Select(piece => new BoxPlanCuttableShape
    {
        Id = piece.Id,
        BoundingBoxMin = piece.BoundingBoxMin,
        BoundingBoxMax = piece.BoundingBoxMax,
        Outline = piece.Outline,
        InteriorCuts = piece.InteriorCuts,
        Engravings = piece.Engravings,
        TextEngravings = piece.TextEngravings,
        RasterEngravings = piece.RasterEngravings
            .Select(engraving => new RasterEngraving
            {
                Href = hrefMap.TryGetValue(engraving.Href, out var mappedHref) ? mappedHref : engraving.Href,
                X = engraving.X,
                Y = engraving.Y,
                Anchor = engraving.Anchor,
                Width = engraving.Width,
                Height = engraving.Height,
            })
            .ToArray(),
    }).ToArray();
}

static BoxPlanCuttableShape[] NormalizeSvgEngravingSources(BoxPlanCuttableShape[] pieces, string inputPath)
{
    var inputDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? Directory.GetCurrentDirectory();
    var remap = new Dictionary<string, string>(StringComparer.Ordinal);

    foreach (var source in pieces
        .SelectMany(piece => piece.SvgEngravings)
        .Select(engraving => engraving.Href)
        .Where(href => !string.IsNullOrWhiteSpace(href))
        .Distinct(StringComparer.Ordinal))
    {
        if (LooksLikeDataUri(source))
        {
            remap[source] = source;
            continue;
        }

        var absolutePath = Path.IsPathRooted(source)
            ? Path.GetFullPath(source)
            : Path.GetFullPath(Path.Combine(inputDirectory, source));

        remap[source] = absolutePath;
    }

    return remap.Count == 0 ? pieces : CloneWithSvgHrefMap(pieces, remap);
}

static BoxPlanCuttableShape[] PrepareSvgEngravingAssets(
    BoxPlanCuttableShape[] pieces,
    BoxPlanSettings settings,
    string outputDirectory)
{
    var svgSources = pieces
        .SelectMany(piece => piece.SvgEngravings)
        .Where(engraving => engraving.InlinedContent is null)
        .Select(engraving => engraving.Href)
        .Where(href => !string.IsNullOrWhiteSpace(href))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    if (svgSources.Length == 0)
        return pieces;

    var remap = new Dictionary<string, string>(StringComparer.Ordinal);

    if (settings.EmbedRasterEngravings)
    {
        foreach (var source in svgSources)
        {
            if (LooksLikeDataUri(source))
            {
                remap[source] = source;
                continue;
            }

            if (!File.Exists(source))
                throw new FileNotFoundException($"SVG engraving source not found: {source}");

            var bytes = File.ReadAllBytes(source);
            remap[source] = $"data:image/svg+xml;base64,{Convert.ToBase64String(bytes)}";
        }

        return CloneWithSvgHrefMap(pieces, remap);
    }

    var folderName = settings.RasterEngravingAssetFolder?.Trim() ?? string.Empty;
    folderName = folderName.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (string.IsNullOrWhiteSpace(folderName))
        folderName = "assets";

    var assetDirectory = Path.Combine(outputDirectory, folderName);
    Directory.CreateDirectory(assetDirectory);

    var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var source in svgSources)
    {
        if (LooksLikeDataUri(source))
        {
            remap[source] = source;
            continue;
        }

        if (!File.Exists(source))
            throw new FileNotFoundException($"SVG engraving source not found: {source}");

        var fileName = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "svg-engraving.svg";

        var uniqueFileName = AllocateUniqueFileName(fileName, usedFileNames);
        var destinationPath = Path.Combine(assetDirectory, uniqueFileName);
        File.Copy(source, destinationPath, overwrite: true);

        remap[source] = $"{folderName.Replace('\\', '/')}/{uniqueFileName}";
    }

    return CloneWithSvgHrefMap(pieces, remap);
}

static string ExtractSvgInnerContent(XElement root, string engravingColor)
{
    var svgNs = XNamespace.Get("http://www.w3.org/2000/svg");

    static XAttribute RecolorAttr(XAttribute a, string color) =>
        new XAttribute(a.Name.LocalName,
            a.Value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? "none" : color);

    static string RewriteStyleColors(string style, string color)
    {
        style = System.Text.RegularExpressions.Regex.Replace(
            style, @"(?<=\bfill\s*:\s*)(?!none\b)[^;]+", color);
        style = System.Text.RegularExpressions.Regex.Replace(
            style, @"(?<=\bstroke\s*:\s*)(?!none\b)[^;]+", color);
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

static BoxPlanCuttableShape[] InlineSvgEngravingContent(BoxPlanCuttableShape[] pieces)
{
    var sources = pieces
        .SelectMany(p => p.SvgEngravings)
        .Select(e => e.Href)
        .Where(h => !string.IsNullOrWhiteSpace(h) && !LooksLikeDataUri(h))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    if (sources.Length == 0)
        return pieces;

    var inlineMap = new Dictionary<string, (string Content, double VbW, double VbH)>(StringComparer.Ordinal);

    foreach (var source in sources)
    {
        if (!File.Exists(source))
            continue;

        try
        {
            var doc = XDocument.Load(source);
            var root = doc.Root;
            if (root is null)
                continue;

            double vbW = 0, vbH = 0;
            var viewBox = root.Attribute("viewBox")?.Value;
            if (viewBox is not null)
            {
                var parts = viewBox.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
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
                    var wm = System.Text.RegularExpressions.Regex.Match(wAttr, @"^[\d.]+");
                    var hm = System.Text.RegularExpressions.Regex.Match(hAttr, @"^[\d.]+");
                    if (wm.Success && hm.Success
                        && double.TryParse(wm.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w2)
                        && double.TryParse(hm.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h2)
                        && w2 > 0 && h2 > 0)
                    {
                        vbW = w2; vbH = h2;
                    }
                }
            }

            if (vbW == 0 || vbH == 0)
                continue;

            var innerXml = ExtractSvgInnerContent(root, "purple");
            if (string.IsNullOrEmpty(innerXml))
                continue;

            inlineMap[source] = (innerXml, vbW, vbH);
        }
        catch
        {
            // SVG could not be parsed — will fall back to <image>
        }
    }

    if (inlineMap.Count == 0)
        return pieces;

    return pieces.Select(piece =>
    {
        if (!piece.SvgEngravings.Any(e => inlineMap.ContainsKey(e.Href)))
            return piece;

        return new BoxPlanCuttableShape
        {
            Id = piece.Id,
            BoundingBoxMin = piece.BoundingBoxMin,
            BoundingBoxMax = piece.BoundingBoxMax,
            Outline = piece.Outline,
            InteriorCuts = piece.InteriorCuts,
            Engravings = piece.Engravings,
            TextEngravings = piece.TextEngravings,
            RasterEngravings = piece.RasterEngravings,
            SvgEngravings = piece.SvgEngravings
                .Select(e =>
                {
                    if (!inlineMap.TryGetValue(e.Href, out var inline))
                        return e;
                    return new SvgEngraving
                    {
                        Href = e.Href,
                        X = e.X,
                        Y = e.Y,
                        Anchor = e.Anchor,
                        Width = e.Width,
                        Height = e.Height,
                        InlinedContent = inline.Content,
                        InlinedViewBoxWidth = inline.VbW,
                        InlinedViewBoxHeight = inline.VbH,
                    };
                })
                .ToArray(),
        };
    }).ToArray();
}

static BoxPlanCuttableShape[] CloneWithSvgHrefMap(
    BoxPlanCuttableShape[] pieces,
    IReadOnlyDictionary<string, string> hrefMap)
{
    return pieces.Select(piece => new BoxPlanCuttableShape
    {
        Id = piece.Id,
        BoundingBoxMin = piece.BoundingBoxMin,
        BoundingBoxMax = piece.BoundingBoxMax,
        Outline = piece.Outline,
        InteriorCuts = piece.InteriorCuts,
        Engravings = piece.Engravings,
        TextEngravings = piece.TextEngravings,
        RasterEngravings = piece.RasterEngravings,
        SvgEngravings = piece.SvgEngravings
            .Select(engraving => new SvgEngraving
            {
                Href = hrefMap.TryGetValue(engraving.Href, out var mappedHref) ? mappedHref : engraving.Href,
                X = engraving.X,
                Y = engraving.Y,
                Anchor = engraving.Anchor,
                Width = engraving.Width,
                Height = engraving.Height,
                InlinedContent = engraving.InlinedContent,
                InlinedViewBoxWidth = engraving.InlinedViewBoxWidth,
                InlinedViewBoxHeight = engraving.InlinedViewBoxHeight,
            })
            .ToArray(),
    }).ToArray();
}

static BoxPlanCuttableShape[] ResolveRasterEngravingDimensions(BoxPlanCuttableShape[] pieces)
{
    var aspectByPath = new Dictionary<string, double?>(StringComparer.Ordinal);

    bool NeedsResolution(RasterEngraving e) => e.Width is null || e.Height is null;

    if (!pieces.Any(p => p.RasterEngravings.Any(NeedsResolution)))
        return pieces;

    return pieces.Select(piece =>
    {
        if (!piece.RasterEngravings.Any(NeedsResolution))
            return piece;

        var resolved = piece.RasterEngravings.Select(e =>
        {
            if (!NeedsResolution(e))
                return e;

            if (!aspectByPath.TryGetValue(e.Href, out var aspect))
            {
                aspect = TryGetRasterAspectRatio(e.Href);
                aspectByPath[e.Href] = aspect;
            }

            var width = e.Width;
            var height = e.Height;

            if (aspect is { } ar && ar > 0)
            {
                if (width is null && height is not null)
                    width = height.Value * ar;
                else if (height is null && width is not null)
                    height = width.Value / ar;
            }

            if (width == e.Width && height == e.Height)
                return e;

            return new RasterEngraving
            {
                Href = e.Href,
                X = e.X,
                Y = e.Y,
                Anchor = e.Anchor,
                Width = width,
                Height = height,
            };
        }).ToArray();

        return new BoxPlanCuttableShape
        {
            Id = piece.Id,
            BoundingBoxMin = piece.BoundingBoxMin,
            BoundingBoxMax = piece.BoundingBoxMax,
            Outline = piece.Outline,
            InteriorCuts = piece.InteriorCuts,
            Engravings = piece.Engravings,
            TextEngravings = piece.TextEngravings,
            RasterEngravings = resolved,
            SvgEngravings = piece.SvgEngravings,
        };
    }).ToArray();
}

static double? TryGetRasterAspectRatio(string path)
{
    if (!File.Exists(path))
        return null;

    try
    {
        var bytes = File.ReadAllBytes(path);
        // PNG: signature 8 bytes + IHDR header 8 bytes = width at offset 16, height at offset 20
        if (bytes.Length >= 24
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            var w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            var h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            if (w > 0 && h > 0)
                return (double)w / h;
        }
        // JPEG: scan for SOF markers (FFC0–FFCF, excluding FFC4/FFC8)
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var i = 2;
            while (i + 8 < bytes.Length)
            {
                if (bytes[i] != 0xFF)
                    break;
                var marker = bytes[i + 1];
                var segLen = (bytes[i + 2] << 8) | bytes[i + 3];
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8)
                {
                    var h = (bytes[i + 5] << 8) | bytes[i + 6];
                    var w = (bytes[i + 7] << 8) | bytes[i + 8];
                    if (w > 0 && h > 0)
                        return (double)w / h;
                }
                i += 2 + segLen;
            }
        }
    }
    catch
    {
        // Unreadable image — can't infer aspect ratio
    }

    return null;
}

static BoxPlanCuttableShape[] ResolveSvgEngravingDimensions(BoxPlanCuttableShape[] pieces)
{
    var aspectByPath = new Dictionary<string, double?>(StringComparer.Ordinal);

    bool NeedsResolution(SvgEngraving e) => e.Width is null || e.Height is null;

    if (!pieces.Any(p => p.SvgEngravings.Any(NeedsResolution)))
        return pieces;

    return pieces.Select(piece =>
    {
        if (!piece.SvgEngravings.Any(NeedsResolution))
            return piece;

        var resolved = piece.SvgEngravings.Select(e =>
        {
            if (!NeedsResolution(e))
                return e;

            if (!aspectByPath.TryGetValue(e.Href, out var aspect))
            {
                aspect = TryGetSvgAspectRatio(e.Href);
                aspectByPath[e.Href] = aspect;
            }

            var width = e.Width;
            var height = e.Height;

            if (aspect is { } ar && ar > 0)
            {
                if (width is null && height is not null)
                    width = height.Value * ar;
                else if (height is null && width is not null)
                    height = width.Value / ar;
            }

            if (width == e.Width && height == e.Height)
                return e;

            return new SvgEngraving
            {
                Href = e.Href,
                X = e.X,
                Y = e.Y,
                Anchor = e.Anchor,
                Width = width,
                Height = height,
                InlinedContent = e.InlinedContent,
                InlinedViewBoxWidth = e.InlinedViewBoxWidth,
                InlinedViewBoxHeight = e.InlinedViewBoxHeight,
            };
        }).ToArray();

        return new BoxPlanCuttableShape
        {
            Id = piece.Id,
            BoundingBoxMin = piece.BoundingBoxMin,
            BoundingBoxMax = piece.BoundingBoxMax,
            Outline = piece.Outline,
            InteriorCuts = piece.InteriorCuts,
            Engravings = piece.Engravings,
            TextEngravings = piece.TextEngravings,
            RasterEngravings = piece.RasterEngravings,
            SvgEngravings = resolved,
        };
    }).ToArray();
}

static double? TryGetSvgAspectRatio(string path)
{
    if (!File.Exists(path))
        return null;

    try
    {
        var doc = XDocument.Load(path);
        var root = doc.Root;
        if (root is null)
            return null;

        var viewBox = root.Attribute("viewBox")?.Value;
        if (viewBox is not null)
        {
            var parts = viewBox.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vbW)
                && double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vbH)
                && vbW > 0 && vbH > 0)
            {
                return vbW / vbH;
            }
        }

        // Fallback: numeric width/height attributes (strip trailing units like px, mm, pt, %)
        var wAttr = root.Attribute("width")?.Value;
        var hAttr = root.Attribute("height")?.Value;
        if (wAttr is not null && hAttr is not null)
        {
            var wNum = System.Text.RegularExpressions.Regex.Match(wAttr, @"^[\d.]+");
            var hNum = System.Text.RegularExpressions.Regex.Match(hAttr, @"^[\d.]+");
            if (wNum.Success && hNum.Success
                && double.TryParse(wNum.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var attrW)
                && double.TryParse(hNum.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var attrH)
                && attrW > 0 && attrH > 0)
            {
                return attrW / attrH;
            }
        }
    }
    catch
    {
        // Malformed SVG — can't infer aspect ratio
    }

    return null;
}

static string AllocateUniqueFileName(string fileName, HashSet<string> usedFileNames)
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

static bool LooksLikeDataUri(string value) =>
    value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

static string RasterMimeTypeFromPath(string path)
{
    var extension = Path.GetExtension(path).ToLowerInvariant();
    return extension switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".tif" => "image/tiff",
        ".tiff" => "image/tiff",
        _ => "application/octet-stream",
    };
}

static string BuildOutputFolderName(string inputPath)
{
    var fullPath = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var name = Directory.Exists(inputPath)
        ? Path.GetFileName(fullPath)
        : Path.GetFileNameWithoutExtension(fullPath);

    if (string.IsNullOrWhiteSpace(name))
    {
        name = "plan";
    }

    return name;
}

static string ResolveDefaultOutputDirectory()
{
    foreach (var root in EnumerateSearchRoots())
    {
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
        {
            var repoStyle = Path.Combine(dir.FullName, "sample-output");
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                Directory.Exists(Path.Combine(dir.FullName, "BoxPlanLib")))
            {
                return repoStyle;
            }
        }
    }

    return Path.Combine(Directory.GetCurrentDirectory(), "sample-output");
}

static string ResolveSamplePlansDirectory()
{
    var roots = EnumerateSearchRoots();

    foreach (var root in roots)
    {
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "sample-plans");
            if (Directory.Exists(direct)) return direct;

            var repoStyle = Path.Combine(dir.FullName, "BoxPlanLib", "sample-plans");
            if (Directory.Exists(repoStyle)) return repoStyle;
        }
    }

    return Path.Combine(AppContext.BaseDirectory, "sample-plans");
}

static IEnumerable<string> EnumerateSearchRoots()
{
    return new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
        .Where(d => !string.IsNullOrWhiteSpace(d))
        .Distinct(StringComparer.OrdinalIgnoreCase);
}
