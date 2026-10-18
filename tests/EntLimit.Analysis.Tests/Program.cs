using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntLimit.Core;
using EntLimit.Runner;

var python = Environment.GetEnvironmentVariable("ENT_LIMIT_TEST_PYTHON") ?? PythonRunner.DiscoverPython();
if (python is null) throw new Exception("Set ENT_LIMIT_TEST_PYTHON to an installed Python 3 executable.");
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
var dedupe = ExerciseCatalog.All.Single(e => e.Coding?.FunctionName == "stable_unique");
var count = ExerciseCatalog.All.Single(e => e.Coding?.FunctionName == "count_statuses");
Task<CodeRunResult> Analyze(string source, Exercise? exercise = null, CancellationToken token = default) => PythonCodeAnalyzer.AnalyzeAsync(exercise ?? dedupe, source, python, token);
bool Has(CodeRunResult result, string kind) => result.Analysis!.Findings.Any(f => f.Kind == kind);
const string naive = "def stable_unique(values):\n    result = []\n    for value in values:\n        if value not in result:\n            result.append(value)\n    return result\n";
var basic = await Analyze(naive);
Check(basic.Status == "analysis" && !basic.Passed && basic.Analysis?.AstAvailable == true, "Analysis is explicitly non-rewarding and AST-backed");
Check(Has(basic, "for") && Has(basic, "list") && Has(basic, "membership_list"), "AST identifies list membership inside real loop");
Check(basic.Analysis!.Findings.Any(f => f.Kind == "membership_list" && f.Line == 4 && f.Evidence.Contains("not in result")), "Findings bind line and exact source evidence");
Check(basic.Analysis.Summary.Contains("下一步") && basic.Analysis.Summary.Contains("集合"), "Analysis summary gives a concrete next improvement");
Check(basic.Analysis.CodeHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(naive))), "Analysis SHA256 binds exact code snapshot");
Check(basic.Analysis.Findings.Any(f => f.Kind == "membership_list" && f.Message.Contains("若") && f.Message.Contains("O(n²)")), "Complexity advice states assumptions rather than claiming a measured speed");
var setCode = await Analyze(dedupe.Coding!.ReferenceCode);
Check(Has(setCode, "set") && Has(setCode, "membership_set"), "Set construction and membership recognized");
var countRepeated = await Analyze("def count_statuses(values):\n    return {value: values.count(value) for value in values}\n", count);
Check(Has(countRepeated, "comprehension") && Has(countRepeated, "count_in_loop"), "Count inside comprehension flagged as repeated scan candidate");
var get = await Analyze(count.Coding!.ReferenceCode, count);
Check(Has(get, "dict") && Has(get, "dict_get"), "Dictionary/get accumulation recognized");
var counter = await Analyze("from collections import Counter\ndef count_statuses(values):\n    return dict(Counter(values))\n", count);
Check(Has(counter, "counter"), "Counter function syntax recognized without executing import");
var keys = await Analyze("def stable_unique(values):\n    return list(dict.fromkeys(values))\n");
Check(Has(keys, "dict_fromkeys"), "dict.fromkeys idiom recognized");
var recursion = await Analyze("def stable_unique(values):\n    if not values: return []\n    return stable_unique(values[1:])\n");
Check(Has(recursion, "recursion") && recursion.Analysis!.Findings.Any(f => f.Kind == "recursion" && f.Message.Contains("基例") && f.Message.Contains("栈")), "Recursive candidate explains base case, shrinking and stack");
var whileCode = await Analyze("def stable_unique(values):\n    while values.count('x') > 0:\n        break\n    return values\n");
Check(Has(whileCode, "while") && Has(whileCode, "count_in_loop"), "While condition repeated count recognized");
var comments = await Analyze("# for Counter recursion dict.fromkeys\ndef stable_unique(values):\n    '''while values.count(x) stable_unique(x) Counter(x)'''\n    return []\n");
Check(!Has(comments, "for") && !Has(comments, "while") && !Has(comments, "counter") && !Has(comments, "recursion") && !Has(comments, "count"), "Comments and strings cannot trigger structural advice");
var unrelated = await Analyze("def helper(values):\n    for x in values: values.count(x)\ndef stable_unique(values):\n    return []\n");
Check(!Has(unrelated, "for") && !Has(unrelated, "count_in_loop"), "Other top-level functions do not contaminate target analysis");
var nested = await Analyze("def stable_unique(values):\n    def helper():\n        return stable_unique(values)\n    return []\n");
Check(!Has(nested, "recursion"), "Nested helper calls are not misattributed as target recursion");
var shadowed = await Analyze("def stable_unique(values):\n    set = list\n    seen = set()\n    return [value for value in values if value not in seen]\n");
Check(shadowed.Analysis!.Limitations.Contains("绑定") && shadowed.Analysis.Findings.Where(f => f.Kind is "set" or "membership_set").All(f => f.Message.Contains("若")), "Shadowed constructor advice remains explicitly conditional");
var fakeCounter = await Analyze("def count_statuses(values):\n    Counter = lambda data: {}\n    return Counter(values)\n", count);
Check(fakeCounter.Analysis!.Findings.Where(f => f.Kind == "counter").All(f => f.Message.Contains("若")), "Same-named Counter is not claimed to have library semantics");
var marker = Path.Combine(Path.GetTempPath(), "EntLimit-analysis-never-exec-" + Guid.NewGuid().ToString("N"));
try
{
    var malicious = await Analyze("__import__('pathlib').Path(" + JsonSerializer.Serialize(marker) + ").write_text('BAD')\nraise RuntimeError('must not execute')\ndef stable_unique(values):\n    return []\n");
    Check(malicious.Status == "analysis" && !File.Exists(marker), "Submitted module side effects and imports never execute during AST analysis");
}
finally { if (File.Exists(marker)) File.Delete(marker); }
var syntax = await Analyze("def stable_unique(:");
Check(syntax.Status == "syntax_error" && syntax.Analysis?.AstAvailable == false && syntax.Message.Contains("第 1 行"), "Syntax error reports line without invented AST findings");
Check(syntax.Analysis!.Findings.Count == 0 && syntax.Analysis.Tutorials.Count >= 2, "Syntax failure can still show clearly generic tutorials");
var missing = await PythonCodeAnalyzer.AnalyzeAsync(dedupe, naive, "C:/not-present/python.exe");
Check(missing.Status == "runtime_missing" && missing.Analysis?.AstAvailable == false && missing.Analysis.Findings.Count == 0 && missing.Message.Contains("通用"), "Missing Python produces explicitly generic guidance");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel(); Check((await Analyze(naive, token: cancel.Token)).Status == "cancelled", "Analysis supports pre-cancellation");
}
Check((await Analyze(new string('x', 65537))).Status == "invalid", "Analysis source size bounded");
var absentFunction = await Analyze("def other(values):\n    for x in values: pass\n");
Check(absentFunction.Analysis?.AstAvailable == true && absentFunction.Analysis.Findings.Count == 0 && absentFunction.Analysis.Summary.Contains("没有找到"), "Missing required function explained separately from syntax error");
Check(basic.Analysis.Tutorials.All(t => t.Steps.Count > 0 && t.Practice.Length > 0 && t.SourceUrl.StartsWith("https://docs.python.org/")), "Tutorials include steps, practice and primary documentation");
Check(basic.Analysis.Tutorials.Any(t => t.Title.Contains("递归") && t.Explanation.Contains("迭代")), "Recursion tutorial avoids recommending recursion for flat counting/dedupe");
var full = await PythonRunner.RunAsync(dedupe, dedupe.Coding.ReferenceCode, python, false);
Check(full.Passed && full.Status == "passed" && full.Analysis?.AstAvailable == true, "Full submission auto-attaches analysis without changing test verdict");
var failed = await PythonRunner.RunAsync(dedupe, "def stable_unique(values): return None\n", python, false);
Check(!failed.Passed && failed.Status == "failed" && failed.Analysis?.AstAvailable == true, "Incorrect full submission also receives analysis");
var example = await PythonRunner.RunAsync(dedupe, dedupe.Coding.ReferenceCode, python, true);
Check(example.Passed && example.Analysis is null, "Example-only execution does not launch analysis");
Console.WriteLine($"{checks} analysis checks passed");
