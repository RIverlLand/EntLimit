using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using EntLimit.Core;

if (args.Length != 2) { Console.Error.WriteLine("Usage: integration-tests <published-app-folder> <isolated-data-folder>"); return 2; }
var appDir = Path.GetFullPath(args[0]);
var dataDir = Path.GetFullPath(args[1]);
if (Process.GetProcessesByName("EntLimit.Monitor").Length != 0)
{ Console.Error.WriteLine("Stop any existing EntLimit monitor before integration tests; refusing to change existing state."); return 2; }
Directory.CreateDirectory(dataDir);
var store = new LocalStore(dataDir);
if (Directory.EnumerateFileSystemEntries(dataDir).Any())
{ Console.Error.WriteLine("Integration data directory must be empty."); return 2; }
Process? monitor = null;
Process? fixture = null;
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
Process StartMonitor()
{
    var start = new ProcessStartInfo(Path.Combine(appDir, "EntLimit.Monitor.exe"))
    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = appDir };
    start.Environment["ENT_LIMIT_DATA_DIR"] = dataDir;
    return Process.Start(start) ?? throw new Exception("Monitor could not start");
}
async Task<IpcResponse> WaitMonitor()
{
    for (int i = 0; i < 12; i++)
    {
        if (monitor?.HasExited == true) throw new Exception("Monitor exited; see isolated data log");
        try { return await PipeClient.SendAsync(new() { Command = "status" }); }
        catch { await Task.Delay(250); }
    }
    throw new Exception("Monitor never became available");
}
void StopOwn(Process? process)
{
    if (process is null || process.HasExited) return;
    var executable = process.MainModule?.FileName;
    if (!string.Equals(executable, Path.Combine(appDir, "EntLimit.Monitor.exe"), StringComparison.OrdinalIgnoreCase)
        && !string.Equals(executable, Path.Combine(appDir, "EntLimit.NativeHost.exe"), StringComparison.OrdinalIgnoreCase))
        throw new Exception("Refusing to stop an unexpected process");
    process.Kill(); process.WaitForExit(5000); process.Dispose();
}

