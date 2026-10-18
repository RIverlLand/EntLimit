namespace EntLimit.Core;

public sealed class CodeAnalysis
{
    public string CodeHash { get; set; } = "";
    public bool AstAvailable { get; set; }
    public string Summary { get; set; } = "";
    public string Limitations { get; set; } = "";
    public List<CodeAnalysisFinding> Findings { get; set; } = [];
    public List<CodeTutorial> Tutorials { get; set; } = [];
}

public sealed class CodeAnalysisFinding
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public int Line { get; set; }
    public string Evidence { get; set; } = "";
}

public sealed class CodeTutorial
{
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Code { get; set; } = "";
    public List<string> Steps { get; set; } = [];
    public string Practice { get; set; } = "";
    public string SourceUrl { get; set; } = "";
}
