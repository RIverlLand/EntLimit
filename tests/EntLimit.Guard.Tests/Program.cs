using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using EntLimit.Core;
using EntLimit.Monitor;
using Microsoft.Win32.SafeHandles;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows guard integration tests only.");
if (args.Length != 2) { Console.Error.WriteLine("guard-tests <published-app-dir> <empty-test-data-dir>"); return 2; }
var app = Path.GetFullPath(args[0]);
var data = Path.GetFullPath(args[1]);
if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any()) throw new Exception("Only an empty dedicated test data directory is permitted.");
Directory.CreateDirectory(data);
var instance = "guard-" + Guid.NewGuid().ToString("N");
Environment.SetEnvironmentVariable("ENT_LIMIT_INSTANCE", instance);
Environment.SetEnvironmentVariable("ENT_LIMIT_DATA_DIR", data);
var monitorPath = Path.Combine(app, "EntLimit.Monitor.exe");
var nativePath = Path.Combine(app, "EntLimit.NativeHost.exe");
if (!File.Exists(monitorPath) || !File.Exists(nativePath)) throw new Exception("Publish both Monitor and NativeHost first.");
var processes = new Dictionary<int, Process>();
int checks = 0;
int serverPid = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
void TestStartupRegistration()
{
    var previous = Environment.GetEnvironmentVariable("ENT_LIMIT_INSTANCE");
    try
    {
        // Every registration below gets an explicit fake. No production registry accessor is instantiated.
        Environment.SetEnvironmentVariable("ENT_LIMIT_INSTANCE", null);
        const string executable = @"C:\Program Files\EntLimit\EntLimit.Monitor.exe";
        const string dataRoot = @"C:\Study Data\EntLimit\";
        const string expected = "\"C:\\Program Files\\EntLimit\\EntLimit.Monitor.exe\" --data-dir \"C:\\Study Data\\EntLimit\\\\\"";
        Check(StartupRegistration.FormatCommand(executable, dataRoot) == expected, "Startup command quotes spaces and doubles trailing directory slash");
        Check(StartupRegistration.FormatCommand(executable, @"C:\").EndsWith("\"C:\\\\\"", StringComparison.Ordinal), "Startup command preserves disk-root trailing slash");
        var invalidRejected = false;
        try { StartupRegistration.FormatCommand(executable, "C:\\invalid\"path"); } catch (ArgumentException) { invalidRejected = true; }
        Check(invalidRejected, "Startup command rejects embedded quote");
        var fake = new FakeStartupValue();
        var registration = new StartupRegistration(executable, dataRoot, fake);
        registration.Apply(true);
        Check(fake.Value == expected && fake.Writes == 1, "Startup enable writes own exact command to fake value");
        Check(registration.Capture() == expected, "Startup capture retains exact prior command");
        registration.Apply(false);
        Check(fake.Value is null && fake.Writes == 2, "Startup disable removes own exact command");
        const string otherInstall = "\"D:\\Other Install\\EntLimit.Monitor.exe\"";
        fake.Value = otherInstall; int writes = fake.Writes;
        registration.Apply(false);
        Check(fake.Value == otherInstall && fake.Writes == writes, "Startup disable preserves another installation's value");
        bool conflict = false;
        try { registration.Apply(true); } catch (InvalidOperationException) { conflict = true; }
        Check(conflict && fake.Value == otherInstall && fake.Writes == writes, "Startup conflict refuses overwrite");
        var captured = registration.Capture(); fake.Value = null; registration.Restore(captured);
        Check(fake.Value == otherInstall, "Startup restore recovers captured prior value");
        registration.Restore(null);
        Check(fake.Value is null, "Startup restore handles absent prior value");
        Environment.SetEnvironmentVariable("ENT_LIMIT_INSTANCE", "isolated-fake-test");
        var isolatedFake = new FakeStartupValue { Value = otherInstall };
        var isolated = new StartupRegistration(executable, dataRoot, isolatedFake);
        bool refused = false;
        try { isolated.Apply(true); } catch (InvalidOperationException) { refused = true; }
        isolated.Apply(false); isolated.Restore(null);
        Check(refused && isolated.Capture() is null && isolatedFake.Reads == 0 && isolatedFake.Writes == 0 && isolatedFake.Value == otherInstall, "Isolated registration never reads or writes even the injected fake");
    }
    finally { Environment.SetEnvironmentVariable("ENT_LIMIT_INSTANCE", previous); }
}
TestStartupRegistration();
ProcessStartInfo StartInfo(string path) => new(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = app };
Process Own(int pid)
{
    if (processes.TryGetValue(pid, out var existing)) return existing;
    var process = Process.GetProcessById(pid);
    if (!string.Equals(process.MainModule?.FileName, monitorPath, StringComparison.OrdinalIgnoreCase)) { process.Dispose(); throw new Exception("Isolated pipe process did not match test Monitor executable."); }
    _ = process.StartTime; _ = process.SafeHandle;
    processes.Add(pid, process);
    return process;
}
async Task<IpcResponse> Send(IpcRequest request, int connectMs = 1000)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    using var pipe = new NamedPipeClientStream(".", PipeProtocol.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(connectMs, deadline.Token);
    if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new Exception("Cannot obtain isolated monitor identity.");
    serverPid = checked((int)pid); Own(serverPid);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(request, PipeProtocol.JsonOptions);
    await pipe.WriteAsync(bytes, deadline.Token); await pipe.WriteAsync(new byte[] { 10 }, deadline.Token); await pipe.FlushAsync(deadline.Token);
    return JsonSerializer.Deserialize<IpcResponse>(await PipeClient.ReadLineAsync(pipe, PipeProtocol.MaxResponseBytes, deadline.Token), PipeProtocol.JsonOptions) ?? throw new Exception("Empty reply.");
}
async Task<IpcResponse> WaitStatus(int? differentFrom = null)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(18))
    {
        try { var status = await Send(new() { Command = "status" }, 250); if (differentFrom is null || serverPid != differentFrom) return status; }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException) { }
        await Task.Delay(200);
    }
    throw new Exception("Isolated monitor did not become available/recover in 18 seconds.");
}
async Task<bool> Unavailable()
{
    try { await Send(new() { Command = "status" }, 250); return false; }
    catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException) { return true; }
}
AppConfig Clone(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
async Task<JsonDocument> NativeSnapshot()
{
    var start = StartInfo(nativePath); start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
    using var host = Process.Start(start) ?? throw new Exception("Cannot start isolated NativeHost.");
    try
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { type = "snapshot" }); var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StandardInput.BaseStream.WriteAsync(header, deadline.Token); await host.StandardInput.BaseStream.WriteAsync(body, deadline.Token); await host.StandardInput.BaseStream.FlushAsync(deadline.Token); host.StandardInput.Close();
        await host.StandardOutput.BaseStream.ReadExactlyAsync(header, deadline.Token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header); if (length is <= 0 or > 256 * 1024) throw new Exception("Invalid NativeHost reply frame.");
        var output = new byte[length]; await host.StandardOutput.BaseStream.ReadExactlyAsync(output, deadline.Token);
        await host.WaitForExitAsync(deadline.Token);
        Check(host.ExitCode == 0, "NativeHost exits normally after isolated snapshot");
        return JsonDocument.Parse(output);
    }
    finally { if (!host.HasExited) host.Kill(entireProcessTree: true); }
}
var initial = Process.Start(StartInfo(monitorPath)) ?? throw new Exception("Cannot start test monitor.");
processes.Add(initial.Id, initial);
try
{
    var status = await WaitStatus();
    Check(serverPid == initial.Id && !status.Config.StartupEnabled && !status.Config.GuardEnabled && status.Config.MonitoringEnabled, "Default flags: startup and guard off, monitoring on; own isolated monitor");
    Check(status.Config.Apps.Count == 0 && status.Config.Domains.Count == 0 && status.Session.Phase == SessionPhase.Locked, "Fresh isolated store has no real targets or permits");
    var startup = Clone(status.Config); startup.StartupEnabled = true;
    var rejected = await Send(new() { Command = "save_config", Config = startup });
    Check(!rejected.Ok && !rejected.Config.StartupEnabled, "Isolated instance refuses startup registration");
    var exercise = status.Exercises.First(e => e.Kind == "scenario");
    var answers = exercise.Parts.ToDictionary(p => p.Id, p => p.Kind == "choice" ? p.CorrectIndex.ToString() : p.AcceptedAnswers[0]);
    var earned = await Send(new() { Command = "grade_exercise", ExerciseId = exercise.Id, Answers = answers, AttemptId = "guard-integration-permit" });
    Check(earned.Ok && earned.Session.Phase == SessionPhase.Ready, "Isolated scenario grants a real test permit");
    var grantTime = earned.Session.GrantedAtUtc; var historyCount = earned.Session.History.Count;
    var guardedConfig = Clone(earned.Config); guardedConfig.GuardEnabled = true; guardedConfig.UnlockMinutes = 180; guardedConfig.Domains = ["guard-test.invalid"];
    var guarded = await Send(new() { Command = "save_config", Config = guardedConfig });
    Check(guarded.Ok && guarded.Config.GuardEnabled && guarded.Session.GrantedAtUtc == grantTime && guarded.Session.History.Count == historyCount && guarded.Session.GrantedMinutes == earned.Session.GrantedMinutes, "Enabling guard and changing future duration preserves earned permit and history");
    await Task.Delay(2000);
    var denied = await Send(new() { Command = "shutdown" });
    Check(!denied.Ok && !initial.HasExited, "Ordinary shutdown rejected while guard is enabled");
    // Kill only the exact Process returned by this test's own Process.Start; never enumerate by name.
    initial.Kill(); await initial.WaitForExitAsync();
    var restored = await WaitStatus(initial.Id); var restoredPid = serverPid; var recovered = Own(restoredPid);
    Check(restored.Ok && restored.Config.GuardEnabled && restored.Session.GrantedAtUtc == grantTime && restored.Session.History.Count == historyCount, "Watchdog restores exact killed monitor and persisted permit");
    var guardPid = Native.ParentPid(restoredPid); var guard = Own(guardPid);
    Check(guardPid != initial.Id && guardPid != restoredPid && !guard.HasExited, "Recovered monitor has a live owned watchdog parent");
    var disabled = await Send(new() { Command = "debug_disable" });
    Check(disabled.Ok && !disabled.Config.StartupEnabled && !disabled.Config.GuardEnabled && !disabled.Config.MonitoringEnabled, "debug_disable clears all three switches");
    Check(await guard.WaitForExitAsync(TimeSpan.FromSeconds(8)), "debug_disable exits watchdog");
    Check(!recovered.HasExited && (await Send(new() { Command = "status" })).Ok, "debug_disable keeps monitor IPC available");
    Check(disabled.Session.GrantedAtUtc == grantTime && disabled.Session.History.Count == historyCount, "Pause preserves permit and learning history");
    using (var native = await NativeSnapshot())
        Check(native.RootElement.GetProperty("ok").GetBoolean() && native.RootElement.GetProperty("config").GetProperty("domains").GetArrayLength() == 0, "Paused live monitor publishes empty browser domain rules");
    var active = Clone(disabled.Config); active.MonitoringEnabled = true; active.GuardEnabled = true;
    var reenabled = await Send(new() { Command = "save_config", Config = active });
    Check(reenabled.Ok && reenabled.Config.GuardEnabled && reenabled.Config.MonitoringEnabled, "Guard can be explicitly re-enabled after debugging pause");
    var activeBytes = await File.ReadAllBytesAsync(Path.Combine(data, "config.json"));
    await Task.Delay(1200);
    var stopped = await Send(new() { Command = "debug_stop" });
    Check(stopped.Ok && !stopped.Config.GuardEnabled && !stopped.Config.StartupEnabled && !stopped.Config.MonitoringEnabled, "debug_stop persists all switches off before exit");
    Check(await recovered.WaitForExitAsync(TimeSpan.FromSeconds(8)), "debug_stop exits monitor");
    await Task.Delay(6500);
    Check(await Unavailable(), "debug_stop prevents watchdog restart");
    Check(!Mutex.TryOpenExisting("Local\\" + PipeProtocol.Name + ".watchdog", out var stoppedGuard), "debug_stop exits watchdog singleton"); stoppedGuard?.Dispose();
    var configFile = Path.Combine(data, "config.json"); var pausedBytes = await File.ReadAllBytesAsync(configFile);
    using (var fallback = await NativeSnapshot())
        Check(fallback.RootElement.GetProperty("ok").GetBoolean() && fallback.RootElement.GetProperty("config").GetProperty("domains").GetArrayLength() == 0, "Stopped paused config fallback clears browser rules");
    Check((await File.ReadAllBytesAsync(configFile)).SequenceEqual(pausedBytes), "NativeHost paused fallback never writes configuration");
    // Synthetic offline test fixture only, after all isolated monitor/guard processes exited.
    await File.WriteAllBytesAsync(configFile, activeBytes);
    try
    {
        using var unavailable = await NativeSnapshot();
        Check(!unavailable.RootElement.GetProperty("ok").GetBoolean(), "Unavailable active monitor does not claim a paused fallback");
    }
    finally { await File.WriteAllBytesAsync(configFile, pausedBytes); }
    Console.WriteLine($"{checks} isolated guard IPC checks passed; data: {data}");
    return 0;
}
finally
{
    try { await Send(new() { Command = "debug_stop" }, 250); }
    catch
    {
        // Emergency cleanup is restricted to this generated test store; tell any owned guard to stop.
        try
        {
            var file = Path.Combine(data, "config.json");
            var config = File.Exists(file) ? JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(file)) ?? new() : new AppConfig();
            config.GuardEnabled = false; config.StartupEnabled = false; config.MonitoringEnabled = false;
            var temporary = file + ".test-cleanup";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(config)); File.Move(temporary, file, true);
        }
        catch (Exception ex) { Console.Error.WriteLine("Isolated test cleanup config failed: " + ex.Message); }
    }
    // Only exact handles already verified from the isolated pipe or its verified watchdog parent.
    foreach (var process in processes.Values.Reverse())
    {
        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }
}

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Entry
    {
        public uint Size, Usage, ProcessId; public UIntPtr Heap; public uint ModuleId, Threads, ParentId; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    internal static int ParentPid(int pid)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0); if (snapshot == new IntPtr(-1)) throw new Exception("Cannot inspect test monitor parent.");
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Exe = "" };
            if (Process32FirstW(snapshot, ref entry)) do { if (entry.ProcessId == pid) return (int)entry.ParentId; } while (Process32NextW(snapshot, ref entry));
            throw new Exception("Cannot find isolated test monitor parent.");
        }
        finally { CloseHandle(snapshot); }
    }
}
static class ProcessWait
{
    public static async Task<bool> WaitForExitAsync(this Process process, TimeSpan timeout)
    {
        try { await process.WaitForExitAsync().WaitAsync(timeout); return true; } catch (TimeoutException) { return false; }
    }
}



sealed class FakeStartupValue : IStartupValue
{
    public string? Value { get; set; }
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public string? Read() { Reads++; return Value; }
    public void Write(string? command) { Writes++; Value = command; }
}
