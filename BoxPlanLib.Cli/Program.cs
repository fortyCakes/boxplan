using BoxPlanLib;

var lib = new BoxPlanLib.BoxPlanLib();
var settings = new BoxPlanSettings
{
    SheetWidth = 400,
    SheetHeight = 400,
    Margin = 5.0,
    Kerf = 0.1,
    MaterialThickness = 3.0,
    FingerJointSize = 5.0,
    Spacing = 1.0,
    Debug = true
};

if (args.Length == 0)
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
        var output = Path.Combine(outputDir, Path.ChangeExtension(Path.GetFileName(input), ".svg"));
        if (!ProcessPlan(lib, settings, input, output)) anyFailed = true;
    }
    return anyFailed ? 1 : 0;
}

var inputPath = args[0];
var outputPath = args.Length > 1
    ? args[1]
    : Path.Combine(ResolveDefaultOutputDirectory(), Path.ChangeExtension(Path.GetFileName(inputPath), ".svg"));

return ProcessPlan(lib, settings, inputPath, outputPath) ? 0 : 1;

static bool ProcessPlan(BoxPlanLib.BoxPlanLib lib, BoxPlanSettings settings, string inputPath, string outputPath)
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Input file not found: {inputPath}");
        return false;
    }

    var yaml = File.ReadAllText(inputPath);
    var parsed = lib.ParsePlan(yaml, settings);
    if (!parsed.Success || parsed.Value is null)
    {
        Console.Error.WriteLine($"Failed to parse {inputPath}:");
        foreach (var err in parsed.Errors) Console.Error.WriteLine($"  {err}");
        return false;
    }

    var pieces = lib.GetCuttableShapes(parsed.Value, settings);
    var svg = lib.GeneratePagedSVG(pieces, settings);

    var outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrWhiteSpace(outputDirectory))
    {
        Directory.CreateDirectory(outputDirectory);
    }

    File.WriteAllText(outputPath, svg);
    Console.WriteLine($"Wrote paged SVG for {pieces.Length} pieces to {Path.GetFullPath(outputPath)}");
    return true;
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
    // Prefer copied content next to the executable, then search parent directories
    // for either sample-plans or BoxPlanLib/sample-plans in source tree layouts.
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
