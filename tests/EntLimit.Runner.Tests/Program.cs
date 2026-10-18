using EntLimit.Core;
using EntLimit.Runner;

if (args.Contains("--discovery-only"))
{
    // A UI thread does not pump posted continuations during a synchronous discovery call.
    var previous = SynchronizationContext.Current;
    try
    {
        SynchronizationContext.SetSynchronizationContext(new NoPumpSynchronizationContext());
        var discovered = PythonRunner.DiscoverPython();
        if (string.IsNullOrWhiteSpace(discovered)) throw new Exception("Discovery failed with a non-pumping UI context.");
        Console.WriteLine("PASS Python discovery on a non-pumping UI synchronization context");
    }
    finally { SynchronizationContext.SetSynchronizationContext(previous); }
    return;
}
var python = Environment.GetEnvironmentVariable("ENT_LIMIT_TEST_PYTHON");
if (string.IsNullOrEmpty(python)) throw new Exception("Set ENT_LIMIT_TEST_PYTHON to an actual Python 3 executable.");
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
var exercise = new Exercise { Kind = "code", Coding = new CodingExercise { FunctionName = "add", Cases = [
    new CodeCase { Name = "example", ArgumentsJson = "[1,2]", ExpectedJson = "3", IsExample = true },
    new CodeCase { Name = "negative", ArgumentsJson = "[-2,3]", ExpectedJson = "1" }
] } };
Task<CodeRunResult> Run(string code, bool examples = false, CancellationToken token = default) => PythonRunner.RunAsync(exercise, code, python, examples, token);
var correct = await Run("def add(a,b):\n    return a+b");
Check(correct.Passed && correct.Cases.Count == 2, "correct function all tests");
Check((await Run("def add(a,b):\n    return a+b", true)).Cases.Count == 1, "examples only");
Check(!(await Run("def add(a,b):\n    return a-b")).Passed, "wrong result");
Check((await Run("def add(:")).Cases.All(c => c.Error.Contains("SyntaxError")), "syntax error feedback");
Check((await Run("def other(): pass")).Cases.All(c => c.Error.Contains("KeyError")), "missing entry function");
Check((await Run("def add(a,b):\n    raise ValueError('oops')")).Cases.All(c => c.Error.Contains("ValueError")), "runtime error feedback");
Check(!(await Run("def add(a,b):\n    return True")).Passed, "boolean not integer");
var fake = await Run("print('{\"passed\":true,\"cases\":[]}')\ndef add(a,b): return 0");
Check(!fake.Passed && fake.StandardOutput.Contains("passed"), "stdout cannot spoof machine result");
var flood = await Run("print('x'*100000)\ndef add(a,b): return a+b");
Check(flood.Passed && flood.StandardOutput.Length < 17000 && flood.StandardOutput.Contains("截断"), "stdout bound");
var timeout = await Run("while True: pass");
Check(timeout.Status == "timeout", "infinite loop timeout");
using (var stop = new CancellationTokenSource(500))
    Check((await Run("while True: pass", token: stop.Token)).Status == "cancelled", "cancel running code");
using (var stop = new CancellationTokenSource())
{
    stop.Cancel();
    Check((await Run("def add(a,b): return a+b", token: stop.Token)).Status == "cancelled", "pre-cancel");
}
Check((await PythonRunner.RunAsync(exercise, "pass", "C:/not-present/python.exe", false)).Status == "runtime_missing", "missing runtime friendly");
Check((await Run(new string('x', 65537))).Status == "invalid", "code size limit");
Check((await Run("import os;os._exit(0)")).Status == "error", "premature exit rejected");
Check((await Run("import os;os._exit(2)")).Status == "error", "nonzero process rejected");
var stderr = await Run("import sys;print('debug',file=sys.stderr)\ndef add(a,b): return a+b");
Check(stderr.Passed && stderr.StandardOutput.Contains("debug"), "stderr capture");
if (OperatingSystem.IsWindows())
{
    var child = await Run("import subprocess\ndef add(a,b):\n    subprocess.run(['cmd.exe','/c','exit','0'])\n    return a+b");
    Check(!child.Passed, "job prevents child process creation");
    var memory = await Run("def add(a,b):\n    z=bytearray(1024*1024*1024)\n    return a+b");
    Check(!memory.Passed, "memory bound");
}
Check(!string.IsNullOrEmpty(PythonRunner.DiscoverPython()), "discover registered runtime");
var unicode = await Run("print('你好，Python')\ndef add(a,b): return a+b");
Check(unicode.Passed && unicode.StandardOutput.Contains("你好"), "UTF8 output");
var continuous = await Run("while True: print('x'*10000)");
Check(continuous.Status == "timeout" && continuous.StandardOutput.Length < 17000, "continuous output flood timeout and bound");
foreach (var sample in ExerciseCatalog.All.Where(e => e.Kind == "code"))
{
    var result = await PythonRunner.RunAsync(sample, sample.Coding!.ReferenceCode, python, false);
    Check(result.Passed, "sample reference " + sample.Id + ": " + result.Message);
}
var typedExercise = new Exercise { Kind = "code", Coding = new CodingExercise { FunctionName = "f", Cases = [new CodeCase { Name = "nested", ArgumentsJson = "[[1,2]]", ExpectedJson = "{\"count\":1}" }] } };
Check(!(await PythonRunner.RunAsync(typedExercise, "def f(a): return {'count':True}", python, false)).Passed, "nested boolean differs from integer");
var mutation = await PythonRunner.RunAsync(typedExercise, "def f(a):\n    a.append(3)\n    return {'count':1}", python, false);
Check(!mutation.Passed && mutation.Cases[0].Error.Contains("参数"), "input mutation rejected");
Console.WriteLine($"{checks} runner checks passed");



sealed class NoPumpSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) { }
}
