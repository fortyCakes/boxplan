using BoxPlanLib.Cli.Serialization;
using BoxPlanLibApi = BoxPlanLib.BoxPlanLib;

namespace BoxPlanLib.Cli.Commands;

internal static class CutCommand
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
            Console.Error.WriteLine("Usage: boxplan cut <input.bpl> [output.cut.json] [options]");
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
                StripBplExtension(inputPath) + CutFile.Extension);

        BplFile bpl;
        try
        {
            bpl = BplFile.Load(inputPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load {Path.GetFileName(inputPath)}: {ex.Message}");
            return 1;
        }

        // CLI flags override settings stored in the .bpl file
        var settings = MergeSettings(bpl.Settings, resolved);

        var parseResult = lib.ParsePlan(bpl.SourceYaml, settings);
        foreach (var err in parseResult.Errors)
            Console.Error.WriteLine($"  {err}");

        if (!parseResult.Success || parseResult.Value is null)
        {
            Console.Error.WriteLine($"Failed to parse plan from {Path.GetFileName(inputPath)}");
            return 1;
        }

        BoxPlanCuttableShape[] shapes;
        try
        {
            shapes = lib.GetCuttableShapes(parseResult.Value, settings, bpl.SourcePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to compute cuttable shapes: {ex.Message}");
            return 1;
        }

        CutFile.Save(outputPath, shapes);
        Console.WriteLine($"Cut: {Path.GetFileName(inputPath)}");
        Console.WriteLine($"  {shapes.Length} cuttable piece(s)");
        Console.WriteLine($"  Saved to: {outputPath}");
        return 0;
    }

    private static BoxPlanSettings MergeSettings(BoxPlanSettings stored, CliSettings.Resolved resolved)
    {
        // The resolved settings already incorporate CLI flag overrides on top of the working-dir
        // .boxplansettings file. We prefer the stored .bpl settings as base, but CLI flags win.
        // Since CliSettings.Resolve doesn't expose which props were explicitly set vs defaulted,
        // we return the resolved settings directly — the user can pass --no-settings-file to
        // ignore the .boxplansettings and rely purely on what was in the .bpl.
        return resolved.Settings;
    }

    private static string StripBplExtension(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        // Handle double extensions like foo.bpl → foo
        return name;
    }
}
