using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EntLimit.Core;

namespace EntLimit.Desktop;

public partial class App : Application
{
    private Mutex? _mutex;
    private static readonly uint ShowMessage = RegisterWindowMessage(PipeProtocol.Name + ".ShowDesktop.v1");
    private static readonly uint GateMessage = RegisterWindowMessage(PipeProtocol.Name + ".GateDesktop.v1");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] == "--preview")
        {
            try
            {
                var preview = new MainWindow(preview: true, previewCode: e.Args.Length > 2 && e.Args[2] == "code");
                preview.PreparePreview(e.Args.Length > 2 ? e.Args[2] : "network");
                int width = e.Args.Length > 3 && int.TryParse(e.Args[3], out var requestedWidth) ? Math.Clamp(requestedWidth, 850, 2560) : 1320;
                int height = e.Args.Length > 4 && int.TryParse(e.Args[4], out var requestedHeight) ? Math.Clamp(requestedHeight, 600, 1600) : 900;
                preview.ApplyCompactLayout(height);
                var visual = (FrameworkElement)preview.Content;
                visual.Measure(new Size(width,height)); visual.Arrange(new Rect(0,0,width,height)); visual.UpdateLayout(); preview.FinalizePreview(); visual.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(System.IO.Path.GetFullPath(e.Args[1])); encoder.Save(output);
                Shutdown(0);
            }
            catch { Shutdown(2); }
            return;
        }
        _mutex = new Mutex(true, PipeProtocol.Name + ".Desktop", out var created);
        bool gate = e.Args.Length == 1 && e.Args[0] == "--gate";
        if (!created)
        {
            PostMessage(new IntPtr(0xffff), gate ? GateMessage : ShowMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown(); return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if ((uint)msg == GateMessage) { window.SetGatePresentation(); window.Show(); window.Activate(); handled = true; }
            else if ((uint)msg == ShowMessage) { window.ClearGatePresentation(); window.Show(); window.WindowState = WindowState.Normal; window.Activate(); handled = true; }
            return IntPtr.Zero;
        });
        if (gate) window.SetGatePresentation();
        window.Show();
        if (gate) window.Activate();
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
