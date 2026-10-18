using System.Text.Json;
using EntLimit.Core;
using EntLimit.Runner;
using EntLimit.JavaPlugin;

// One newline-delimited JSON request/reply. stdout never carries user/JDK output directly.
RunnerPluginRequest? request = null;
CodeRunResult result;
if (args.Length > 0)
{
    try
    {
        if (args.Length != 4 || args[3] != "--consent") throw new InvalidOperationException("Explicit --consent is required before installing executable plugins.");
        if (args.Length == 4 && args[0] == "--seal-install")
        {
            RunnerPluginRegistry.CreateManifest(args[1], "java-local", "Java 本地编译运行器", "java", "EntLimit.JavaPlugin.exe");
            RunnerPluginRegistry.RegisterInstalled(args[2], Path.Combine(args[1], "manifest.json"), args[3] == "--consent");
        }
        else if (args.Length == 4 && args[0] == "--register-plugin") RunnerPluginRegistry.RegisterInstalled(args[1], args[2], args[3] == "--consent");
        else throw new InvalidOperationException("Unsupported install command. Explicit --consent is required.");
        Console.WriteLine("Runner plugin registered. Only install executable plugins from sources you trust."); return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
    { Console.Error.WriteLine("Plugin installation failed: " + ex.Message); return 1; }
}
try
{
    if (ExecutionSafety.IsElevated) result = new() { Status = "unsafe_host", Message = "不能在管理员进程执行用户代码。" };
    else
    {
        var input = await PipeClient.ReadLineAsync(Console.OpenStandardInput(), 256 * 1024);
        request = JsonSerializer.Deserialize<RunnerPluginRequest>(input, PipeProtocol.JsonOptions);
        result = request is null || request.ProtocolVersion != 1 || request.Exercise is null || request.Code is null ? new() { Status = "invalid", Message = "Unsupported plugin protocol." } : await JavaRunner.RunAsync(request);
    }
}
catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
{ result = new() { Status = "error", Message = "Java 插件请求无效或运行失败。" }; }
Console.WriteLine(JsonSerializer.Serialize(new RunnerPluginReply { RequestId = request?.RequestId ?? "", Result = result }));
return 0;
