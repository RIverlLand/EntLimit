using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EntLimit.Core;
using EntLimit.Runner;

namespace EntLimit.Monitor;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        bool watchdog = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--watchdog") watchdog = true;
            else if (args[i] == "--data-dir" && i + 1 < args.Length)
                Environment.SetEnvironmentVariable("ENT_LIMIT_DATA_DIR", Path.GetFullPath(args[++i]));
            else return;
        }
        if (watchdog) { GuardRuntime.RunWatchdog(DataPaths.GetRoot()); return; }
        using var mutex = new Mutex(true, GuardRuntime.MonitorMutexName, out bool owner);
        if (!owner) return;
        ApplicationConfiguration.Initialize();
        try { Application.Run(new MonitorContext()); }
        catch (Exception ex)
        {
            // Background failures must never take focus from a running fullscreen game.
            try
            {
                string root = DataPaths.GetRoot(); Directory.CreateDirectory(root);
                string path = Path.Combine(root, "monitor-startup-error.log");
                File.WriteAllText(path, DateTimeOffset.UtcNow + " " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine);
            }
            catch { }
            Environment.ExitCode = 1;
        }
    }
}

internal sealed class MonitorContext : ApplicationContext
{
    readonly LocalStore store = new(DataPaths.GetRoot());
    AppConfig config;
    SessionEngine engine;
    List<AppCatalogEntry> catalog;
    readonly Control dispatcher = new();
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    readonly CancellationTokenSource stop = new();
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    readonly DateTimeOffset anchor = DateTimeOffset.UtcNow;
    readonly Native.WinEvent callback;
    readonly IntPtr hook;
    readonly ProcessLineage lineage = new();
    readonly StartupRegistration startup = new(Environment.ProcessPath!, DataPaths.GetRoot());
    DateTimeOffset lastGate = DateTimeOffset.MinValue;
    DateTimeOffset lastSaved;
    DateTimeOffset lastGateLog = DateTimeOffset.MinValue;
    string? lastGatedApp;
    bool disposed;
    long rewardEpoch;
    CodeWork? codeWork;
    readonly ContentPackStore content;
    string? discoveredPython;
    readonly Task<string?> pythonDiscovery = Task.Run(PythonRunner.DiscoverPython);
    readonly Task<List<RunnerPluginInfo>> pluginDiscovery = Task.Run(() => RunnerDispatcher.GetInstalled());
    List<RunnerPluginInfo> runnerPlugins = [];
    sealed class CodeWork
    {
        public required string Id;
        public required Exercise Exercise;
        public required string AttemptId;
        public required Task<CodeRunResult> Task;
        public required CancellationTokenSource Cancellation;
        public bool ExamplesOnly, AnalysisOnly, AllowGrant, RewardProcessed, Cancelled;
        public long RewardEpoch;
        public string Message = "正在运行…";
    }
    DateTimeOffset Now => anchor + elapsed.Elapsed;
    bool EmergencyStopped => EmergencyStop.IsActive(store.Root);
    bool MonitoringActive => config.MonitoringEnabled && !EmergencyStopped;

