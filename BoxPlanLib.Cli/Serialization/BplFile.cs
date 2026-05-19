using System.Text.Json;
using System.Text.Json.Serialization;
using BoxPlanLib;

namespace BoxPlanLib.Cli.Serialization;

internal sealed class BplFile
{
    public const string Extension = ".bpl";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public int Version { get; init; } = 1;
    public required string SourceYaml { get; init; }
    public required string SourcePath { get; init; }
    public required BoxPlanSettings Settings { get; init; }

    public static void Save(string path, string sourceYaml, string sourcePath, BoxPlanSettings settings)
    {
        var file = new BplFile
        {
            SourceYaml = sourceYaml,
            SourcePath = sourcePath,
            Settings = settings,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file, Options));
    }

    public static BplFile Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BplFile>(json, Options)
            ?? throw new InvalidDataException($"Could not deserialize {path}");
    }
}
