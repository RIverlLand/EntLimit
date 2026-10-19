using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EntLimit.Core;
using EntLimit.Runner;

namespace EntLimit.JavaPlugin;

public static class JavaRunner
{
    public static async Task<CodeRunResult> RunAsync(RunnerPluginRequest request, CancellationToken token = default)
    {
        static CodeRunResult Fail(string status, string message) => new() { Status = status, Message = message };
        if (ExecutionSafety.IsElevated) return Fail("unsafe_host", "Java 练习不能在管理员进程执行。");
        var coding = request.Exercise.Coding;
        if (coding is null || request.Exercise.LanguageId != "java" || Encoding.UTF8.GetByteCount(request.Code) > 64 * 1024 || !Regex.IsMatch(coding.FunctionName, "^[A-Za-z_][A-Za-z_0-9]*$")) return Fail("invalid", "Java 题目或函数名无效。");
        if (string.IsNullOrWhiteSpace(request.RuntimePath)) return Fail("runtime_missing", "请选择 JDK 根目录（其中应有 bin/javac.exe 和 bin/java.exe）。不会自动下载 JDK。");
        string jdk = Path.GetFullPath(request.RuntimePath);
        string javac = Path.Combine(jdk, "bin", "javac.exe"), java = Path.Combine(jdk, "bin", "java.exe");
        if (!File.Exists(javac) || !File.Exists(java)) return Fail("runtime_missing", "选定目录不是完整 JDK；需要同时包含 javac.exe 与 java.exe。");
        var cases = coding.Cases.Where(c => !request.ExamplesOnly || c.IsExample).ToList();
        if (cases.Count is < 1 or > 128) return Fail("invalid", "没有可用测试用例或用例过多。");
        string folder;
        try { folder = RunnerTemp.CreateDirectory("EntLimit-java-"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Fail("error", "无法创建 Java 运行目录，请检查数据目录或临时目录的写入权限。"); }
        try
        {
            Directory.CreateDirectory(folder);
            var types = coding.ParameterTypes;
            if (types.Length == 0)
            {
                using var first = JsonDocument.Parse(cases[0].ArgumentsJson); types = first.RootElement.EnumerateArray().Select(Infer).ToArray();
            }
            if (types.Length > 8 || types.Any(t => t is not ("int" or "string" or "bool" or "int[]" or "string[]"))) return Fail("invalid", "Java 首版参数支持 int/string/bool/int[]/string[]，空数组需要显式 ParameterTypes。");
            string returnType = coding.ReturnType;
            if (string.IsNullOrEmpty(returnType)) { using var expected = JsonDocument.Parse(cases[0].ExpectedJson); returnType = Infer(expected.RootElement); }
            if (returnType is not ("int" or "string" or "bool" or "int[]" or "string[]")) return Fail("invalid", "Java 返回类型支持 int/string/bool/int[]/string[]，空数组需显式 ReturnType。");
            var source = new StringBuilder("import java.nio.file.*; import java.nio.charset.StandardCharsets; import java.util.*; public class EntLimitTests {\n" + Helpers + "\npublic static void main(String[] args) throws Exception { StringBuilder rows=new StringBuilder(\"[\");\n");
            for (int i = 0; i < cases.Count; i++)
            {
                using var input = JsonDocument.Parse(cases[i].ArgumentsJson);
                if (input.RootElement.ValueKind != JsonValueKind.Array || input.RootElement.GetArrayLength() != types.Length) return Fail("invalid", "测试参数与 Java 类型声明不匹配。");
                source.Append("{ String actual=\"\", error=\"\"; boolean mutated=false; try {\n");
                var names = new List<string>(); var equality = new List<string>(); int argIndex = 0;
                foreach (var item in input.RootElement.EnumerateArray())
                {
                    string name = "a" + argIndex; string type = types[argIndex++]; names.Add(name);
                    source.Append(JavaType(type)).Append(' ').Append(name).Append('=').Append(Literal(item, type)).Append(";\n");
                    if (type.EndsWith("[]")) { source.Append(JavaType(type)).Append(' ').Append(name).Append("Copy=").Append(name).Append(".clone();\n"); equality.Add("Arrays.equals(" + name + "," + name + "Copy)"); }
                }
                source.Append(JavaType(returnType)).Append(" value=Solution.").Append(coding.FunctionName).Append('(').Append(string.Join(',', names)).Append("); actual=json(value);\n");
                if (equality.Count > 0) source.Append("mutated=!(").Append(string.Join("&&", equality)).Append(");\n");
                source.Append("}catch(Throwable ex){ error=ex.getClass().getSimpleName()+\": \"+String.valueOf(ex.getMessage()); if(error.length()>1500)error=error.substring(0,1500); }\n");
                if (i > 0) source.Append("rows.append(',');\n");
                source.Append("rows.append(\"{\\\"actual\\\":\").append(quote(actual.length()>4000?actual.substring(0,4000):actual)).append(\",\\\"error\\\":\").append(quote(error)).append(\",\\\"mutated\\\":\").append(mutated).append('}'); }\n");
            }
            source.Append("rows.append(']');Files.writeString(Path.of(\"result.json\"),rows.toString(),StandardCharsets.UTF_8); } }\n");
            await File.WriteAllTextAsync(Path.Combine(folder, "Solution.java"), request.Code, new UTF8Encoding(false), token);
            await File.WriteAllTextAsync(Path.Combine(folder, "EntLimitTests.java"), source.ToString(), new UTF8Encoding(false), token);
            var compile = await BoundedProcess.RunAsync(javac, ["-J-Xmx128m", "-J-XX:MaxMetaspaceSize=128m", "-J-XX:ReservedCodeCacheSize=32m", "-encoding", "UTF-8", "-proc:none", "-classpath", folder, "-d", folder, "Solution.java", "EntLimitTests.java"], folder, null, 20, 16000, token, 1, 768);
            if (compile.Cancelled) return Fail("cancelled", "Java 编译已取消。");
            if (compile.TimedOut) return Fail("timeout", "Java 编译超过 20 秒，已停止。");
            if (compile.ExitCode != 0) return Fail("compile_error", "Java 编译失败：\n" + compile.Error + compile.Output);
            var run = await BoundedProcess.RunAsync(java, ["-Xmx128m", "-Xss512k", "-XX:MaxMetaspaceSize=128m", "-XX:ReservedCodeCacheSize=32m", "-Dfile.encoding=UTF-8", "-cp", folder, "EntLimitTests"], folder, null, 5, 16000, token, 1, 768);
            if (run.Cancelled) return Fail("cancelled", "Java 运行已取消。");
            if (run.TimedOut) return new() { Status = "timeout", Message = "Java 运行超过 5 秒，已停止。", StandardOutput = run.Output };
            string resultPath = Path.Combine(folder, "result.json");
            if (run.ExitCode != 0 || !File.Exists(resultPath) || new FileInfo(resultPath).Length > 1024 * 1024) return Fail("error", "Java 进程异常退出或未返回有效结果。\n" + run.Error);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, token));
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != cases.Count) return Fail("error", "Java 测试结果不完整。");
            var result = new CodeRunResult { StandardOutput = run.Output + (run.Error.Length > 0 ? "\n[stderr]\n" + run.Error : "") };
            for (int i = 0; i < cases.Count; i++)
            {
                var row = document.RootElement[i]; string actual = row.GetProperty("actual").GetString() ?? ""; string error = row.GetProperty("error").GetString() ?? "";
                if (row.GetProperty("mutated").GetBoolean()) error = "请不要修改传入数组；创建新的结果。";
                bool passed = false;
                if (error.Length == 0) { try { using var got = JsonDocument.Parse(actual); using var expected = JsonDocument.Parse(cases[i].ExpectedJson); passed = Equal(got.RootElement, expected.RootElement); } catch (JsonException) { error = "返回值无法转换为支持的 JSON 类型。"; } }
                result.Cases.Add(new() { Name = cases[i].Name, Expected = cases[i].ExpectedJson, Actual = actual, Error = error, Passed = passed });
            }
            result.Passed = result.Cases.All(c => c.Passed); result.Status = result.Passed ? "passed" : "failed"; result.Message = result.Passed ? "Java 全部测试通过。" : "Java 仍有测试未通过，请查看用例反馈。"; return result;
        }
        catch (OperationCanceledException) { return Fail("cancelled", "Java 练习已取消。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        { return Fail("error", "Java 运行失败，请检查 JDK 与题目参数类型。"); }
        finally { try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
    static string Infer(JsonElement value) => value.ValueKind switch { JsonValueKind.String => "string", JsonValueKind.Number => "int", JsonValueKind.True or JsonValueKind.False => "bool", JsonValueKind.Array when value.GetArrayLength() > 0 => Infer(value[0]) + "[]", _ => "unsupported" };
    static string JavaType(string type) => type.Replace("string", "String").Replace("bool", "boolean");
    static string Literal(JsonElement value, string type) => type switch
    {
        "int" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "string" when value.ValueKind == JsonValueKind.String => "new String(new char[]{" + string.Join(',', value.GetString()!.Select(c => "(char)" + (int)c)) + "})",
        "bool" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean() ? "true" : "false",
        "int[]" or "string[]" when value.ValueKind == JsonValueKind.Array => "new " + JavaType(type) + "{" + string.Join(',', value.EnumerateArray().Select(v => Literal(v, type[..^2]))) + "}",
        _ => throw new InvalidDataException("Unsupported Java argument type.")
    };
    static bool Equal(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Equal(p.First, p.Second)),
            JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count() && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var v) && Equal(p.Value, v)),
            JsonValueKind.Number => a.TryGetDecimal(out var av) && b.TryGetDecimal(out var bv) && av == bv,
            JsonValueKind.String => a.GetString() == b.GetString(),
            _ => a.GetRawText() == b.GetRawText()
        };
    }
    const string Helpers = """
static String quote(String value){StringBuilder b=new StringBuilder("\"");for(int i=0;i<value.length();i++){char c=value.charAt(i);switch(c){case '"':b.append("\\\"");break;case '\\':b.append("\\\\");break;case '\n':b.append("\\n");break;case '\r':b.append("\\r");break;case '\t':b.append("\\t");break;default:if(c<32)b.append(String.format("\\u%04x",(int)c));else b.append(c);}}return b.append('"').toString();}
static String json(Object value){if(value==null)return "null";if(value instanceof String)return quote((String)value);if(value instanceof Number||value instanceof Boolean)return value.toString();if(value instanceof int[]){StringJoiner j=new StringJoiner(",","[","]");for(int n:(int[])value)j.add(Integer.toString(n));return j.toString();}if(value instanceof String[]){StringJoiner j=new StringJoiner(",","[","]");for(String s:(String[])value)j.add(s==null?"null":quote(s));return j.toString();}throw new IllegalArgumentException("Supported return types: int, boolean, String, int[], String[]");}
""";
}
