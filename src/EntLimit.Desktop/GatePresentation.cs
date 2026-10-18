using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using EntLimit.Core;

namespace EntLimit.Desktop;

public partial class MainWindow
{
    private bool _gatePresentation;

    public void SetGatePresentation()
    {
        _gatePresentation = true;
        Navigate_Click(NavToday, new RoutedEventArgs());
        WindowState = WindowState.Maximized;
        Topmost = true;
        ApplyGateZOrder();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (_gatePresentation) ApplyGateZOrder(); }));
        Notice.Text = "当前没有可用许可，学习入口已最大化置顶；完成练习后自动取消置顶。调试停用和紧急关闭入口仍可用。";
    }

    public void ClearGatePresentation()
    {
        if (!_gatePresentation) return;
        _gatePresentation = false;
        Topmost = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x13);
    }

    private void UpdateGatePresentation(IpcResponse response)
    {
        if (response.EmergencyStopped || !response.Config.MonitoringEnabled
            || response.Session.Phase is SessionPhase.Ready or SessionPhase.Active)
            ClearGatePresentation();
    }

    private void ApplyGateZOrder()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x13);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
