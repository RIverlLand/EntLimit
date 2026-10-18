using System.Diagnostics;
using EntLimit.Core;

if (args.Length == 1 && args[0] == "--status")
{
    var snapshot = await PipeClient.SendAsync(new() { Command = "status" });
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { snapshot.Ok, Phase = snapshot.Session.Phase.ToString(), ExerciseCount = snapshot.Exercises.Count, PythonDetected = !string.IsNullOrWhiteSpace(snapshot.PythonPath), ManagedApps = snapshot.Config.Apps.Count, HistoryCount = snapshot.Session.History.Count }));
    return snapshot.Ok ? 0 : 1;
}
if (args.Length != 3) { Console.Error.WriteLine("sample-tests <app-dir> <empty-data-dir> <python.exe>"); return 2; }
var app = Path.GetFullPath(args[0]);
var data = Path.GetFullPath(args[1]);
if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any()) throw new Exception("Test data directory must be empty.");
Directory.CreateDirectory(data);
Environment.SetEnvironmentVariable("ENT_LIMIT_INSTANCE", "sample-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("ENT_LIMIT_DATA_DIR", data);
var start = new ProcessStartInfo(Path.Combine(app, "EntLimit.Monitor.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = app, WindowStyle = ProcessWindowStyle.Hidden };
start.Environment["ENT_LIMIT_INSTANCE"] = Environment.GetEnvironmentVariable("ENT_LIMIT_INSTANCE");
start.Environment["ENT_LIMIT_DATA_DIR"] = data;
using var monitor = Process.Start(start) ?? throw new Exception("No monitor");
Process? restartedMonitor = null;
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
Task<IpcResponse> Send(IpcRequest r) => PipeClient.SendAsync(r);
async Task<IpcResponse> Poll(string job)
{
    for (int i = 0; i < 100; i++)
    {
        var response = await Send(new() { Command = "code_status", JobId = job });
        if (!response.Ok || response.CodeJob?.State != "running") return response;
        await Task.Delay(100);
    }
    throw new Exception("Code job did not complete");
}
async Task<IpcResponse> Code(Exercise e, string code, string attempt, bool examples = false)
{
    var begin = await Send(new() { Command = "start_code", ExerciseId = e.Id, Code = code, PythonPath = args[2], AttemptId = attempt, ExamplesOnly = examples });
    Check(begin.Ok && begin.CodeJob is not null, "Code job accepted");
    return await Poll(begin.CodeJob!.JobId);
}
try
{
    IpcResponse? initial = null;
    for (int i = 0; i < 15; i++) { try { initial = await Send(new() { Command = "status" }); break; } catch { await Task.Delay(250); } }
    Check(initial is not null && initial.Ok && initial.Session.Phase == SessionPhase.Locked, "Isolated monitor starts locked with no targets");
    var exercises = initial!.Exercises;
    var originals = exercises.Where(e => e.ReviewOfId is null).ToList();
    var reviews = exercises.Where(e => e.ReviewOfId is not null).ToList();
    Check(exercises.Count == 8 && originals.Count == 4 && reviews.Count == 4
        && originals.All(o => reviews.Count(r => r.ReviewOfId == o.Id) == 1)
        && reviews.All(r => r.Kind == "scenario" && r.Mode == "light" && originals.Any(o => o.Id == r.ReviewOfId && o.TopicId == r.TopicId)),
        "Published catalog contains four original exercises and four linked light scenario reviews");
    var net = originals.Single(e => e.Kind == "scenario" && e.Mode == "standard");
    var bad = await Send(new() { Command = "grade_exercise", ExerciseId = net.Id, AttemptId = "network", Answers = [] });
    Check(bad.Ok && bad.Grade?.Passed == false && bad.Session.Phase == SessionPhase.Locked && bad.Session.History.Count == 0, "Incomplete network answers cannot grant");
    Check(!ExerciseSelection.Progress(net, bad.Session.History, exercises).Passed, "Incorrect original submission cannot mark exercise passed");
    var answers = net.Parts.ToDictionary(p => p.Id, p => p.Kind == "choice" ? p.CorrectIndex.ToString() : p.AcceptedAnswers[0]);
    var good = await Send(new() { Command = "grade_exercise", ExerciseId = net.Id, AttemptId = "network", Answers = answers });
    Check(good.Ok && good.Grade?.Passed == true && good.Session.Phase == SessionPhase.Ready && good.Session.History.Count == 1, "All network parts atomically grant 90 minutes");
    Check(ExerciseSelection.Progress(net, good.Session.History, exercises).Passed
        && ExerciseSelection.Rank(exercises, good.Session.History, net.TopicId, "light").Select(e => e.ReviewOfId).SequenceEqual(new[] { net.Id }),
        "Successful original history marks pass and prioritizes its linked review in simple mode");
    var grantTime = good.Session.GrantedAtUtc;
    var again = await Send(new() { Command = "grade_exercise", ExerciseId = net.Id, AttemptId = "network", Answers = answers });
    Check(again.Session.GrantedAtUtc == grantTime && again.Session.History.Count == 1, "Network retry cannot duplicate reward");
    await Send(new() { Command = "finish" });
    var py = originals.Single(e => e.Kind == "code" && e.Mode == "standard");
    var analysisBefore = await Send(new() { Command = "status" });
    var marker = Path.Combine(data, "manual-analysis-must-not-execute.txt");
    var analyzedCode = "import pathlib\npathlib.Path(" + System.Text.Json.JsonSerializer.Serialize(marker) + ").write_text('executed')\ndef " + py.Coding!.FunctionName + "(items):\n    result = []\n    for item in items:\n        if item not in result:\n            result.append(item)\n    return result\n";
    var analysisStart = await Send(new() { Command = "start_analysis", ExerciseId = py.Id, Code = analyzedCode, PythonPath = args[2] });
    Check(analysisStart.Ok && analysisStart.CodeJob is { AnalysisOnly: true } && analysisStart.CodeJob.ExerciseId == py.Id, "Manual analysis job identifies its exercise and analysis-only operation");
    var analyzed = await Poll(analysisStart.CodeJob!.JobId);
    Check(analyzed.CodeJob?.Result is { Passed: false, Analysis.AstAvailable: true } && !File.Exists(marker), "Manual analysis reads syntax without executing submitted top-level file writes");
    var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(analyzedCode)));
    Check(analyzed.CodeJob!.Result!.Analysis!.CodeHash == expectedHash && analyzed.CodeJob.Result.Analysis.Findings.Any(f => f.Kind == "membership_list" && f.Line > 0), "Analysis binds exact code hash and reports actual list lookup evidence");
    Check(analyzed.Session.Phase == analysisBefore.Session.Phase && analyzed.Session.History.Count == analysisBefore.Session.History.Count && analyzed.Session.GrantedAtUtc == analysisBefore.Session.GrantedAtUtc, "Manual analysis cannot grant a permit or add learning history");
    var malformed = await Send(new() { Command = "start_analysis", ExerciseId = py.Id, Code = "def broken(:\n", PythonPath = args[2] });
    var syntaxAnalysis = await Poll(malformed.CodeJob!.JobId);
    Check(syntaxAnalysis.CodeJob?.Result is { Status: "syntax_error", Analysis.AstAvailable: false } && syntaxAnalysis.Session.Phase == SessionPhase.Locked, "Syntax errors remain unrecognized and cannot unlock entertainment");
    var absentAnalysisStart = await Send(new() { Command = "start_analysis", ExerciseId = py.Id, Code = analyzedCode, PythonPath = Path.Combine(data, "missing-python.exe") });
    var absentAnalysis = await Poll(absentAnalysisStart.CodeJob!.JobId);
    Check(absentAnalysis.CodeJob?.Result is { Status: "runtime_missing", Analysis: { AstAvailable: false, Tutorials.Count: > 0 } } && absentAnalysis.Session.History.Count == analysisBefore.Session.History.Count, "Missing Python only offers labeled generic tutorials and no reward");
    var wrong = await Code(py, "def " + py.Coding!.FunctionName + "(*args):\n    return None\n", "wrong-code");
    Check(wrong.CodeJob?.Result?.Passed == false && wrong.Session.Phase == SessionPhase.Locked, "Wrong code does not grant");
    Check(wrong.CodeJob?.Result?.Analysis is { AstAvailable: true }, "Incorrect full submission automatically includes code analysis");
    var example = await Code(py, py.Coding.ReferenceCode, "examples", true);
    Check(example.CodeJob?.Result?.Passed == true && example.Session.Phase == SessionPhase.Locked, "Running examples cannot grant");
    Check(example.CodeJob?.Result?.Analysis is null, "Example-only runs do not launch optional analysis");
    var full = await Code(py, py.Coding.ReferenceCode, "full");
    Check(full.CodeJob?.Result?.Passed == true && full.Session.Phase == SessionPhase.Ready && full.Session.History.Count == 2, "Actual full Python checks grant and persist history");
    Check(full.CodeJob?.Result?.Analysis is { AstAvailable: true } && full.CodeJob.Result.Analysis.CodeHash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(py.Coding.ReferenceCode))), "Successful full submission includes exact-snapshot analysis without changing grading");
    var duplicate = await Send(new() { Command = "code_status", JobId = full.CodeJob!.JobId });
    Check(duplicate.Session.History.Count == 2 && duplicate.Session.GrantedAtUtc == full.Session.GrantedAtUtc, "Repeated polling cannot duplicate reward");
    await Send(new() { Command = "finish" });
    var running = await Send(new() { Command = "start_code", ExerciseId = py.Id, Code = "while True:\n    pass\n", PythonPath = args[2], AttemptId = "cancel" });
    var watch = Stopwatch.StartNew();
    var responsive = await Send(new() { Command = "status" });
    Check(responsive.Ok && watch.Elapsed < TimeSpan.FromSeconds(2), "Monitor remains responsive during infinite user code");
    var cancelled = await Send(new() { Command = "cancel_code", JobId = running.CodeJob!.JobId });
    Check(cancelled.Ok && cancelled.CodeJob?.State == "cancelled" && cancelled.Session.Phase == SessionPhase.Locked, "Cancel cannot grant");
    await Task.Delay(500);
    var missing = await Send(new() { Command = "start_code", ExerciseId = py.Id, Code = py.Coding.ReferenceCode, PythonPath = Path.Combine(data, "absent-python.exe"), AttemptId = "missing" });
    var absent = await Poll(missing.CodeJob!.JobId);
    Check(absent.CodeJob?.Result?.Passed == false && absent.Session.Phase == SessionPhase.Locked, "Missing interpreter produces feedback without grant");
    // A failed session write must leave both history and permit uncommitted.
    var sessionFile = Path.Combine(data, "session.json");
    var saved = sessionFile + ".held";
    File.Move(sessionFile, saved); Directory.CreateDirectory(sessionFile);
    try
    {
        var failed = await Send(new() { Command = "grade_exercise", ExerciseId = net.Id, AttemptId = "write-failure", Answers = answers });
        Check(!failed.Ok && failed.Session.Phase == SessionPhase.Locked && failed.Session.History.Count == 2, "Sample grant honors atomic persistence failure");
    }
    finally { Directory.Delete(sessionFile); File.Move(saved, sessionFile); }
    var fresh = await Send(new() { Command = "grade_exercise", ExerciseId = net.Id, AttemptId = "fresh-network", Answers = answers });
    var practicing = await Code(py, py.Coding.ReferenceCode, "practice-with-permit");
    Check(practicing.Session.History.Count == fresh.Session.History.Count + 1 && practicing.Session.GrantedAtUtc == fresh.Session.GrantedAtUtc && practicing.Session.Phase == SessionPhase.Ready, "Extra practice saves knowledge without replacing earned permit");
    await Send(new() { Command = "finish" });
    var stale = await Send(new() { Command = "start_code", ExerciseId = py.Id, Code = "import time\ntime.sleep(1)\n" + py.Coding.ReferenceCode, PythonPath = args[2], AttemptId = "stale-code" });
    await Send(new() { Command = "grade_exercise", ExerciseId = net.Id, AttemptId = "other-session", Answers = answers });
    await Send(new() { Command = "finish" });
    var staleResult = await Poll(stale.CodeJob!.JobId);
    Check(staleResult.CodeJob?.Result?.Passed == true && staleResult.Session.Phase == SessionPhase.Locked, "Late code result cannot reopen a manually ended later permit");
    // Review coverage comes after the original sample checks so existing exact-history assertions stay meaningful.
    var beforeReview = await Send(new() { Command = "status" });
    var review = exercises.Single(e => e.ReviewOfId == net.Id);
    var sourceBeforeReview = ExerciseSelection.Progress(net, beforeReview.Session.History, exercises);
    Check(sourceBeforeReview.Passed && sourceBeforeReview.ReviewCount == 0,
        "Existing successful original history needs no migration to derive passed status");
    await Send(new() { Command = "finish" });
    var wrongReview = await Send(new() { Command = "grade_exercise", ExerciseId = review.Id, AttemptId = "review-retry", Answers = [] });
    Check(wrongReview.Ok && wrongReview.Grade?.Passed == false && wrongReview.Session.Phase == SessionPhase.Locked
        && wrongReview.Session.History.Count == beforeReview.Session.History.Count
        && ExerciseSelection.Progress(net, wrongReview.Session.History, exercises).ReviewCount == 0,
        "Incorrect linked review neither records completion nor unlocks after finish");
    var reviewAnswers = review.Parts.ToDictionary(p => p.Id, p => p.Kind == "choice" ? p.CorrectIndex.ToString() : p.AcceptedAnswers[0]);
    var correctReview = await Send(new() { Command = "grade_exercise", ExerciseId = review.Id, AttemptId = "review-retry", Answers = reviewAnswers });
    Check(correctReview.Ok && correctReview.Grade?.Passed == true && correctReview.Session.Phase == SessionPhase.Ready
        && correctReview.Session.GrantedMinutes == 90 && correctReview.Session.History.Count == beforeReview.Session.History.Count + 1
        && correctReview.Session.History.Last().QuestionId == review.Id && correctReview.Session.History.Last().Mode == "light",
        "Correct linked review records its own ID and grants the same 90-minute permit");
    var reviewGrant = correctReview.Session.GrantedAtUtc;
    var reviewCount = correctReview.Session.History.Count;
    var reviewRetry = await Send(new() { Command = "grade_exercise", ExerciseId = review.Id, AttemptId = "review-retry", Answers = reviewAnswers });
    Check(reviewRetry.Session.History.Count == reviewCount && reviewRetry.Session.GrantedAtUtc == reviewGrant
        && ExerciseSelection.Progress(net, reviewRetry.Session.History, exercises).ReviewCount == 1,
        "Repeated review attempt is idempotent for reward, history, and review statistics");
    var unpassedOriginal = originals.First(o => !ExerciseSelection.Progress(o, reviewRetry.Session.History, exercises).Passed);
    var orphanReview = exercises.Single(e => e.ReviewOfId == unpassedOriginal.Id);
    var orphanAnswers = orphanReview.Parts.ToDictionary(p => p.Id, p => p.Kind == "choice" ? p.CorrectIndex.ToString() : p.AcceptedAnswers[0]);
    // Direct IPC must enforce the source prerequisite as well as the UI selection policy.
    var reviewedWithoutSource = await Send(new() { Command = "grade_exercise", ExerciseId = orphanReview.Id, AttemptId = "review-without-source", Answers = orphanAnswers });
    Check(!reviewedWithoutSource.Ok && reviewedWithoutSource.Grade is null
        && reviewedWithoutSource.Session.History.Count == reviewRetry.Session.History.Count
        && !ExerciseSelection.Progress(unpassedOriginal, reviewedWithoutSource.Session.History, exercises).Passed
        && !ExerciseSelection.Progress(orphanReview, reviewedWithoutSource.Session.History, exercises).SourcePassed
        && ExerciseSelection.Progress(unpassedOriginal, reviewedWithoutSource.Session.History, exercises).ReviewCount == 0,
        "Direct review of an unpassed source is rejected without recording or changing source progress");
    Check(ExerciseSelection.Rank(exercises, reviewedWithoutSource.Session.History, unpassedOriginal.TopicId, "light")
            .All(e => e.Id != orphanReview.Id)
        && ExerciseSelection.Rank(exercises, reviewedWithoutSource.Session.History, unpassedOriginal.TopicId, "standard")
            .Any(e => e.Id == unpassedOriginal.Id),
        "Simple ranking excludes reviews of unpassed sources while standard mode retains the unpassed original");
    var practiceReview = await Send(new() { Command = "grade_exercise", ExerciseId = review.Id, AttemptId = "review-extra-practice", Answers = reviewAnswers });
    var progressBeforeRestart = ExerciseSelection.Progress(net, practiceReview.Session.History, exercises);
    Check(practiceReview.Ok && practiceReview.Session.Phase == SessionPhase.Ready
        && practiceReview.Session.GrantedAtUtc == reviewGrant && practiceReview.Session.GrantedMinutes == 90
        && progressBeforeRestart.ReviewCount == 2 && progressBeforeRestart.PassedCount == sourceBeforeReview.PassedCount,
        "Extra review accumulates review count without extending permit or inflating original pass count");
    var persistedHistoryCount = practiceReview.Session.History.Count;
    Check((await Send(new() { Command = "shutdown" })).Ok, "Review fixture checkpoint accepts graceful shutdown");
    await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    restartedMonitor = Process.Start(start) ?? throw new Exception("No restarted test monitor");
    IpcResponse? afterRestart = null;
    for (int i = 0; i < 15; i++) { try { afterRestart = await Send(new() { Command = "status" }); break; } catch { await Task.Delay(250); } }
    Check(afterRestart is not null && afterRestart.Ok && afterRestart.Session.History.Count == persistedHistoryCount
        && afterRestart.Session.GrantedAtUtc == reviewGrant && afterRestart.Session.GrantedMinutes == 90,
        "Monitor restart restores review history and original earned-permit timestamp");
    var restoredProgress = ExerciseSelection.Progress(net, afterRestart!.Session.History, afterRestart.Exercises);
    Check(restoredProgress.Passed && restoredProgress.PassedCount == sourceBeforeReview.PassedCount
        && restoredProgress.ReviewCount == 2 && restoredProgress.LastReviewedAt == progressBeforeRestart.LastReviewedAt
        && !ExerciseSelection.Progress(unpassedOriginal, afterRestart.Session.History, afterRestart.Exercises).Passed,
        "Restart reconstructs original passes and separate review statistics from persisted history");
    Check(ExerciseSelection.Rank(afterRestart.Exercises, afterRestart.Session.History, net.TopicId, "light")
        .Select(e => e.ReviewOfId).SequenceEqual(new[] { net.Id }), "Restarted simple selection still prioritizes only the passed source's review");
    Console.WriteLine($"{checks} sample IPC checks passed");
    return 0;
}
finally
{
    try { await Send(new() { Command = "shutdown" }); } catch { }
    if (!monitor.WaitForExit(5000)) throw new Exception("Isolated test monitor did not shut down");
    if (restartedMonitor is not null)
    {
        try { if (!restartedMonitor.WaitForExit(5000)) { restartedMonitor.Kill(); throw new Exception("Restarted isolated monitor did not shut down"); } }
        finally { restartedMonitor.Dispose(); }
    }
}
