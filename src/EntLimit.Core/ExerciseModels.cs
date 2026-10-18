namespace EntLimit.Core;

public sealed class Exercise
{
    public string Id { get; set; } = "";
    public string? ReviewOfId { get; set; }
    public string TopicId { get; set; } = "";
    public string Mode { get; set; } = "standard";
    public string Kind { get; set; } = "scenario";
    public string Title { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string Takeaway { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string SourceType { get; set; } = "reference";
    public string LanguageId { get; set; } = "python";
    public string SourceLabel { get; set; } = "";
    public int EstimatedMinutes { get; set; }
    public List<ExercisePart> Parts { get; set; } = [];
    public CodingExercise? Coding { get; set; }
}

public sealed class ExercisePart
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "choice";
    public string Prompt { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string[] Options { get; set; } = [];
    public string[] AcceptedAnswers { get; set; } = [];
    public string[] Hints { get; set; } = [];
    public int CorrectIndex { get; set; }
}

public sealed class CodingExercise
{
    public string FunctionName { get; set; } = "";
    public string[] ParameterTypes { get; set; } = [];
    public string ReturnType { get; set; } = "";
    public string StarterCode { get; set; } = "";
    public string ReferenceCode { get; set; } = "";
    // Optional learning notes; omitted fields remain compatible with existing packs.
    public string SolutionExplanation { get; set; } = "";
    public string Complexity { get; set; } = "";
    public string AlternativeCode { get; set; } = "";
    public string AlternativeExplanation { get; set; } = "";
    public List<CodeCase> Cases { get; set; } = [];
    public string[] Hints { get; set; } = [];
}

public sealed class CodeCase
{
    public string Name { get; set; } = "";
    public string ArgumentsJson { get; set; } = "[]";
    public string ExpectedJson { get; set; } = "null";
    public bool IsExample { get; set; }
}

public sealed class ExerciseGrade
{
    public bool Passed { get; set; }
    public List<PartGrade> Parts { get; set; } = [];
}

public sealed class PartGrade
{
    public string PartId { get; set; } = "";
    public bool Correct { get; set; }
    public string Feedback { get; set; } = "";
}