    public MonitorContext()
    {
        config = store.LoadConfig(); engine = new(store.LoadSession()) { WarningMinutes = config.WarningMinutes }; catalog = store.LoadCatalog();
        content = new ContentPackStore(store.Root);
        dispatcher.CreateControl();
        var menu = new ContextMenuStrip();
        tray = new NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "EntLimit", ContextMenuStrip = menu, Visible = true };
        menu.Items.Add("打开 EntLimit", null, (_, _) => OpenUi());
        menu.Items.Add("结束本次娱乐", null, (_, _) => { if (EmergencyStopped) return; engine.Finish(); rewardEpoch++; Persist(); RecheckForeground(); UpdateTray(); });
        menu.Items.Add("退出监控", null, (_, _) => { if (!config.GuardEnabled || EmergencyStopped) ExitThread(); else tray.Text = "EntLimit · 守护已启用，请使用调试停用入口退出"; });
        menu.Items.Add("调试：停用全部功能并退出", null, (_, _) =>
        {
            try { DisableAll(); ExitThread(); }
            catch (Exception ex) { Log(ex); tray.Text = "EntLimit · 停用失败，请打开设置重试"; }
        });
        tray.DoubleClick += (_, _) => OpenUi();
        // Persisted identities can be frontend child EXEs. Validate those exact PID,
        // creation time and image triples; a main-executable name scan loses their grace.
        var alive = engine.State.ActiveProcesses.Concat(engine.State.DrainingProcesses).Distinct()
            .Where(p => Native.Identity(p.Pid) is { } actual && actual == p).ToArray();
        if (!EmergencyStopped) { engine.Reconcile(alive, Now); Persist(); }
        callback = (_, _, hwnd, _, _, _, _) =>
        {
            // Events can queue while IPC or startup runs. Always sample current focus on
            // the mutation thread, rather than minimizing an obsolete event window.
            if (!disposed && hwnd != IntPtr.Zero)
            { try { dispatcher.BeginInvoke(RecheckForeground); } catch (InvalidOperationException) { } }
        };
        hook = Native.SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 2);
        if (hook == IntPtr.Zero) throw new InvalidOperationException("无法注册前台窗口监听。");
        timer.Tick += (_, _) => CheckTracked(); timer.Start();
        _ = ServeAsync(stop.Token);
        GuardRuntime.WriteMonitorOwner(store.Root);
        GuardRuntime.EnsureStarted(config, store.Root);
        RecheckForeground(); UpdateTray();
    }

    void Persist() { if (EmergencyStopped) return; store.SaveSession(engine.State); lastSaved = Now; }
    void UpdateTray() => tray.Text = EmergencyStopped ? "EntLimit · 已紧急停用，请在设置中明确恢复" : !config.MonitoringEnabled ? "EntLimit · 管理已暂停" : engine.State.Phase switch
    { SessionPhase.Locked => "EntLimit · 先学一点", SessionPhase.Ready => "EntLimit · 已解锁，等待开始", SessionPhase.Active => "EntLimit · 安心娱乐", _ => "EntLimit · 时间已到，请完成后收尾" };

    void Foreground(IntPtr hwnd)
    {
        try { ForegroundCore(hwnd); } catch (Exception ex) { Log(ex); }
    }
    void RecheckForeground()
    {
        if (!disposed) Foreground(Native.GetForegroundWindow());
    }
    void ForegroundCore(IntPtr hwnd)
    {
        if (!MonitoringActive || hwnd == IntPtr.Zero || hwnd != Native.GetForegroundWindow() || !config.Apps.Any(a => a.Enabled)) return;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        var id = Native.Identity((int)pid); if (id is null) return;
        var (app, childMatch) = MatchApp(id);
        if (app is null) return;
        Tick();
        if (engine.CanUse(id, Now))
        {
            var phase = engine.State.Phase;
            bool wasTracked = engine.State.ActiveProcesses.Contains(id);
            if (engine.ObserveUse(id, Now) && (phase == SessionPhase.Ready || !wasTracked && engine.State.Phase == SessionPhase.Active || phase != engine.State.Phase)) Persist();
            UpdateTray(); return;
        }
        var top = Native.GetAncestor(hwnd, 2);
        if (top == IntPtr.Zero) top = hwnd;
        Native.GetWindowThreadProcessId(top, out uint topPid);
        if (!MonitoringActive || topPid != pid || hwnd != Native.GetForegroundWindow()) return;
        bool minimized = Native.ShowWindowAsync(top, 6);
        if (lastGatedApp != app.Id || Now - lastGateLog >= TimeSpan.FromSeconds(30))
        {
            lastGatedApp = app.Id; lastGateLog = Now;
            LogEvent($"gate matchedConfiguredApp=true mode={(childMatch ? "child" : "primary")} pid={pid} phase={engine.State.Phase} minimizeQueued={minimized}");
        }
        if (MonitoringActive && Now - lastGate > TimeSpan.FromSeconds(2))
        { lastGate = Now; OpenUi(gate: true); }
    }

    IpcResponse AdminGate(IpcRequest request)
    {
        if (!MonitoringActive || request.GateProcess is not { } supplied || request.WindowHandle == 0) return Snapshot();
        var hwnd = new IntPtr(request.WindowHandle);
        if (hwnd != Native.GetForegroundWindow()) return Snapshot();
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != supplied.Pid || Native.Identity((int)pid) is not { } actual || actual != supplied) return Snapshot();
        var (app, _) = MatchApp(actual);
        if (app is null) return Snapshot();
        if (engine.CanUse(actual, Now))
        {
            var phase = engine.State.Phase;
            bool tracked = engine.State.ActiveProcesses.Contains(actual);
            if (engine.ObserveUse(actual, Now) && (phase == SessionPhase.Ready
                || !tracked && engine.State.Phase == SessionPhase.Active || phase != engine.State.Phase)) Persist();
            return Snapshot();
        }
        // The explicit elevated helper may minimize only this verified foreground identity.
        // Monitoring and reward decisions stay in this ordinary user process.
        if (Now - lastGate > TimeSpan.FromSeconds(2)) { lastGate = Now; OpenUi(gate: true); }
        var denied = Snapshot(); denied.GateDenied = MonitoringActive; return denied;
    }

    (ManagedApp? App, bool Child) MatchApp(ProcessIdentity id)
    {
        var enabled = config.Apps.Where(a => a.Enabled).ToArray();
        var app = enabled.FirstOrDefault(a => ApplicationProcessMatcher.Matches(a, id, []));
        bool childMatch = false;
        if (app is null)
        {
            var potential = enabled.Where(a => ProcessLineage.IsPotentialChild(a, id)).ToArray();
            if (potential.Length == 0) return (null, false);
            var ancestors = lineage.ValidatedAncestors(id);
            app = potential.FirstOrDefault(a => ApplicationProcessMatcher.Matches(a, id, ancestors));
            childMatch = app is not null;
        }
        return (app, childMatch);
    }

    List<ManagedWindowStatus> InspectManagedWindows()
    {
        var result = new List<ManagedWindowStatus>();
        if (!config.Apps.Any(a => a.Enabled)) return result;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            var identity = Native.Identity((int)pid);
            if (identity is null) return true;
            var (app, child) = MatchApp(identity);
            if (app is not null) result.Add(new() { AppId = app.Id, ProcessName = Path.GetFileNameWithoutExtension(identity.ExecutablePath), ProcessId = identity.Pid, WindowHandle = hwnd.ToInt64(), IsMinimized = Native.IsIconic(hwnd), MatchKind = child ? "child" : "primary" });
            return result.Count < 200;
        }, IntPtr.Zero);
        return result;
    }

    void Tick()
    {
        if (EmergencyStopped) { UpdateTray(); return; }
        var phase = engine.State.Phase;
        var result = engine.Tick(Now);
        if (result.Changed && (phase != engine.State.Phase || result.Warning || result.Expired || Now - lastSaved >= TimeSpan.FromMinutes(1))) Persist();
        if (result.Warning) Remind($"还有 {config.WarningMinutes} 分钟，到合适的地方就收尾。", false);
        if (result.Expired) Remind("娱乐时间已结束，当前游戏退出后重新学习解锁。", true);
        UpdateTray();
    }

    void CheckTracked()
    {
        try
        {
            if (EmergencyStopped) { UpdateTray(); return; }
            Tick();
            GuardRuntime.EnsureStarted(config, store.Root);
            var candidates = engine.State.ActiveProcesses.Concat(engine.State.DrainingProcesses).Distinct().ToArray();
            var alive = candidates.Where(p => Native.Identity(p.Pid) is { } actual && actual == p).ToArray();
            if (alive.Length != candidates.Length) { engine.Reconcile(alive, Now); Persist(); }
            // Cheap fallback for a missed foreground event or a restored window.
            RecheckForeground();
            UpdateTray();
        }
        catch (Exception ex) { Log(ex); }
    }

    void Remind(string message, bool expired)
    {
        if (!MonitoringActive) return;
        if (config.SoundEnabled) System.Media.SystemSounds.Asterisk.Play();
        // Explicit opt-in: ordinary HWND cannot guarantee visibility over exclusive fullscreen.
        if (config.OverlayEnabled) { var overlay = new ReminderForm(message); overlay.Show(); }
    }

    void OpenUi(bool gate = false)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "EntLimit.Desktop.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("主界面程序缺失。请从完整的发布目录运行。");
            var start = new ProcessStartInfo(path) { UseShellExecute = true };
            if (gate) start.ArgumentList.Add("--gate");
            Process.Start(start);
        }
        catch (Exception ex) { Log(ex); tray.Text = "EntLimit · 无法打开主界面，请检查发布目录"; }
    }

    async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeProtocol.Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                string line = await PipeClient.ReadLineAsync(pipe, PipeProtocol.MaxRequestBytes, timeout.Token);
                IpcRequest? parsed = null;
                try { parsed = JsonSerializer.Deserialize<IpcRequest>(line, PipeProtocol.JsonOptions); } catch (JsonException) { }
                List<AppCatalogEntry>? scanned = null;
                if (parsed?.Command == "scan")
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(120));
                    scanned = await Task.Run(() => AppScanner.Scan(timeout.Token), timeout.Token);
                }
                var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        var request = parsed ?? throw new JsonException();
                        if (scanned is not null) { store.SaveCatalog(scanned); catalog = scanned; }
                        tcs.SetResult(Handle(request));
                    }
                    catch (JsonException) { tcs.SetResult(Snapshot("请求格式无效。")); }
                    catch (Exception ex) { Log(ex); tcs.SetResult(Snapshot("无法完成操作，请检查本地数据。")); }
                });
                var response = await tcs.Task.WaitAsync(timeout.Token);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, PipeProtocol.JsonOptions).AsMemory(), timeout.Token);
                // Acknowledge on the pipe before ending the UI message loop. This lets
                // a controlled updater wait for successful shutdown and the mutex.
                if (parsed?.Command is "shutdown" or "debug_stop" && response.Ok)
                { dispatcher.BeginInvoke(ExitThread); return; }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log(ex); }
        }
    }

    IpcResponse Snapshot(string? error = null)
    {
        var snapshot = new IpcResponse { Ok = error is null, Error = error, EmergencyStopped = EmergencyStopped, Config = config, Session = engine.State, Catalog = catalog, Questions = SeedQuestions.All.ToList(), Exercises = content.Exercises.ToList(), Topics = content.Topics.ToList(), ContentPacks = content.Packs.ToList(), ContentWarnings = content.Warnings.ToList(), PythonPath = discoveredPython, RunnerPlugins = runnerPlugins };
        // Copy on the mutation thread so serialization on the pipe thread cannot race updates.
        var copy = JsonSerializer.Deserialize<IpcResponse>(JsonSerializer.Serialize(snapshot, PipeProtocol.JsonOptions), PipeProtocol.JsonOptions)!;
        if (copy.EmergencyStopped)
            copy.Config.StartupEnabled = copy.Config.GuardEnabled = copy.Config.MonitoringEnabled = copy.Config.SoundEnabled = copy.Config.OverlayEnabled = false;
        return copy;
    }
    IpcResponse Handle(IpcRequest request)
    {
        Tick();
        switch (request.Command)
        {
            case "export_pack_prompt":
                var prompted = Snapshot(); prompted.PromptText = content.ExportPrompt(); return prompted;
            case "preview_pack":
                try { var previewed = Snapshot(); previewed.PackPreview = content.Preview(request.PackJson ?? ""); return previewed; }
                catch (InvalidDataException ex) { return Snapshot(ex.Message); }
            case "import_pack":
                if (codeWork is { RewardProcessed: false, Cancelled: false }) return Snapshot("请先完成或停止当前代码任务，再导入题库。");
                try { content.Import(request.PackJson ?? "", request.PackChoices ?? [], request.ExpectedCatalogRevision ?? ""); }
                catch (InvalidDataException ex) { return Snapshot(ex.Message); }
                break;
            case "admin_gate":
                return AdminGate(request);
            case "grade_exercise":
                var exercise = content.Exercises.FirstOrDefault(x => x.Id == request.ExerciseId && x.Kind == "scenario");
                if (exercise is null || !ValidAttempt(request.AttemptId)) return Snapshot("练习或答题记录无效。");
                if (!string.IsNullOrEmpty(exercise.ReviewOfId) && !engine.State.History.Any(x => x.QuestionId == exercise.ReviewOfId))
                    return Snapshot("这是一道已学内容的复习题，请先完成对应原题。");
                if (request.Answers is null || request.Answers.Count > 30 || request.Answers.Any(x => x.Key.Length > 128 || x.Value is null || x.Value.Length > 2048)) return Snapshot("请填写有效答案。");
                var grade = ExerciseGrader.Grade(exercise, request.Answers);
                if (grade.Passed) CommitExercise(exercise, request.AttemptId!, true);
                UpdateTray();
                var graded = Snapshot(); graded.Grade = grade; return graded;
            case "start_code":
                return StartCode(request);
            case "start_analysis":
                return StartCode(request, analysisOnly: true);
            case "code_status":
                return CodeStatus(request.JobId);
            case "cancel_code":
                if (codeWork is null || codeWork.Id != request.JobId) return Snapshot("这个运行任务已经不存在。");
                codeWork.Cancelled = true; codeWork.Cancellation.Cancel(); codeWork.Message = codeWork.AnalysisOnly ? "已停止本次分析，草稿会保留。" : "已停止本次运行，草稿会保留。";
                return CodeStatus(request.JobId);
            case "inspect_windows":
                var diagnostic = Snapshot(); diagnostic.ManagedWindows = InspectManagedWindows(); return diagnostic;
            case "status":
                if (pythonDiscovery.IsCompletedSuccessfully) discoveredPython = pythonDiscovery.Result;
                if (pluginDiscovery.IsCompletedSuccessfully) runnerPlugins = pluginDiscovery.Result;
                break;
            case "scan": break; // Discovery ran on worker; only publishing mutates on this thread.
            case "save_config":
                if (request.Config is null) return Snapshot("缺少配置。");
                if (EmergencyStopped && (request.Config.MonitoringEnabled || request.Config.GuardEnabled || request.Config.StartupEnabled || request.Config.SoundEnabled || request.Config.OverlayEnabled))
                    return Snapshot("紧急停用闩锁仍在生效，请使用明确恢复入口（resume_management）后再启用功能。");
                if (request.Config.Apps is null || request.Config.Domains is null || request.Config.Apps.Count > 1000 || request.Config.Domains.Count > 1000) return Snapshot("应用和网站配置无效或条目过多。");
                if (request.Config.Apps.Any(a => a is null || string.IsNullOrWhiteSpace(a.Id) || a.Id.Length > 128 || string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 512) || request.Config.Apps.Select(a => a.Id).Distinct().Count() != request.Config.Apps.Count) return Snapshot("应用名称或标识无效。");
                if (request.Config.Domains.Any(d => string.IsNullOrWhiteSpace(d) || d.Length > 253 || Uri.CheckHostName(d) != UriHostNameType.Dns)) return Snapshot("请填写有效的网站域名，例如 www.bilibili.com。");
                if (request.Config.UnlockMinutes < 90 || request.Config.UnlockMinutes > 1440 || request.Config.WarningMinutes < 0 || request.Config.WarningMinutes >= request.Config.UnlockMinutes) return Snapshot("娱乐时长须在 90 至 1440 分钟之间，提前提醒时间须小于时长。");
                foreach (var managedEntry in request.Config.Apps)
                {
                    if (string.IsNullOrWhiteSpace(managedEntry.ExecutablePath) || !Path.IsPathFullyQualified(managedEntry.ExecutablePath) || !managedEntry.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(managedEntry.ExecutablePath)) return Snapshot("请选择有效且存在的绝对 EXE 路径。");
                    if (Path.GetFileName(managedEntry.ExecutablePath).StartsWith("EntLimit.", StringComparison.OrdinalIgnoreCase)) return Snapshot("EntLimit 自身不能加入受管控列表。");
                }
                SaveConfig(request.Config);
                LogEvent($"config_saved enabledApps={config.Apps.Count(a => a.Enabled)}");
                RecheckForeground(); break;
            case "complete":
                if (!MonitoringActive) return Snapshot("管理已暂停；恢复管理后可通过此入口获得许可。");
                var q = SeedQuestions.All.FirstOrDefault(q => q.Id == request.QuestionId);
                if (q is null || string.IsNullOrWhiteSpace(request.AttemptId)) return Snapshot("题目或答题记录无效。");
                // Earned knowledge and permit are committed together before becoming usable.
                var candidateState = JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(engine.State, PipeProtocol.JsonOptions), PipeProtocol.JsonOptions)!;
                var candidateEngine = new SessionEngine(candidateState) { WarningMinutes = config.WarningMinutes };
                if (!candidateEngine.Complete(q, request.AnswerIndex, request.AttemptId, Now, config.UnlockMinutes)) return Snapshot("答案尚未正确，或当前娱乐尚未结束。");
                if (!MonitoringActive) return Snapshot("管理已暂停，本次未发放许可。");
                store.SaveSession(candidateState);
                engine = candidateEngine; lastSaved = Now; rewardEpoch++; break;
            case "launch":
                var app = config.Apps.FirstOrDefault(a => a.Id == request.AppId && a.Enabled);
                if (app is null || !File.Exists(app.ExecutablePath)) return Snapshot("应用不存在或尚未启用。");
                if (MonitoringActive && engine.State.Phase is not (SessionPhase.Ready or SessionPhase.Active)) return Snapshot("请先完成学习，再启动应用。");
                Process.Start(new ProcessStartInfo(app.ExecutablePath) { UseShellExecute = true });
                if (MonitoringActive) { engine.ActivateWebsite(Now); Persist(); } break;
            case "browser_use":
                if (!MonitoringActive) break;
                if (!engine.ActivateWebsite(Now)) return Snapshot("请先完成学习，或结束当前娱乐。");
                Persist(); break;
            case "finish":
                if (EmergencyStopped) return Snapshot("紧急停用期间保留原许可，请恢复管理后再结束娱乐。");
                engine.Finish(); rewardEpoch++; Persist(); RecheckForeground(); break;
            case "debug_disable":
            case "debug_stop": DisableAll(); break;
            case "resume_management": ResumeManagement(); RecheckForeground(); break;
            case "shutdown":
                if (config.GuardEnabled && !EmergencyStopped) return Snapshot("自用守护已启用。请使用调试停用入口（debug_stop）关闭全部功能并退出。");
                Persist(); break;
            case "open_ui": OpenUi(); break;
            default: return Snapshot("未知操作。");
        }
        UpdateTray(); return Snapshot();
    }

    static bool ValidAttempt(string? attempt) => !string.IsNullOrWhiteSpace(attempt) && attempt.Length <= 128;

    void SaveConfig(AppConfig next, bool forceRemoveStartup = false, bool resuming = false)
    {
        // Includes registry/config rollback and starting a watchdog, without waiting
        // for that process. Watchdog read/check/recovery uses this same transaction.
        using var guardControl = GuardRuntime.AcquireControl();
        if (EmergencyStopped && !resuming && (next.MonitoringEnabled || next.GuardEnabled || next.StartupEnabled || next.SoundEnabled || next.OverlayEnabled))
            throw new InvalidOperationException("紧急停用仍在生效，请先明确恢复管理。");
        if (next.StartupEnabled && !next.MonitoringEnabled || next.GuardEnabled && !next.MonitoringEnabled)
            throw new InvalidOperationException("请先恢复管理，再启用自启或守护。");
        bool changeStartup = next.StartupEnabled != config.StartupEnabled || forceRemoveStartup;
        string? previousStartup = changeStartup ? startup.Capture() : null;
        var previousConfig = config;
        bool saved = false;
        try
        {
            if (changeStartup) startup.Apply(next.StartupEnabled);
            store.SaveConfig(next); saved = true;
            GuardRuntime.EnsureStarted(next, store.Root);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            if (saved) { try { store.SaveConfig(previousConfig); } catch (Exception ex) { failures.Add(ex); } }
            if (changeStartup) { try { startup.Restore(previousStartup); } catch (Exception ex) { failures.Add(ex); } }
            if (failures.Count > 1) { config = store.LoadConfig(); throw new AggregateException("配置与自启回滚失败，请检查本地设置。", failures); }
            throw;
        }
        config = next; engine.WarningMinutes = config.WarningMinutes;
    }
    void DisableAll()
    {
        // Report a failed session commit before disabling anything or claiming exit.
        Persist();
        var next = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, PipeProtocol.JsonOptions), PipeProtocol.JsonOptions)!;
        next.StartupEnabled = next.GuardEnabled = next.MonitoringEnabled = next.SoundEnabled = next.OverlayEnabled = false;
        SaveConfig(next, forceRemoveStartup: true);
        rewardEpoch++; // An in-flight code result may still save knowledge, never grant after stop.
        UpdateTray();
    }

    void ResumeManagement()
    {
        using var guardControl = GuardRuntime.AcquireControl();
        var next = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, PipeProtocol.JsonOptions), PipeProtocol.JsonOptions)!;
        next.MonitoringEnabled = true; next.StartupEnabled = next.GuardEnabled = false;
        // Commit safe settings first. If saving or deleting fails, the latch remains
        // active and effective snapshots still say paused; never reopen guard/startup.
        SaveConfig(next, forceRemoveStartup: true, resuming: true);
        File.Delete(Path.Combine(store.Root, EmergencyStop.FileName));
        if (EmergencyStopped) throw new IOException("紧急停用闩锁未能清除，管理仍保持暂停。");
        rewardEpoch++;
        UpdateTray();
    }

    bool CommitExercise(Exercise exercise, string attempt, bool allowGrant)
    {
        var state = JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(engine.State, PipeProtocol.JsonOptions), PipeProtocol.JsonOptions)!;
        var previous = state.History.FirstOrDefault(x => x.AttemptId == attempt);
        if (previous is not null)
        {
            if (previous.QuestionId != exercise.Id) throw new InvalidOperationException("答题记录与练习不匹配。");
            return false;
        }
        var candidate = new SessionEngine(state) { WarningMinutes = config.WarningMinutes };
        bool granted = MonitoringActive && allowGrant && state.Phase == SessionPhase.Locked;
        if (granted)
        {
            if (!candidate.CompleteValidated(exercise, attempt, Now, config.UnlockMinutes)) throw new InvalidOperationException("当前许可状态不允许重复发放。");
        }
        else state.History.Add(new LearningRecord { AttemptId = attempt, QuestionId = exercise.Id, TopicId = exercise.TopicId, Mode = exercise.Mode, Takeaway = exercise.Takeaway, CompletedAtUtc = Now });
        if (granted && !MonitoringActive) return false;
        store.SaveSession(state);
        engine = candidate; lastSaved = Now;
        if (granted) rewardEpoch++;
        return granted;
    }

    IpcResponse StartCode(IpcRequest request, bool analysisOnly = false)
    {
        try { ExecutionSafety.EnsurePracticeAllowed(); }
        catch (InvalidOperationException ex) { return Snapshot(ex.Message); }
        if (codeWork is { Task.IsCompleted: false }) return Snapshot("已有运行或分析任务，请先停止或等待完成。");
        var exercise = content.Exercises.FirstOrDefault(x => x.Id == request.ExerciseId && x.Kind == "code" && x.Coding is not null);
        if (exercise is null || (!analysisOnly && !ValidAttempt(request.AttemptId)) || string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 65536) return Snapshot("请填写有效代码（最多 64 KiB 字符）。");
        if (request.RuntimePath is { Length: > 2048 }) return Snapshot("运行环境路径过长。");
        if (request.PythonPath is { Length: > 2048 }) return Snapshot("Python 路径过长。");
        if (analysisOnly && exercise.LanguageId != "python") return Snapshot("本地语法分析目前仅支持 Python；其他语言可在 API 页明确使用在线分析。");
        codeWork?.Cancellation.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        string code = request.Code;
        string? python = !string.IsNullOrWhiteSpace(request.RuntimePath) ? request.RuntimePath : string.IsNullOrWhiteSpace(request.PythonPath) ? exercise.LanguageId == "python" ? discoveredPython : null : request.PythonPath;
        codeWork = new CodeWork
        {
            Id = Guid.NewGuid().ToString("N"), Exercise = exercise, AttemptId = analysisOnly ? Guid.NewGuid().ToString("N") : request.AttemptId!, Cancellation = cancellation,
            ExamplesOnly = !analysisOnly && request.ExamplesOnly, AnalysisOnly = analysisOnly,
            AllowGrant = !analysisOnly && MonitoringActive && engine.State.Phase == SessionPhase.Locked,
            RewardEpoch = rewardEpoch,
            Message = analysisOnly ? "正在分析代码结构，不会执行你的代码…" : "正在运行…",
            // Both operations share one cancellable job slot, away from the monitor
            // mutation thread. Manual analysis never invokes the user-code runner.
            Task = analysisOnly
                ? Task.Run(() => PythonCodeAnalyzer.AnalyzeAsync(exercise, code, python, cancellation.Token))
                : Task.Run(() => RunnerDispatcher.RunAsync(exercise, code, python, request.ExamplesOnly, cancellation.Token))
        };
        return CodeStatus(codeWork.Id);
    }

    IpcResponse CodeStatus(string? jobId)
    {
        var job = codeWork;
        if (job is null || job.Id != jobId) return Snapshot("这个运行任务已经不存在。");
        CodeRunResult? result = null;
        string state = job.Cancelled ? "cancelled" : "running";
        if (job.Task.IsCompleted)
        {
            if (job.Task.IsCompletedSuccessfully) result = job.Task.Result;
            else result = new CodeRunResult { Status = job.Task.IsCanceled ? "cancelled" : "error", Message = job.AnalysisOnly ? "本次代码分析未完成，请检查 Python 环境后重试。" : "本次运行未完成，请检查 Python 环境后重试。" };
            state = job.Cancelled ? "cancelled" : "completed";
            if (!job.RewardProcessed && !job.Cancelled)
            {
                // Even an analyzer result accidentally marked Passed cannot record
                // learning or unlock entertainment through this analysis-only route.
                if (job.AnalysisOnly)
                {
                    job.Message = !string.IsNullOrWhiteSpace(result.Message) ? result.Message
                        : result.Status == "analysis" ? "代码结构分析已完成，没有执行你的代码。" : "本次代码分析未完成。";
                }
                else if (!job.ExamplesOnly && result.Passed)
                {
                    bool granted = CommitExercise(job.Exercise, job.AttemptId, job.AllowGrant && job.RewardEpoch == rewardEpoch);
                    job.Message = granted ? "全部检查通过，学习记录和娱乐许可已保存。" : "练习已通过，学习记录已保存；本次不叠加或重置许可。";
                }
                else job.Message = result.Passed
                    ? job.ExamplesOnly ? "示例通过了。提交检查后可完成练习。" : "练习已通过；当前已有许可，本次不叠加时长。"
                    : result.Message;
                job.RewardProcessed = true;
            }
        }
        UpdateTray();
        var response = Snapshot();
        response.CodeJob = new() { JobId = job.Id, ExerciseId = job.Exercise.Id, State = state, ExamplesOnly = job.ExamplesOnly, AnalysisOnly = job.AnalysisOnly, Message = job.Message, Result = result };
        return response;
    }

    void Log(Exception ex)
        => LogEvent(ex.GetType().Name + ": " + ex.Message);
    void LogEvent(string message)
    {
        try
        {
            Directory.CreateDirectory(store.Root); var path = Path.Combine(store.Root, "monitor-errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, DateTimeOffset.UtcNow + " " + message + Environment.NewLine);
        }
        catch { }
    }
    protected override void Dispose(bool disposing)
    {
        disposed = true;
        if (disposing)
        {
            try { Persist(); } catch (Exception ex) { Log(ex); }
            stop.Cancel(); timer.Dispose(); if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook); tray.Visible = false; tray.Dispose(); dispatcher.Dispose(); stop.Dispose();
        }
        base.Dispose(disposing);
    }
}
