using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EntLimit.Desktop;

internal sealed record WindowMaterialStatus(
    bool Applied,
    string Mode,
    string Reason,
    bool? CompositionEnabled = null,
    bool? TransparencyEnabled = null,
    int? NativeResult = null);

internal static class WindowMaterial
{
    // Supported, per-window DWM attributes only. Never changes display modes,
    // global transparency, DPI settings, registry values, or other windows.
    private static readonly ConditionalWeakTable<Window, MaterialBinding> Bindings = new();
    public static WindowMaterialStatus LastStatus { get; private set; } = new(false, "Solid", "尚未初始化窗口材质。");
    public static event Action<WindowMaterialStatus>? StatusChanged;

    public static void TryApply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        Bindings.GetValue(window, static owner => new MaterialBinding(owner)).Apply();
    }

    public static WindowMaterialStatus GetStatus(Window window) => Bindings.TryGetValue(window, out var binding) ? binding.Status : new(false, "Solid", "尚未初始化窗口材质。");

    private sealed class MaterialBinding
    {
        private readonly Window _window;
        private HwndSource? _source;
        private bool _closed, _updateQueued, _nativeTouched;
        public WindowMaterialStatus Status { get; private set; } = new(false, "Solid", "尚未初始化窗口材质。");

        public MaterialBinding(Window window)
        {
            _window = window;
            _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            _source?.AddHook(WindowMessage);
            window.Closed += Closed;
            SystemParameters.StaticPropertyChanged += SystemParameterChanged;
        }

        public void Apply()
        {
            if (_closed) return;
            bool? transparency = ReadTransparencyPreference();
            if (SystemParameters.HighContrast) { Fallback("高对比度模式：使用系统实色背景。", null, transparency); return; }
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) { Fallback("此 Windows 版本不支持系统 Acrylic：使用实色背景。", null, transparency); return; }
            if (transparency == false) { Fallback("Windows 的透明效果已关闭：使用实色背景。", null, false); return; }

            try
            {
                int result = DwmIsCompositionEnabled(out bool composition);
                if (result < 0 || !composition) { Fallback("桌面合成当前不可用：使用实色背景。", composition, transparency, result); return; }
                var handle = new WindowInteropHelper(_window).Handle;
                _source ??= HwndSource.FromHwnd(handle);
                if (handle == IntPtr.Zero || _source?.CompositionTarget is not { } target) { Fallback("窗口合成目标尚未就绪：使用实色背景。", true, transparency); return; }

                // DWMSBT_TRANSIENTWINDOW draws Desktop Acrylic behind the entire window.
                // The WPF surface must actually be transparent, rather than a 220/255 tint.
                int backdrop = 3;
                result = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
                if (result < 0) { Fallback("系统未接受 Acrylic 材质：使用实色背景。", true, transparency, result); return; }
                _nativeTouched = true;
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                result = DwmExtendFrameIntoClientArea(handle, ref margins);
                if (result < 0) { Fallback("系统无法扩展窗口材质：使用实色背景。", true, transparency, result); return; }

                target.BackgroundColor = Colors.Transparent;
                _window.Background = Brushes.Transparent;
                Publish(new(true, "Acrylic", "已启用本窗口系统 Acrylic；激活状态与节能策略等仍由 Windows 决定实际呈现。", true, transparency, result));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException or InvalidOperationException)
            {
                Fallback("系统材质暂不可用：使用实色背景。", null, transparency, ex.HResult);
            }
        }

        private void Fallback(string reason, bool? composition, bool? transparency, int? result = null)
        {
            if (_nativeTouched)
            {
                try
                {
                    int backdrop = 1; // DWMSBT_NONE: undo only this window's backdrop.
                    var handle = new WindowInteropHelper(_window).Handle;
                    DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
                    var margins = new Margins(); DwmExtendFrameIntoClientArea(handle, ref margins);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException) { }
                _nativeTouched = false;
            }
            var color = SystemParameters.HighContrast ? SystemColors.WindowColor : Color.FromRgb(241,245,250);
            _window.Background = new SolidColorBrush(color);
            if (_source?.CompositionTarget is { } target) target.BackgroundColor = color;
            Publish(new(false, "Solid", reason, composition, transparency, result));
        }

        private void Publish(WindowMaterialStatus status)
        {
            Status = LastStatus = status;
            StatusChanged?.Invoke(status);
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Follow actual system changes; no timer or permanent background polling.
            if (message is 0x031E or 0x001A or 0x031A or 0x0320) QueueUpdate();
            return IntPtr.Zero;
        }
        private void SystemParameterChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is null or nameof(SystemParameters.HighContrast) or "WindowGlassColor" or "WindowGlassBrush") QueueUpdate();
        }
        private void QueueUpdate()
        {
            if (_closed || _window.Dispatcher.HasShutdownStarted) return;
            if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(QueueUpdate)); return; }
            if (_updateQueued) return;
            _updateQueued = true;
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { _updateQueued = false; if (!_closed) Apply(); }));
        }
        private void Closed(object? sender, EventArgs args)
        {
            _closed = true;
            SystemParameters.StaticPropertyChanged -= SystemParameterChanged;
            _window.Closed -= Closed;
            _source?.RemoveHook(WindowMessage);
            _source = null;
        }
    }

    private static bool? ReadTransparencyPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("EnableTransparency") is int value ? value != 0 : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
