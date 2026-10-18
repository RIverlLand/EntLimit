using System.Text;
using System.Text.Json;
using EntLimit.Core;

namespace EntLimit.Runner;

public static class RunnerDispatcher
{
    public static List<RunnerPluginInfo> GetInstalled() => GetInstalled(AppContext.BaseDirectory);
    public static List<RunnerPluginInfo> GetInstalled(string appRoot)
    {
        var result = RunnerPluginRegistry.GetInstalled(appRoot);
        result.Insert(0, new() { Id = "python-builtin", Name = "Python 本地运行器", LanguageId = "python", Trusted = true }); return result;
    }
    public static Task<CodeRunResult> RunAsync(Exercise exercise, string code, string? runtimePath, bool examplesOnly, CancellationToken cancellationToken = default)
        => RunAsync(exercise, code, runtimePath, examplesOnly, AppContext.BaseDirectory, cancellationToken);
    public static async Task<CodeRunResult> RunAsync(Exercise exercise, string code, string? runtimePath, bool examplesOnly, string appRoot, CancellationToken cancellationToken = default)
    {
        static CodeRunResult Fail(string status, string message) => new() { Status = status, Message = message };
        if (ExecutionSafety.IsElevated) return Fail("unsafe_host", "练习代码不能在管理员进程中运行，请用普通权限启动 EntLimit。");
        if (exercise.LanguageId == "python") return await PythonRunner.RunAsync(exercise, code, runtimePath, examplesOnly, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return Fail("cancelled", "运行已取消。");
        if (exercise.Kind != "code" || exercise.Coding is null || Encoding.UTF8.GetByteCount(code) > 64 * 1024) return Fail("invalid", "代码或题目不满足运行要求。");
        var cases = exercise.Coding.Cases.Where(c => !examplesOnly || c.IsExample).ToList();
        if (cases.Count is < 1 or > 128) return Fail("invalid", "测试用例数量应为 1–128。");
        try
        {
            var plugin = RunnerPluginRegistry.ResolveLanguage(appRoot, exercise.LanguageId);
            var request = new RunnerPluginRequest { RequestId = Guid.NewGuid().ToString("N"), Exercise = exercise, Code = code, RuntimePath = runtimePath, ExamplesOnly = examplesOnly };
            string input = JsonSerializer.Serialize(request) + "\n";
            if (Encoding.UTF8.GetByteCount(input) > 256 * 1024) return Fail("invalid", "插件请求过大。");
            var output = await BoundedProcess.RunAsync(plugin.Executable, [], Path.GetDirectoryName(plugin.Executable)!, input, 35, 1024 * 1024, cancellationToken, 4, 768).ConfigureAwait(false);
            if (output.Cancelled) return Fail("cancelled", "插件运行已取消。");
            if (output.TimedOut) return Fail("timeout", "插件运行超时，已停止其进程树。");
            if (output.ExitCode != 0) return Fail("error", "插件进程异常退出。");
            var reply = JsonSerializer.Deserialize<RunnerPluginReply>(output.Output, PipeProtocol.JsonOptions);
            if (reply is null || reply.ProtocolVersion != 1 || reply.RequestId != request.RequestId) return Fail("error", "插件协议或请求标识不匹配。");
            var result = reply.Result;
            if (result is null || result.Cases is null || result.Cases.Count > 128 || result.Status is null || result.Message is null || result.StandardOutput is null || result.Cases.Any(c => c is null || c.Name is null || c.Expected is null || c.Actual is null || c.Error is null)) return Fail("error", "插件返回了无效结果结构。");
            if (result.Status is "passed" or "failed")
            {
                if (result.Cases.Count != cases.Count || result.Cases.Where((c, i) => c.Name != cases[i].Name).Any()) return Fail("error", "插件测试结果不完整。");
                result.Passed = result.Cases.Count > 0 && result.Cases.All(c => c.Passed); result.Status = result.Passed ? "passed" : "failed";
            }
            else result.Passed = false;
            return result;
        }
        catch (OperationCanceledException) { return Fail("cancelled", "运行已取消。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        { return Fail("plugin_unavailable", "运行器插件未安装、未经授权或文件校验失败，请检查插件安装。"); }
    }
}
