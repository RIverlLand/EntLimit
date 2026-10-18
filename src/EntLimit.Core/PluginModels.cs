namespace EntLimit.Core;

public sealed class RunnerPluginManifest
{
    public int ProtocolVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string LanguageId { get; set; } = "";
    public string Executable { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = [];
}
public sealed class InstalledRunnerPlugin
{
    public string Id { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
}
public sealed class RunnerPluginInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string LanguageId { get; set; } = "";
    public bool Trusted { get; set; }
    public string Message { get; set; } = "";
}
public sealed class RunnerPluginRequest
{
    public int ProtocolVersion { get; set; } = 1;
    public string RequestId { get; set; } = "";
    public Exercise Exercise { get; set; } = new();
    public string Code { get; set; } = "";
    public string? RuntimePath { get; set; }
    public bool ExamplesOnly { get; set; }
}
public sealed class RunnerPluginReply
{
    public int ProtocolVersion { get; set; } = 1;
    public string RequestId { get; set; } = "";
    public CodeRunResult Result { get; set; } = new();
}
