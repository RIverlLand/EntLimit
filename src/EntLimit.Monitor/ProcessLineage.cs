using System.Diagnostics;
using EntLimit.Core;

namespace EntLimit.Monitor;

/// <summary>On-demand bounded ancestry evidence, never a continuous process scan.</summary>
internal sealed class ProcessLineage
{
    Dictionary<int, int> parents = [];
    readonly Stopwatch age = Stopwatch.StartNew();
    DateTimeOffset capturedAt = DateTimeOffset.MinValue;

    public IReadOnlyList<ProcessIdentity> ValidatedAncestors(ProcessIdentity candidate)
    {
        // A process born after the cached snapshot cannot reuse an older PID's edge.
        if (capturedAt == DateTimeOffset.MinValue || age.Elapsed >= TimeSpan.FromSeconds(2)
            || candidate.StartedAtUtc > capturedAt)
        {
            capturedAt = DateTimeOffset.UtcNow;
            parents = Native.ParentProcessSnapshot();
            age.Restart();
        }
        var result = new List<ProcessIdentity>();
        var visited = new HashSet<int> { candidate.Pid };
        var child = candidate;
        for (int depth = 0; depth < 12; depth++)
        {
            if (child.StartedAtUtc > capturedAt || !parents.TryGetValue(child.Pid, out int parentPid)
                || parentPid <= 0 || !visited.Add(parentPid)) break;
            var parent = Native.Identity(parentPid);
            // Parent PID reuse, exited/inaccessible parents and cycles fail closed.
            if (parent is null || parent.StartedAtUtc > child.StartedAtUtc) break;
            result.Add(parent);
            child = parent;
        }
        // The foreground process may have exited or had its PID reused during probing.
        return Native.Identity(candidate.Pid) == candidate ? result : [];
    }

    public static bool IsPotentialChild(ManagedApp app, ProcessIdentity candidate)
    {
        try
        {
            string main = Path.GetFullPath(app.ExecutablePath);
            string? directory = Path.GetDirectoryName(main);
            if (string.IsNullOrEmpty(directory) || string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetPathRoot(main)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
            string prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(candidate.ExecutablePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }
}
