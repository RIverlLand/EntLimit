using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntLimit.Core;

namespace EntLimit.Runner;

/// <summary>Local syntax inspection only: the submitted module is parsed, never imported or executed.</summary>
public static class PythonCodeAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static async Task<CodeRunResult> AnalyzeAsync(Exercise exercise, string code, string? pythonPath, CancellationToken cancellationToken = default)
    {
        if (exercise.LanguageId != "python") return new() { Status = "unsupported_language", Message = "本地 AST 分析仅支持 Python；Java 可使用明确授权的在线分析，不会误用 Python 分析器。" };
        var analysis = CreateGeneral(exercise, code);
        CodeRunResult Result(string status, string message)
        {
            if (!analysis.AstAvailable)
                analysis.Summary = message + " 尚未取得当前代码 AST；教程仅为题目通用参考，不能视为识别结果。";
            return new() { Status = status, Passed = false, Message = message, Analysis = analysis };
        }
        if (ExecutionSafety.IsElevated) return Result("unsafe_host", "代码分析不能在管理员进程中运行，请用普通权限启动 EntLimit。");
        if (cancellationToken.IsCancellationRequested) return Result("cancelled", "分析已取消。");
        if (exercise.Coding is null || exercise.Kind != "code") return Result("invalid", "这道题没有可分析的目标函数。");
        if (Encoding.UTF8.GetByteCount(code) > 64 * 1024) return Result("invalid", "代码超过 64 KiB，请缩短后分析。");
        pythonPath = string.IsNullOrWhiteSpace(pythonPath) ? PythonRunner.DiscoverPython() : pythonPath;
        if (pythonPath is null || !File.Exists(pythonPath) || pythonPath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            return Result("runtime_missing", "没有可用 Python，以下仅为题目通用教程，尚未分析当前代码。请选择已安装的 python.exe。");
        var folder = Path.Combine(Path.GetTempPath(), "EntLimit-analysis-" + Guid.NewGuid().ToString("N"));
        Process? process = null; WindowsJob? job = null;
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "input.json"), JsonSerializer.Serialize(new { code, function = exercise.Coding.FunctionName }), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(folder, "analyzer.py"), Script, cancellationToken).ConfigureAwait(false);
            var start = new ProcessStartInfo(pythonPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var arg in new[] { "-I", "-X", "utf8", "-u", Path.Combine(folder, "analyzer.py") }) start.ArgumentList.Add(arg);
            process = new Process { StartInfo = start }; process.Start(); job = WindowsJob.Attach(process);
            var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); stop.CancelAfter(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                Kill(process); job?.Dispose(); job = null;
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                return Result(cancellationToken.IsCancellationRequested ? "cancelled" : "timeout", cancellationToken.IsCancellationRequested ? "分析已取消。" : "语法分析超过 5 秒，已停止。以下仅为通用教程。");
            }
            job?.Dispose(); job = null;
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            string resultFile = Path.Combine(folder, "ast.json");
            if (process.ExitCode != 0 || !File.Exists(resultFile) || new FileInfo(resultFile).Length > 256 * 1024)
                return Result("error", "无法取得语法分析结果，以下仅为通用教程。");
            var parsed = JsonSerializer.Deserialize<AstResult>(await File.ReadAllTextAsync(resultFile, cancellationToken).ConfigureAwait(false), JsonOptions);
            if (parsed is null) return Result("error", "分析结果无效，以下仅为通用教程。");
            if (parsed.Error.Length > 0) return Result("syntax_error", parsed.Error + " 当前代码未被执行；下面仍可阅读通用参考。");
            analysis.AstAvailable = true;
            BuildFindings(exercise, analysis, parsed);
            return Result("analysis", "当前代码的本地语法分析完成，不会授予或延长许可。");
        }
        catch (OperationCanceledException) { return Result("cancelled", "分析已取消。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or JsonException or TimeoutException or ArgumentException)
        { return Result("error", "本地分析器未能运行，以下仅为通用教程。请检查 Python 路径。"); }
        finally
        {
            if (process is not null) { Kill(process); process.Dispose(); } job?.Dispose();
            try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
    private static async Task<string> Drain(StreamReader reader)
    {
        var result = new StringBuilder(); var chunk = new char[2048]; int read;
        while ((read = await reader.ReadAsync(chunk).ConfigureAwait(false)) > 0) if (result.Length < 8192) result.Append(chunk, 0, Math.Min(read, 8192 - result.Length));
        return result.ToString();
    }
    private sealed class AstResult { public string Error { get; set; } = ""; public bool Found { get; set; } public List<AstFeature> Features { get; set; } = []; }
    private sealed class AstFeature { public string Kind { get; set; } = ""; public int Line { get; set; } public string Evidence { get; set; } = ""; }

    private static CodeAnalysis CreateGeneral(Exercise exercise, string code)
    {
        var analysis = new CodeAnalysis { CodeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))), Summary = "尚未取得当前代码的 AST；以下仅为这道题的通用参考，不代表识别到了你的写法。", Limitations = "静态结构分析不会执行代码、验证正确性或测量速度。容器类型仅依据本题输入约束和简单局部赋值推测，不是完整的数据流或绑定分析；同名重绑定可能改变语义。复杂度为条件性推导；字符串长度、哈希碰撞、输入分布和常数开销都可能影响表现。" };
        if (exercise.Coding is { } coding)
        {
            analysis.Tutorials.Add(new() { Title = "先用易读的逐步写法", Explanation = coding.SolutionExplanation, Code = coding.ReferenceCode, SourceUrl = "https://docs.python.org/3/tutorial/datastructures.html", Steps = ["先明确每一步保留的状态和返回值。", "手动跟踪空输入、重复项和大小写/空白的例子。", "通过测试后再比较可读性与复杂度。"], Practice = "选择一组包含重复值的输入，在纸上写出每一轮状态，再运行示例核对。" });
            if (coding.AlternativeCode.Length > 0) analysis.Tutorials.Add(new() { Title = "再认识 Python 惯用写法", Explanation = coding.AlternativeExplanation + " 更短不等于更快；先确认语义一致，再用代表性输入比较。", Code = coding.AlternativeCode, SourceUrl = coding.FunctionName == "count_statuses" ? "https://docs.python.org/3/library/collections.html#collections.Counter" : "https://docs.python.org/3/library/stdtypes.html#dict.fromkeys", Steps = ["把紧凑表达式拆成输入、变换、累计三个部分。", "判断它是否保留首次出现顺序、大小写与空白规则。", "先重视表达清晰；只有实测瓶颈才值得专门优化。"], Practice = "不用看参考，把惯用写法展开为显式循环，并说明两者何时得到相同结果。" });
            analysis.Tutorials.Add(new() { Title = "递归什么时候合适？", SourceUrl = "https://docs.python.org/3/library/sys.html#sys.getrecursionlimit", Explanation = "递归应先确定基例、每次规模如何缩小，以及递归栈需要多少空间。对本题这种扁平去重或计数，迭代通常更直接；不用为了高级而改成递归。Python 没有自动消除尾递归，深输入还可能触及递归深度限制。", Code = "def count_nested(items):\n    total = 0\n    for item in items:\n        if isinstance(item, list):\n            total += count_nested(item)\n        else:\n            total += 1\n    return total\n", Steps = ["基例：空列表没有元素，返回 0。", "缩小：只递归处理子列表；假设输入无环，每个调用处理更小结构。", "合并：普通元素加 1，子列表加其递归结果。", "栈：额外调用空间与最大嵌套深度相关；极深结构应考虑显式栈。"], Practice = "手动跟踪 [1, [2, [], [3]]]，画出调用与返回。然后思考：若列表包含自身，为什么必须额外检测环？" });
        }
        return analysis;
    }

    private static void BuildFindings(Exercise exercise, CodeAnalysis analysis, AstResult parsed)
    {
        if (!parsed.Found) { analysis.Summary = "语法有效，但没有找到题目要求的目标函数。请保留指定函数名。"; return; }
        foreach (var feature in parsed.Features.Take(80))
        {
            var (title, message) = feature.Kind switch
            {
                "for" => ("显式遍历", "这里使用 for。若单次处理为平均 O(1)，单层遍历 n 个元素通常是 O(n)；若循环体又扫描输入，则需要累计内层成本。"),
                "while" => ("while 循环", "这里使用 while。检查循环变量是否向终止条件前进；仅凭 while 不能判断次数或复杂度。"),
                "list" => ("列表构造候选", "若这里确实构造列表（list 未被遮蔽），列表保留顺序。向末尾 append 平均摊销 O(1)，但对列表做成员查找通常需要线性扫描。"),
                "set" => ("集合构造候选", "若 set 没有被同名变量/参数遮蔽，且这里确实构造集合，它适合已见值检查，平均查找 O(1)，但不提供本题的首次出现顺序；通常搭配结果列表。哈希最坏情况和字符串成本需另计。"),
                "dict" => ("字典构造候选", "若这里确实构造字典（dict 未被遮蔽），它适合按键累计，现代 Python 保留插入顺序。平均哈希操作 O(1)，并非无条件最坏 O(1)。"),
                "membership_list" => ("列表成员查找候选", "若推测的接收者确实是列表，且这行在长度 n 的输入循环中、被检查列表也随输入增长，每次最坏 O(n)，整段可能达到 O(n²)。可比较 set 做已见值检查，但须保留输出顺序。"),
                "membership_set" => ("集合成员查找候选", "若推测的接收者确实是集合，并在循环外初始化，单次查找平均 O(1)；一趟处理通常平均 O(n)，额外空间与不同值数量相关。"),
                "membership_dict" => ("字典键成员查找候选", "若这里确实检查字典键，平均 O(1)；是否有重复遍历还要看外围控制流。"),
                "membership_unknown" => ("成员检查，容器类型未确认", "看到了 in/not in，但静态证据不足以确定右侧容器类型；不能据此断言是线性或常数查找。"),
                "count_in_loop" => ("循环内调用 count", "若接收者是输入列表/字符串，count 会重新扫描；在 n 次外循环中可能形成 O(n²)。计数题可比较一次遍历加字典/Counter，先确认清洗规则与结果语义。"),
                "count" => ("调用 count", "count 的成本取决于接收者类型；列表/字符串通常需要扫描，不能因为函数名相同就确定对象类型。"),
                "dict_get" => ("get 默认值累计", "get(key, 0) 可读地表达第一次出现的默认计数。若接收者是字典且每项处理一次，累计通常平均 O(n)，空间 O(k)。"),
                "counter" => ("Counter 写法", "若这里确实引用 collections.Counter，可表达频次统计；自定义同名函数不能保证同样语义。仍需先满足题目空白/大小写清洗规则。"),
                "dict_fromkeys" => ("dict.fromkeys 写法", "若 dict 未被遮蔽，fromkeys 可按首次出现顺序去重。输入要可哈希；此处字符串满足要求。它更简洁，不保证对所有输入更快。"),
                "comprehension" => ("推导式/生成器表达式", "紧凑表达式仍然有遍历成本；多个 for 或每项重复扫描会增加工作量。生成器可减少中间列表，但也应比较可读性。"),
                "recursion" => ("同名递归调用候选", "这里调用了目标函数的名字；若未被局部重绑定，可能是递归。检查基例、规模缩小和栈深；对扁平去重/计数，迭代往往更合适。"),
                _ => ("语法结构", "发现了这个语法结构；需要结合控制流和数据类型理解。")
            };
            analysis.Findings.Add(new() { Kind = feature.Kind, Line = feature.Line, Evidence = feature.Evidence, Title = title, Message = message });
        }
        string next = parsed.Features.Any(f => f.Kind == "count_in_loop") ? "下一步先检查循环中的 count 是否反复扫描同一列表；若是，比较一次遍历字典累计或 Counter。"
            : parsed.Features.Any(f => f.Kind == "membership_list") ? "下一步检查列表成员查找是否在循环中随结果增长；若是，尝试集合记录已见项，同时保留结果列表的顺序。"
            : parsed.Features.Any(f => f.Kind is "counter" or "dict_fromkeys") ? "已出现惯用写法候选；下一步确认同名绑定、清洗规则和边界输入，而非继续追求更短。"
            : parsed.Features.Any(f => f.Kind == "recursion") ? "下一步明确递归基例、规模如何缩小及最大栈深；本题扁平输入通常更适合迭代。"
            : "下一步用空输入、重复值及大小写/空白边界核对语义，再考虑是否需要简化表达。";
        analysis.Summary = $"已解析目标函数 {exercise.Coding!.FunctionName}。{next} 这是规则分析，不证明正确性、等价性或实际速度。";
    }

    private const string Script = """
import ast, json, pathlib
root = pathlib.Path(__file__).parent
payload = json.loads((root / 'input.json').read_text(encoding='utf-8-sig'))
code = payload['code']
lines = code.splitlines()
result = {'found': False, 'error': '', 'features': []}
try:
    tree = ast.parse(code, filename='<your-code>')
    function = next((n for n in tree.body if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef)) and n.name == payload['function']), None)
    if function is not None:
        result['found'] = True
        class Inspector(ast.NodeVisitor):
            def __init__(self):
                self.types = {arg.arg: 'list' for arg in function.args.args[:1]}
                self.loops = 0
            def add(self, node, kind):
                if len(result['features']) < 80:
                    line = getattr(node, 'lineno', 0)
                    result['features'].append({'kind': kind, 'line': line, 'evidence': lines[line-1].strip()[:180] if 0 < line <= len(lines) else ''})
            def visit_FunctionDef(self, node): pass
            def visit_AsyncFunctionDef(self, node): pass
            def visit_Lambda(self, node): pass
            def visit_ClassDef(self, node): pass
            def visit_Assign(self, node):
                kind = self.container(node.value)
                for target in node.targets:
                    if isinstance(target, ast.Name):
                        if kind: self.types[target.id] = kind
                        else: self.types.pop(target.id, None)
                self.generic_visit(node)
            def container(self, node):
                if isinstance(node, (ast.List, ast.ListComp)): return 'list'
                if isinstance(node, (ast.Set, ast.SetComp)): return 'set'
                if isinstance(node, (ast.Dict, ast.DictComp)): return 'dict'
                if isinstance(node, ast.Name): return self.types.get(node.id, '')
                if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id in ('list', 'set', 'dict'): return node.func.id
                return ''
            def visit_For(self, node):
                self.add(node, 'for')
                self.visit(node.iter)
                self.loops += 1
                for n in node.body: self.visit(n)
                self.loops -= 1
                for n in node.orelse: self.visit(n)
            def visit_While(self, node):
                self.add(node, 'while')
                self.loops += 1
                self.visit(node.test)
                for n in node.body: self.visit(n)
                self.loops -= 1
                for n in node.orelse: self.visit(n)
            def visit_List(self, node): self.add(node, 'list'); self.generic_visit(node)
            def visit_Set(self, node): self.add(node, 'set'); self.generic_visit(node)
            def visit_Dict(self, node): self.add(node, 'dict'); self.generic_visit(node)
            def visit_ListComp(self, node): self.comprehension(node)
            def visit_SetComp(self, node): self.comprehension(node)
            def visit_DictComp(self, node): self.comprehension(node)
            def visit_GeneratorExp(self, node): self.comprehension(node)
            def comprehension(self, node):
                self.add(node, 'comprehension')
                self.loops += 1
                self.generic_visit(node)
                self.loops -= 1
            def visit_Compare(self, node):
                for operator, right in zip(node.ops, node.comparators):
                    if isinstance(operator, (ast.In, ast.NotIn)):
                        self.add(node, 'membership_' + (self.container(right) or 'unknown'))
                self.generic_visit(node)
            def visit_Call(self, node):
                if isinstance(node.func, ast.Name):
                    if node.func.id in ('list', 'set', 'dict'): self.add(node, node.func.id)
                    if node.func.id == 'Counter': self.add(node, 'counter')
                    if node.func.id == payload['function']: self.add(node, 'recursion')
                elif isinstance(node.func, ast.Attribute):
                    if node.func.attr == 'count': self.add(node, 'count_in_loop' if self.loops else 'count')
                    if node.func.attr == 'get': self.add(node, 'dict_get')
                    if node.func.attr == 'Counter': self.add(node, 'counter')
                    if node.func.attr == 'fromkeys' and isinstance(node.func.value, ast.Name) and node.func.value.id == 'dict': self.add(node, 'dict_fromkeys')
                self.generic_visit(node)
        inspector = Inspector()
        for statement in function.body: inspector.visit(statement)
except SyntaxError as ex:
    result['error'] = '第 ' + str(ex.lineno or 0) + ' 行语法错误：' + (ex.msg or '')[:200]
except (ValueError, RecursionError, MemoryError) as ex:
    result['error'] = type(ex).__name__ + ': 代码结构过深或无法解析。'
(root / 'ast.json').write_text(json.dumps(result, ensure_ascii=False), encoding='utf-8')
""";
}
