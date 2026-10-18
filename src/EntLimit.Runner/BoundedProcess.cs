using System.Diagnostics;
using System.Text;

namespace EntLimit.Runner;

public sealed record ProcessOutcome(int ExitCode, string Output, string Error, bool TimedOut, bool Cancelled);
public static class BoundedProcess
{
    public static async Task<ProcessOutcome> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, string? input, int seconds, int outputLimit, CancellationToken token, uint activeProcesses = 1, int memoryMiB = 512)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory, RedirectStandardInput = input is not null, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" }) start.Environment.Remove(name);
        using var process = Process.Start(start) ?? throw new IOException("Process did not start.");
        WindowsJob? assigned;
        try { assigned = WindowsJob.Attach(process, activeProcesses, memoryMiB); }
        catch { Kill(process); throw; }
        using var job = assigned;
        var output = Read(process.StandardOutput, outputLimit); var error = Read(process.StandardError, outputLimit);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        bool timeout = false, cancelled = false;
        try
        {
            if (input is not null) { await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token).ConfigureAwait(false); process.StandardInput.Close(); }
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timeout = !token.IsCancellationRequested; cancelled = token.IsCancellationRequested;
            Kill(process); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        finally { if (!process.HasExited) Kill(process); }
        job?.Dispose(); // Close any inherited child pipes before awaiting the drain.
        var streams = await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        return new(process.ExitCode, streams[0], streams[1], timeout, cancelled);
    }
    static void Kill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
    static async Task<string> Read(StreamReader reader, int limit)
    {
        var builder = new StringBuilder(); var buffer = new char[4096]; int count; bool truncated = false;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            int remaining = Math.Max(0, limit - builder.Length); builder.Append(buffer, 0, Math.Min(count, remaining)); if (count > remaining) truncated = true;
        }
        if (truncated) builder.Append("\n[输出已截断]"); return builder.ToString();
    }
}
