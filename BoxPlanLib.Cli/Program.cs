using BoxPlanLib;

var inputPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "sample-plans", "divider-test.yml");

var outputPath = args.Length > 1
    ? args[1]
    : Path.ChangeExtension(Path.GetFileName(inputPath), ".svg");

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    return 1;
}

var lib = new BoxPlanLib.BoxPlanLib();
var settings = new BoxPlanSettings
{
    SheetWidth = 300,
    SheetHeight = 300,
    Margin = 5.0,
    Kerf = 0.1,
    MaterialThickness = 3.0,
    FingerJointSize = 5.0,
    Spacing = 1.0,
    Debug = true
};

var yaml = File.ReadAllText(inputPath);
var parsed = lib.ParsePlan(yaml);
if (!parsed.Success || parsed.Value is null)
{
    Console.Error.WriteLine($"Failed to parse {inputPath}:");
    foreach (var err in parsed.Errors) Console.Error.WriteLine($"  {err}");
    return 1;
}

var pieces = lib.GetCuttableShapes(parsed.Value, settings);
var svg = lib.GeneratePagedSVG(pieces, settings);

File.WriteAllText(outputPath, svg);
Console.WriteLine($"Wrote paged SVG for {pieces.Length} pieces to {Path.GetFullPath(outputPath)}");
return 0;
