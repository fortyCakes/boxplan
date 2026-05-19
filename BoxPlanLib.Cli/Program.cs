using BoxPlanLib;
using BoxPlanLib.Cli;
using BoxPlanLib.Cli.Commands;
using BoxPlanLib.Model;

var lib = new BoxPlanLib.BoxPlanLib();
var workingDirectory = Directory.GetCurrentDirectory();

// Detect subcommand as the first argument when it's a known keyword (not a flag or file path).
var knownCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "help", "parse", "cut", "layout", "plan", "optimise" };

string? subcommand = null;
if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) && knownCommands.Contains(args[0]))
{
    subcommand = args[0].ToLowerInvariant();
    args = args[1..];
}

if (subcommand == "parse")
    return ParseCommand.Run(lib, args, workingDirectory);

if (subcommand == "cut")
    return CutCommand.Run(lib, args, workingDirectory);

if (subcommand == "layout")
    return LayoutCommand.Run(lib, args, workingDirectory);

if (subcommand == "optimise")
    return OptimiseCommand.Run(lib, args, workingDirectory);

// "plan" or no subcommand — existing behaviour.
CliSettings.Resolved resolved;
try
{
    resolved = CliSettings.Resolve(args, workingDirectory);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException or FileNotFoundException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine("Use --help for usage.");
    return 2;
}

if (resolved.HelpRequested || subcommand == "help")
{
    Console.WriteLine(CliSettings.BuildHelpText());
    return 0;
}

if (resolved.LoadedSettingsPath is not null)
    Console.WriteLine($"Loaded settings from {resolved.LoadedSettingsPath}");

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

// ── plan helpers ──────────────────────────────────────────────────────────────

static bool ProcessPlan(BoxPlanLib.BoxPlanLib lib, BoxPlanSettings settings, string inputPath, string outputDirectory)
{
    var shapes = ReadPlanPieces(lib, settings, inputPath);
    var fileName = Path.GetFileName(inputPath);

    if (shapes is null)
    {
        Console.Error.WriteLine($"Failed — errors in: {fileName}");
        return false;
    }

    var planName = Path.GetFileNameWithoutExtension(inputPath);
    var pageCount = WritePagedSvgFiles(lib, settings, shapes, outputDirectory, planName);
    Console.WriteLine($"Wrote {pageCount} page SVG file(s) for {shapes.Length} pieces to {Path.GetFullPath(outputDirectory)}");
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

    var loadedPlans = new List<(string Path, BoxPlanCuttableShape[] Pieces)>();
    var errorFiles = new List<string>();

    foreach (var input in inputs)
    {
        var shapes = ReadPlanPieces(lib, settings, input);
        if (shapes is null)
        {
            errorFiles.Add(Path.GetFileName(input));
            continue;
        }
        loadedPlans.Add((input, shapes));
        Console.WriteLine($"Loaded {shapes.Length} pieces from {Path.GetFileName(input)}");
    }

    if (errorFiles.Count > 0)
    {
        Console.Error.WriteLine($"Failed — errors in:");
        foreach (var f in errorFiles)
            Console.Error.WriteLine($"  {f}");
        return false;
    }

    if (loadedPlans.Count == 0)
    {
        Console.Error.WriteLine($"No cuttable pieces were produced from plans in {inputDirectory}");
        return false;
    }

    var combinedPieces = loadedPlans.SelectMany(x => x.Pieces).ToArray();
    var planName = BuildOutputFolderName(inputDirectory);
    var pageCount = WritePagedSvgFiles(lib, settings, combinedPieces, outputDirectory, planName);
    Console.WriteLine(
        $"Wrote {pageCount} page SVG file(s) for {combinedPieces.Length} combined pieces from {loadedPlans.Count} plan(s) to {Path.GetFullPath(outputDirectory)}");

    return true;
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

    try
    {
        return lib.GetCuttableShapes(parsed.Value, settings, inputPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to compute cuttable shapes for {Path.GetFileName(inputPath)}: {ex.Message}");
        return null;
    }
}

static int WritePagedSvgFiles(
    BoxPlanLib.BoxPlanLib lib,
    BoxPlanSettings settings,
    BoxPlanCuttableShape[] pieces,
    string outputDirectory,
    string planName)
{
    Directory.CreateDirectory(outputDirectory);

    IReadOnlyList<string> pageSvgs;
    try
    {
        pageSvgs = lib.GeneratePagedSVGPages(pieces, settings, outputDirectory);
    }
    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
    {
        Console.Error.WriteLine($"Failed to prepare engravings for '{planName}': {ex.Message}");
        return 0;
    }

    foreach (var existingSvg in Directory.GetFiles(outputDirectory, "*.svg", SearchOption.TopDirectoryOnly))
        File.Delete(existingSvg);

    var safePlanName = string.IsNullOrWhiteSpace(planName) ? "plan" : planName;

    for (var i = 0; i < pageSvgs.Count; i++)
    {
        var fileName = $"{safePlanName}-page-{i + 1}.svg";
        File.WriteAllText(Path.Combine(outputDirectory, fileName), pageSvgs[i]);
    }

    return pageSvgs.Count;
}

static string BuildOutputFolderName(string inputPath)
{
    var fullPath = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var name = Directory.Exists(inputPath)
        ? Path.GetFileName(fullPath)
        : Path.GetFileNameWithoutExtension(fullPath);

    return string.IsNullOrWhiteSpace(name) ? "plan" : name;
}

static string ResolveDefaultOutputDirectory()
{
    foreach (var root in EnumerateSearchRoots())
    {
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                Directory.Exists(Path.Combine(dir.FullName, "BoxPlanLib")))
            {
                return Path.Combine(dir.FullName, "sample-output");
            }
        }
    }
    return Path.Combine(Directory.GetCurrentDirectory(), "sample-output");
}

static string ResolveSamplePlansDirectory()
{
    foreach (var root in EnumerateSearchRoots())
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

static IEnumerable<string> EnumerateSearchRoots() =>
    new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
        .Where(d => !string.IsNullOrWhiteSpace(d))
        .Distinct(StringComparer.OrdinalIgnoreCase);
