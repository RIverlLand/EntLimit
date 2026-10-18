using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using EntLimit.Core;

if (args.Length != 2 || !OperatingSystem.IsWindows())
{ Console.Error.WriteLine("Usage: window-tests <isolated-publication-folder> <new-data-folder> (interactive Windows session)"); return 2; }
// Set before the first PipeProtocol access. The monitor mutex and IPC must both be isolated.
var instance = "WindowTest" + Guid.NewGuid().ToString("N");
Environment.SetEnvironmentVariable("ENT_LIMIT_INSTANCE", instance);
var appDir = Path.GetFullPath(args[0]);
var dataDir = Path.GetFullPath(args[1]);
var fixtureExe = Path.Combine(appDir, "WindowFixture.exe");
var monitorExe = Path.Combine(appDir, "EntLimit.Monitor.exe");
if (!File.Exists(fixtureExe) || !File.Exists(monitorExe) || File.Exists(Path.Combine(appDir, "EntLimit.Desktop.exe")))
{ Console.Error.WriteLine("Use a dedicated publication with Monitor and WindowFixture, without Desktop (no gate UI child launch)."); return 2; }
if (Directory.Exists(dataDir) && Directory.EnumerateFileSystemEntries(dataDir).Any())
{ Console.Error.WriteLine("Data directory must be new or empty."); return 2; }
Directory.CreateDirectory(dataDir);
var store = new LocalStore(dataDir);
Process? monitor = null, fixture = null, other = null;
var owned = new Dictionary<int, string>();
var fixturePipe = instance + "A";
var otherPipe = instance + "B";
IntPtr window = IntPtr.Zero, otherWindow = IntPtr.Zero;
int checks = 0;
void Check(bool value, string label)
{ if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
Process Start(string executable, string? argument = null)
{
    var psi = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = appDir };
    if (argument is not null) psi.ArgumentList.Add(argument);
    psi.Environment["ENT_LIMIT_INSTANCE"] = instance;
    psi.Environment["ENT_LIMIT_DATA_DIR"] = dataDir;
    var process = Process.Start(psi) ?? throw new Exception("Owned process failed to start");
    owned.Add(process.Id, executable); return process;
}
async Task<IntPtr> FixtureCommand(string suffix, string command)
{
    using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await using var pipe = new NamedPipeClientStream(".", "EntLimit.WindowFixture." + suffix, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(ct.Token);
    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    using var reader = new StreamReader(pipe, leaveOpen: true);
    await writer.WriteLineAsync(command.AsMemory(), ct.Token);
    return new IntPtr(long.Parse(await reader.ReadLineAsync(ct.Token) ?? throw new IOException("Missing fixture reply")));
}
async Task Wait(Func<Task<bool>> predicate, string label, int milliseconds = 6500)
{
    var elapsed = Stopwatch.StartNew();
    while (elapsed.ElapsedMilliseconds < milliseconds)
    { if (await predicate()) return; await Task.Delay(100); }
    throw new Exception("Timed out: " + label);
}
async Task<IpcResponse> Status() => await PipeClient.SendAsync(new() { Command = "status" });
async Task WaitMonitor()
{
    await Wait(async () =>
    {
        if (monitor?.HasExited == true) throw new Exception("Isolated monitor exited");
        try { return (await Status()).Ok; } catch (TimeoutException) { return false; }
    }, "monitor startup");
}
async Task Activate(string suffix, Process process)
{
    // Allow only the child fixture's own foreground request, never activate user windows.
    Native.AllowSetForegroundWindow((uint)process.Id);
    await FixtureCommand(suffix, "activate");
    await Wait(() => Task.FromResult(Native.ForegroundPid() == process.Id), "fixture foreground activation", 2000);
}
async Task StopMonitor()
{
    if (monitor is null || monitor.HasExited) return;
    Check((await PipeClient.SendAsync(new() { Command = "shutdown" })).Ok, "Isolated monitor acknowledges graceful shutdown");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await monitor.WaitForExitAsync(timeout.Token);
    monitor.Dispose(); monitor = null;
}
void Cleanup(Process? process)
{
    if (process is null) return;
    try
    {
        if (!process.HasExited)
        {
            if (!owned.TryGetValue(process.Id, out var expected)
                || !string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(expected).StartsWith(appDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Refusing unexpected process cleanup");
            process.Kill(entireProcessTree: false); process.WaitForExit(3000);
        }
    }
    finally { process.Dispose(); }
}
try
{
    monitor = Start(monitorExe); await WaitMonitor();
    fixture = Start(fixtureExe, fixturePipe);
    window = await FixtureCommand(fixturePipe, "status");
    await Activate(fixturePipe, fixture);
    Native.GetWindowThreadProcessId(window, out var pid);
    Check(pid == fixture.Id && !Native.IsIconic(window), "Fixture is the real visible owned window");
    var config = new AppConfig { SoundEnabled = false, OverlayEnabled = false,
        Apps = [new ManagedApp { Name = "Regression fixture", ExecutablePath = fixtureExe }] };
    Check((await PipeClient.SendAsync(new() { Command = "save_config", Config = config })).Ok, "Save configuration accepted while fixture foreground");
    await Wait(() => Task.FromResult(Native.IsIconic(window)), "immediate gate", 2000);
    Check((await Status()).Session.Phase == SessionPhase.Locked, "Foreground managed window is minimized immediately on locked save_config");
    var q = (await Status()).Questions.First(x => x.Mode == "light");
    var grant = await PipeClient.SendAsync(new() { Command = "complete", QuestionId = q.Id,
        AnswerIndex = q.CorrectIndex, AttemptId = instance });
    Check(grant.Ok && grant.Session.Phase == SessionPhase.Ready, "Light exercise grants permit");
    await Activate(fixturePipe, fixture);
    await Wait(async () => (await Status()).Session.Phase == SessionPhase.Active, "foreground use begins permit");
    var active = (await Status()).Session;
    Check(!Native.IsIconic(window) && active.DeadlineUtc - active.StartedAtUtc == TimeSpan.FromMinutes(90), "Restored fixture stays visible and receives exactly 90 minutes");
    Check((await PipeClient.SendAsync(new() { Command = "finish" })).Ok, "Explicit finish accepted");
    await Wait(() => Task.FromResult(Native.IsIconic(window)), "finish gate", 2000);
    Check((await Status()).Session.History.Count == 1, "Finish gates current window and preserves learning");

    // Both fixtures share an executable, so disable the app during focus churn, then
    // use a distinct copied fixture executable for the unmanaged foreground window.
    config.Apps[0].Enabled = false;
    await PipeClient.SendAsync(new() { Command = "save_config", Config = config });
    var otherExe = Path.Combine(appDir, "OtherWindowFixture.exe");
    if (!File.Exists(otherExe)) throw new Exception("Publish a second fixture copy named OtherWindowFixture.exe");
    other = Start(otherExe, otherPipe); otherWindow = await FixtureCommand(otherPipe, "status");
    for (int i = 0; i < 6; i++)
    { await Activate(fixturePipe, fixture); await Activate(otherPipe, other); }
    config.Apps[0].Enabled = true;
    await PipeClient.SendAsync(new() { Command = "save_config", Config = config });
    await Task.Delay(1000);
    Check(!Native.IsIconic(window) && !Native.IsIconic(otherWindow) && Native.ForegroundPid() == other.Id,
        "Focus churn and queued stale events do not gate a nonforeground target or unmanaged current window");
    await FixtureCommand(otherPipe, "close");
    using (var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3))) await other.WaitForExitAsync(closeTimeout.Token);
    other.Dispose(); other = null;
    await StopMonitor();

    var now = DateTimeOffset.UtcNow;
    config.OverlayEnabled = true; // Exercise the actual nonactivating reminder, not just silent expiry.
    store.SaveConfig(config);
    store.SaveSession(new SessionState { Phase = SessionPhase.Active, GrantedMinutes = 90,
        GrantedAtUtc = now.AddMinutes(-89), StartedAtUtc = now.AddMinutes(-89), DeadlineUtc = now.AddSeconds(3),
        LastObservedUtc = now, History = active.History,
        ActiveProcesses = [new ProcessIdentity(fixture.Id, new DateTimeOffset(fixture.StartTime.ToUniversalTime()), fixtureExe.ToUpperInvariant())] });
    await Activate(fixturePipe, fixture);
    monitor = Start(monitorExe); await WaitMonitor();
    await Wait(async () => (await Status()).Session.Phase == SessionPhase.Draining, "expiry drain", 6500);
    var draining = (await Status()).Session;
    Check(draining.ExpiryDelivered && draining.DrainingProcesses.Count == 1 && !Native.IsIconic(window)
        && Native.ForegroundPid() == fixture.Id, "Enabled expiry overlay preserves visible foreground fixture without stealing focus");
    await FixtureCommand(fixturePipe, "close");
    using (var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3))) await fixture.WaitForExitAsync(closeTimeout.Token);
    fixture.Dispose(); fixture = null;
    await Wait(async () => (await Status()).Session.Phase == SessionPhase.Locked, "exit relock");
    Check((await Status()).Session.History.Count == 1, "Fixture exit automatically relocks and retains learning");
    await StopMonitor();
    Console.WriteLine($"{checks}/{checks} actual window regression checks passed"); return 0;
}
catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
finally { Cleanup(other); Cleanup(fixture); Cleanup(monitor); }

internal static class Native
{
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(uint pid);
    internal static int ForegroundPid() { GetWindowThreadProcessId(GetForegroundWindow(), out var pid); return (int)pid; }
}
