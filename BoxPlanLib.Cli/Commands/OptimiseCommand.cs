using BoxPlanLib.Cli.Serialization;
using BoxPlanLibApi = BoxPlanLib.BoxPlanLib;

namespace BoxPlanLib.Cli.Commands;

internal static class OptimiseCommand
{
    private const int DefaultPatience = 5;
    private const int DefaultBatchSize = 20;
    private const int DefaultMaxRounds = 50;
    private const double DefaultScrapValue = 0.5;
    private const double DefaultMinScrapSize = 50.0;

    public static int Run(BoxPlanLibApi lib, string[] args, string workingDirectory)
    {
        int patience = DefaultPatience;
        int batchSize = DefaultBatchSize;
        int maxRounds = DefaultMaxRounds;
        double scrapValue = DefaultScrapValue;
        double minScrapSize = DefaultMinScrapSize;

        // Strip optimise-specific flags before CliSettings.Resolve (which throws on unknown flags)
        var filteredArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (TryConsumeIntFlag(args, ref i, "--patience", arg, out var iv))
                patience = iv;
            else if (TryConsumeIntFlag(args, ref i, "--batch-size", arg, out iv))
                batchSize = iv;
            else if (TryConsumeIntFlag(args, ref i, "--max-rounds", arg, out iv))
                maxRounds = iv;
            else if (TryConsumeDoubleFlag(args, ref i, "--scrap-value", arg, out var dv))
                scrapValue = dv;
            else if (TryConsumeDoubleFlag(args, ref i, "--min-scrap-size", arg, out dv))
                minScrapSize = dv;
            else
                filteredArgs.Add(arg);
        }

        if (patience < 1) { Console.Error.WriteLine("Error: --patience must be >= 1"); return 2; }
        if (batchSize < 1) { Console.Error.WriteLine("Error: --batch-size must be >= 1"); return 2; }
        if (maxRounds < 0) { Console.Error.WriteLine("Error: --max-rounds must be >= 0 (0 = unlimited)"); return 2; }
        if (scrapValue < 0) { Console.Error.WriteLine("Error: --scrap-value must be >= 0"); return 2; }
        if (minScrapSize < 0) { Console.Error.WriteLine("Error: --min-scrap-size must be >= 0"); return 2; }

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
        settings.ScrapValue = scrapValue;
        settings.MinScrapSize = minScrapSize;

        Console.WriteLine($"Optimising layout for {shapes.Length} piece(s)");
        Console.WriteLine($"  patience={patience}  batch-size={batchSize}  max-rounds={(maxRounds == 0 ? "unlimited" : maxRounds)}  scrap-value={scrapValue:F2}  min-scrap-size={minScrapSize:F0}mm");
        Console.WriteLine($"  sheet={settings.SheetWidth}x{settings.SheetHeight}mm  margin={settings.Margin}mm  spacing={settings.Spacing}mm");
        Console.WriteLine();

        (int pages, double utility) baseline;
        try { baseline = lib.MeasureLayout(shapes, CloneWithSeed(settings, 0)); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to run baseline layout: {ex.Message}"); return 1; }

        var bestPages = baseline.pages;
        var bestUtility = baseline.utility;
        int bestSeed = 0;
        int patienceCounter = 0;

        Console.WriteLine($"Round   0: seed={0,-8}  pages={bestPages}  utility={bestUtility:F3}  (baseline)");

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
            (int pages, double utility) result;
            try { result = lib.MeasureLayout(shapes, CloneWithSeed(settings, seed)); }
            catch (Exception ex) { Console.Error.WriteLine($"Round {round}: error — {ex.Message}"); return 1; }

            bool improved = result.utility < bestUtility - 1e-6;

            string status;
            if (improved)
            {
                bestPages = result.pages;
                bestUtility = result.utility;
                bestSeed = seed;
                patienceCounter = 0;
                status = "*** IMPROVED ***";
            }
            else
            {
                patienceCounter++;
                status = $"(no improvement, patience {patienceCounter}/{patience})";
            }

            Console.WriteLine($"Round {round,3}: seed={seed,-8}  pages={result.pages}  utility={result.utility:F3}  best={bestPages}p/{bestUtility:F3}  {status}");
        }

        Console.WriteLine();
        Console.WriteLine($"Best result: {bestPages} page(s), utility score {bestUtility:F3}, seed={bestSeed}");
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
        ScrapValue = s.ScrapValue,
        MinScrapSize = s.MinScrapSize,
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

    private static bool TryConsumeDoubleFlag(string[] args, ref int i, string flagName, string current, out double value)
    {
        value = 0;
        if (current.StartsWith(flagName + "=", StringComparison.OrdinalIgnoreCase))
            return double.TryParse(current[(flagName.Length + 1)..], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        if (string.Equals(current, flagName, StringComparison.OrdinalIgnoreCase)
            && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value))
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
        Console.WriteLine("Scores layouts by utility = pages - scrap-value * total-scrap-fraction (lower is better).");
        Console.WriteLine();
        Console.WriteLine("Optimise-specific options:");
        Console.WriteLine("  --patience <n>       Consecutive rounds with no improvement before stopping (default: 5)");
        Console.WriteLine("  --batch-size <n>     Shuffled orderings per round (default: 20)");
        Console.WriteLine("  --max-rounds <n>     Hard cap on rounds; 0 = unlimited (default: 50)");
        Console.WriteLine("  --scrap-value <f>    Value of reusable offcut material relative to a pristine sheet");
        Console.WriteLine("                       per unit area. 0 = ignore scrap, 1 = scrap worth same as sheet.");
        Console.WriteLine("                       Scoring: utility = pages - scrap-value * total-scrap-fraction.");
        Console.WriteLine("                       (default: 0.5)");
        Console.WriteLine("  --min-scrap-size <f> Minimum dimension (mm) for a trailing offcut to be counted as");
        Console.WriteLine("                       reusable. Strips smaller than this in both X and Y are treated");
        Console.WriteLine("                       as waste and receive no scrap credit. (default: 50)");
        Console.WriteLine();
        Console.WriteLine("All standard layout options (--sheet-width, --spacing, etc.) are also accepted.");
    }
}
