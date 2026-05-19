using System.Globalization;
using System.Reflection;
using BoxPlanLib;
using YamlDotNet.Serialization;

namespace BoxPlanLib.Cli;

internal static class CliSettings
{
    public const string FileName = ".boxplansettings";

    public sealed class Resolved
    {
        public required BoxPlanSettings Settings { get; init; }
        public required List<string> Positional { get; init; }
        public string? InputDirectory { get; init; }
        public required bool HelpRequested { get; init; }
        public required bool SaveSettingsRequested { get; init; }
        public string? LoadedSettingsPath { get; init; }
        public required string SaveSettingsPath { get; init; }
    }

    public static Resolved Resolve(string[] args, string workingDirectory)
    {
        var positional = new List<string>();
        var flagOverrides = new Dictionary<PropertyInfo, object>();
        string? settingsOverridePath = null;
        string? inputDirectory = null;
        var skipSettingsFile = false;
        var helpRequested = false;
        var saveSettingsRequested = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            var (name, inlineValue) = SplitFlag(arg);

            if (name.Equals("help", StringComparison.OrdinalIgnoreCase) || name == "h")
            {
                helpRequested = true;
                continue;
            }

            if (name.Equals("settings", StringComparison.OrdinalIgnoreCase))
            {
                settingsOverridePath = inlineValue ?? ConsumeNextValue(args, ref i, name);
                continue;
            }

            if (name.Equals("input-dir", StringComparison.OrdinalIgnoreCase))
            {
                inputDirectory = inlineValue ?? ConsumeNextValue(args, ref i, name);
                continue;
            }

            if (name.Equals("no-settings-file", StringComparison.OrdinalIgnoreCase))
            {
                skipSettingsFile = true;
                continue;
            }

            if (name.Equals("save-settings", StringComparison.OrdinalIgnoreCase))
            {
                saveSettingsRequested = true;
                continue;
            }

            if (name.StartsWith("no-", StringComparison.Ordinal))
            {
                var negatedProp = FindProperty(name[3..]);
                if (negatedProp is not null && negatedProp.PropertyType == typeof(bool))
                {
                    flagOverrides[negatedProp] = false;
                    continue;
                }
            }

            var prop = FindProperty(name)
                ?? throw new ArgumentException($"Unknown flag: --{name}");

            object value;
            if (prop.PropertyType == typeof(bool))
            {
                if (inlineValue is not null)
                {
                    value = ParseBool(inlineValue, name);
                }
                else if (i + 1 < args.Length
                         && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                         && TryParseBool(args[i + 1], out var peeked))
                {
                    value = peeked;
                    i++;
                }
                else
                {
                    value = true;
                }
            }
            else
            {
                var raw = inlineValue ?? ConsumeNextValue(args, ref i, name);
                value = ConvertValue(raw, prop.PropertyType, $"--{name}");
            }

            flagOverrides[prop] = value;
        }

        var settings = BuildDefaults();
        string? loadedPath = null;

