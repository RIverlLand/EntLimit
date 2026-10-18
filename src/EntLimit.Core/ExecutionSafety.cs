using System.Security.Principal;

namespace EntLimit.Core;

public static class ExecutionSafety
{
    public static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return true; }
        }
    }

    public static void EnsurePracticeAllowed()
    {
        if (IsElevated) throw new InvalidOperationException("为避免以管理员权限执行练习代码，请普通方式启动 EntLimit。管理员辅助进程仅用于窗口管控。");
    }
}
