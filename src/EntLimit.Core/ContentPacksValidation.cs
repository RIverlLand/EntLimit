using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EntLimit.Core;

public static class ContentPackValidation
{
    public const int MaxPackBytes = 48 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24
    };
    public static ExerciseContentPack Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxPackBytes)
            throw new InvalidDataException("题库包须为非空 JSON，且不超过 48 KiB（UTF-8）。");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            RejectDuplicateKeys(document.RootElement);
            var pack = JsonSerializer.Deserialize<ExerciseContentPack>(json, JsonOptions)
                ?? throw new InvalidDataException("题库包为空。");
            ValidatePack(pack);
            return pack;
        }
        catch (JsonException) { throw new InvalidDataException("JSON 字段、类型或结构不符合题库包 v1 标准。"); }
    }
    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("JSON 不能包含重复字段。");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
    public static bool IsId(string? id) => id is not null && Regex.IsMatch(id, "^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant);
    private static bool Text(string? value, int max, bool required = true) => value is not null && value.Length <= max && (!required || !string.IsNullOrWhiteSpace(value));
    private static bool Strings(string[]? values, int maxCount, int maxLength, bool required = false) => values is not null
        && values.Length <= maxCount && (!required || values.Length > 0) && values.All(v => Text(v, maxLength));
    public static void ValidatePack(ExerciseContentPack pack)
    {
        if (pack.SchemaVersion != 1 || !IsId(pack.PackId) || pack.PackId == "builtin" || !Text(pack.Name, 120)
            || !Text(pack.Version, 40) || !Text(pack.Description, 2000, false) || !Text(pack.Author, 120, false)
            || pack.Topics is null || pack.Topics.Count > 20 || pack.Topics.Count == 0
            || pack.Exercises is null || pack.Exercises.Count == 0 || pack.Exercises.Count > 30)
            throw new InvalidDataException("题库包元数据无效：需要 v1、稳定 ID、名称、版本、主题和 1–30 道题。");
        ValidateTopics(pack.Topics);
        ValidateExercises(pack.Exercises, validateReviewLinks: false);
        if (pack.Exercises.Any(e => !pack.Topics.Any(t => t.Id == e.TopicId)))
            throw new InvalidDataException("每道题的 topicId 必须在包的 topics 中声明。");
    }
    public static void ValidateTopics(IEnumerable<TopicDefinition> topics)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topic in topics)
            if (topic is null || !IsId(topic.Id) || topic.Id == "all" || !ids.Add(topic.Id)
                || !Text(topic.Name, 100) || !Text(topic.Description, 2000, false))
                throw new InvalidDataException("主题 ID、名称或说明无效；主题 ID 不能重复或使用 all。");
    }
    public static void ValidateExercises(IReadOnlyCollection<Exercise> exercises, bool validateReviewLinks = true)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in exercises)
        {
            if (e is null || !IsId(e.Id) || !ids.Add(e.Id) || !IsId(e.TopicId)
                || !Text(e.Title, 200) || !Text(e.Scenario, 14000) || !Text(e.Takeaway, 3000)
                || e.EstimatedMinutes is < 1 or > 60 || e.Mode is not ("standard" or "light")
                || !Text(e.SourceLabel, 500) || e.SourceType is not ("original" or "reference")
                || !Text(e.SourceUrl, 2048, false) || !IsId(e.LanguageId)
                || e.Parts is null || e.Parts.Count > 20 || e.ReviewOfId is not null && !IsId(e.ReviewOfId))
                throw new InvalidDataException("题目元数据、ID、文本长度或类型无效。");
            if (!(e.SourceType == "original" && e.SourceUrl.Length == 0)
                && (!Uri.TryCreate(e.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)))
                throw new InvalidDataException("参考来源须为 HTTPS；原创题可将 sourceType 设为 original 并留空 sourceUrl。");
            if (e.Kind == "scenario")
            {
                if (e.Parts.Count == 0 || e.Coding is not null) throw new InvalidDataException("场景题需要问题，且不能含 coding。");
                var parts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.Parts)
                    if (p is null || !IsId(p.Id) || !parts.Add(p.Id) || !Text(p.Prompt, 3000) || !Text(p.Explanation, 5000)
                        || !Strings(p.Hints, 10, 2000, true) || !Strings(p.Options, 12, 2000)
                        || !Strings(p.AcceptedAnswers, 30, 500) || p.Kind is not ("choice" or "fill")
                        || p.Kind == "choice" && (p.Options.Length < 2 || p.CorrectIndex < 0 || p.CorrectIndex >= p.Options.Length)
                        || p.Kind == "fill" && p.AcceptedAnswers.Length == 0)
                        throw new InvalidDataException("填空/选择题字段、选项、正确答案或提示无效。");
            }
            else if (e.Kind == "code" && e.Coding is { } c)
            {
                if (!Regex.IsMatch(c.FunctionName ?? "", "^[A-Za-z_][A-Za-z0-9_]{0,79}$")
                    || !Text(c.StarterCode, 20000) || !Text(c.ReferenceCode, 20000)
                    || !Text(c.SolutionExplanation, 12000, false) || !Text(c.Complexity, 8000, false)
                    || !Text(c.AlternativeCode, 20000, false) || !Text(c.AlternativeExplanation, 12000, false)
                    || !Strings(c.Hints, 10, 2000, true) || !Strings(c.ParameterTypes, 10, 50)
                    || !Text(c.ReturnType, 50, false) || c.Cases is null || c.Cases.Count is < 2 or > 40
                    || !c.Cases.Any(t => t is not null && t.IsExample) || !c.Cases.Any(t => t is not null && !t.IsExample))
                    throw new InvalidDataException("编程题函数、代码、测试或说明无效。");
                foreach (var test in c.Cases)
                {
                    if (test is null || !Text(test.Name, 120) || !Text(test.ArgumentsJson, 8000) || !Text(test.ExpectedJson, 8000))
                        throw new InvalidDataException("测试用例无效。");
                    try
                    {
                        using var arguments = JsonDocument.Parse(test.ArgumentsJson, new JsonDocumentOptions { MaxDepth = 12 });
                        using var expected = JsonDocument.Parse(test.ExpectedJson, new JsonDocumentOptions { MaxDepth = 12 });
                        if (arguments.RootElement.ValueKind != JsonValueKind.Array || arguments.RootElement.GetArrayLength() > 10)
                            throw new InvalidDataException("测试 argumentsJson 必须是最多 10 个位置参数的 JSON 数组。");
                    }
                    catch (JsonException) { throw new InvalidDataException("测试用例需要有效 JSON。"); }
                }
            }
            else throw new InvalidDataException("题型仅支持 scenario 或 code。");
        }
        if (!validateReviewLinks) return;
        foreach (var review in exercises.Where(e => e.ReviewOfId is not null))
        {
            var source = exercises.FirstOrDefault(e => e.Id == review.ReviewOfId);
            if (source is null || source.ReviewOfId is not null || source.TopicId != review.TopicId
                || review.Mode != "light" || review.Kind != "scenario" || review.EstimatedMinutes > 2)
                throw new InvalidDataException("复习题须指向同主题原题，并为 1–2 分钟的 light 场景题。");
        }
    }
}
