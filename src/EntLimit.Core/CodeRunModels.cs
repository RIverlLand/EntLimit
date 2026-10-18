namespace EntLimit.Core;

public sealed class CodeRunResult
{
    public bool Passed { get; set; }
    public string Status { get; set; } = "error";
    public string Message { get; set; } = "";
    public string StandardOutput { get; set; } = "";
    public List<CodeCaseResult> Cases { get; set; } = [];
    public CodeAnalysis? Analysis { get; set; }
}

public sealed class CodeCaseResult
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Expected { get; set; } = "";
    public string Actual { get; set; } = "";
    public string Error { get; set; } = "";
}
