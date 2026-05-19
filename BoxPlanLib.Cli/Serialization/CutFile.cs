using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoxPlanLib.Cli.Serialization;

internal static class CutFile
{
    public const string Extension = ".cut.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(string path, BoxPlanCuttableShape[] shapes)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(shapes, Options));
    }

    public static BoxPlanCuttableShape[] Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BoxPlanCuttableShape[]>(json, Options)
            ?? throw new InvalidDataException($"Could not deserialize {path}");
    }
}
