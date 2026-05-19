using BoxPlanLib.Cli.Serialization;
using BoxPlanLibApi = BoxPlanLib.BoxPlanLib;

namespace BoxPlanLib.Cli.Commands;

internal static class ParseCommand
{
    public static int Run(BoxPlanLibApi lib, string[] args, string workingDirectory)
    {
        CliSettings.Resolved resolved;
        try
        {
            resolved = CliSettings.Resolve(args, workingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException or FileNotFoundException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }

        if (resolved.HelpRequested)
        {
            Console.WriteLine(CliSettings.BuildHelpText());
            return 0;
        }

        if (resolved.Positional.Count == 0)
        {
            Console.Error.WriteLine("Usage: boxplan parse <input.yml> [output.bpl] [options]");
            return 2;
        }

        var inputPath = Path.GetFullPath(resolved.Positional[0]);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input file not found: {inputPath}");
            return 1;
        }

        var outputPath = resolved.Positional.Count > 1
            ? Path.GetFullPath(resolved.Positional[1])
            : Path.Combine(
                Path.GetDirectoryName(inputPath) ?? workingDirectory,
                Path.GetFileNameWithoutExtension(inputPath) + BplFile.Extension);

        var yaml = File.ReadAllText(inputPath);
        var result = lib.ParsePlan(yaml, resolved.Settings);

        foreach (var err in result.Errors)
            Console.Error.WriteLine($"  {err}");

        if (!result.Success || result.Value is null)
        {
            Console.Error.WriteLine($"Failed to parse: {Path.GetFileName(inputPath)}");
            return 1;
        }

        var plan = result.Value;
        Console.WriteLine($"Parsed: {Path.GetFileName(inputPath)}");
        Console.WriteLine($"  {plan.Shapes.Count} shape(s): {string.Join(", ", plan.Shapes.Select(s => s.Id))}");

        BplFile.Save(outputPath, yaml, inputPath, resolved.Settings);
        Console.WriteLine($"  Saved to: {outputPath}");
        return 0;
    }
}
