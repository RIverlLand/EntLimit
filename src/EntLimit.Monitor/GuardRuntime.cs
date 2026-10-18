using System.Diagnostics;
using System.Text.Json;
using EntLimit.Core;

namespace EntLimit.Monitor;

/// <summary>Cooperative same-user recovery only. Users can disable it or stop both processes.</summary>
internal static class GuardRuntime
{
    public static string MonitorMutexName => "Local\\" + PipeProtocol.Name + ".monitor";
    public static string WatchdogMutexName => "Local\\" + PipeProtocol.Name + ".watchdog";
    public static string ControlMutexName => "Local\\" + PipeProtocol.Name + ".guard-control";
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    sealed record Owner(string Instance, ProcessIdentity Process);
    static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("Monitor path unavailable.");

    // Both roles hold their own lifetime mutex, then this short transaction mutex.
    // Probing the other role's mutex is always nonblocking; never wait for its exit
    // or pipe response while holding control. Disposal stays on the acquiring thread.
    public static IDisposable AcquireControl()
    {
        var mutex = new Mutex(false, ControlMutexName);
        bool acquired;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(300)); }
            catch (AbandonedMutexException) { acquired = true; }
        }
        catch { mutex.Dispose(); throw; }
        if (!acquired) { mutex.Dispose(); throw new TimeoutException("守护配置正在更新，请稍后重试。"); }
        return new ControlLease(mutex);
    }
    sealed class ControlLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    public static void WriteMonitorOwner(string root)
    {
        var identity = Native.Identity(Environment.ProcessId) ?? throw new InvalidOperationException("Monitor identity unavailable.");
        string path = Path.Combine(root, "monitor-owner.json"), temporary = path + ".tmp";
        Directory.CreateDirectory(root);
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Owner(PipeProtocol.Name, identity)));
        File.Move(temporary, path, overwrite: true);
    }
    public static bool MutexHeld(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
        using (mutex)
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return true;
            mutex.ReleaseMutex(); return false;
        }
    }
    public static void EnsureStarted(AppConfig config, string root)
    {
        if (EmergencyStop.IsActive(root) || !config.MonitoringEnabled || !config.GuardEnabled || MutexHeld(WatchdogMutexName)) return;
        Start(root, watchdog: true)?.Dispose();
    }
    static Process? Start(string root, bool watchdog)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
        if (watchdog) info.ArgumentList.Add("--watchdog");
        info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(root);
        return Process.Start(info);
    }
    static AppConfig? ReadConfig(string root)
    {
        string path = Path.Combine(root, "config.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<AppConfig>(stream, Json);
    }
    static bool IsExpectedOwner(string root)
    {
        try
        {
            var owner = JsonSerializer.Deserialize<Owner>(File.ReadAllText(Path.Combine(root, "monitor-owner.json")), Json);
            return owner is not null && owner.Instance == PipeProtocol.Name
                && string.Equals(Path.GetFullPath(owner.Process.ExecutablePath), Path.GetFullPath(Executable), StringComparison.OrdinalIgnoreCase)
                && Native.Identity(owner.Process.Pid) == owner.Process;
        }
        catch { return false; }
    }
    public static void RunWatchdog(string root)
    {
        if (EmergencyStop.IsActive(root)) return;
        using var mutex = new Mutex(true, WatchdogMutexName, out bool created);
        if (!created) return;
        var backoff = Stopwatch.StartNew();
        int failureDelay = 2;
        while (true)
        {
            try
            {
                // Serialize read/check/start with disabling guard. A snapshot read
                // before debug_stop must never restart the monitor after its exit.
                using var control = AcquireControl();
                if (EmergencyStop.IsActive(root)) return;
                var config = ReadConfig(root);
                if (config is null || !config.MonitoringEnabled || !config.GuardEnabled) return;
                // Mutex presence, rather than an IPC timeout, protects scans and a busy UI.
                // A conflicting instance never causes duplicate restarts or termination.
                if (MutexHeld(MonitorMutexName))
                {
                    if (IsExpectedOwner(root)) { failureDelay = 2; backoff.Restart(); }
                }
                else if (backoff.Elapsed >= TimeSpan.FromSeconds(failureDelay))
                {
                    if (EmergencyStop.IsActive(root)) return;
                    Start(root, watchdog: false)?.Dispose();
                    failureDelay = Math.Min(60, failureDelay * 2); backoff.Restart();
                }
            }
            catch { failureDelay = Math.Min(60, failureDelay * 2); backoff.Restart(); }
            Thread.Sleep(2000);
        }
    }
}
