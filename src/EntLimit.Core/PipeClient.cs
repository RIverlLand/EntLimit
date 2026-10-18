using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace EntLimit.Core;

public static class PipeProtocol
{
    static PipeProtocol() { }
    public const int MaxRequestBytes = 256 * 1024;
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static string Name { get; } = BuildName();

    private static string BuildName()
    {
        string identity = Environment.UserName;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var current = WindowsIdentity.GetCurrent();
                identity = current.User?.Value ?? identity;
            }
            catch (System.Security.SecurityException) { }
        }
        var name = "EntLimit.v1." + string.Concat(identity.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_'));
        // Separate test instances share neither the production pipe nor its singleton mutex.
        var instance = Environment.GetEnvironmentVariable("ENT_LIMIT_INSTANCE");
        if (!string.IsNullOrWhiteSpace(instance))
        {
            if (instance.Length > 64 || instance.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new InvalidOperationException("Invalid EntLimit instance identifier.");
            name += "." + instance;
        }
        return name;
    }
}

public static class PipeClient
{
    public static async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.SerializeToUtf8Bytes(request, PipeProtocol.JsonOptions);
        if (json.Length > PipeProtocol.MaxRequestBytes) throw new InvalidDataException("Request is too large.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = string.Equals(request.Command, "scan", StringComparison.OrdinalIgnoreCase) ? 120 : 5;
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeProtocol.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(json, timeout.Token);
            await pipe.WriteAsync(new byte[] { (byte)'\n' }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            var line = await ReadLineAsync(pipe, PipeProtocol.MaxResponseBytes, timeout.Token);
            return JsonSerializer.Deserialize<IpcResponse>(line, PipeProtocol.JsonOptions)
                ?? throw new InvalidDataException("Monitor returned an empty response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"EntLimit monitor did not respond within {timeoutSeconds} seconds.");
        }
    }

    /// <summary>Bounded UTF-8 newline framing; shared by the monitor to enforce request limits.</summary>
    public static async Task<string> ReadLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) throw new EndOfStreamException("Pipe closed before a complete response.");
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            var bytes = newline >= 0 ? newline : count;
            if (output.Length + bytes > maxBytes) throw new InvalidDataException("Pipe message exceeds its size limit.");
            output.Write(buffer, 0, bytes);
            if (newline >= 0) return new UTF8Encoding(false, true).GetString(output.ToArray()).TrimEnd('\r');
        }
    }
}
