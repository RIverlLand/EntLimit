using System.Text;
using System.Text.Json;
using EntLimit.Core;

var tests = new List<(string Name, Action Run)>();
void Add(string name, Action run) => tests.Add((name, run));
void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
void Reject(Action run)
{
    try { run(); } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return; }
    throw new Exception("Expected rejection");
}
string Json<T>(T value) => JsonSerializer.Serialize(value, ContentPackValidation.JsonOptions);
T Copy<T>(T value) => JsonSerializer.Deserialize<T>(Json(value), ContentPackValidation.JsonOptions)!;
ExerciseContentPack Pack(string id = "custom-example", string topic = "network")
{
    var exercise = Copy(ExerciseCatalog.All.First(e => e.Kind == "scenario" && e.ReviewOfId is null));
    exercise.Id = id; exercise.TopicId = topic; exercise.Title = "原创学习包 " + id;
    exercise.Scenario = "用于验证导入新题，不同于已经安装的场景描述。" + id;
    exercise.SourceType = "original"; exercise.SourceUrl = ""; exercise.SourceLabel = "EntLimit 导入测试原创";
    return new ExerciseContentPack
    {
        PackId = "test-pack", Name = "测试题库包", Topics = [new() { Id = topic, Name = topic == "network" ? "计算机网络基础" : "自定义主题" }], Exercises = [exercise]
    };
}
void WithStore(Action<ContentPackStore, string> run)
{
    var root = Path.Combine(Path.GetTempPath(), "EntLimit-content-test-" + Guid.NewGuid().ToString("N"));
    try { run(new(root), root); }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
void ImportAll(ContentPackStore store, ExerciseContentPack pack)
{
    var json = Json(pack); var preview = store.Preview(json);
    store.Import(json, preview.Rows.ToDictionary(r => r.ExerciseId, r => r.Status == "identical" ? "skip" : r.Status == "update" ? "update" : "add"), preview.CatalogRevision);
}
Add("Original package parses and external fields remain optional", () =>
{
    var p = Pack(); var parsed = ContentPackValidation.Parse(Json(p));
    Check(parsed.Exercises.Single().LanguageId == "python" && parsed.Exercises[0].SourceUrl == "", "Defaults/original source changed");
});
Add("Preview is read-only and does not create files", () => WithStore((s, root) =>
{
    var revision = s.Revision; var preview = s.Preview(Json(Pack()));
    Check(preview.Rows.Single().Status == "new" && s.Revision == revision && !Directory.Exists(root), "Preview mutated state");
}));
Add("Atomic import persists new exercises and dynamic topic across restart", () => WithStore((s, root) =>
{
    var pack = Pack(topic: "databases"); ImportAll(s, pack);
    Check(s.Exercises.Any(e => e.Id == "custom-example") && s.Topics.Any(t => t.Id == "databases"), "No new content/topic");
    var restarted = new ContentPackStore(root);
    Check(restarted.Exercises.Any(e => e.Id == "custom-example") && restarted.Packs.Any(p => p.PackId == pack.PackId), "Content lost after restart");
    Check(!File.Exists(Path.Combine(root, "session.json")) && !File.Exists(Path.Combine(root, "config.json")), "Content wrote session/config");
}));
Add("Identical ID is recognized and skip leaves revision/files unchanged", () => WithStore((s, root) =>
{
    var pack = Pack(); ImportAll(s, pack); var revision = s.Revision;
    var preview = s.Preview(Json(pack)); Check(preview.Rows.Single().Status == "identical", "Exact duplicate not detected");
    s.Import(Json(pack), new Dictionary<string,string> { [pack.Exercises[0].Id] = "skip" }, preview.CatalogRevision);
    Check(s.Revision == revision && !File.Exists(Path.Combine(root, "packs", "installed.json.bak")), "Skip rewrote storage");
}));
Add("Same-ID update is explicit and creates backup", () => WithStore((s, root) =>
{
    var pack = Pack(); ImportAll(s, pack); pack.Version = "2"; pack.Exercises[0].Title += " 修订";
    var preview = s.Preview(Json(pack)); Check(preview.Rows.Single().Status == "update", "Update not classified");
    Reject(() => s.Import(Json(pack), new Dictionary<string,string> { [pack.Exercises[0].Id] = "add" }, preview.CatalogRevision));
    ImportAll(s, pack);
    Check(s.Exercises.Single(e => e.Id == pack.Exercises[0].Id).Title.EndsWith("修订") && File.Exists(Path.Combine(root, "packs", "installed.json.bak")), "Update or backup missing");
}));
Add("Same-ID update cannot change topic, kind, language or review semantics", () => WithStore((s, _) =>
{
    var source = Copy(ExerciseCatalog.All.First(e => e.Kind == "scenario" && e.ReviewOfId is null));
    var p = Pack(); p.Exercises = [source];
    source.LanguageId = "java"; Reject(() => s.Preview(Json(p)));
    source.LanguageId = "python"; source.TopicId = "databases"; p.Topics = [new() { Id = "databases", Name = "数据库" }]; Reject(() => s.Preview(Json(p)));
}));
Add("Similar new ID warns and defaults to skip", () => WithStore((s, _) =>
{
    var pack = Pack(); var source = ExerciseCatalog.All.First(e => e.Kind == "scenario" && e.ReviewOfId is null);
    pack.Exercises[0].Title = source.Title;
    var preview = s.Preview(Json(pack));
    Check(preview.Rows.Single().Status == "similar" && preview.Rows[0].SimilarToId == source.Id && preview.Rows[0].SuggestedAction == "skip", "Similarity warning missing");
}));
Add("Stale revision, incomplete choices and unauthorized actions reject", () => WithStore((s, _) =>
{
    var pack = Pack(); var json = Json(pack); var preview = s.Preview(json);
    Reject(() => s.Import(json, new Dictionary<string,string>(), preview.CatalogRevision));
    Reject(() => s.Import(json, new Dictionary<string,string> { [pack.Exercises[0].Id] = "update" }, preview.CatalogRevision));
    ImportAll(s, Pack("other"));
    Reject(() => s.Import(json, new Dictionary<string,string> { [pack.Exercises[0].Id] = "add" }, preview.CatalogRevision));
    Check(!s.Exercises.Any(e => e.Id == "custom-example"), "Rejected import changed snapshot");
}));
Add("Invalid package cannot replace last good snapshot or files", () => WithStore((s, root) =>
{
    var pack = Pack(); ImportAll(s, pack); var revision = s.Revision;
    var path = Path.Combine(root, "packs", "installed.json"); var file = File.ReadAllText(path);
    pack.Exercises[0].Parts[0].Kind = "shell";
    Reject(() => s.Preview(Json(pack)));
    Check(s.Revision == revision && File.ReadAllText(path) == file, "Invalid package damaged snapshot/storage");
}));
Add("Path traversal IDs never become files", () => WithStore((s, root) =>
{
    foreach (var id in new[] { "../escape", "C:\\target", "foo/bar", "UPPERCASE", "all?", ".hidden" })
    {
        var pack = Pack(); pack.PackId = id; Reject(() => s.Preview(Json(pack)));
    }
    Check(!Directory.Exists(root), "Rejected IDs created paths");
}));
Add("Oversized, unknown, duplicate and null fields reject", () =>
{
    Reject(() => ContentPackValidation.Parse(new string('x', 49153)));
    Reject(() => ContentPackValidation.Parse(Json(Pack()).TrimEnd('}') + ",\"script\":\"run.exe\"}"));
    Reject(() => ContentPackValidation.Parse(Json(Pack()).TrimEnd('}') + ",\"schemaVersion\":1}"));
    var pack = Pack(); pack.Exercises[0].Parts = null!; Reject(() => ContentPackValidation.Parse(Json(pack)));
    pack = Pack(); pack.Exercises[0].Parts[0].Hints = null!; Reject(() => ContentPackValidation.Parse(Json(pack)));
    pack = Pack(); pack.Topics = null!; Reject(() => ContentPackValidation.Parse(Json(pack)));
});
Add("Non-HTTPS references and arbitrary commands cannot be imported", () =>
{
    foreach (var url in new[] { "file:///C:/secret", "javascript:alert(1)", "http://example.com", "https://name:password@example.com" })
    {
        var p = Pack(); p.Exercises[0].SourceType = "reference"; p.Exercises[0].SourceUrl = url;
        Reject(() => ContentPackValidation.Parse(Json(p)));
    }
});
Add("Review must have existing same-topic original and cannot become orphan after skip", () => WithStore((s, _) =>
{
    var p = Pack(); var review = Copy(p.Exercises[0]); review.Id = "new-review"; review.ReviewOfId = p.Exercises[0].Id; review.Mode = "light"; review.EstimatedMinutes = 2;
    p.Exercises.Add(review);
    var preview = s.Preview(Json(p));
    Reject(() => s.Import(Json(p), new Dictionary<string,string> { [p.Exercises[0].Id] = "skip", [review.Id] = "add" }, preview.CatalogRevision));
    Check(!s.Exercises.Any(e => e.Id == review.Id), "Orphan review installed");
    review.ReviewOfId = "missing-original"; Reject(() => s.Preview(Json(p)));
}));
Add("Save failure does not expose imported content", () => WithStore((s, root) =>
{
    Directory.CreateDirectory(Path.Combine(root, "packs", "installed.json"));
    var revision = s.Revision; var p = Pack(); var preview = s.Preview(Json(p));
    Reject(() => s.Import(Json(p), new Dictionary<string,string> { [p.Exercises[0].Id] = "add" }, preview.CatalogRevision));
    Check(s.Revision == revision && !s.Exercises.Any(e => e.Id == p.Exercises[0].Id), "Unpersisted content exposed");
}));
Add("Corrupt primary recovers validated backup without losing evidence", () => WithStore((s, root) =>
{
    var first = Pack("first"); ImportAll(s, first); ImportAll(s, Pack("second"));
    var path = Path.Combine(root, "packs", "installed.json"); File.WriteAllText(path, "broken");
    var recovered = new ContentPackStore(root);
    Check(recovered.Warnings.Count == 1 && recovered.Exercises.Any(e => e.Id == "first") && !recovered.Exercises.Any(e => e.Id == "second"), "Backup not recovered");
    Check(File.ReadAllText(path) == "broken", "Read recovery overwrote evidence");
    ImportAll(recovered, Pack("third"));
    Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt").Length == 1 && new ContentPackStore(root).Exercises.Any(e => e.Id == "third"), "Evidence/new transaction missing");
}));
Add("Corrupt primary and backup fail safely without import overwrite", () => WithStore((s, root) =>
{
    ImportAll(s, Pack()); var path = Path.Combine(root, "packs", "installed.json");
    File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", "also broken");
    var recovered = new ContentPackStore(root); Check(recovered.Warnings.Count == 1, "No surfaced error");
    Reject(() => ImportAll(recovered, Pack("next")));
    Check(File.ReadAllText(path) == "broken", "Corrupt primary overwritten");
}));
Add("AI prompt exports schema and catalog without private state or filesystem paths", () => WithStore((s, root) =>
{
    Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "session.json"), "PRIVATE_ATTEMPT_HISTORY");
    File.WriteAllText(Path.Combine(root, "desktop-preferences.json"), "PRIVATE_USER_CODE");
    var prompt = s.ExportPrompt();
    Check(prompt.Contains("JSON Schema") && prompt.Contains("schemaVersion") && prompt.Contains(ExerciseCatalog.All[0].Id), "Schema/catalog missing");
    Check(!prompt.Contains("PRIVATE_") && !prompt.Contains(root), "Private state exported");
}));
Add("Published optional Java, English and Chinese samples validate and do not auto-install", () => WithStore((s, _) =>
{
    string folder = Path.Combine(AppContext.BaseDirectory, "content", "examples");
    foreach (string file in new[] { "java-basics.json", "english-basics.json", "chinese-reading.json" })
    {
        var json = File.ReadAllText(Path.Combine(folder, file));
        var pack = ContentPackValidation.Parse(json);
        Check(s.Preview(json).Rows.Count == pack.Exercises.Count, "Published sample preview invalid");
        Check(!s.Topics.Any(t => pack.Topics.Any(p => p.Id == t.Id)), "Optional sample auto-enabled");
        if (file == "java-basics.json")
            Check(pack.Exercises.Single().LanguageId == "java" && pack.Exercises[0].Coding!.ParameterTypes.SequenceEqual(["string[]"]), "Java signature missing");
        else Check(pack.Exercises.Count >= 2 && pack.Exercises.All(e => e.Kind == "scenario"), "Need at least two scenario samples");
        ImportAll(s, pack);
    }
    Check(s.Topics.Any(t => t.Id == "java") && s.Topics.Any(t => t.Id == "english") && s.Topics.Any(t => t.Id == "chinese"), "Examples did not create dynamic topics");
}));
foreach (var (name, run) in tests) { run(); Console.WriteLine("PASS " + name); }
Console.WriteLine($"{tests.Count} content checks passed.");

