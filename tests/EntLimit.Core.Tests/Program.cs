using System.Text;
using EntLimit.Core;

var epoch = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
var a = new ProcessIdentity(101, epoch, @"C:\GAMES\ONE.EXE");
var b = new ProcessIdentity(102, epoch, @"C:\GAMES\TWO.EXE");
var standard = SeedQuestions.All.First(x => x.Mode == "standard");
var light = SeedQuestions.All.First(x => x.Mode == "light");
var tests = new List<(string, Action)>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
SessionEngine Granted(Question? q = null, int minutes = 90)
{
    var e = new SessionEngine(new SessionState());
    q ??= standard;
    Check(e.Complete(q, q.CorrectIndex, Guid.NewGuid().ToString(), epoch, minutes), "Grant failed");
    return e;
}
void Add(string name, Action run) => tests.Add((name, run));
void ExpectThrows<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

Add("Standard and light each grant the same 90 minutes", () =>
{
    foreach (var q in new[] { standard, light })
    {
        var e = Granted(q);
        Check(e.State.Phase == SessionPhase.Ready && e.State.GrantedMinutes == 90, "Wrong grant");
        Check(e.State.DeadlineUtc is null && e.State.History.Single().Takeaway == q.Takeaway, "Missing learning record");
    }
});
Add("Wrong answers cannot grant; retry can", () =>
{
    var e = new SessionEngine(new SessionState());
    Check(!e.Complete(standard, (standard.CorrectIndex + 1) % standard.Options.Length, "retry", epoch, 90), "Wrong answer granted");
    Check(e.State.Phase == SessionPhase.Locked && e.State.History.Count == 0, "Wrong answer mutated grant");
    Check(e.Complete(standard, standard.CorrectIndex, "retry", epoch, 90), "Correct retry rejected");
});
Add("Duplicate completion is idempotent during active entertainment", () =>
{
    var e = new SessionEngine(new SessionState());
    Check(e.Complete(standard, standard.CorrectIndex, "same", epoch, 90), "Initial grant failed");
    e.ObserveUse(a, epoch);
    var deadline = e.State.DeadlineUtc;
    Check(e.Complete(standard, standard.CorrectIndex, "same", epoch.AddMinutes(80), 120), "Duplicate wasn't acknowledged");
    Check(e.State.DeadlineUtc == deadline && e.State.GrantedMinutes == 90 && e.State.History.Count == 1, "Duplicate extended grant");
    Check(!e.Complete(light, light.CorrectIndex, "new", epoch, 90), "Active grant replaced");
});
Add("Unused Ready grant does not run down; first use starts timer", () =>
{
    var e = Granted();
    e.Tick(epoch.AddHours(2));
    Check(e.State.Phase == SessionPhase.Ready, "Ready expired");
    Check(e.ObserveUse(a, epoch.AddHours(2)), "First use refused");
    Check(e.State.DeadlineUtc == epoch.AddHours(2).AddMinutes(90), "Timer didn't start on use");
});
Add("Crash/relaunch before deadline preserves remaining time", () =>
{
    var e = Granted(); e.ObserveUse(a, epoch);
    e.Reconcile([], epoch.AddMinutes(20));
    var restarted = new ProcessIdentity(103, epoch.AddMinutes(21), a.ExecutablePath);
    Check(e.ObserveUse(restarted, epoch.AddMinutes(21)), "Relaunch refused");
    Check(e.State.DeadlineUtc == epoch.AddMinutes(90), "Relaunch restarted timer");
});
Add("Two games drain; new processes cannot inherit grace", () =>
{
    var e = Granted(); e.ObserveUse(a, epoch); e.ObserveUse(b, epoch.AddMinutes(1));
    var result = e.Tick(epoch.AddMinutes(90));
    Check(result.Expired && e.State.Phase == SessionPhase.Draining, "No expiry/draining");
    Check(e.CanUse(a, epoch.AddMinutes(95)), "Existing game lost grace");
    var reusedPid = new ProcessIdentity(a.Pid, epoch.AddMinutes(95), a.ExecutablePath);
    Check(!e.CanUse(reusedPid, epoch.AddMinutes(95)), "PID reuse inherited grace");
    Check(!e.CanUse(new ProcessIdentity(999, epoch, a.ExecutablePath), epoch.AddMinutes(95)), "New game inherited grace");
    e.Reconcile([b], epoch.AddMinutes(96));
    Check(e.State.Phase == SessionPhase.Draining, "Locked before final game exit");
    e.Reconcile([], epoch.AddMinutes(97));
    Check(e.State.Phase == SessionPhase.Locked, "Didn't lock after last exit");
});
Add("Exited games at expiry do not receive grace", () =>
{
    var e = Granted(); e.ObserveUse(a, epoch);
    e.Reconcile([], epoch.AddMinutes(90));
    Check(e.State.Phase == SessionPhase.Locked, "Stale game received grace");
});
Add("Warnings and expiry are one-shot; large jumps skip warning", () =>
{
    var e = Granted(); e.ObserveUse(a, epoch);
    Check(e.Tick(epoch.AddMinutes(80)).Warning, "No pre-expiry warning");
    Check(!e.Tick(epoch.AddMinutes(81)).Warning, "Warning repeated");
    Check(e.Tick(epoch.AddMinutes(90)).Expired, "No expiry");
    Check(!e.Tick(epoch.AddMinutes(91)).Expired, "Expiry repeated");
    var jumped = Granted(); jumped.ObserveUse(a, epoch);
    var result = jumped.Tick(epoch.AddHours(3));
    Check(result.Expired && !result.Warning, "Stale warning emitted after deadline");
});
Add("Backward UTC clock cannot increase remaining allowance", () =>
{
    var e = Granted(); e.ObserveUse(a, epoch); e.Tick(epoch.AddMinutes(80));
    e.Tick(epoch.AddMinutes(-10));
    Check(e.State.LastObservedUtc == epoch.AddMinutes(80), "Clock rolled back");
    Check(e.State.DeadlineUtc == epoch.AddMinutes(90), "Clock changed deadline");
    e.Tick(epoch.AddMinutes(90));
    Check(e.State.Phase == SessionPhase.Draining, "Deadline not honored");
});
Add("Granted duration survives changed requested duration", () =>
{
    var e = Granted(minutes: 120); e.ObserveUse(a, epoch);
    Check(e.State.DeadlineUtc == epoch.AddMinutes(120), "Fixed grant lost");
    Check(!e.Complete(light, light.CorrectIndex, "replacement", epoch.AddMinutes(10), 90), "Replacement accepted");
    Check(e.State.DeadlineUtc == epoch.AddMinutes(120), "Replacement shortened grant");
});
Add("Website first use starts shared permit and doesn't restart it", () =>
{
    var e = Granted();
    Check(e.ActivateWebsite(epoch.AddMinutes(4)), "Website first use failed");
    Check(e.State.DeadlineUtc == epoch.AddMinutes(94), "Incorrect website deadline");
    e.ActivateWebsite(epoch.AddMinutes(20));
    Check(e.State.DeadlineUtc == epoch.AddMinutes(94), "Website visit restarted timer");
    Check(!e.ActivateWebsite(epoch.AddMinutes(94)), "Expired website permit accepted");
    Check(e.State.Phase == SessionPhase.Locked, "No process website permit didn't finish");
});
Add("Explicit finish clears permit while preserving learning history", () =>
{
    var e = Granted(); e.ObserveUse(a, epoch); e.Finish();
    Check(e.State.Phase == SessionPhase.Locked && e.State.DeadlineUtc is null, "Finish incomplete");
    Check(e.State.History.Count == 1 && !e.CanUse(a, epoch.AddMinutes(1)), "Finish lost history or allowed use");
});
Add("Local JSON roundtrip, backup, restore and corrupt-file preservation", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "EntLimit.Tests." + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new LocalStore(root);
        Check(store.LoadConfig().Apps.Count == 0 && store.LoadSession().Phase == SessionPhase.Locked, "Defaults unexpectedly managed");
        var e = Granted(); e.ObserveUse(a, epoch); e.Tick(epoch.AddMinutes(80));
        store.SaveSession(e.State);
        var restored = new SessionEngine(store.LoadSession());
        Check(!restored.Tick(epoch.AddMinutes(81)).Warning, "Restored warning replayed");
        Check(restored.State.History.Count == 1 && restored.State.DeadlineUtc == epoch.AddMinutes(90), "Restore lost grant");
        store.SaveSession(restored.State);
        Check(File.Exists(Path.Combine(root, "session.json.bak")), "Backup missing");
        restored.Reconcile([], epoch.AddMinutes(91));
        Check(restored.State.Phase == SessionPhase.Locked, "Old process persisted after reboot");
        var file = Path.Combine(root, "config.json");
        File.WriteAllText(file, "{broken");
        ExpectThrows<InvalidDataException>(() => store.LoadConfig());
        ExpectThrows<InvalidDataException>(() => store.SaveConfig(new AppConfig()));
        Check(File.ReadAllText(file) == "{broken", "Corrupt document overwritten");
        Check(!Directory.GetFiles(root, "*.tmp").Any(), "Temporary writes leaked");
    }
    finally { DeleteTestRoot(root); }
});
Add("Validated backups recover grants and preserve corruption evidence", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "EntLimit.Tests." + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new LocalStore(root);
        var e = Granted(); e.ObserveUse(a, epoch);
        store.SaveSession(e.State);
        store.SaveSession(e.State);
        var file = Path.Combine(root, "session.json");
        var backupBefore = File.ReadAllText(file + ".bak");
        File.WriteAllText(file, "{broken-primary");
        var restored = store.LoadSession();
        Check(restored.Phase == SessionPhase.Active && restored.DeadlineUtc == epoch.AddMinutes(90)
            && restored.History.Count == 1, "Valid backup failed to preserve grant/history");
        Check(File.ReadAllText(file) == "{broken-primary", "Load discarded corruption evidence");
        Check(File.Exists(Path.Combine(root, "recovery.log")), "Recovery was silent");
        store.SaveSession(restored);
        Check(File.ReadAllText(file + ".bak") == backupBefore, "Corrupt primary overwrote valid backup");
        var evidence = Directory.GetFiles(root, "session.json.*.corrupt");
        Check(evidence.Length == 1 && File.ReadAllText(evidence[0]) == "{broken-primary", "Corruption evidence missing");
        Check(store.LoadSession().History.Count == 1, "Recovered primary invalid");

        store.SaveConfig(new AppConfig { UnlockMinutes = 120 });
        store.SaveConfig(new AppConfig { UnlockMinutes = 120 });
        File.WriteAllText(Path.Combine(root, "config.json"), "{\"SchemaVersion\":999}");
        Check(store.LoadConfig().UnlockMinutes == 120, "Semantically invalid config did not recover");
        store.SaveCatalog([new AppCatalogEntry { Name = "Example", ExecutablePath = a.ExecutablePath, Source = "Manual" }]);
        store.SaveCatalog(store.LoadCatalog());
        File.WriteAllText(Path.Combine(root, "catalog.json"), "null");
        Check(store.LoadCatalog().Single().Name == "Example", "Catalog did not recover");
    }
    finally { DeleteTestRoot(root); }
});
Add("Corrupt primary and backup fail without resetting permits", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "EntLimit.Tests." + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var store = new LocalStore(root);
        var path = Path.Combine(root, "session.json");
        File.WriteAllText(path, "{bad-primary");
        File.WriteAllText(path + ".bak", "{bad-backup");
        ExpectThrows<InvalidDataException>(() => store.LoadSession());
        ExpectThrows<InvalidDataException>(() => store.SaveSession(new SessionState()));
        Check(File.ReadAllText(path) == "{bad-primary" && File.ReadAllText(path + ".bak") == "{bad-backup",
            "Invalid originals were changed");
    }
    finally { DeleteTestRoot(root); }
});
Add("Bounded pipe framing rejects oversize/truncated messages", () =>
{
    using var good = new MemoryStream(Encoding.UTF8.GetBytes("{\"Ok\":true}\n"));
    Check(PipeClient.ReadLineAsync(good, 64).GetAwaiter().GetResult() == "{\"Ok\":true}", "Bad framing");
    using var oversized = new MemoryStream(Encoding.UTF8.GetBytes("123456\n"));
    ExpectThrows<InvalidDataException>(() => PipeClient.ReadLineAsync(oversized, 5).GetAwaiter().GetResult());
    using var truncated = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
    ExpectThrows<EndOfStreamException>(() => PipeClient.ReadLineAsync(truncated, 64).GetAwaiter().GetResult());
});
Add("Seed pool covers both topics and modes with source links", () =>
{
    Check(SeedQuestions.All.Count >= 12, "Seed pool too small");
    Check(SeedQuestions.All.Select(x => x.Id).Distinct().Count() == SeedQuestions.All.Count, "Duplicate questions");
    foreach (var topic in new[] { "python", "network" })
    foreach (var mode in new[] { "standard", "light" })
        Check(SeedQuestions.All.Any(x => x.TopicId == topic && x.Mode == mode), "Missing topic/mode");
    foreach (var q in SeedQuestions.All)
        Check(q.CorrectIndex >= 0 && q.CorrectIndex < q.Options.Length && q.SourceUrl.StartsWith("https://")
            && !string.IsNullOrWhiteSpace(q.Takeaway), "Invalid seed exercise");
});

var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} checks passed");
return failures == 0 ? 0 : 1;

static void DeleteTestRoot(string root)
{
    var resolved = Path.GetFullPath(root);
    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(resolved).StartsWith("EntLimit.Tests.", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing to delete outside the named temporary test workspace.");
    if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
}