try
{
    monitor = StartMonitor();
    var initial = await WaitMonitor();
    Check(initial.Ok && initial.Session.Phase == SessionPhase.Locked && initial.Config.Apps.Count == 0, "Default IPC state is opt-in and locked");
    var q = initial.Questions.First(x => x.Mode == "light" && x.TopicId == "network");
    var wrong = await PipeClient.SendAsync(new() { Command = "complete", QuestionId = q.Id, AnswerIndex = (q.CorrectIndex + 1) % q.Options.Length, AttemptId = "integration-attempt" });
    Check(!wrong.Ok && wrong.Session.Phase == SessionPhase.Locked, "Monitor refuses wrong answer");
    var sessionFile = Path.Combine(dataDir, "session.json");
    var heldSession = Path.Combine(dataDir, "session.json.test-hold");
    File.Move(sessionFile, heldSession);
    Directory.CreateDirectory(sessionFile); // An empty directory at the file path simulates write failure.
    try
    {
        var blocked = await PipeClient.SendAsync(new() { Command = "complete", QuestionId = q.Id, AnswerIndex = q.CorrectIndex, AttemptId = "blocked-write" });
        Check(!blocked.Ok && blocked.Session.Phase == SessionPhase.Locked && blocked.Session.History.Count == 0, "Persistence failure cannot expose an uncommitted permit");
    }
    finally { Directory.Delete(sessionFile, recursive: false); File.Move(heldSession, sessionFile); }
    var granted = await PipeClient.SendAsync(new() { Command = "complete", QuestionId = q.Id, AnswerIndex = q.CorrectIndex, AttemptId = "integration-attempt" });
    Check(granted.Ok && granted.Session.Phase == SessionPhase.Ready && granted.Session.History.Count == 1, "Answer persists learning and Ready grant");
    var active = await PipeClient.SendAsync(new() { Command = "browser_use" });
    Check(active.Ok && active.Session.Phase == SessionPhase.Active && active.Session.DeadlineUtc - active.Session.StartedAtUtc == TimeSpan.FromMinutes(90), "Browser use begins 90-minute shared session");
    var config = active.Config; config.UnlockMinutes = 180; config.SoundEnabled = false;
    var saved = await PipeClient.SendAsync(new() { Command = "save_config", Config = config });
    Check(saved.Ok && saved.Session.GrantedMinutes == 90 && saved.Session.DeadlineUtc == active.Session.DeadlineUtc, "Changing rules cannot shorten or replace earned permit");
    StopOwn(monitor); monitor = StartMonitor();
    var restored = await WaitMonitor();
    Check(restored.Session.Phase == SessionPhase.Active && restored.Session.DeadlineUtc == active.Session.DeadlineUtc && restored.Session.History.Count == 1, "Monitor restart preserves grant and history");

    var hostStart = new ProcessStartInfo(Path.Combine(appDir, "EntLimit.NativeHost.exe"))
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    using (var host = Process.Start(hostStart) ?? throw new Exception("Native host start failed"))
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, type = "snapshot" });
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, request.Length);
        await host.StandardInput.BaseStream.WriteAsync(header); await host.StandardInput.BaseStream.WriteAsync(request); await host.StandardInput.BaseStream.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await host.StandardOutput.BaseStream.ReadExactlyAsync(header, timeout.Token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Check(length > 0 && length < 65536, "Native host returns bounded clean length framing");
        var bytes = new byte[length]; await host.StandardOutput.BaseStream.ReadExactlyAsync(bytes, timeout.Token);
        using var response = JsonDocument.Parse(bytes);
        Check(response.RootElement.GetProperty("ok").GetBoolean() && response.RootElement.GetProperty("session").GetProperty("phase").GetString() == "Active", "Native host projects state without history");
        Check(!response.RootElement.TryGetProperty("questions", out _) && !response.RootElement.TryGetProperty("history", out _), "Browser receives no question history");
        host.StandardInput.Close(); await host.WaitForExitAsync(timeout.Token);
    }
    var scanWatch = Stopwatch.StartNew();
    var scan = await PipeClient.SendAsync(new() { Command = "scan" });
    Check(scan.Ok && store.LoadCatalog().Count == scan.Catalog.Count, "Explicit application scan cached on disk");
    Console.WriteLine($"SCAN candidates={scan.Catalog.Count}; durationSeconds={scanWatch.Elapsed.TotalSeconds:F1}");
    await Task.Delay(5000); // Let scan allocations and compilation settle before the idle sample.
    monitor.Refresh(); var cpu = monitor.TotalProcessorTime; var sample = Stopwatch.StartNew();
    await Task.Delay(15000); monitor.Refresh();
    Console.WriteLine($"IDLE workingSetMiB={monitor.WorkingSet64 / 1048576.0:F1}; singleCoreCpuPercent={(monitor.TotalProcessorTime-cpu).TotalSeconds/sample.Elapsed.TotalSeconds*100:F2}; sampleSeconds={sample.Elapsed.TotalSeconds:F1}");
    var finished = await PipeClient.SendAsync(new() { Command = "finish" });
    Check(finished.Ok && finished.Session.Phase == SessionPhase.Locked && finished.Session.History.Count == 1, "Explicit finish relocks but retains learning");
    StopOwn(monitor); monitor = null;

    // Hidden native host waiting for a length frame serves as harmless process-exit fixture.
    fixture = Process.Start(hostStart) ?? throw new Exception("Grace fixture start failed");
    var now = DateTimeOffset.UtcNow;
    var identity = new ProcessIdentity(fixture.Id, new DateTimeOffset(fixture.StartTime.ToUniversalTime()), Path.Combine(appDir, "EntLimit.NativeHost.exe").ToUpperInvariant());
    store.SaveConfig(new AppConfig { SoundEnabled = false, Apps = [new ManagedApp { Name = "Test fixture", ExecutablePath = Path.Combine(appDir, "EntLimit.NativeHost.exe") }] });
    store.SaveSession(new SessionState { Phase = SessionPhase.Active, GrantedAtUtc = now.AddMinutes(-91), StartedAtUtc = now.AddMinutes(-91), DeadlineUtc = now.AddMinutes(-1), LastObservedUtc = now.AddMinutes(-1), ActiveProcesses = [identity], History = finished.Session.History });
    monitor = StartMonitor();
    var grace = await WaitMonitor();
    Check(grace.Session.Phase == SessionPhase.Draining && grace.Session.DrainingProcesses.Count == 1, "Expired session restart preserves same live process grace");
    fixture.StandardInput.Close(); await fixture.WaitForExitAsync(); fixture.Dispose(); fixture = null;
    await Task.Delay(5500);
    var relocked = await PipeClient.SendAsync(new() { Command = "status" });
    Check(relocked.Session.Phase == SessionPhase.Locked, "Actual tracked process exit relocks automatically");
    Console.WriteLine($"{checks}/{checks} integration checks passed");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
finally { StopOwn(fixture); StopOwn(monitor); }
