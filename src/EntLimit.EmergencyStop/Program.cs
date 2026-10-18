using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using EntLimit.Core;
using Microsoft.Win32;

namespace EntLimit.EmergencyRecovery;

internal static class Program
{
    static readonly string[] Names = ["EntLimit.Monitor", "EntLimit.Desktop", "EntLimit.AdminBroker"];
    [STAThread] static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool quiet = args.Contains("--quiet");
        bool retry = args.Contains("--elevated-retry");
        bool test = args.Contains("--test") && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ENT_LIMIT_INSTANCE"));
        if (args.Any(a => a is not ("--quiet" or "--elevated-retry" or "--verify" or "--test"))) return 2;
        if (args.Contains("--test") && !test) return 2;
        string app = Path.GetFullPath(AppContext.BaseDirectory);
        // This executable is intentionally installation-local; no caller-supplied PID/path/command.
        if (!File.Exists(Path.Combine(app, "EntLimit.Monitor.exe")))
        {
            if (!quiet) MessageBox.Show("请从 EntLimit 安装目录或开始菜单运行紧急关闭程序。", "EntLimit 紧急关闭");
            return 2;
        }
        string root = DataPaths.GetRoot();
        if (args.Contains("--verify"))
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "emergency-verify.json"), JsonSerializer.Serialize(new { Standalone = true, Elevated = ExecutionSafety.IsElevated }));
            return 0;
        }
        try
        {
            Directory.CreateDirectory(root);
            using var latch = new FileStream(Path.Combine(root, EntLimit.Core.EmergencyStop.FileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            latch.Flush(true);
        }
        catch
        {
            if (!quiet) MessageBox.Show("无法保存紧急停用标记。请检查本用户数据目录权限；未声称已关闭应用。", "EntLimit 紧急关闭");
            return 1;
        }
        if (!test) RemoveOwnStartup(app, root);
        int count = 0; bool denied = false;
        int session = Process.GetCurrentProcess().SessionId;
        HashSet<int>? testPids = null;
        if (test)
        {
            // Test harness must name its own processes explicitly; production has no PID input.
            testPids = (Environment.GetEnvironmentVariable("ENT_LIMIT_TEST_PIDS") ?? "").Split(',').Where(x => int.TryParse(x, out _)).Select(int.Parse).ToHashSet();
            if (testPids.Count == 0)
            {
                if (!quiet) MessageBox.Show("隔离测试缺少进程白名单；紧急停用标记已保存，但尚未结束进程。", "EntLimit 紧急关闭");
                return 2;
            }
        }
        for (int round = 0; round < 3; round++)
        {
            foreach (string name in Names)
            foreach (var process in Process.GetProcessesByName(name))
            using (process)
            {
                try
                {
                    if (process.SessionId != session || (testPids is not null && !testPids.Contains(process.Id))) continue;
                    _ = process.Handle; // Pin before revalidating PID identity.
                    string? image = process.MainModule?.FileName;
                    if (!string.Equals(image, Path.Combine(app, name + ".exe"), StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill(); if (process.WaitForExit(3000)) count++; else denied = true;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { denied = true; }
            }
            Thread.Sleep(250);
        }
        // A cooperative elevated broker exits on the latch even when OpenProcess is denied.
        bool protectedInstall = app.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(app, "installed.flag"));
        if (denied && protectedInstall && !ExecutionSafety.IsElevated && !retry && !quiet && !test)
        {
            if (MessageBox.Show("紧急停用已生效。部分高权限 EntLimit 进程无法直接结束，是否通过 Windows 管理员确认重试？", "EntLimit 紧急关闭", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = "--elevated-retry", WorkingDirectory = app }); return 0; }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        if (!quiet) MessageBox.Show($"紧急停用标记已保留，已结束 {count} 个本安装 EntLimit 进程。\n学习记录、历史答案、API 配置和游戏进程未改动。\n再次打开后，须主动恢复管控。" + (denied ? "\n部分进程未能直接结束；管理员辅助会自行检查停用标记。" : ""), "EntLimit 紧急关闭");
        return denied ? 1 : 0;
    }

    static void RemoveOwnStartup(string app, string root)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            string expected = Quote(Path.Combine(app, "EntLimit.Monitor.exe")) + " --data-dir " + Quote(root);
            if (string.Equals(key?.GetValue("EntLimit") as string, expected, StringComparison.Ordinal)) key!.DeleteValue("EntLimit", false);
        }
        catch { /* Latch remains effective without registry access. */ }
    }
    static string Quote(string value) => "\"" + value + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
}
