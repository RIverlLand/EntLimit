namespace EntLimit.Core;

public sealed class TopicDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class ExerciseContentPack
{
    public int SchemaVersion { get; set; } = 1;
    public string PackId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public List<TopicDefinition> Topics { get; set; } = [];
    public List<Exercise> Exercises { get; set; } = [];
}

public sealed class ContentPackInfo
{
    public string PackId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public int ExerciseCount { get; set; }
}

public sealed class ContentPackPreview
{
    public string PackId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CatalogRevision { get; set; } = "";
    public List<ContentPackRow> Rows { get; set; } = [];
}

public sealed class ContentPackRow
{
    public string ExerciseId { get; set; } = "";
    public string Title { get; set; } = "";
    public string TopicId { get; set; } = "";
    public string Status { get; set; } = "new";
    public string? SimilarToId { get; set; }
    public string SuggestedAction { get; set; } = "add";
}
