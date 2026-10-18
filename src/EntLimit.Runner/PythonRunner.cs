using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EntLimit.Core;
using Microsoft.Win32;

namespace EntLimit.Runner;

/// <summary>Resource-bounded local practice runner. Not a security sandbox for hostile code.</summary>
public static class PythonRunner
{
    public const int TimeoutSeconds = 5;
    private const int OutputLimit = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string? DiscoverPython()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe") };
        if (OperatingSystem.IsWindows())
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"Software\Python\PythonCore");
                    foreach (var version in root?.GetSubKeyNames() ?? [])
                    {
                        using var key = root?.OpenSubKey(version + @"\InstallPath");
                        if (key?.GetValue("ExecutablePath") is string executable) candidates.Add(executable);
                        if (key?.GetValue("") is string directory) candidates.Add(Path.Combine(directory, "python.exe"));
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
                if (Directory.Exists(folder))
                    candidates.AddRange(Directory.EnumerateDirectories(folder).Take(20).Select(d => Path.Combine(d, "python.exe")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(folder))
                try { candidates.Add(Path.Combine(folder.Trim('"'), OperatingSystem.IsWindows() ? "python.exe" : "python3")); } catch (ArgumentException) { }
        var discoveryTime = Stopwatch.StartNew();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
        {
            if (discoveryTime.Elapsed > TimeSpan.FromSeconds(3)) break;
            if (IsUsablePython(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsUsablePython(string path)
    {
        if (!File.Exists(path) || path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var process = new Process { StartInfo = MakeStart(path) };
            process.StartInfo.ArgumentList.Add("-I");
            process.StartInfo.ArgumentList.Add("-X");
            process.StartInfo.ArgumentList.Add("utf8");
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("import sys;sys.exit(0 if sys.version_info >= (3,8) else 1)");
            process.Start();
            var output = DrainAsync(process.StandardOutput);
            var error = DrainAsync(process.StandardError);
            if (!process.WaitForExit(1500)) { Kill(process); return false; }
            Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or TimeoutException) { return false; }
    }

    public static async Task<CodeRunResult> RunAsync(Exercise exercise, string code, string? pythonPath, bool examplesOnly, CancellationToken cancellationToken = default)
    {
        if (ExecutionSafety.IsElevated) return Fail("unsafe_host", "练习代码不能在管理员进程中运行，请用普通权限启动 EntLimit。");
        var result = await RunCoreAsync(exercise, code, pythonPath, examplesOnly, cancellationToken).ConfigureAwait(false);
        if (!examplesOnly && result.Status is not ("cancelled" or "invalid") && !cancellationToken.IsCancellationRequested)
        {
            try { result.Analysis = (await PythonCodeAnalyzer.AnalyzeAsync(exercise, code, pythonPath, cancellationToken).ConfigureAwait(false)).Analysis; }
            catch (Exception)
            {
                // Optional guidance can never replace a completed judge verdict or earned result.
                result.Analysis = new CodeAnalysis { CodeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(code))), Summary = "分析暂不可用；判题结果保持不变。可稍后手动分析。", Limitations = "尚未取得当前代码 AST，不能据此识别写法或推导性能。" };
            }
        }
        return result;
    }

    private static async Task<CodeRunResult> RunCoreAsync(Exercise exercise, string code, string? pythonPath, bool examplesOnly, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Fail("cancelled", "运行已取消。");
        if (exercise.Coding is null || exercise.Kind != "code") return Fail("invalid", "这道练习没有可运行的代码测试。");
        if (Encoding.UTF8.GetByteCount(code) > 64 * 1024) return Fail("invalid", "代码超过 64 KiB，请缩短后再试。");
        var cases = exercise.Coding.Cases.Where(c => !examplesOnly || c.IsExample).ToList();
        if (cases.Count == 0) return Fail("invalid", "没有可运行的测试用例。");
        pythonPath = string.IsNullOrWhiteSpace(pythonPath) ? DiscoverPython() : pythonPath;
        if (pythonPath is null || !IsUsablePython(pythonPath))
            return Fail("runtime_missing", "未找到 Python 3.8 或以上版本。请在练习页选择已安装的 python.exe；本次不会自动下载。");
        var folder = Path.Combine(Path.GetTempPath(), "EntLimit-run-" + Guid.NewGuid().ToString("N"));
        Process? process = null;
        WindowsJob? job = null;
        try
        {
            Directory.CreateDirectory(folder);
            var payload = new { code, function = exercise.Coding.FunctionName, cases = cases.Select(c => new { name = c.Name, arguments = c.ArgumentsJson, expected = c.ExpectedJson }) };
            await File.WriteAllTextAsync(Path.Combine(folder, "input.json"), JsonSerializer.Serialize(payload), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(folder, "runner.py"), Harness, cancellationToken);
            process = new Process { StartInfo = MakeStart(pythonPath) };
            process.StartInfo.WorkingDirectory = folder;
            process.StartInfo.ArgumentList.Add("-I");
            process.StartInfo.ArgumentList.Add("-X");
            process.StartInfo.ArgumentList.Add("utf8");
            process.StartInfo.ArgumentList.Add("-u");
            process.StartInfo.ArgumentList.Add(Path.Combine(folder, "runner.py"));
            process.Start();
            // Fail closed if Windows resource limits cannot be attached. No game processes are touched.
            job = WindowsJob.Attach(process);
            var stdout = DrainAsync(process.StandardOutput);
            var stderr = DrainAsync(process.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                Kill(process);
                job?.Dispose(); job = null;
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2));
                var stopped = Fail(cancellationToken.IsCancellationRequested ? "cancelled" : "timeout", cancellationToken.IsCancellationRequested ? "运行已取消，草稿保留。" : $"运行超过 {TimeoutSeconds} 秒，已停止。检查是否存在无限循环。");
                stopped.StandardOutput = await stdout;
                return stopped;
            }
            // Closing the job also closes any surviving children before draining their inherited pipes.
            job?.Dispose(); job = null;
            var captured = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2));
            if (process.ExitCode != 0) return WithOutput(Fail("error", "Python 进程异常退出，可能超过资源限制。" + captured[1]), captured[0]);
            var resultPath = Path.Combine(folder, "result.json");
            if (!File.Exists(resultPath) || new FileInfo(resultPath).Length > 1024 * 1024)
                return WithOutput(Fail("error", "运行未返回有效的测试结果。"), captured[0]);
            var result = JsonSerializer.Deserialize<CodeRunResult>(await File.ReadAllTextAsync(resultPath, cancellationToken), JsonOptions);
            if (result is null || result.Cases.Count != cases.Count || result.Cases.Where((c, i) => c.Name != cases[i].Name).Any())
                return WithOutput(Fail("error", "测试结果不完整，请重试。"), captured[0]);
            result.Passed = result.Cases.All(c => c.Passed);
            result.Status = result.Passed ? "passed" : "failed";
            result.Message = result.Passed ? (examplesOnly ? "示例全部通过；提交检查后可获得许可。" : "全部测试通过。") : "还有测试未通过，查看结果或使用提示再试一次。";
            result.StandardOutput = captured[0] + (captured[1].Length > 0 ? "\n[stderr]\n" + captured[1] : "");
            return result;
        }
        catch (OperationCanceledException) { return Fail("cancelled", "运行已取消。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or JsonException or TimeoutException)
        { return Fail("error", "无法完成本次运行，请检查 Python 路径或重试。"); }
        finally
        {
            if (process is not null) { Kill(process); process.Dispose(); }
            job?.Dispose();
            try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static ProcessStartInfo MakeStart(string path) => new(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
    private static CodeRunResult Fail(string status, string message) => new() { Status = status, Message = message };
    private static CodeRunResult WithOutput(CodeRunResult result, string output) { result.StandardOutput = output; return result; }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { } }
    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var text = new StringBuilder(); var chunk = new char[2048]; bool truncated = false; int read;
        while ((read = await reader.ReadAsync(chunk).ConfigureAwait(false)) != 0)
        {
            int available = Math.Max(0, OutputLimit - text.Length);
            text.Append(chunk, 0, Math.Min(available, read));
            if (read > available) truncated = true;
        }
        if (truncated) text.Append("\n[输出过长，已截断]");
        return text.ToString();
    }

    private const string Harness = """
import json, pathlib, copy
root = pathlib.Path(__file__).parent
payload = json.loads((root / 'input.json').read_text(encoding='utf-8-sig'))
def strict_equal(left, right):
    if type(left) is not type(right):
        return False
    if isinstance(left, list):
        return len(left) == len(right) and all(strict_equal(a, b) for a, b in zip(left, right))
    if isinstance(left, dict):
        return left.keys() == right.keys() and all(strict_equal(left[k], right[k]) for k in left)
    return left == right
rows = []
namespace = {'__name__': '__exercise__'}
initial_error = ''
try:
    exec(compile(payload['code'], '<your-code>', 'exec'), namespace)
    function = namespace[payload['function']]
    if not callable(function):
        raise TypeError('Expected a callable function: ' + payload['function'])
except BaseException as ex:
    initial_error = type(ex).__name__ + ': ' + str(ex)[:1200]
for case in payload['cases']:
    row = {'name': case['name'], 'passed': False, 'expected': case['expected'], 'actual': '', 'error': initial_error}
    if not initial_error:
        try:
            arguments = json.loads(case['arguments'])
            original = copy.deepcopy(arguments)
            actual = function(*arguments)
            encoded = json.dumps(actual, ensure_ascii=False, allow_nan=False)
            expected = json.loads(case['expected'])
            # JSON bool is distinct from numeric 0/1 for these exercises.
            row['passed'] = strict_equal(actual, expected) and strict_equal(arguments, original)
            if not strict_equal(arguments, original):
                row['error'] = '请不要修改传入的参数；请创建新的结果。'
            row['actual'] = encoded[:4000]
        except BaseException as ex:
            row['error'] = type(ex).__name__ + ': ' + str(ex)[:1200]
    rows.append(row)
(root / 'result.json').write_text(json.dumps({'cases': rows}, ensure_ascii=False), encoding='utf-8')
""";
}
