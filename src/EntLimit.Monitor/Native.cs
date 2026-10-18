using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EntLimit.Core;

namespace EntLimit.Monitor;

internal static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WinEvent(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    internal delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableName;
    }
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr handle, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insert, int x, int y, int width, int height, uint flags);

    internal static Dictionary<int, int> ParentProcessSnapshot()
    {
        var parents = new Dictionary<int, int>();
        IntPtr handle = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS: no heaps/modules.
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero) return parents;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExecutableName = "" };
            if (!Process32First(handle, ref entry)) return parents;
            do
            {
                if (entry.ProcessId <= int.MaxValue && entry.ParentProcessId <= int.MaxValue)
                    parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            } while (Process32Next(handle, ref entry));
            return parents;
        }
        finally { CloseHandle(handle); }
    }

    internal static ProcessIdentity? Identity(int pid)
    {
        IntPtr handle = OpenProcess(0x1000, false, pid); if (handle == IntPtr.Zero) return null;
        try
        {
            uint length = 32768; var path = new StringBuilder((int)length);
            if (!QueryFullProcessImageName(handle, 0, path, ref length)) return null;
            if (!GetProcessTimes(handle, out long created, out _, out _, out _)) return null;
            return new ProcessIdentity(pid, new DateTimeOffset(DateTime.FromFileTimeUtc(created)), Path.GetFullPath(path.ToString()).ToUpperInvariant());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
        finally { CloseHandle(handle); }
    }
}

internal sealed class ReminderForm : Form
{
    readonly System.Windows.Forms.Timer lifetime = new() { Interval = 8000 };
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x80000; return cp; }
    }
    public ReminderForm(string message)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        BackColor = Color.FromArgb(30, 38, 50); ForeColor = Color.White; Opacity = .94;
        Size = new Size(440, 80); StartPosition = FormStartPosition.Manual;
        var screen = Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea;
        Location = new Point(screen.Right - Width - 24, screen.Top + 24);
        Controls.Add(new Label { Text = message, Dock = DockStyle.Fill, Padding = new Padding(18), Font = new Font("Microsoft YaHei UI", 10), TextAlign = ContentAlignment.MiddleLeft });
        lifetime.Tick += (_, _) => Close();
    }
    protected override void OnShown(EventArgs e)
    { base.OnShown(e); Native.SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height, 0x10 | 0x40); lifetime.Start(); }
    protected override void Dispose(bool disposing) { if (disposing) lifetime.Dispose(); base.Dispose(disposing); }
}
