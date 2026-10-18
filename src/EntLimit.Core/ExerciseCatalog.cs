using System.Text.Json;

namespace EntLimit.Core;

public static class ExerciseCatalog
{
    private static readonly Lazy<IReadOnlyList<Exercise>> Loaded = new(Load);
    public static IReadOnlyList<Exercise> All => Loaded.Value;
    public static IReadOnlyList<TopicDefinition> BuiltInTopics =>
    [
        new() { Id = "network", Name = "计算机网络基础", Description = "网络场景与基础概念" },
        new() { Id = "python", Name = "编程 · Python", Description = "Python 编程练习" }
    ];
    private static IReadOnlyList<Exercise> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "content", "sample-exercises.json");
        using var stream = File.Exists(path) ? File.OpenRead(path)
            : typeof(ExerciseCatalog).Assembly.GetManifestResourceStream("EntLimit.Core.sample-exercises.json")
                ?? throw new InvalidDataException("Bundled exercise content is missing.");
        var pack = JsonSerializer.Deserialize<BuiltInPack>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty exercise content.");
        if (pack.SchemaVersion != 1 || pack.Exercises is null || pack.Exercises.Count == 0)
            throw new InvalidDataException("Unsupported exercise content version.");
        ContentPackValidation.ValidateExercises(pack.Exercises);
        return pack.Exercises.AsReadOnly();
    }
    private sealed class BuiltInPack
    {
        public int SchemaVersion { get; set; }
        public List<Exercise> Exercises { get; set; } = [];
    }
}
