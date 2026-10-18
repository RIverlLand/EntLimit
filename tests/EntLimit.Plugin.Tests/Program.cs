using System.Text.Json;
using EntLimit.Core;
using EntLimit.JavaPlugin;
using EntLimit.Runner;

if (args.Length != 2) { Console.Error.WriteLine("plugin-tests <published-JavaPlugin-directory> <test-JDK-root>"); return 2; }
var source = Path.GetFullPath(args[0]); var jdk = Path.GetFullPath(args[1]);
if (!File.Exists(Path.Combine(source, "EntLimit.JavaPlugin.exe"))) throw new Exception("Publish JavaPlugin before testing.");
var root = Path.Combine(Path.GetTempPath(), "EntLimit-plugin-tests-" + Guid.NewGuid().ToString("N"));
var plugin = Path.Combine(root, "plugins", "java"); Directory.CreateDirectory(plugin);
foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
{
    var destination = Path.Combine(plugin, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
}
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
bool Reject(Action action) { try { action(); return false; } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException) { return true; } }
var exercise = new Exercise { Id = "java-count-nonempty-v1", TopicId = "java", Kind = "code", Mode = "standard", LanguageId = "java", Coding = new() { FunctionName = "countNonEmpty", ParameterTypes = ["string[]"], ReturnType = "int", Cases = [
    new() { Name = "example", ArgumentsJson = "[[\"ready\",\"\",\"ok\"]]", ExpectedJson = "2", IsExample = true },
    new() { Name = "empty", ArgumentsJson = "[[]]", ExpectedJson = "0" },
    new() { Name = "unicode", ArgumentsJson = "[[\"你好\",\" \",\"\"]]", ExpectedJson = "2" }
] } };
const string correct = "public class Solution { public static int countNonEmpty(String[] values) { int n=0; for(String value:values) if(!value.isEmpty()) n++; return n; } }";
Task<CodeRunResult> Run(string code, bool examples = false, CancellationToken token = default, string? runtime = null) => RunnerDispatcher.RunAsync(exercise, code, runtime ?? jdk, examples, root, token);
try
{
    RunnerPluginRegistry.CreateManifest(plugin, "java-local", "Java local test runner", "java", "EntLimit.JavaPlugin.exe");
    var manifest = Path.Combine(plugin, "manifest.json");
    Check(RunnerPluginRegistry.GetInstalled(root).Count == 0, "Manifest file alone never authorizes executable plugin");
    Check(Reject(() => RunnerPluginRegistry.RegisterInstalled(root, manifest, false)), "Plugin install requires explicit consent");
    RunnerPluginRegistry.RegisterInstalled(root, manifest, true);
    Check(RunnerPluginRegistry.GetInstalled(root).Single().Trusted, "Explicit install hash-pins plugin and runtime dependencies");
    Check(Reject(() => RunnerPluginRegistry.ResolveRelative(plugin, "../cmd.exe")) && Reject(() => RunnerPluginRegistry.ResolveRelative(plugin, "C:/Windows/System32/cmd.exe")), "Plugin traversal and absolute paths refused");
    var passed = await Run(correct);
    Check(passed.Passed && passed.Cases.Count == 3, "Real Java static String[] function compiles and passes original sample");
    Check((await Run(correct, true)).Cases.Count == 1, "Java examples-only runs subset");
    Check(!(await Run("public class Solution { public static int countNonEmpty(String[] values) {return -1;} }")).Passed, "Wrong Java result rejected");
    Check((await Run("public class Solution { !!! }")).Status == "compile_error", "Java syntax diagnostics returned");
    Check((await Run(correct, runtime: Path.Combine(root, "no-jdk"))).Status == "runtime_missing", "Missing JDK is clear and causes no download");
    var mutated = await Run("public class Solution { public static int countNonEmpty(String[] values) {if(values.length>0)values[0]=\"changed\";return 0;} }");
    Check(!mutated.Passed && mutated.Cases.Any(c => c.Error.Contains("数组")), "Java input mutation detected");
    var output = await Run("public class Solution { public static int countNonEmpty(String[] values) { System.out.print(\"x\".repeat(100000)); int n=0;for(String s:values)if(!s.isEmpty())n++;return n;} }");
    Check(output.Passed && output.StandardOutput.Length < 17000, "Java stdout flood bounded without spoofing machine result");
    Check((await Run("public class Solution { public static int countNonEmpty(String[] values) {while(true){} } }")).Status == "timeout", "Java infinite loop timeout");
    using (var cancel = new CancellationTokenSource(500)) Check((await Run(correct, token: cancel.Token)).Status == "cancelled", "Plugin process tree cancellation");
    var environment = Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS"); Environment.SetEnvironmentVariable("JAVA_TOOL_OPTIONS", "-XX:THIS_MUST_BE_REMOVED");
    try { Check((await Run(correct)).Passed, "Inherited Java injection options cleared"); } finally { Environment.SetEnvironmentVariable("JAVA_TOOL_OPTIONS", environment); }
    var bytes = await File.ReadAllBytesAsync(manifest); await File.AppendAllTextAsync(manifest, " ");
    Check((await Run(correct)).Status == "plugin_unavailable", "Manifest change invalidates consent hash"); await File.WriteAllBytesAsync(manifest, bytes);
    var dependency = Path.Combine(plugin, "EntLimit.JavaPlugin.dll"); var original = await File.ReadAllBytesAsync(dependency); await File.AppendAllTextAsync(dependency, "tampered");
    Check((await Run(correct)).Status == "plugin_unavailable", "Plugin DLL tampering rejected before execution"); await File.WriteAllBytesAsync(dependency, original);
    var extra = Path.Combine(plugin, "untrusted.dll"); await File.WriteAllTextAsync(extra, "not trusted");
    Check((await Run(correct)).Status == "plugin_unavailable", "Unpinned executable dependency rejected"); File.Delete(extra);
    var wrongManifest = JsonSerializer.Deserialize<RunnerPluginManifest>(bytes)!; wrongManifest.Executable = "run.cmd"; wrongManifest.Files["run.cmd"] = new string('A',64); await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(wrongManifest));
    Check(Reject(() => RunnerPluginRegistry.RegisterInstalled(root, manifest, true)), "Shell script plugin entry refused even with explicit request"); await File.WriteAllBytesAsync(manifest, bytes);
    using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); Check((await Run(correct, token: cancelled.Token)).Status == "cancelled", "Pre-cancel avoids plugin process"); }
    var direct = await JavaRunner.RunAsync(new() { Exercise = exercise, Code = correct, RuntimePath = jdk });
    Check(direct.Passed, "Java bridge also works without outer plugin host");
    var sum = new Exercise { Id = "java-sum-test", Kind = "code", LanguageId = "java", Coding = new() { FunctionName = "sum", ParameterTypes = ["int[]"], ReturnType = "int", Cases = [new() { Name = "integers", ArgumentsJson = "[[1,-2,4]]", ExpectedJson = "3" }] } };
    Check((await JavaRunner.RunAsync(new() { Exercise = sum, Code = "public class Solution { public static int sum(int[] a) {int total=0;for(int x:a)total+=x;return total;} }", RuntimePath = jdk })).Passed, "Java int[] parameter bridge works");
    var echo = new Exercise { Id = "java-string-test", Kind = "code", LanguageId = "java", Coding = new() { FunctionName = "echo", ParameterTypes = ["string"], ReturnType = "string", Cases = [new() { Name = "escaped", ArgumentsJson = JsonSerializer.Serialize(new[] { "你好\n\\\"" }), ExpectedJson = JsonSerializer.Serialize("你好\n\\\"") }] } };
    Check((await JavaRunner.RunAsync(new() { Exercise = echo, Code = "public class Solution { public static String echo(String value) {return value;} }", RuntimePath = jdk })).Passed, "Java string/Unicode/control characters roundtrip safely");
    var malformed = JsonSerializer.Deserialize<RunnerPluginManifest>(bytes)!; malformed.Files = null!; await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(malformed));
    Check(Reject(() => RunnerPluginRegistry.RegisterInstalled(root, manifest, true)), "Null manifest file table rejected without crashing"); await File.WriteAllBytesAsync(manifest, bytes);
    malformed = JsonSerializer.Deserialize<RunnerPluginManifest>(bytes)!; malformed.Id = null!; await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(malformed));
    Check(Reject(() => RunnerPluginRegistry.RegisterInstalled(root, manifest, true)), "Null plugin identity rejected without crashing"); await File.WriteAllBytesAsync(manifest, bytes);
    var indexPath = Path.Combine(root, "plugins", "installed-plugins.json"); var indexBytes = await File.ReadAllBytesAsync(indexPath); await File.WriteAllTextAsync(indexPath, "[null]");
    Check(RunnerPluginRegistry.GetInstalled(root).Single().Trusted == false && RunnerDispatcher.GetInstalled(root).Any(p => p.Id == "python-builtin" && p.Trusted), "Malformed index stays unavailable while built-in Python remains usable"); await File.WriteAllBytesAsync(indexPath, indexBytes);
    malformed = JsonSerializer.Deserialize<RunnerPluginManifest>(bytes)!; malformed.Executable = "cmd.exe"; await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(malformed));
    Check(Reject(() => RunnerPluginRegistry.RegisterInstalled(root, manifest, true)), "Command interpreter executable cannot become plugin entry point"); await File.WriteAllBytesAsync(manifest, bytes);
    var shellFolder = Path.Combine(root, "plugins", "renamed-shell"); Directory.CreateDirectory(shellFolder);
    File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Path.Combine(shellFolder, "ExampleRunner.exe"));
    RunnerPluginRegistry.CreateManifest(shellFolder, "shell-test", "Do not run", "shell-test", "ExampleRunner.exe");
    Check(Reject(() => RunnerPluginRegistry.RegisterInstalled(root, Path.Combine(shellFolder, "manifest.json"), true)), "Renaming a Windows command interpreter does not bypass entry validation");
    Console.WriteLine($"{checks} plugin checks passed"); return 0;
}
finally { try { Directory.Delete(root, true); } catch (IOException) { } }
