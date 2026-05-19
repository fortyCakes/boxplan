using BoxPlanLib.Cli.Serialization;
using BoxPlanLibApi = BoxPlanLib.BoxPlanLib;

namespace BoxPlanLib.Cli.Commands;

internal static class OptimiseCommand
{
    private const int DefaultPatience = 5;
    private const int DefaultBatchSize = 20;
    private const int DefaultMaxRounds = 50;

    public static int Run(BoxPlanLibApi lib, string[] args, string workingDirectory)
    {
        int patience = DefaultPatience;
        int batchSize = DefaultBatchSize;
        int maxRounds = DefaultMaxRounds;

        // Strip optimise-specific flags before CliSettings.Resolve (which throws on unknown flags)
        var filteredArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (TryConsumeIntFlag(args, ref i, "--patience", arg, out var v))
                patience = v;
            else if (TryConsumeIntFlag(args, ref i, "--batch-size", arg, out v))
                batchSize = v;
            else if (TryConsumeIntFlag(args, ref i, "--max-rounds", arg, out v))
                maxRounds = v;
            else
                filteredArgs.Add(arg);
        }

        if (patience < 1) { Console.Error.WriteLine("Error: --patience must be >= 1"); return 2; }
        if (batchSize < 1) { Console.Error.WriteLine("Error: --batch-size must be >= 1"); return 2; }
        if (maxRounds < 0) { Console.Error.WriteLine("Error: --max-rounds must be >= 0 (0 = unlimited)"); return 2; }

