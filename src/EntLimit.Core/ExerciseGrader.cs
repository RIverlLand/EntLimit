using System.Globalization;
using System.Text;

namespace EntLimit.Core;

public static class ExerciseGrader
{
    public static ExerciseGrade Grade(Exercise exercise, Dictionary<string, string> answers)
    {
        ArgumentNullException.ThrowIfNull(exercise);
        ArgumentNullException.ThrowIfNull(answers);
        var result = new ExerciseGrade();
        if (exercise.Kind != "scenario" || exercise.Parts.Count == 0) return result;
        foreach (var part in exercise.Parts)
        {
            answers.TryGetValue(part.Id, out var raw);
            var answer = Normalize(raw);
            var correct = part.Kind switch
            {
                "choice" => int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && index >= 0 && index < part.Options.Length && index == part.CorrectIndex,
                "fill" => answer.Length > 0 && part.AcceptedAnswers.Any(x => Normalize(x) == answer),
                _ => false
            };
            result.Parts.Add(new PartGrade
            {
                PartId = part.Id, Correct = correct,
                Feedback = answer.Length == 0 ? "这小题还没有作答，可以先看看提示。"
                    : correct ? "答对了。" + part.Explanation
                    : "再试一次。" + part.Hints.FirstOrDefault("留意场景中的条件，再检查一下你的答案。")
            });
        }
        result.Passed = result.Parts.All(x => x.Correct);
        return result;
    }

    private static string Normalize(string? value) => (value ?? "").Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
}
