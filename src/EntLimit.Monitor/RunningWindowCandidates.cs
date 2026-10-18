using System.Runtime.InteropServices;
using EntLimit.Core;

namespace EntLimit.Monitor;

internal static class RunningWindowCandidates
{
    delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr window, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);

    internal static IReadOnlyList<ProcessIdentity> Collect(CancellationToken cancellationToken)
    {
        var identities = new Dictionary<int, ProcessIdentity>();
        // No process-wide enumeration: only visible user-facing desktop windows are candidates.
        EnumWindows((window, _) =>
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0 || GetWindow(window, 4) != IntPtr.Zero) return true;
            long style = IntPtr.Size == 8 ? GetWindowLongPtr64(window, -20).ToInt64() : GetWindowLong32(window, -20);
            if ((style & 0x80) != 0) return true; // WS_EX_TOOLWINDOW
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            Native.GetWindowThreadProcessId(window, out uint process);
            if (process == 0 || process > int.MaxValue || identities.ContainsKey((int)process)) return true;
            var identity = Native.Identity((int)process);
            if (identity is not null) identities[(int)process] = identity;
            return true;
        }, IntPtr.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        return identities.Values.ToArray();
    }
}
