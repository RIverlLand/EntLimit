using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace WindowFixture;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        if (args.Length != 1 || !args[0].All(char.IsAsciiLetterOrDigit)) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new FixtureForm("EntLimit.WindowFixture." + args[0]));
    }
}

internal sealed class FixtureForm : Form
{
    readonly CancellationTokenSource stop = new();
    readonly string pipeName;
    public FixtureForm(string pipeName)
    {
        this.pipeName = pipeName;
        Text = "EntLimit regression fixture — harmless test window";
        Size = new Size(520, 200);
        Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Text = "Automated regression fixture\nOnly this window is controlled by the test harness." });
        Shown += (_, _) => _ = Serve();
        FormClosed += (_, _) => stop.Cancel();
    }
    async Task Serve()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var command = await reader.ReadLineAsync(timeout.Token);
                // Continuation runs on the Form's own UI thread; no cross-process window mutation.
                if (command == "activate")
                {
                    WindowState = FormWindowState.Normal;
                    Show(); Activate();
                    SetForegroundWindow(Handle);
                }
                await writer.WriteLineAsync(Handle.ToInt64().ToString().AsMemory(), timeout.Token);
                if (command == "close") { Close(); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { if (!stop.IsCancellationRequested) Close(); }
    }
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
}
