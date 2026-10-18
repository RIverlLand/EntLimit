using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;

namespace EntLimit.Core;

/// <summary>Monitor-owned content writer. All candidate changes validate and persist before the live snapshot changes.</summary>
public sealed class ContentPackStore
{
    private readonly string directory;
    private List<ExerciseContentPack> installed = [];
    private bool recovered;
    public IReadOnlyList<Exercise> Exercises { get; private set; } = ExerciseCatalog.All;
    public IReadOnlyList<TopicDefinition> Topics { get; private set; } = ExerciseCatalog.BuiltInTopics;
    public IReadOnlyList<ContentPackInfo> Packs { get; private set; } = [];
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    public string Revision => Hash(JsonSerializer.Serialize(new { Exercises, Topics }, ContentPackValidation.JsonOptions));
    public ContentPackStore(string dataRoot)
    {
        directory = Path.Combine(Path.GetFullPath(dataRoot), "packs");
        Packs = MakeInfos(installed);
        var path = Path.Combine(directory, "installed.json");
        if (!File.Exists(path)) return;
        try { SetLoaded(Read(path)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            try
            {
                SetLoaded(Read(path + ".bak")); recovered = true;
                Warnings = ["题库主文件无效，已读取有效备份；原文件保留，未改变学习记录或许可。"];
            }
            catch (Exception backupError) when (backupError is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
            { Warnings = ["已安装题库无法读取，保留原文件并使用内置题库；修复或移开损坏文件前不能导入覆盖。"] ; }
        }
    }
    private void SetLoaded(List<ExerciseContentPack> packs)
    {
        var merged = Merge(packs); installed = packs;
        Exercises = merged.Exercises; Topics = merged.Topics; Packs = MakeInfos(packs);
    }
    private static List<ContentPackInfo> MakeInfos(List<ExerciseContentPack> packs) =>
        new[] { new ContentPackInfo { PackId = "builtin", Name = "内置原创练习", Version = "1", ExerciseCount = ExerciseCatalog.All.Count } }
        .Concat(packs.Select(p => new ContentPackInfo { PackId = p.PackId, Name = p.Name, Version = p.Version, ExerciseCount = p.Exercises.Count })).ToList();
    private static List<ExerciseContentPack> Read(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("题库存储文件无效。");
        InstalledContent document;
        try { document = JsonSerializer.Deserialize<InstalledContent>(File.ReadAllText(path), ContentPackValidation.JsonOptions)
            ?? throw new InvalidDataException("题库存储文件为空。"); }
        catch (JsonException) { throw new InvalidDataException("题库存储 JSON 无效，原文件保留。"); }
        if (document.SchemaVersion != 1 || document.Packs is null) throw new InvalidDataException("题库存储版本无效。");
        Merge(document.Packs);
        return document.Packs;
    }
    private static (List<Exercise> Exercises, List<TopicDefinition> Topics) Merge(List<ExerciseContentPack> packs)
    {
        if (packs.Count > 30 || packs.Any(p => p is null) || packs.Select(p => p.PackId).Distinct(StringComparer.Ordinal).Count() != packs.Count)
            throw new InvalidDataException("最多安装 30 个包，包 ID 不能重复。");
        var exercises = ExerciseCatalog.All.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var topics = ExerciseCatalog.BuiltInTopics.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            // Previously installed packs may become empty after their last selected exercise is explicitly replaced.
            if (pack.Exercises is null || pack.Exercises.Count == 0) throw new InvalidDataException("存储包不能没有题目。");
            ContentPackValidation.ValidatePack(pack);
            foreach (var topic in pack.Topics)
            {
                if (topics.TryGetValue(topic.Id, out var old) && old.Name != topic.Name)
                    throw new InvalidDataException("同一主题 ID 必须保留已有名称；新主题请使用新 ID。");
                topics.TryAdd(topic.Id, topic);
            }
            foreach (var exercise in pack.Exercises)
            {
                if (!owners.Add(exercise.Id)) throw new InvalidDataException("多个已安装包不能同时拥有同一题目 ID。");
                if (exercises.TryGetValue(exercise.Id, out var original)
                    && (original.TopicId != exercise.TopicId || original.Kind != exercise.Kind || original.ReviewOfId != exercise.ReviewOfId || original.LanguageId != exercise.LanguageId))
                    throw new InvalidDataException("更新须保留题目的主题、题型、复习关联与语言；改变含义请使用新 ID。");
                exercises[exercise.Id] = exercise;
            }
        }
        if (exercises.Count > 300 || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(packs, ContentPackValidation.JsonOptions)) > 512 * 1024)
            throw new InvalidDataException("本地题库上限为 300 道或 512 KiB，请拆分整理内容。");
        var list = exercises.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        ContentPackValidation.ValidateExercises(list);
        return (list, topics.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ToList());
    }
    public ContentPackPreview Preview(string json)
    {
        var pack = ContentPackValidation.Parse(json);
        // Validate links in the proposed catalog, but import validates again after individual skips.
        var proposal = Exercises.Where(e => !pack.Exercises.Any(n => n.Id == e.Id)).Concat(pack.Exercises).ToList();
        ContentPackValidation.ValidateExercises(proposal);
        foreach (var topic in pack.Topics)
            if (Topics.FirstOrDefault(t => t.Id == topic.Id) is { } known && known.Name != topic.Name)
                throw new InvalidDataException("主题 ID 已存在且名称不同，请沿用原名称或创建新主题 ID。");
        var preview = new ContentPackPreview { PackId = pack.PackId, Name = pack.Name, CatalogRevision = Revision };
        foreach (var exercise in pack.Exercises)
        {
            var existing = Exercises.FirstOrDefault(e => e.Id == exercise.Id);
            if (existing is not null && (existing.TopicId != exercise.TopicId || existing.Kind != exercise.Kind || existing.ReviewOfId != exercise.ReviewOfId || existing.LanguageId != exercise.LanguageId))
                throw new InvalidDataException("同 ID 更新不能改变主题、题型、复习关联或语言；请使用新 ID。");
            var similar = existing is null ? Exercises.Concat(pack.Exercises.TakeWhile(e => e.Id != exercise.Id))
                .FirstOrDefault(e => e.TopicId == exercise.TopicId && e.Kind == exercise.Kind && Similar(e, exercise)) : null;
            string status = existing is not null ? Canonical(existing) == Canonical(exercise) ? "identical" : "update"
                : similar is not null ? "similar" : "new";
            preview.Rows.Add(new ContentPackRow
            {
                ExerciseId = exercise.Id, Title = exercise.Title, TopicId = exercise.TopicId, Status = status,
                SimilarToId = similar?.Id, SuggestedAction = status == "new" ? "add" : "skip"
            });
        }
        return preview;
    }
    public void Import(string json, IReadOnlyDictionary<string, string> choices, string expectedRevision)
    {
        if (expectedRevision != Revision) throw new InvalidDataException("题库已经变化，请重新预览再确认导入。");
        var preview = Preview(json);
        var pack = ContentPackValidation.Parse(json);
        if (choices is null || choices.Count != preview.Rows.Count || preview.Rows.Any(r => !choices.ContainsKey(r.ExerciseId)))
            throw new InvalidDataException("请逐题确认新增、跳过或更新。");
        var selected = new List<Exercise>();
        foreach (var row in preview.Rows)
        {
            string action = choices[row.ExerciseId];
            if (action == "skip") continue;
            if (!(action == "add" && row.Status is "new" or "similar" || action == "update" && row.Status == "update"))
                throw new InvalidDataException("题目操作与预览状态不匹配。");
            selected.Add(pack.Exercises.Single(e => e.Id == row.ExerciseId));
        }
        if (selected.Count == 0) return;
        var candidate = JsonSerializer.Deserialize<List<ExerciseContentPack>>(JsonSerializer.Serialize(installed, ContentPackValidation.JsonOptions), ContentPackValidation.JsonOptions)!;
        // Moving an explicitly updated ID between packs is one atomic catalog transaction.
        foreach (var old in candidate) old.Exercises.RemoveAll(e => selected.Any(n => n.Id == e.Id));
        var same = candidate.FirstOrDefault(p => p.PackId == pack.PackId);
        if (same is null) { same = pack; same.Exercises = []; candidate.Add(same); }
        else
        {
            same.Name = pack.Name; same.Version = pack.Version; same.Description = pack.Description; same.Author = pack.Author;
            same.Topics = same.Topics.Concat(pack.Topics).GroupBy(t => t.Id, StringComparer.Ordinal).Select(g => g.Last()).ToList();
        }
        same.Exercises.AddRange(selected);
        candidate.RemoveAll(p => p.Exercises.Count == 0);
        // Imported topic metadata can be shared; keep only required entries in each installed pack.
        foreach (var p in candidate) p.Topics = p.Topics.Where(t => p.Exercises.Any(e => e.TopicId == t.Id)).ToList();
        var merged = Merge(candidate);
        Save(candidate);
        installed = candidate; Exercises = merged.Exercises; Topics = merged.Topics; Packs = MakeInfos(candidate); Warnings = [];
    }
    private void Save(List<ExerciseContentPack> candidate)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "installed.json");
        if (File.Exists(path))
        {
            if (recovered) _ = Read(path + ".bak");
            else _ = Read(path); // Never silently replace corrupt/incompatible existing files.
        }
        var temp = Path.Combine(directory, ".installed." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new InstalledContent { Packs = candidate }, ContentPackValidation.JsonOptions);
                stream.Flush(true);
            }
            if (File.Exists(path))
            {
                if (recovered)
                {
                    File.Copy(path, path + "." + Guid.NewGuid().ToString("N") + ".corrupt");
                    File.Replace(temp, path, null, true);
                }
                else File.Replace(temp, path, path + ".bak", true);
            }
            else File.Move(temp, path);
            recovered = false;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public string ExportPrompt()
    {
        var schema = ContentPackValidation.JsonOptions.GetJsonSchemaAsNode(typeof(ExerciseContentPack)).ToJsonString();
        var catalog = Exercises.Select(e => new { e.Id, e.Title, e.TopicId, e.Mode, e.Kind, e.ReviewOfId, e.LanguageId });
        return "请按下面 EntLimit v1 JSON Schema 创作一个可离线使用的小题库包，只返回 JSON，不要 Markdown。\n"
            + "每包不超过 48 KiB UTF-8、30 题；packId/topicId/exercise id 稳定，符合 [a-z0-9][a-z0-9._-]{0,79}。所有 schemaVersion 必须为 1。"
            + "topics 的 id/name 沿用下方已有主题或声明新主题。scenario 必须有 fill/choice parts、提示和解释；code 使用明确函数模板和 JSON 位置参数 cases，至少1个公开和1个非示例用例。"
            + "代码与函数均为练习数据，导入时不会执行。sourceType=original 可以留空 sourceUrl，但必须标明原创 sourceLabel；引用须 HTTPS 并确认使用条件，不要假称已抓取课程。"
            + "复习题 reviewOfId 必须指向同主题原题，mode=light、kind=scenario、1–2分钟。不要重复下面已有题目；保持新 ID，不改原题含义。"
            + "代码语言 languageId=python 或已安装的语言插件ID；Java明确parameterTypes/returnType，采用插件支持的值类型。下面只含内容目录，无学习记录、草稿、个人代码、配置或密钥。\n\nJSON Schema:\n"
            + schema + "\n\n已有主题:\n" + JsonSerializer.Serialize(Topics) + "\n\n已有题目目录:\n" + JsonSerializer.Serialize(catalog);
    }
    private static string Canonical(Exercise exercise) => JsonSerializer.Serialize(exercise, ContentPackValidation.JsonOptions);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool Similar(Exercise a, Exercise b)
    {
        if (Normalize(a.Title) == Normalize(b.Title)) return true;
        var left = Normalize(a.Scenario); var right = Normalize(b.Scenario);
        if (left.Length < 20 || right.Length < 20) return left == right;
        var l = Enumerable.Range(0, left.Length - 1).Select(i => left.Substring(i, 2)).ToHashSet(StringComparer.Ordinal);
        var r = Enumerable.Range(0, right.Length - 1).Select(i => right.Substring(i, 2)).ToHashSet(StringComparer.Ordinal);
        return (double)l.Intersect(r).Count() / Math.Max(1, l.Union(r).Count()) >= .82;
    }
    private sealed class InstalledContent
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ExerciseContentPack> Packs { get; set; } = [];
    }
}