        if (!skipSettingsFile)
        {
            if (settingsOverridePath is not null)
            {
                var fullPath = Path.GetFullPath(settingsOverridePath);
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException($"Settings file not found: {fullPath}");
                }
                ApplyYamlFile(settings, fullPath);
                loadedPath = fullPath;
            }
            else
            {
                var defaultPath = Path.Combine(workingDirectory, FileName);
                if (File.Exists(defaultPath))
                {
                    ApplyYamlFile(settings, defaultPath);
                    loadedPath = defaultPath;
                }
            }
        }

        foreach (var (prop, value) in flagOverrides)
        {
            prop.SetValue(settings, value);
        }

        var savePath = settingsOverridePath is not null
            ? Path.GetFullPath(settingsOverridePath)
            : Path.Combine(workingDirectory, FileName);

        return new Resolved
        {
            Settings = settings,
            Positional = positional,
            InputDirectory = inputDirectory,
            HelpRequested = helpRequested,
            SaveSettingsRequested = saveSettingsRequested,
            LoadedSettingsPath = loadedPath,
            SaveSettingsPath = savePath,
        };
    }

    public static BoxPlanSettings BuildDefaults() => new()
    {
        SheetWidth = 400,
        SheetHeight = 400,
        Margin = 5.0,
        Kerf = 0.1,
        MaterialThickness = 3.0,
        FingerJointSize = 5.0,
        Spacing = 1.0,
        Debug = true,
        Labels = false,
        UseAdvancedLayoutOptimizer = true,
        UseOrToolsSequenceOptimization = false,
        LayoutSearchIterations = 5,
        LayoutRandomSeed = Random.Shared.Next(),
        EmbedRasterEngravings = true,
        VectorizeRasterEngravings = true,
        RasterEngravingAssetFolder = "assets",
        FlexLineSpacing = 0.8,
        FlexLineLengthFraction = 0.3,
        FlexLengthCompensationFactor = 1.0,
    };

    public static string BuildHelpText()
    {
        var lines = new List<string>
        {
            "Usage: boxplan <command> [options] ...",
            "",
            "Commands:",
            "  parse  <input.yml> [output.bpl]        Parse plan YAML and save as .bpl",
            "  cut    <input.bpl> [output.cut.json]   Compute cuttable shapes and save",
            "  layout <input.cut.json> [output-dir]   Lay out shapes and output SVG",
            "  plan   <input.yml|dir> [output-dir]    Full pipeline in one pass (default)",
            "  help                                   Show this help",
            "",
            "Omitting a command defaults to 'plan' — existing usage unchanged.",
            "The 'plan' command also accepts --input-dir <path> and processes a directory.",
            "If no input is given, every .yml in the sample-plans directory is processed.",
            "",
            "Settings precedence (highest wins):",
            "  1. CLI flags",
            $"  2. {FileName} (YAML) in the working directory, or --settings <path>",
            "  3. Built-in defaults",
            "",
            "Options (apply to all commands):",
            "  --settings <path>          Load settings from the given YAML file",
            "  --input-dir <path>         (plan) Process all files in a directory into combined output",
            "  --no-settings-file         Ignore any .boxplansettings file",
            $"  --save-settings            Write resolved settings to {FileName} (or --settings path)",
            "  --help, -h                 Show this help text",
            "",
            "Setting flags:",
        };

        foreach (var prop in SettingProperties())
        {
            var flag = ToKebab(prop.Name);
            var typeHint = TypeHint(prop.PropertyType);
            lines.Add($"  --{flag} <{typeHint}>".PadRight(40) + $"{prop.Name} ({prop.PropertyType.Name})");
            if (prop.PropertyType == typeof(bool))
            {
                lines.Add($"  --no-{flag}".PadRight(40) + $"Set {prop.Name} to false");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static void WriteSettingsFile(BoxPlanSettings settings, string path)
    {
        var lines = new List<string>();
        foreach (var prop in SettingProperties())
        {
            var key = ToKebab(prop.Name);
            var value = prop.GetValue(settings);
            lines.Add($"{key}: {FormatValue(value)}");
        }
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static string FormatValue(object? value) => value switch
    {
        bool b => b ? "true" : "false",
        double d => d.ToString("G", CultureInfo.InvariantCulture),
        int n => n.ToString(CultureInfo.InvariantCulture),
        null => "",
        _ => value.ToString() ?? "",
    };

    private static void ApplyYamlFile(BoxPlanSettings settings, string path)
    {
        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder().Build();
        Dictionary<object, object?>? raw;
        try
        {
            raw = deserializer.Deserialize<Dictionary<object, object?>>(yaml);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to parse {path}: {ex.Message}", ex);
        }
        if (raw is null) return;

        foreach (var (keyObj, valueObj) in raw)
        {
            var key = keyObj?.ToString();
            if (string.IsNullOrWhiteSpace(key)) continue;
            var prop = FindProperty(key);
            if (prop is null)
            {
                throw new InvalidDataException($"Unknown setting '{key}' in {path}");
            }

            var rawText = valueObj?.ToString() ?? string.Empty;
            try
            {
                prop.SetValue(settings, ConvertValue(rawText, prop.PropertyType, key));
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                throw new InvalidDataException($"Invalid value for '{key}' in {path}: {ex.Message}", ex);
            }
        }
    }

    private static PropertyInfo? FindProperty(string flagName)
    {
        var pascal = ToPascal(flagName);
        return typeof(BoxPlanSettings).GetProperty(
            pascal,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
    }

    private static IEnumerable<PropertyInfo> SettingProperties() =>
        typeof(BoxPlanSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

    private static (string Name, string? InlineValue) SplitFlag(string token)
    {
        var body = token[2..];
        var eq = body.IndexOf('=');
        return eq < 0 ? (body, null) : (body[..eq], body[(eq + 1)..]);
    }

    private static string ConsumeNextValue(string[] args, ref int i, string flagName)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"--{flagName} requires a value");
        }
        i++;
        return args[i];
    }

    private static object ConvertValue(string raw, Type targetType, string label)
    {
        if (targetType == typeof(bool)) return ParseBool(raw, label);
        if (targetType == typeof(int))
        {
            return int.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
        if (targetType == typeof(double))
        {
            return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        if (targetType == typeof(string)) return raw;
        throw new NotSupportedException($"Unsupported setting type {targetType.Name} for '{label}'");
    }

    private static bool ParseBool(string raw, string label)
    {
        if (TryParseBool(raw, out var value)) return value;
        throw new FormatException($"Cannot parse '{raw}' as boolean for '{label}'");
    }

    private static bool TryParseBool(string raw, out bool value)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "true": case "yes": case "on": case "1":
                value = true; return true;
            case "false": case "no": case "off": case "0":
                value = false; return true;
            default:
                value = false; return false;
        }
    }

    private static string ToPascal(string kebab)
    {
        var parts = kebab.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    private static string ToKebab(string pascal)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (i > 0 && char.IsUpper(c)) sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string TypeHint(Type t)
    {
        if (t == typeof(bool)) return "bool";
        if (t == typeof(int)) return "int";
        if (t == typeof(double)) return "number";
        return t.Name.ToLowerInvariant();
    }
}