        CliSettings.Resolved resolved;
        try
        {
            resolved = CliSettings.Resolve(filteredArgs.ToArray(), workingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException or FileNotFoundException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }

        if (resolved.HelpRequested) { PrintHelp(); return 0; }

        if (resolved.Positional.Count == 0)
        {
            Console.Error.WriteLine("Usage: boxplan optimise <input.cut.json> [output-dir] [options]");
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
            : Path.Combine(Path.GetDirectoryName(inputPath) ?? workingDirectory, planName);

        BoxPlanCuttableShape[] shapes;
        try { shapes = CutFile.Load(inputPath); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load {Path.GetFileName(inputPath)}: {ex.Message}");
            return 1;
        }

        if (shapes.Length == 0)
        {
            Console.Error.WriteLine("No shapes found in input file.");
            return 1;
        }

        var settings = resolved.Settings;
        settings.UseAdvancedLayoutOptimizer = true;
        settings.LayoutSearchIterations = batchSize;

        Console.WriteLine($"Optimising layout for {shapes.Length} piece(s)");
        Console.WriteLine($"  patience={patience}  batch-size={batchSize}  max-rounds={(maxRounds == 0 ? "unlimited" : maxRounds)}");
        Console.WriteLine($"  sheet={settings.SheetWidth}x{settings.SheetHeight}mm  margin={settings.Margin}mm  spacing={settings.Spacing}mm");
        Console.WriteLine();

        (int pages, double density) baseline;
        try { baseline = lib.MeasureLayout(shapes, CloneWithSeed(settings, 0)); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to run baseline layout: {ex.Message}"); return 1; }

        var bestPages = baseline.pages;
        var bestDensity = baseline.density;
        int bestSeed = 0;
        int patienceCounter = 0;

        Console.WriteLine($"Round   0: seed={0,-8}  pages={bestPages}  density={bestDensity:F3}  (baseline)");

        for (int round = 1; ; round++)
        {
            if (maxRounds > 0 && round > maxRounds)
            {
                Console.WriteLine($"\nReached max-rounds limit ({maxRounds}).");
                break;
            }
            if (patienceCounter >= patience)
            {
                Console.WriteLine($"\nPatience exhausted ({patience} consecutive rounds without improvement).");
                break;
            }

            // seed = round * batchSize: each round gets its own window of batchSize shuffled
            // orderings in the RNG sequence — no two rounds generate the same permutations.
            int seed = round * batchSize;
            (int pages, double density) result;
            try { result = lib.MeasureLayout(shapes, CloneWithSeed(settings, seed)); }
            catch (Exception ex) { Console.Error.WriteLine($"Round {round}: error — {ex.Message}"); return 1; }

            bool improved = result.pages < bestPages
                || (result.pages == bestPages && result.density < bestDensity - 1e-6);

            string status;
            if (improved)
            {
                bestPages = result.pages;
                bestDensity = result.density;
                bestSeed = seed;
                patienceCounter = 0;
                status = "*** IMPROVED ***";
            }
            else
            {
                patienceCounter++;
                status = $"(no improvement, patience {patienceCounter}/{patience})";
            }

            Console.WriteLine($"Round {round,3}: seed={seed,-8}  pages={result.pages}  density={result.density:F3}  best={bestPages}p/{bestDensity:F3}  {status}");
        }

        Console.WriteLine();
        Console.WriteLine($"Best result: {bestPages} page(s), density score {bestDensity:F3}, seed={bestSeed}");
        Console.WriteLine($"Generating SVG output to: {Path.GetFullPath(outputDirectory)}");

        Directory.CreateDirectory(outputDirectory);
        IReadOnlyList<string> pageSvgs;
        try { pageSvgs = lib.GeneratePagedSVGPages(shapes, CloneWithSeed(settings, bestSeed), outputDirectory); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            Console.Error.WriteLine($"Failed to prepare engravings: {ex.Message}");
            return 1;
        }

        foreach (var svg in Directory.GetFiles(outputDirectory, "*.svg", SearchOption.TopDirectoryOnly))
            File.Delete(svg);
        for (var i = 0; i < pageSvgs.Count; i++)
            File.WriteAllText(Path.Combine(outputDirectory, $"{planName}-page-{i + 1}.svg"), pageSvgs[i]);

        Console.WriteLine($"Wrote {pageSvgs.Count} page SVG file(s) for {shapes.Length} pieces to {Path.GetFullPath(outputDirectory)}");
        return pageSvgs.Count > 0 ? 0 : 1;
    }

    private static BoxPlanSettings CloneWithSeed(BoxPlanSettings s, int seed) => new()
    {
        SheetWidth = s.SheetWidth,
        SheetHeight = s.SheetHeight,
        Margin = s.Margin,
        Kerf = s.Kerf,
        MaterialThickness = s.MaterialThickness,
        FingerJointSize = s.FingerJointSize,
        Spacing = s.Spacing,
        Debug = s.Debug,
        Labels = s.Labels,
        UseAdvancedLayoutOptimizer = true,
        UseOrToolsSequenceOptimization = s.UseOrToolsSequenceOptimization,
        LayoutSearchIterations = s.LayoutSearchIterations,
        LayoutRandomSeed = seed,
        EmbedRasterEngravings = s.EmbedRasterEngravings,
        VectorizeRasterEngravings = s.VectorizeRasterEngravings,
        RasterEngravingAssetFolder = s.RasterEngravingAssetFolder,
        FlexLineSpacing = s.FlexLineSpacing,
        FlexLineLengthFraction = s.FlexLineLengthFraction,
        FlexLengthCompensationFactor = s.FlexLengthCompensationFactor,
    };

    private static bool TryConsumeIntFlag(string[] args, ref int i, string flagName, string current, out int value)
    {
        value = 0;
        if (current.StartsWith(flagName + "=", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(current[(flagName.Length + 1)..], out value);
        if (string.Equals(current, flagName, StringComparison.OrdinalIgnoreCase)
            && i + 1 < args.Length && int.TryParse(args[i + 1], out value))
        {
            i++;
            return true;
        }
        return false;
    }

    private static string StripCutJsonExtension(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(CutFile.Extension, StringComparison.OrdinalIgnoreCase)
            ? name[..^CutFile.Extension.Length]
            : Path.GetFileNameWithoutExtension(name);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: boxplan optimise <input.cut.json> [output-dir] [options]");
        Console.WriteLine();
        Console.WriteLine("Iteratively runs the advanced layout optimizer with different random seeds,");
        Console.WriteLine("stopping when packing quality is unlikely to improve further.");
        Console.WriteLine("Tracks page count (primary) and density score (secondary: lower = tighter packing).");
        Console.WriteLine();
        Console.WriteLine("Optimise-specific options:");
        Console.WriteLine("  --patience <n>     Consecutive rounds with no improvement before stopping (default: 5)");
        Console.WriteLine("  --batch-size <n>   Shuffled orderings per round (default: 20)");
        Console.WriteLine("  --max-rounds <n>   Hard cap on rounds; 0 = unlimited (default: 50)");
        Console.WriteLine();
        Console.WriteLine("All standard layout options (--sheet-width, --spacing, etc.) are also accepted.");
    }
}
