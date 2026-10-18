using System.Buffers.Binary;
using System.Text.Json;
using EntLimit.Core;

// stdout is exclusively the native-messaging binary protocol.
const int MaxMessage = 256 * 1024;
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
while (true)
{
    var header = new byte[4];
    using var frameTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var frameCancellation = CancellationTokenSource.CreateLinkedTokenSource(frameTimeout.Token);
    byte[] body;
    try
    {
        if (!await ReadExact(input, header, frameCancellation.Token)) break;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > MaxMessage) break;
        body = new byte[(int)length];
        if (!await ReadExact(input, body, frameCancellation.Token)) break;
    }
    catch (OperationCanceledException) { break; }
    object reply;
    try
    {
        using var request = JsonDocument.Parse(body);
        var type = request.RootElement.GetProperty("type").GetString();
        var command = type switch
        {
            "snapshot" or "status" => "status",
            "open_learning" or "open_ui" => "open_ui",
            "use" or "browser_use" => "browser_use",
            _ => throw new InvalidDataException()
        };
        IpcResponse response;
        if (EmergencyStop.IsActive(DataPaths.GetRoot())) response = EmergencySnapshot();
        else try { response = await PipeClient.SendAsync(new IpcRequest { Command = command }); }
        catch
        {
            if (EmergencyStop.IsActive(DataPaths.GetRoot())) response = EmergencySnapshot();
            else
            {
                // A deliberate debug stop leaves a persisted opt-out. Read only that
                // configuration, so the browser can clear cached redirect rules after exit.
                // Never reconstruct or award a permit while the monitor is unavailable.
                string path = Path.Combine(DataPaths.GetRoot(), "config.json");
                if (!File.Exists(path) || new FileInfo(path).Length > MaxMessage) throw;
                using var configStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var paused = JsonSerializer.Deserialize<AppConfig>(configStream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (paused is null || paused.SchemaVersion != 1 || paused.MonitoringEnabled || command != "status") throw;
                response = new IpcResponse { Config = paused, Session = new SessionState() };
            }
        }
        // Also honor a latch written while IPC was outstanding.
        if (EmergencyStop.IsActive(DataPaths.GetRoot())) response = EmergencySnapshot();
        reply = new
        {
            ok = response.Ok,
            error = response.Error,
            emergencyStopped = response.EmergencyStopped,
            config = new { domains = response.Config.MonitoringEnabled ? response.Config.Domains : [], monitoringEnabled = response.Config.MonitoringEnabled },
            session = new { phase = response.Session.Phase.ToString(), deadlineUtc = response.Session.DeadlineUtc }
        };
    }
    catch (JsonException) { reply = new { ok = false, error = "Invalid request." }; }
    catch (InvalidDataException) { reply = new { ok = false, error = "Unsupported request." }; }
    catch (Exception) { reply = new { ok = false, error = "EntLimit monitor unavailable. Open the desktop app and retry." }; }
    var bytes = JsonSerializer.SerializeToUtf8Bytes(reply);
    BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
    await output.WriteAsync(header);
    await output.WriteAsync(bytes);
    await output.FlushAsync();
}

static IpcResponse EmergencySnapshot() => new()
{
    EmergencyStopped = true,
    Config = new AppConfig { MonitoringEnabled = false, StartupEnabled = false, GuardEnabled = false, SoundEnabled = false, OverlayEnabled = false },
    Session = new SessionState()
};

static async Task<bool> ReadExact(Stream stream, byte[] bytes, CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < bytes.Length)
    {
        // WaitAsync also bounds streams whose underlying console read cannot cancel.
        var count = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).AsTask().WaitAsync(cancellationToken);
        if (count == 0) return false;
        offset += count;
    }
    return true;
}
