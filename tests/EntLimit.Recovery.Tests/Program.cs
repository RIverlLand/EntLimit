using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EntLimit.Core;
using EntLimit.Monitor;
using Microsoft.Win32.SafeHandles;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows recovery checks only.");
if (args.Length != 3) { Console.Error.WriteLine("recovery-tests <published-app-dir> <empty-test-base> <emergency-stop.ps1>"); return 2; }
var app = Path.GetFullPath(args[0]); var root = Path.GetFullPath(args[1]); var script = Path.GetFullPath(args[2]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new Exception("A dedicated empty test base is required.");
if (!File.Exists(script)) throw new Exception("Emergency script not found.");
Directory.CreateDirectory(root);
var data = Path.Combine(root, "startup data with spaces"); Directory.CreateDirectory(data);
var foreignCwd = Path.Combine(root, "unrelated working directory"); Directory.CreateDirectory(foreignCwd);
var wrongEnvData = Path.Combine(root, "wrong inherited data path");
var instance = "recovery-" + Guid.NewGuid().ToString("N");
Environment.SetEnvironmentVariable("ENT_LIMIT_INSTANCE", instance);
Environment.SetEnvironmentVariable("ENT_LIMIT_DATA_DIR", data);
var monitorPath = Path.Combine(app, "EntLimit.Monitor.exe"); var nativePath = Path.Combine(app, "EntLimit.NativeHost.exe");
if (!File.Exists(monitorPath) || !File.Exists(nativePath)) throw new Exception("Publish both Monitor and NativeHost before integration checks.");
var processes = new Dictionary<int, Process>();
int checks = 0; int serverPid = 0; Process? suspended = null;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
Process Own(int pid)
{
    if (processes.TryGetValue(pid, out var existing)) return existing;
    var process = Process.GetProcessById(pid); _ = process.SafeHandle;
    if (!string.Equals(process.MainModule?.FileName, monitorPath, StringComparison.OrdinalIgnoreCase)) { process.Dispose(); throw new Exception("Test-owned monitor executable mismatch."); }
    processes.Add(pid, process); return process;
}
Process StartLikeLogin()
{
    var command = StartupRegistration.FormatCommand(monitorPath, data + Path.DirectorySeparatorChar);
    Environment.SetEnvironmentVariable("ENT_LIMIT_DATA_DIR", wrongEnvData);
    try
    {
        // This executes the exact Run value syntax via Windows parsing; no shell and no real registry entry.
        var process = Native.Create(monitorPath, command, foreignCwd);
        processes.Add(process.Id, process); return process;
    }
    finally { Environment.SetEnvironmentVariable("ENT_LIMIT_DATA_DIR", data); }
}
async Task<IpcResponse> Send(IpcRequest request, int connectMs = 500)
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    using var pipe = new NamedPipeClientStream(".", PipeProtocol.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(connectMs, stop.Token);
    if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new Win32Exception();
    serverPid = (int)pid; Own(serverPid);
    await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request, PipeProtocol.JsonOptions), stop.Token);
    await pipe.WriteAsync(new byte[] { 10 }, stop.Token); await pipe.FlushAsync(stop.Token);
    return JsonSerializer.Deserialize<IpcResponse>(await PipeClient.ReadLineAsync(pipe, PipeProtocol.MaxResponseBytes, stop.Token), PipeProtocol.JsonOptions)!;
}
async Task<IpcResponse> WaitStatus()
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(15))
    {
        try { return await Send(new() { Command = "status" }, 200); }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { await Task.Delay(200); }
    }
    throw new Exception("Isolated monitor did not start.");
}
bool WatchdogExists()
{
    bool found = Mutex.TryOpenExisting("Local\\" + PipeProtocol.Name + ".watchdog", out var mutex); mutex?.Dispose(); return found;
}
async Task WaitGuard(bool exists)
{
    var timer = Stopwatch.StartNew();
    while (WatchdogExists() != exists && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(200);
    Check(WatchdogExists() == exists, exists ? "Login-style monitor starts configured watchdog" : "Emergency marker makes watchdog exit");
}
async Task<JsonDocument> NativeSnapshot()
{
    var info = new ProcessStartInfo(nativePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = foreignCwd, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    using var host = Process.Start(info)!;
    try
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(9)); var input = JsonSerializer.SerializeToUtf8Bytes(new { type = "snapshot" }); var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, input.Length);
        await host.StandardInput.BaseStream.WriteAsync(header, stop.Token); await host.StandardInput.BaseStream.WriteAsync(input, stop.Token); await host.StandardInput.BaseStream.FlushAsync(stop.Token); host.StandardInput.Close();
        await host.StandardOutput.BaseStream.ReadExactlyAsync(header, stop.Token); int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > 256 * 1024) throw new Exception("Bad native frame.");
        var body = new byte[length]; await host.StandardOutput.BaseStream.ReadExactlyAsync(body, stop.Token); await host.WaitForExitAsync(stop.Token);
        return JsonDocument.Parse(body);
    }
    finally { if (!host.HasExited) host.Kill(entireProcessTree: true); }
}
async Task EmergencyStop(int pid)
{
    string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    var info = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = foreignCwd };
    foreach (var item in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-AppDirectory", app, "-DataDirectory", data, "-Instance", instance, "-ProcessIds", pid.ToString() }) info.ArgumentList.Add(item);
    using var helper = Process.Start(info)!; var output = helper.StandardOutput.ReadToEndAsync(); var error = helper.StandardError.ReadToEndAsync();
    try
    {
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(helper.ExitCode == 0, "Independent emergency script completes without responsive IPC: " + await output + await error);
    }
    finally { if (!helper.HasExited) helper.Kill(entireProcessTree: true); }
}
var store = new LocalStore(data);
store.SaveConfig(new AppConfig { GuardEnabled = true, MonitoringEnabled = true, StartupEnabled = false, SoundEnabled = false, OverlayEnabled = false, Domains = ["recovery-test.invalid"] });
Process? monitor = null;
try
{
    monitor = StartLikeLogin(); var initial = await WaitStatus();
    Check(serverPid == monitor.Id && initial.Ok && initial.Config.GuardEnabled && initial.Config.MonitoringEnabled, "Exact startup command launches monitor from a different working directory");
    Check(File.Exists(Path.Combine(data, "monitor-owner.json")) && !Directory.Exists(wrongEnvData), "Quoted --data-dir overrides inherited environment and preserves spaces/trailing slash");
    Check(!Mutex.TryOpenExisting(PipeProtocol.Name + ".Desktop", out var desktop), "Login-style launch does not start the Desktop UI"); desktop?.Dispose();
    using (var duplicate = StartLikeLogin())
    {
        await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
        Check(!monitor.HasExited && (await Send(new() { Command = "status" })).Ok && serverPid == monitor.Id, "Repeated startup command respects monitor singleton");
    }
    await WaitGuard(true);
    var exercise = initial.Exercises.First(e => e.Kind == "scenario");
    var answers = exercise.Parts.ToDictionary(p => p.Id, p => p.Kind == "choice" ? p.CorrectIndex.ToString() : p.AcceptedAnswers[0]);
    var earned = await Send(new() { Command = "grade_exercise", ExerciseId = exercise.Id, AttemptId = "recovery-earned", Answers = answers });
    Check(earned.Ok && earned.Session.Phase == SessionPhase.Ready, "Isolated fixture earns permit before emergency stop");
    var grant = earned.Session.GrantedAtUtc; var history = earned.Session.History.Count;
    var sidecarData = Path.Combine(root, "other isolated instance");
    var sidecarStart = new ProcessStartInfo(monitorPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = foreignCwd };
    sidecarStart.Environment["ENT_LIMIT_INSTANCE"] = "bystander-" + Guid.NewGuid().ToString("N");
    sidecarStart.Environment["ENT_LIMIT_DATA_DIR"] = sidecarData;
    var sidecar = Process.Start(sidecarStart) ?? throw new Exception("Cannot create same-directory isolation fixture.");
    processes.Add(sidecar.Id, sidecar); _ = sidecar.SafeHandle;
    var sidecarWait = Stopwatch.StartNew();
    while (!File.Exists(Path.Combine(sidecarData, "monitor-owner.json")) && sidecarWait.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(100);
    Check(!sidecar.HasExited && File.Exists(Path.Combine(sidecarData, "monitor-owner.json")), "Same-directory independent monitor fixture is running");
    // Suspend only the precise child process created by this harness, never entertainment apps or another instance.
    if (Native.NtSuspendProcess(monitor.SafeHandle) != 0) throw new Exception("Cannot suspend own isolated monitor.");
    suspended = monitor;
    var configPath = Path.Combine(data, "config.json"); var sessionPath = Path.Combine(data, "session.json");
    var configBefore = await File.ReadAllBytesAsync(configPath); var sessionBefore = await File.ReadAllBytesAsync(sessionPath);
    await EmergencyStop(monitor.Id);
    await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); suspended = null;
    Check(File.Exists(Path.Combine(data, "emergency-stop.flag")), "Emergency script commits standalone stop marker");
    Check(!sidecar.HasExited, "Emergency PID whitelist preserves another isolated monitor in the same app directory");
    Check((await File.ReadAllBytesAsync(configPath)).SequenceEqual(configBefore) && (await File.ReadAllBytesAsync(sessionPath)).SequenceEqual(sessionBefore), "Emergency escape preserves config and earned-session bytes");
    await WaitGuard(false); await Task.Delay(4500);
    bool absent;
    try { await Send(new() { Command = "status" }, 200); absent = false; } catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { absent = true; }
    Check(absent, "Watchdog does not restart monitor after marker even though saved guard remains enabled");
    await File.WriteAllTextAsync(configPath, "{ damaged config");
    try
    {
        using var native = await NativeSnapshot();
        Check(native.RootElement.GetProperty("ok").GetBoolean() && native.RootElement.GetProperty("config").GetProperty("domains").GetArrayLength() == 0 && !native.RootElement.GetProperty("config").GetProperty("monitoringEnabled").GetBoolean(), "Emergency marker clears browser rules even with unreadable config");
    }
    finally { await File.WriteAllBytesAsync(configPath, configBefore); }
    monitor = StartLikeLogin(); var paused = await WaitStatus();
    Check(paused.Ok && !paused.Config.MonitoringEnabled && !paused.Config.GuardEnabled && !paused.Config.StartupEnabled, "Restart with emergency marker remains paused with startup and guard off");
    Check(paused.Session.GrantedAtUtc == grant && paused.Session.History.Count == history && paused.Session.Phase == SessionPhase.Ready, "Paused restart preserves earned permit and learning history");
    Check(!WatchdogExists(), "Paused restart does not launch watchdog");
    var ordinary = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(paused.Config))!; ordinary.MonitoringEnabled = true;
    var ordinarySaved = await Send(new() { Command = "save_config", Config = ordinary });
    Check(!ordinarySaved.Config.MonitoringEnabled && File.Exists(Path.Combine(data, "emergency-stop.flag")), "Ordinary save cannot silently bypass emergency marker");
    var resumed = await Send(new() { Command = "resume_management" });
    Check(resumed.Ok && resumed.Config.MonitoringEnabled && !resumed.Config.GuardEnabled && !resumed.Config.StartupEnabled && !File.Exists(Path.Combine(data, "emergency-stop.flag")), "Explicit resume clears marker and restores monitoring without guard or startup");
    Check(resumed.Session.GrantedAtUtc == grant && resumed.Session.History.Count == history && resumed.Session.GrantedMinutes == earned.Session.GrantedMinutes, "Explicit resume preserves earned permit duration/history");
    using (var native = await NativeSnapshot())
        Check(native.RootElement.GetProperty("config").GetProperty("monitoringEnabled").GetBoolean(), "Browser sees explicitly resumed management");
    Check((await Send(new() { Command = "debug_stop" })).Ok, "Normal debug stop remains available after emergency recovery");
    await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"{checks} isolated recovery/startup checks passed; retained data: {data}"); return 0;
}
finally
{
    if (suspended is not null)
    {
        try { if (!suspended.HasExited) Native.NtResumeProcess(suspended.SafeHandle); } catch (InvalidOperationException) { }
    }
    // The independent marker keeps a guard from recreating a monitor if any test assertion fails.
    try { await File.WriteAllTextAsync(Path.Combine(data, "emergency-stop.flag"), "test cleanup"); } catch { }
    try { await Send(new() { Command = "debug_stop" }, 200); } catch { }
    foreach (var process in processes.Values)
    {
        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        process.Dispose();
    }
}

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("ntdll.dll")] internal static extern int NtSuspendProcess(SafeProcessHandle process);
    [DllImport("ntdll.dll")] internal static extern int NtResumeProcess(SafeProcessHandle process);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags; public ushort ShowWindow, Reserved2; public IntPtr ReservedPtr, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInfo processInfo);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
    internal static Process Create(string executable, string commandLine, string cwd)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(executable, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, cwd, ref startup, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
        {
            // Pin the original CreateProcess handle until the managed handle is open, even for a fast duplicate exit.
            var process = Process.GetProcessById((int)info.ProcessId); _ = process.SafeHandle; return process;
        }
        finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
    }
}
