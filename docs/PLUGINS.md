# Runner plugins (protocol v1)

Runner plugins are executable code, distinct from passive exercise/content packs. Importing a topic JSON never installs or runs a plugin. Python remains the built-in default (`Exercise.LanguageId = "python"`); the Windows distribution explicitly ships the Java plugin. No runtime, compiler or JDK is silently downloaded. Plugins run only after a user starts an exercise, with no resident Java process.

## Install and trust

The app reads **only** `plugins/installed-plugins.json`, not every manifest found on disk. Each index entry pins the exact SHA256 of a manifest. That manifest pins the launcher EXE and every dependency/runtime file. Execution rechecks those hashes. Unlisted files (except the manifest itself and optional development PDBs), changed DLLs/EXEs/config/data/scripts, unsafe relative paths, absolute paths, ADS paths, symlinks and junctions are rejected. Known command interpreters and `.cmd`/`.ps1`/other script entry points cannot be plugins. Directory traversal is bounded. A bad individual plugin is unavailable; built-in Python is independent.

The release packager publishes the self-contained `EntLimit.JavaPlugin` into `plugins/java`, then runs:

```powershell
./scripts/package-java-plugin.ps1 -AppDirectory <published-app-directory>
```

This seals the shipped Java files and creates its explicit installed index. It does not execute an exercise, install a JDK, or read learning data.

For a separately reviewed external plugin, copy its complete self-contained directory beneath `plugins/<id>`, including a hash manifest following `docs/plugins/manifest.example.json`. Then explicitly authorize it using the bundled registration tool:

```powershell
./plugins/java/EntLimit.JavaPlugin.exe --register-plugin <app-directory> <absolute-manifest-path-under-plugins> --consent
```

Registration validates all paths and hashes before updating the installed index. It never launches the newly registered plugin. Program Files installations may need administrator permission for this metadata-only change. **Actual practice refuses an elevated host**, independently of executable manifests; the optional administrator window broker never runs student code.

Hash pinning records explicit local trust. It is not a digital signature or protection against an administrator deliberately replacing both the files and trust index. Use only plugins you trust. Updating any plugin dependency requires reviewing and registering its new manifest.

## JSON transport

The parent starts the pinned EXE directly using `ProcessStartInfo.ArgumentList`, with no shell. One UTF-8 newline-delimited JSON request arrives on stdin; exactly one JSON reply is written to stdout, then the plugin exits. JDK and user-program stdout/stderr must be captured inside `Result.StandardOutput`, never written directly to the protocol stream.

Request (`RunnerPluginRequest`):

```json
{"ProtocolVersion":1,"RequestId":"unique-id","Exercise":{"Id":"java-sample","Kind":"code","LanguageId":"java","Coding":{"FunctionName":"countNonEmpty","ParameterTypes":["string[]"],"ReturnType":"int","Cases":[{"Name":"example","ArgumentsJson":"[[\"a\",\"\"]]","ExpectedJson":"1","IsExample":true}]}},"Code":"public class Solution { public static int countNonEmpty(String[] a) { return 1; } }","RuntimePath":"C:/my-jdk","ExamplesOnly":false}
```

Reply (`RunnerPluginReply`):

```json
{"ProtocolVersion":1,"RequestId":"unique-id","Result":{"Passed":true,"Status":"passed","Message":"All tests passed","StandardOutput":"","Cases":[{"Name":"example","Passed":true,"Expected":"1","Actual":"1","Error":""}]}}
```

The host verifies protocol/request IDs, a complete ordered result for every selected test, and result structure, then recomputes Passed from cases. Failures such as `runtime_missing`, `compile_error`, `timeout`, `cancelled`, `error`, `invalid`, `unsafe_host` cannot grant a permit. Examples and analysis never grant. Plugins do not write config/history/permits; the ordinary Monitor commits accepted successful full submissions.

Limits: code 64 KiB, request 256 KiB, cases 1–128, plugin output 1 MiB, outer plugin deadline 35 seconds. Cancellation kills only the owned plugin process tree. The Windows Job Object allows the host plus bounded compiler/runtime children; each compiler/runtime is separately constrained to one process and 768 MiB committed process memory. Closing jobs kills remaining owned processes. These are resource limits, **not a hostile-code security sandbox**: student Java/Python retain ordinary user filesystem/network permissions and could intentionally inspect or tamper with local test harnesses. Do not execute untrusted downloaded code automatically.

## Java bridge

The Java plugin accepts a **JDK root folder** containing `bin/javac.exe` and `bin/java.exe`; JDK 11+ is required by the harness. Development validation uses a SHA256-verified workspace-only JDK 21; it is not part of the installer. The selected paths are absolute. Inherited `JAVA_TOOL_OPTIONS`, `JDK_JAVA_OPTIONS`, `_JAVA_OPTIONS`, and `CLASSPATH` are removed. The compiler uses `-proc:none`, UTF-8 and an owned temporary classpath, so no content-provided annotation processor is loaded.

The submitted source must define `public class Solution` with the required `public static` method. First-version parameter/return types are `int` (32-bit), `bool` (Java boolean), `string` (Java String), `int[]`, and `string[]` (one-dimensional arrays). At most eight parameters are supported. JSON null/object/nested arrays are unsupported. `ArgumentsJson` is an array of positional arguments. Supply explicit `ParameterTypes`/`ReturnType` for empty arrays; otherwise the first case can infer supported types. Java string arguments are generated from numeric UTF-16 chars instead of interpolating source literals.

Compilation has a 20-second deadline; execution has a shared five-second deadline. Heap is capped at 128 MiB, stack at 512 KiB and metadata/code cache explicitly bounded. Separate result files isolate debug stdout from test results; counts and file lengths are checked. Input arrays are cloned to detect unwanted mutation. Temporary files are cleaned after exit/cancellation. Like the Python practice harness, this does not attempt adversarial anti-cheating.

Java is never sent to the Python AST analyzer. The UI can offer separately authorized online analysis; optional local Python guidance remains Python-only.

## Validation

`tests/EntLimit.Plugin.Tests` accepts `<published-JavaPlugin-folder> <test-JDK-root>`. It copies the plugin to a uniquely generated temporary app directory; it never changes production data, targets, startup entries or API settings. Checks cover explicit consent, path rejection, a real original String[]→int sample, examples, wrong answers, compilation errors, absent JDK, array mutation, stdout flooding, infinite loops, cancellation, Java environment injection, changed manifest/DLL/unlisted files, command interpreters, malformed/null metadata, int[] and Unicode/control-character bridging. No system JDK installation occurs.
