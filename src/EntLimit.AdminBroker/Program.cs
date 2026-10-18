using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using EntLimit.Core;
using EntLimit.Monitor;
using Microsoft.Win32.SafeHandles;

namespace EntLimit.AdminBroker;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        string? requestedSid = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--owner-sid" && i + 1 < args.Length) requestedSid = args[++i];
            else return 2;
        }
        if (!ExecutionSafety.IsElevated || sid != requestedSid) return 2;
        // An administrator helper must never load executable code from a per-user writable installation.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string app = Path.GetFullPath(AppContext.BaseDirectory);
        if (!app.StartsWith(programFiles.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(app, "installed.flag"))) return 2;
        for (var dir = new DirectoryInfo(app); dir is not null && dir.FullName.Length >= programFiles.Length; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return 2;
        using var singleton = new Mutex(true, @"Local\EntLimit.AdminBroker." + sid, out bool owner);
        if (!owner) return 0;
        ApplicationConfiguration.Initialize();
        Application.Run(new BrokerContext(app));
        return 0;
    }
}

internal sealed class BrokerContext : ApplicationContext
{
    readonly string app;
    readonly string root = DataPaths.GetRoot();
    readonly Control dispatcher = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    readonly Native.WinEvent callback;
    readonly IntPtr hook;
    bool busy;
    int failures;
    readonly DateTimeOffset expires = DateTimeOffset.UtcNow.AddHours(8);
    public BrokerContext(string applicationRoot)
    {
        app = applicationRoot;
        dispatcher.CreateControl();
        callback = (_, _, _, _, _, _, _) => { if (!dispatcher.IsDisposed) dispatcher.BeginInvoke(new Action(async () => await CheckAsync())); };
        hook = Native.SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0);
        timer.Tick += async (_, _) => await CheckAsync();
        timer.Start();
        _ = CheckAsync();
    }

    async Task CheckAsync()
    {
        if (busy) return;
        if (EntLimit.Core.EmergencyStop.IsActive(root) || DateTimeOffset.UtcNow >= expires) { ExitThread(); return; }
        busy = true;
        try
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            var identity = Native.Identity((int)pid);
            using var foreground = Process.GetProcessById((int)pid);
            if (identity is null || foreground.SessionId != Process.GetCurrentProcess().SessionId) return;
            // System shell, secure desktop and EntLimit's own processes are never administrator gate targets.
            string image = Path.GetFileNameWithoutExtension(identity.ExecutablePath);
            if (pid == Environment.ProcessId || image.StartsWith("EntLimit.", StringComparison.OrdinalIgnoreCase)
                || new[] { "explorer", "dwm", "winlogon", "csrss", "lsass", "services", "svchost", "consent", "sihost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "ctfmon" }.Contains(image, StringComparer.OrdinalIgnoreCase)) return;
            var request = new IpcRequest { Command = "admin_gate", GateProcess = identity, WindowHandle = hwnd.ToInt64() };
            var reply = await SendVerifiedAsync(request);
            failures = 0;
            if (!reply.Config.MonitoringEnabled || reply.EmergencyStopped) { ExitThread(); return; }
            if (!reply.Ok || !reply.GateDenied || EntLimit.Core.EmergencyStop.IsActive(root)) return;
            Native.GetWindowThreadProcessId(hwnd, out uint recheck);
            if (Native.GetForegroundWindow() != hwnd || recheck != pid || Native.Identity((int)pid) != identity) return;
            Native.ShowWindowAsync(hwnd, 6); // Only minimize. No kill, suspend, launch, user code or commands.
        }
        catch { if (++failures >= 3) ExitThread(); }
        finally { busy = false; }
    }

    async Task<IpcResponse> SendVerifiedAsync(IpcRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var pipe = new NamedPipeClientStream(".", PipeProtocol.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid)) throw new IOException();
        using var server = Process.GetProcessById((int)serverPid);
        _ = server.Handle;
        if (server.SessionId != Process.GetCurrentProcess().SessionId
            || !string.Equals(server.MainModule?.FileName, Path.Combine(app, "EntLimit.Monitor.exe"), StringComparison.OrdinalIgnoreCase)) throw new IOException();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, PipeProtocol.JsonOptions);
        await pipe.WriteAsync(json, timeout.Token); await pipe.WriteAsync(new byte[] { 10 }, timeout.Token);
        string response = await PipeClient.ReadLineAsync(pipe, PipeProtocol.MaxResponseBytes, timeout.Token);
        return JsonSerializer.Deserialize<IpcResponse>(response, PipeProtocol.JsonOptions) ?? throw new IOException();
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverPid);
    protected override void Dispose(bool disposing)
    {
        if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
        if (disposing) { timer.Dispose(); dispatcher.Dispose(); }
        base.Dispose(disposing);
    }
}