if (args.Length == 2 && args[0] == "--monitor")
{
    var monitorPath = Path.GetFullPath(args[1]);
    var data = Path.Combine(Path.GetTempPath(), "EntLimit-pack-ipc-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(data);
    Environment.SetEnvironmentVariable("ENT_LIMIT_INSTANCE", "pack-" + Guid.NewGuid().ToString("N"));
    Environment.SetEnvironmentVariable("ENT_LIMIT_DATA_DIR", data);
    var start = new System.Diagnostics.ProcessStartInfo(monitorPath)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        WorkingDirectory = Path.GetDirectoryName(monitorPath)!
    };
    var runtimeDirectory = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
    var sdkRoot = runtimeDirectory.Parent?.Parent?.Parent?.FullName;
    if (sdkRoot is not null) start.Environment["DOTNET_ROOT"] = sdkRoot;
    start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
    System.Diagnostics.Process? monitor = null;
    async Task<IpcResponse> Send(IpcRequest request) => await PipeClient.SendAsync(request);
    async Task<IpcResponse> WaitReady()
    {
        for (int i = 0; i < 30; i++)
        {
            try { return await Send(new() { Command = "status" }); } catch { await Task.Delay(100); }
        }
        throw new Exception("Monitor startup timed out. Exited=" + monitor?.HasExited + " " + (File.Exists(Path.Combine(data, "monitor-startup-error.log")) ? File.ReadAllText(Path.Combine(data, "monitor-startup-error.log")) : "no startup log"));
    }
    try
    {
        monitor = System.Diagnostics.Process.Start(start) ?? throw new Exception("Monitor not started");
        var initial = await WaitReady();
        Check(initial.Ok && initial.Session.History.Count == 0 && initial.Session.Phase == SessionPhase.Locked, "Isolated start wrong");
        var pack = Pack("ipc-new", "contenttest"); var json = Json(pack);
        var preview = await Send(new() { Command = "preview_pack", PackJson = json });
        Check(preview.Ok && preview.PackPreview?.Rows.Single().Status == "new" && preview.Exercises.Count == initial.Exercises.Count, "Preview mutated or missing");
        var imported = await Send(new() { Command = "import_pack", PackJson = json, PackChoices = new() { ["ipc-new"] = "add" }, ExpectedCatalogRevision = preview.PackPreview!.CatalogRevision });
        Check(imported.Ok && imported.Exercises.Any(e => e.Id == "ipc-new") && imported.Topics.Any(t => t.Id == "contenttest")
            && imported.Session.History.Count == 0 && imported.Session.Phase == SessionPhase.Locked, "Import lost metadata or changed permit");
        var wrong = await Send(new() { Command = "grade_exercise", ExerciseId = "ipc-new", Answers = [], AttemptId = "pack-ipc" });
        Check(wrong.Ok && wrong.Grade?.Passed == false && wrong.Session.History.Count == 0, "Wrong imported answer granted");
        var exercise = imported.Exercises.Single(e => e.Id == "ipc-new");
        var answers = exercise.Parts.ToDictionary(p => p.Id, p => p.Kind == "choice" ? p.CorrectIndex.ToString() : p.AcceptedAnswers[0]);
        var passed = await Send(new() { Command = "grade_exercise", ExerciseId = exercise.Id, Answers = answers, AttemptId = "pack-ipc" });
        Check(passed.Ok && passed.Grade?.Passed == true && passed.Session.Phase == SessionPhase.Ready && passed.Session.GrantedMinutes == 90
            && passed.Session.History.Single().QuestionId == exercise.Id, "Imported exercise not gradeable");
        var grantedAt = passed.Session.GrantedAtUtc;
        pack.Exercises[0].Title += " v2"; json = Json(pack);
        preview = await Send(new() { Command = "preview_pack", PackJson = json });
        var updated = await Send(new() { Command = "import_pack", PackJson = json, PackChoices = new() { [exercise.Id] = "update" }, ExpectedCatalogRevision = preview.PackPreview!.CatalogRevision });
        Check(updated.Ok && updated.Session.History.Count == 1 && updated.Session.GrantedAtUtc == grantedAt && updated.Exercises.Single(e => e.Id == exercise.Id).Title.EndsWith("v2"), "Update reset history/grant");
        var illegal = await Send(new() { Command = "import_pack", PackJson = "{}", PackChoices = [], ExpectedCatalogRevision = "old" });
        Check(!illegal.Ok && illegal.Session.GrantedAtUtc == grantedAt && illegal.Exercises.Any(e => e.Id == exercise.Id), "Rejected IPC changed content/permit");
        var prompt = await Send(new() { Command = "export_pack_prompt" });
        Check(prompt.Ok && prompt.PromptText!.Contains("ipc-new") && !prompt.PromptText.Contains("pack-ipc"), "IPC prompt missing content or leaked attempts");
        var invalidGate = await Send(new() { Command = "admin_gate" });
        Check(invalidGate.Ok && !invalidGate.GateDenied && invalidGate.Session.GrantedAtUtc == grantedAt, "Invalid elevated gate changed permit");
        await Send(new() { Command = "shutdown" }); await monitor.WaitForExitAsync(); monitor.Dispose();
        monitor = System.Diagnostics.Process.Start(start) ?? throw new Exception("Monitor restart failed");
        var restarted = await WaitReady();
        Check(restarted.Exercises.Any(e => e.Id == exercise.Id) && restarted.Session.GrantedAtUtc == grantedAt
            && restarted.Session.History.Count == 1 && restarted.Topics.Any(t => t.Id == "contenttest"), "Restart lost pack or learning");
        Console.WriteLine("PASS Real IPC pack preview/import/grade/update/error/prompt/restart preserve history and 90 minute permit");
    }
    finally
    {
        if (monitor is not null)
        {
            try { await Send(new() { Command = "shutdown" }); await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { if (!monitor.HasExited) monitor.Kill(true); }
            monitor.Dispose();
        }
        if (Directory.Exists(data)) Directory.Delete(data, true);
    }
}
