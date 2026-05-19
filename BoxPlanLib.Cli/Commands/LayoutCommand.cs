using BoxPlanLib.Cli.Serialization;
using BoxPlanLibApi = BoxPlanLib.BoxPlanLib;

namespace BoxPlanLib.Cli.Commands;

internal static class LayoutCommand
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
            Console.Error.WriteLine("Usage: boxplan layout <input.cut.json> [output-dir] [options]");
            return 2;
        }

        var inputPath = Path.GetFullPath(resolved.Positional[0]);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input file not found: {inputPath}");
            return 1;
        }

        var planName = StripCutJsonExtension(inputPath);
        var outputDirectory = resolved.Positional.Count > 1
            ? Path.GetFullPath(resolved.Positional[1])
            : Path.Combine(
                Path.GetDirectoryName(inputPath) ?? workingDirectory,
                planName);

        BoxPlanCuttableShape[] shapes;
        try
        {
            shapes = CutFile.Load(inputPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load {Path.GetFileName(inputPath)}: {ex.Message}");
            return 1;
        }

        Directory.CreateDirectory(outputDirectory);

        IReadOnlyList<string> pageSvgs;
        try
        {
            pageSvgs = lib.GeneratePagedSVGPages(shapes, resolved.Settings, outputDirectory);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            Console.Error.WriteLine($"Failed to prepare engravings: {ex.Message}");
            return 1;
        }

        foreach (var existingSvg in Directory.GetFiles(outputDirectory, "*.svg", SearchOption.TopDirectoryOnly))
            File.Delete(existingSvg);

        for (var i = 0; i < pageSvgs.Count; i++)
        {
            var fileName = $"{planName}-page-{i + 1}.svg";
            File.WriteAllText(Path.Combine(outputDirectory, fileName), pageSvgs[i]);
        }

        Console.WriteLine($"Wrote {pageSvgs.Count} page SVG file(s) for {shapes.Length} pieces to {Path.GetFullPath(outputDirectory)}");
        return pageSvgs.Count > 0 ? 0 : 1;
    }

    private static string StripCutJsonExtension(string path)
    {
        var name = Path.GetFileName(path);
        // Strip .cut.json → base name
        if (name.EndsWith(CutFile.Extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^CutFile.Extension.Length];
        else
            name = Path.GetFileNameWithoutExtension(name);
        return name;
    }
}
