using BoxPlanLib;
using BoxPlanLib.Cli;
using BoxPlanLib.Model;

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
    return NormalizeRasterEngravingSources(pieces, inputPath);
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
