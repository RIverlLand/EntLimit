using System.Text.Json;
using EntLimit.Core;

var tests = new List<(string Name, Action Run)>();
void Add(string name, Action action) => tests.Add((name, action));
void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
var exercises = ExerciseCatalog.All;
var network = exercises.First(x => x.Kind == "scenario" && x.Mode == "standard");
var light = exercises.First(x => x.Kind == "scenario" && x.Mode == "light");
var code = exercises.First(x => x.Kind == "code");
var now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
Dictionary<string, string> Correct(Exercise e) => e.Parts.ToDictionary(p => p.Id,
    p => p.Kind == "fill" ? p.AcceptedAnswers[0] : p.CorrectIndex.ToString());

Add("Bundled catalog has each topic/mode and valid original attribution", () =>
{
    Check(exercises.Count == 8 && exercises.Count(x => x.ReviewOfId is null) == 4, "Expected four originals and four reviews");
    foreach (var topic in new[] { "network", "python" })
        foreach (var mode in new[] { "standard", "light" })
            Check(exercises.Count(x => x.ReviewOfId is null && x.TopicId == topic && x.Mode == mode) == 1, "Missing original topic/mode");
    Check(exercises.All(x => x.SourceLabel.Contains("原创") && x.SourceUrl.StartsWith("https://")), "Missing provenance");
});
Add("All-correct scenario passes both modes", () =>
{
    foreach (var e in exercises.Where(x => x.Kind == "scenario"))
        Check(ExerciseGrader.Grade(e, Correct(e)).Passed, "Correct answer rejected");
});
Add("Missing answer and extra answer cannot pass", () =>
{
    var answers = Correct(network);
    answers.Remove(network.Parts[0].Id);
    answers["invented"] = "192.168.10.0";
    var result = ExerciseGrader.Grade(network, answers);
    Check(!result.Passed && !result.Parts[0].Correct, "Missing part incorrectly accepted");
    Check(result.Parts[0].Feedback.Contains("还没有作答"), "Missing answer not explained");
});
Add("All wrong choices and invalid indexes fail", () =>
{
    var part = network.Parts.First(x => x.Kind == "choice");
    foreach (var value in new[] { "-1", "99", "one", "1.0", "" }.Concat(
        Enumerable.Range(0, part.Options.Length).Where(i => i != part.CorrectIndex).Select(i => i.ToString())))
    {
        var answers = Correct(network); answers[part.Id] = value;
        var result = ExerciseGrader.Grade(network, answers);
        Check(!result.Passed && result.Parts.Count == network.Parts.Count, "Bad choice accepted or feedback incomplete");
    }
});
Add("Fill allows case, boundary whitespace and fullwidth equivalents", () =>
{
    var answers = Correct(light);
    answers["service"] = "  ｄｎｓ　";
    Check(ExerciseGrader.Grade(light, answers).Passed, "Reasonable DNS spelling rejected");
    var netAnswers = Correct(network); netAnswers["network"] = "１９２．１６８．１０．０／２４";
    Check(ExerciseGrader.Grade(network, netAnswers).Passed, "Fullwidth IP rejected");
});
Add("Fill rejects substring and wrong subnet", () =>
{
    foreach (var value in new[] { "192.168.10.0 and 192.168.20.0", "192.168.10.20", "192.168.10.0/16", "", "1 92.168.10.0" })
    {
        var answers = Correct(network); answers["network"] = value;
        Check(!ExerciseGrader.Grade(network, answers).Passed, "Overbroad fill accepted");
    }
});
Add("Incorrect answer gets guidance, correct answer gets explanation", () =>
{
    var answers = Correct(network); answers["printer"] = "0";
    var result = ExerciseGrader.Grade(network, answers);
    Check(result.Parts.Single(x => x.PartId == "printer").Feedback.Contains(network.Parts[1].Hints[0]), "No targeted hint");
    Check(result.Parts[0].Feedback.Contains(network.Parts[0].Explanation), "No explanation");
});
Add("Choice grader cannot grant code or empty scenarios", () =>
{
    Check(!ExerciseGrader.Grade(code, []).Passed, "Code accepted without execution");
    Check(!ExerciseGrader.Grade(new Exercise(), []).Passed, "Empty exercise accepted");
});
Add("Coding cases have parseable positional args and exercise edge conditions", () =>
{
    foreach (var e in exercises.Where(x => x.Kind == "code"))
    {
        Check(e.Coding!.Cases.Any(x => x.IsExample) && e.Coding.Cases.Any(x => !x.IsExample), "Missing submit cases");
        Check(e.Coding.Cases.Any(x => x.ArgumentsJson == "[[]]"), "Missing empty input");
        foreach (var test in e.Coding.Cases)
        {
            using var args = JsonDocument.Parse(test.ArgumentsJson);
            using var expected = JsonDocument.Parse(test.ExpectedJson);
            Check(args.RootElement.ValueKind == JsonValueKind.Array && args.RootElement.GetArrayLength() == 1, "Invalid arguments");
        }
    }
});
Add("Validated normal and light have equal grant and history", () =>
{
    foreach (var exercise in new[] { network, light, code })
    {
        var engine = new SessionEngine(new SessionState());
        Check(engine.CompleteValidated(exercise, "fresh", now, 90), "Validated grant failed");
        Check(engine.State.Phase == SessionPhase.Ready && engine.State.GrantedMinutes == 90 && engine.State.DeadlineUtc is null, "Wrong permit");
        Check(engine.State.History.Single().QuestionId == exercise.Id && engine.State.History[0].Takeaway == exercise.Takeaway, "Wrong history");
    }
});
Add("Duplicate grant stays idempotent while active and after finish", () =>
{
    var engine = new SessionEngine(new SessionState());
    Check(engine.CompleteValidated(network, "once", now, 90), "Grant failed");
    engine.ObserveUse(new ProcessIdentity(1, now, "GAME.EXE"), now);
    var deadline = engine.State.DeadlineUtc;
    Check(engine.CompleteValidated(network, "once", now.AddMinutes(80), 120), "Duplicate not acknowledged");
    Check(engine.State.DeadlineUtc == deadline && engine.State.GrantedMinutes == 90 && engine.State.History.Count == 1, "Duplicate changed permit");
    Check(!engine.CompleteValidated(light, "once", now, 90), "Attempt collision accepted");
    Check(!engine.CompleteValidated(code, "new", now, 90), "Active permit overwritten");
    engine.Finish();
    Check(engine.CompleteValidated(network, "once", now, 90) && engine.State.Phase == SessionPhase.Locked, "Retry regranted after finish");
});
Add("Ready and draining permits cannot be replaced", () =>
{
    var engine = new SessionEngine(new SessionState());
    engine.CompleteValidated(network, "once", now, 120);
    Check(!engine.CompleteValidated(light, "second", now, 90) && engine.State.GrantedMinutes == 120, "Ready replaced");
    engine.ObserveUse(new ProcessIdentity(1, now, "GAME.EXE"), now);
    engine.Tick(now.AddMinutes(120));
    Check(engine.State.Phase == SessionPhase.Draining, "Expected draining");
    Check(!engine.CompleteValidated(light, "second", now, 90) && engine.State.History.Count == 1, "Draining replaced");
});
Add("Invalid duration does not mutate permit/history", () =>
{
    foreach (var duration in new[] { 0, 89, 1441 })
    {
        var engine = new SessionEngine(new SessionState());
        try { engine.CompleteValidated(network, "bad", now, duration); throw new Exception("Expected validation exception"); }
        catch (ArgumentOutOfRangeException) { }
        Check(engine.State.Phase == SessionPhase.Locked && engine.State.History.Count == 0, "Invalid grant mutated state");
    }
});
Add("Legacy and new completions share attempt deduplication", () =>
{
    var engine = new SessionEngine(new SessionState());
    var old = SeedQuestions.All[0];
    engine.Complete(old, old.CorrectIndex, "legacy", now, 90);
    engine.Finish();
    Check(!engine.CompleteValidated(network, "legacy", now, 90), "Legacy collision accepted");
    Check(engine.CompleteValidated(network, "next", now.AddHours(1), 90), "Fresh completion rejected");
});
LearningRecord Passed(Exercise e, int hours, string? attempt = null) => new()
{
    AttemptId = attempt ?? Guid.NewGuid().ToString("N"), QuestionId = e.Id,
    TopicId = e.TopicId, Mode = e.Mode, CompletedAtUtc = now.AddHours(hours)
};
Exercise Review(Exercise e) => exercises.Single(r => r.ReviewOfId == e.Id);
Add("Old successful exact IDs are recognized without progress migration", () =>
{
    var history = new[] { Passed(network, -20) };
    var p = ExerciseSelection.Progress(network, history, exercises);
    Check(p.Passed && p.PassedCount == 1 && p.ReviewCount == 0 && p.LastPassedAt == now.AddHours(-20), "Old history not recognized");
    Check(!ExerciseSelection.Progress(light, history, exercises).Passed, "Unrelated original marked passed");
});
Add("Normal mode pools prefer new standard then new light then completed recall", () =>
{
    Check(ExerciseSelection.Rank(exercises, [], "network", "standard").Select(e => e.Id).SequenceEqual([network.Id]), "Expected standard only");
    var history = new[] { Passed(network, -20) };
    Check(ExerciseSelection.Rank(exercises, history, "network", "standard").Select(e => e.Id).SequenceEqual([light.Id]), "Completed standard leaked into new pool");
    var all = history.Append(Passed(light, -10)).ToArray();
    var pool = ExerciseSelection.Rank(exercises, all, "network", "standard");
    Check(pool.Count == 2 && pool[0].Id == network.Id && pool.All(e => e.ReviewOfId is null), "All-complete fallback wrong");
});
Add("Light mode reviews completed standard originals before new light content", () =>
{
    var history = new[] { Passed(network, -20) };
    var pool = ExerciseSelection.Rank(exercises, history, "network", "light");
    Check(pool.Count == 1 && pool[0].ReviewOfId == network.Id, "Standard completion not offered as light review");
    Check(ExerciseSelection.Rank(exercises, [], "network", "light").Single().Id == light.Id, "Cold start should use original light exercise");
});
Add("Light mode falls back to original standard when topic has no light or eligible review", () =>
{
    Check(ExerciseSelection.Rank([network, Review(network)], [], "network", "light").Single().Id == network.Id, "No-light fallback failed");
});
Add("Review completion cannot fabricate an original completion", () =>
{
    var review = Review(code);
    var history = new[] { Passed(review, -5) };
    var source = ExerciseSelection.Progress(code, history, exercises);
    Check(!source.Passed && source.PassedCount == 0 && source.ReviewCount == 1, "Review incorrectly completed source");
    Check(!ExerciseSelection.Progress(review, history, exercises).SourcePassed, "Source incorrectly passed");
    Check(!ExerciseSelection.Rank(exercises, history, "python", "light").Any(e => e.Id == review.Id), "Review without source pass eligible");
});
Add("Review chronology includes latest source and review activity", () =>
{
    var subnetReview = Review(network); var dnsReview = Review(light);
    var history = new[] { Passed(network, -30), Passed(light, -20), Passed(subnetReview, -1) };
    var pool = ExerciseSelection.Rank(exercises, history, "network", "light");
    Check(pool[0].Id == dnsReview.Id && pool[1].Id == subnetReview.Id, "Recent review not demoted");
    var reread = history.Append(Passed(light, 0));
    Check(ExerciseSelection.Rank(exercises, reread, "network", "light")[0].Id == subnetReview.Id, "Recent source practice not considered");
    Check(ExerciseSelection.Rank(exercises, history, "network", "standard")[0].Id == light.Id, "Normal fallback ignored recent review");
});
Add("Tie rankings use lower review count then stable ID", () =>
{
    var subnetReview = Review(network); var dnsReview = Review(light);
    var history = new[] { Passed(network, 0), Passed(light, 0), Passed(subnetReview, -20) };
    Check(ExerciseSelection.Rank(exercises, history, "network", "light")[0].Id == dnsReview.Id, "Tie did not prefer fewer reviews");
    var ranked = ExerciseSelection.Rank(exercises.Reverse(), [Passed(network, 0), Passed(light, 0)], "network", "light");
    Check(ranked.Select(e => e.Id).SequenceEqual(ranked.Select(e => e.Id).Order(StringComparer.Ordinal)), "Final tie unstable");
});
Add("Progress separates original passes and review counts and deduplicates attempts", () =>
{
    var review = Review(network); var sourcePass = Passed(network, -20, "original-once"); var reviewPass = Passed(review, -10, "review-once");
    var history = new[] { sourcePass, sourcePass, reviewPass, reviewPass, Passed(review, -5, "review-twice") };
    var p = ExerciseSelection.Progress(network, history, exercises);
    Check(p.PassedCount == 1 && p.ReviewCount == 2 && p.LastPassedAt == now.AddHours(-20)
        && p.LastReviewedAt == now.AddHours(-5) && p.LastPracticedAt == now.AddHours(-5), "Counts or timestamps conflated");
    var rp = ExerciseSelection.Progress(review, history, exercises);
    Check(rp.SourcePassed && rp.Passed && rp.PassedCount == 2 && rp.ReviewCount == 2, "Review badge progress wrong");
});
Add("Missing attempt evidence or unrelated legacy history cannot invent progress", () =>
{
    var history = new[] { new LearningRecord { QuestionId = network.Id }, new LearningRecord { AttemptId = "unknown", QuestionId = "retired-seed" } };
    Check(!ExerciseSelection.Progress(network, history, exercises).Passed, "Empty/unknown completion evidence accepted");
    Check(ExerciseSelection.Rank(exercises, history, "network", "standard").Single().Id == network.Id, "Unknown history changed priority");
});
Add("Topic isolation and empty topic pool", () =>
{
    var history = new[] { Passed(code, -20) };
    Check(ExerciseSelection.Rank(exercises, history, "network", "light").All(e => e.TopicId == "network" && e.ReviewOfId is null), "Cross-topic review leaked");
    Check(ExerciseSelection.Rank(exercises, history, "missing", "light").Count == 0, "Missing topic returned exercises");
});
Add("Each original has one short independently graded review", () =>
{
    foreach (var original in exercises.Where(e => e.ReviewOfId is null))
    {
        var review = Review(original);
        Check(review.TopicId == original.TopicId && review.Mode == "light" && review.Kind == "scenario"
            && review.EstimatedMinutes <= 2 && review.Parts.Count is >= 1 and <= 2, "Review shape invalid");
        Check(ExerciseGrader.Grade(review, Correct(review)).Passed && !ExerciseGrader.Grade(review, []).Passed, "Review not independently graded");
    }
});
Add("All-topic pool preserves global new-standard priority", () =>
{
    var standard = ExerciseSelection.Rank(exercises, [], "all", "standard");
    Check(standard.Count == 2 && standard.Select(e => e.TopicId).Distinct().Count() == 2
        && standard.All(e => e.Mode == "standard" && e.ReviewOfId is null), "All-topic pool did not span new standard topics");
    var history = new[] { Passed(network, -5) };
    Check(ExerciseSelection.Rank(exercises, history, "all", "standard").Single().Id == code.Id,
        "Completed network standard or lower light leaked ahead of new Python standard");
    var nextPool = ExerciseSelection.Rank(exercises, history.Append(Passed(code, -4)), "all", "standard");
    Check(nextPool.Count == 2 && nextPool.All(e => e.Mode == "light" && e.ReviewOfId is null), "Expected original light pool after all standards passed");
});
Add("All-topic light pool contains eligible reviews only", () =>
{
    var history = new[] { Passed(network, -20), Passed(code, -10) };
    var pool = ExerciseSelection.Rank(exercises, history, "all", "light");
    Check(pool.Count == 2 && pool.All(e => e.ReviewOfId == network.Id || e.ReviewOfId == code.Id), "Ineligible review or fresh light leaked");
    Check(pool[0].Id == Review(network).Id, "Review age ordering lost across topics");
    var cold = ExerciseSelection.Rank(exercises, [], "all", "light");
    Check(cold.Count == 2 && cold.All(e => e.ReviewOfId is null && e.Mode == "light"), "Cold all-topic light pool wrong");
});
Add("Recommended pick follows rank and avoids previous within priority pool", () =>
{
    var pool = ExerciseSelection.Rank(exercises, [], "all", "standard");
    Check(ExerciseSelection.Pick(exercises, [], "all", "standard")?.Id == pool[0].Id, "Recommended first pick differs from rank");
    Check(ExerciseSelection.Pick(exercises, [], "all", "standard", avoidExerciseId: pool[0].Id)?.Id == pool[1].Id, "Next recommended pick failed to avoid current");
    Check(ExerciseSelection.Pick(exercises, [], "all", "standard", avoidExerciseId: "not-in-pool")?.Id == pool[0].Id, "Unknown avoid changed recommendation");
});
Add("Seeded random picks cover eligible candidates and are repeatable", () =>
{
    var left = new Random(20261018); var right = new Random(20261018);
    var sampled = new HashSet<string>();
    var expected = ExerciseSelection.Rank(exercises, [], "all", "standard").Select(e => e.Id).ToHashSet();
    for (int i = 0; i < 100; i++)
    {
        var a = ExerciseSelection.Pick(exercises, [], "all", "standard", random: true, rng: left)!;
        var b = ExerciseSelection.Pick(exercises, [], "all", "standard", random: true, rng: right)!;
        Check(a.Id == b.Id && expected.Contains(a.Id), "Seed changed or random escaped eligible pool");
        sampled.Add(a.Id);
    }
    Check(sampled.SetEquals(expected), "Random never reached an eligible candidate");
});
Add("Random next pick excludes current while multiple eligible candidates exist", () =>
{
    var current = ExerciseSelection.Rank(exercises, [], "all", "standard")[0];
    var rng = new Random(17);
    for (int i = 0; i < 30; i++)
    {
        var chosen = ExerciseSelection.Pick(exercises, [], "all", "standard", random: true, avoidExerciseId: current.Id, rng: rng)!;
        Check(chosen.Id != current.Id && chosen.Mode == "standard" && chosen.ReviewOfId is null, "Random repeated current or lowered priority");
    }
});
Add("Single eligible item is returned even when avoided, never replaced by lower tier", () =>
{
    foreach (var random in new[] { false, true })
    {
        Check(ExerciseSelection.Pick(exercises, [], "network", "standard", random, network.Id, new Random(3))?.Id == network.Id,
            "Single standard skipped into lower light tier");
        var history = new[] { Passed(network, -1) };
        Check(ExerciseSelection.Pick(exercises, history, "all", "light", random, Review(network).Id, new Random(3))?.Id == Review(network).Id,
            "Single eligible review skipped into unrelated new light");
    }
});
Add("Specific-topic picks stay isolated and empty pools return null", () =>
{
    Check(ExerciseSelection.Pick(exercises, [], "network", "standard", random: true, rng: new Random(1))?.TopicId == "network", "Topic isolation lost");
    Check(ExerciseSelection.Pick(exercises, [], "python", "light", random: true, rng: new Random(1))?.TopicId == "python", "Light topic isolation lost");
    Check(ExerciseSelection.Pick(exercises, [], "missing", "standard") is null && ExerciseSelection.Pick([], [], "all", "light", true) is null,
        "Empty pool did not return null");
});
Add("All-topic completed fallback keeps original age ordering and excludes review forms", () =>
{
    var originals = exercises.Where(e => e.ReviewOfId is null).ToList();
    var history = originals.Select((e, i) => Passed(e, -20 + i)).ToList();
    history.Add(Passed(Review(originals[0]), -1));
    var pool = ExerciseSelection.Rank(exercises, history, "all", "standard");
    Check(pool.Count == 4 && pool.All(e => e.ReviewOfId is null) && pool[^1].Id == originals[0].Id, "Fallback ignored review age or leaked review forms");
});
foreach (var (name, run) in tests) { run(); Console.WriteLine("PASS " + name); }
Console.WriteLine($"{tests.Count} exercise checks passed.");
