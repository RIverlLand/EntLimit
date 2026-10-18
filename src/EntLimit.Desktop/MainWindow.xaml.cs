using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EntLimit.Core;
using Microsoft.Win32;

namespace EntLimit.Desktop;

public partial class MainWindow : Window
{
    private IpcResponse? _snapshot;
    private AppConfig? _editorConfig;
    private AppConfig? _persistedConfig;
    private List<AppCatalogEntry> _catalogEntries = [];
    private Exercise? _exercise;
    private string _attemptId = Guid.NewGuid().ToString("N");
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _busy;
    private bool _loaded;
    private bool _refreshInFlight;
    private bool _closing, _closeAllowed;

    public MainWindow(bool preview = false, bool previewCode = false)
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyCompactLayout(ActualHeight);
        Navigate_Click(NavToday, new RoutedEventArgs());
        ModeBox.SelectedIndex = 0;
        TopicBox.SelectedIndex = 1;
        StrategyBox.SelectedIndex = 0;
        CodeEditor.Loaded += (_,_) => AttachCodeHighlight();
        _preview = preview;
        if (!preview)
        {
            WindowMaterial.StatusChanged += MaterialStatusChanged;
            Closed += (_, _) => WindowMaterial.StatusChanged -= MaterialStatusChanged;
            SourceInitialized += (_,_) => { WindowMaterial.TryApply(this); UpdateMaterialStatus(); };
        }
        Loaded += async (_, _) =>
        {
            if (_preview) { _snapshot = new IpcResponse { Exercises = ExerciseCatalog.All.ToList() }; _loaded = true; TopicBox.SelectedIndex = previewCode ? 0 : 1; SelectQuestion(); return; }
            ReadPreferences();
            InitializeOnlineUi();
            RestoreFilterPreferences();
            await ConnectAsync();
            _loaded = true;
            SelectQuestion();
            _refreshTimer.Tick += async (_, _) =>
            {
                if (_busy || _refreshInFlight) return;
                _refreshInFlight = true;
                try { await RefreshAsync(false); }
                catch { Notice.Text = "后台暂时无法连接。已保存的列表会保留，可以点击刷新重试。"; }
                finally { _refreshInFlight = false; }
            };
            _refreshTimer.Start();
        };
        Closed += (_,_) => { _onlineCancellation?.Cancel(); _apiClient?.Dispose(); };
        Closing += async (_, args) =>
        {
            if (_closeAllowed) return;
            args.Cancel = true;
            if (_closing) return;
            _closing = true;
            _onlineCancellation?.Cancel();
            _refreshTimer.Stop(); _codePoll.Stop(); _draftTimer.Stop(); SavePreferences();
            if (_codeJobId is not null) await CancelCurrentJobAsync();
            _closeAllowed = true; Close();
        };
    }

    private void MaterialStatusChanged(WindowMaterialStatus _) => UpdateMaterialStatus();
    private void UpdateMaterialStatus()
    {
        var status = WindowMaterial.GetStatus(this);
        MaterialIndicator.Text = status.Applied ? "系统毛玻璃" : "实色模式";
        MaterialIndicator.ToolTip = status.Reason;
        try
        {
            string folder = DataPaths.GetRoot(); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "desktop-material.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(status)); File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }


    private async Task ConnectAsync()
    {
        try { await RefreshAsync(true); return; }
        catch { }
        var path = Path.Combine(AppContext.BaseDirectory, "EntLimit.Monitor.exe");
        if (!File.Exists(path))
        {
            Notice.Text = "找不到后台监控。请从完整的 artifacts/app 目录启动，或运行 scripts/build.ps1。";
            StatusTitle.Text = "后台未启动"; return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory });
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(400);
                try { await RefreshAsync(true); return; } catch { }
            }
            Notice.Text = "后台尚未连接，请稍后点击刷新。若配置损坏，可查看 data 中的诊断文件。";
            StatusTitle.Text = "等待后台连接";
        }
        catch (Exception) { Notice.Text = "后台启动失败，请检查完整发布目录及 .NET 运行环境。"; }
    }

    private async Task<IpcResponse> SendAsync(IpcRequest request)
    {
        var response = await PipeClient.SendAsync(request);
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? "操作未完成。");
        _snapshot = response;
        UpdateTopicChoices(response); UpdateExtensionList(response);
        if (string.IsNullOrEmpty(_preferences.PythonPath) && !string.IsNullOrEmpty(response.PythonPath)) { _preferences.PythonPath = response.PythonPath; UpdateRuntimeLabel(); }
        RenderStatus(response);
        return response;
    }

    private async Task RefreshAsync(bool loadEditors)
    {
        var response = await SendAsync(new() { Command = "status" });
        if (loadEditors) LoadEditors(response);
    }

    private void LoadEditors(IpcResponse response, bool reloadRules = true)
    {
        var selectedAppId = (ManagedGrid.SelectedItem as ManagedApp)?.Id;
        _editorConfig = response.Config;
        _persistedConfig = CopyConfig(response.Config);
        ManagedGrid.ItemsSource = response.Config.Apps;
        if (selectedAppId is not null) ManagedGrid.SelectedItem = response.Config.Apps.FirstOrDefault(x => x.Id == selectedAppId);
        _catalogEntries = response.Catalog;
        ApplyCatalogFilter();
        if (reloadRules)
        {
            MinutesBox.Text = response.Config.UnlockMinutes.ToString();
            WarningBox.Text = response.Config.WarningMinutes.ToString();
            SoundBox.IsChecked = response.Config.SoundEnabled;
            OverlayBox.IsChecked = response.Config.OverlayEnabled;
            MonitoringBox.IsChecked = response.Config.MonitoringEnabled;
            StartupBox.IsChecked = response.Config.StartupEnabled;
            GuardBox.IsChecked = response.Config.GuardEnabled;
            DomainsBox.Text = string.Join(Environment.NewLine, response.Config.Domains);
        }
    }

    private void ApplyCatalogFilter()
    {
        if (CatalogGrid is null || CatalogSearchBox is null || CatalogSourceBox is null || CatalogCount is null) return;
        string query = CatalogSearchBox.Text.Trim();
        string source = (CatalogSourceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var selectable = _catalogEntries.Where(x => AppCatalogPolicy.IsSelectableCandidate(x.Name, x.ExecutablePath, x.Source)).ToList();
        var shown = selectable.Where(x => query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(x => source switch { "installed" => (x.Source ?? "").Contains("安装"), "steam" => (x.Source ?? "").Contains("Steam", StringComparison.OrdinalIgnoreCase), "running" => x.HasVisibleWindow, _ => true }).ToList();
        CatalogGrid.ItemsSource = shown;
        CatalogCount.Text = $"显示 {shown.Count} / {selectable.Count} 个可选应用；已隐藏 {_catalogEntries.Count - selectable.Count} 个系统、辅助或开发工具项。按 Ctrl / Shift 多选，拖动横条调整列表高度。";
    }
    private void CatalogFilter_Changed(object sender, TextChangedEventArgs e) => ApplyCatalogFilter();
    private void CatalogSource_Changed(object sender, SelectionChangedEventArgs e) => ApplyCatalogFilter();

    private void RenderStatus(IpcResponse response)
    {
        UpdateGatePresentation(response);
        var state = response.Session;
        DetectExerciseCatalogChange(response);
        StatusTitle.Text = state.Phase switch
        {
            SessionPhase.Locked => "先学一点，再安心玩",
            SessionPhase.Ready => $"已获得 {state.GrantedMinutes} 分钟 · 等你开始",
            SessionPhase.Active => $"安心玩吧 · 还剩 {Math.Max(0, (int)Math.Ceiling(((state.DeadlineUtc ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalMinutes))} 分钟",
            SessionPhase.Draining => "时间到了 · 当前游戏可以安心收尾",
            _ => "学习与娱乐，按自己的节奏"
        };
        StatusDetail.Text = state.Phase switch
        {
            SessionPhase.Locked => "正常练习或快速复习，都能获得同样的娱乐许可。",
            SessionPhase.Ready => "首次启动或使用受管控目标后开始计时，切换游戏不会重复出题。",
            SessionPhase.Active => $"本次到 {state.DeadlineUtc?.ToLocalTime():HH:mm}。到期不会关闭游戏或切走窗口。",
            SessionPhase.Draining => "退出已登记的游戏后重新锁住；无法自动判断一局结束。也可以点击“这次已结束”。",
            _ => ""
        };
        if (!response.Config.MonitoringEnabled)
        {
            StatusTitle.Text = "调试中 · 管控已暂停";
            StatusDetail.Text = "应用可直接使用，学习记录与已有许可保留。网站扩展同步后解除限制；在规则页可重新启用。";
        }
        if (response.EmergencyStopped)
        {
            StatusTitle.Text = "紧急停用中 · 管控不会自动恢复";
            StatusDetail.Text = "学习记录与已有许可保留。请在规则页点击“解除紧急停用并恢复管控”；自启和守护仍需另外开启。";
        }
        ResumeManagementButton.Visibility = EmergencyStateText.Visibility = response.EmergencyStopped ? Visibility.Visible : Visibility.Collapsed;
        ResumeManagementButton.IsEnabled = response.EmergencyStopped && !_busy;
        MonitoringBox.IsEnabled = StartupBox.IsEnabled = GuardBox.IsEnabled = !response.EmergencyStopped;
        UpdateExerciseProgress();
        HistoryList.ItemsSource = state.History.OrderByDescending(x => x.CompletedAtUtc).ToList();
        CompleteButton.IsEnabled = !_busy && _codeJobId is null && _cloudExerciseId is null && !ExecutionSafety.IsElevated && !_exerciseCatalogStale;
        PracticeNote.Text = response.EmergencyStopped
            ? "紧急停用期间可以继续练习；不新增娱乐许可，已有许可保持原来的时间线。"
            : state.Phase == SessionPhase.Locked ? "完成整组练习后获得许可；运行示例不解锁。草稿仅保存在本机。" : "当前已有许可，可以继续练习；完成本题不会叠加或重置现有时长。";
    }

    private void SelectQuestion() => RenderExercise();

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        SetLearningControls();
        ManagedGrid.IsEnabled = false;
        AddCandidatesButton.IsEnabled = false;
        CompleteButton.IsEnabled = false;
        try { await operation(); }
        catch (Exception ex) { Notice.Text = ex is InvalidOperationException ? ex.Message : "操作未完成：后台可能未连接。点击刷新重试，已获得的许可会保留。"; }
        finally { _busy = false; ManagedGrid.IsEnabled = true; AddCandidatesButton.IsEnabled = true; SetLearningControls(); if (_snapshot is not null) RenderStatus(_snapshot); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { await RefreshAsync(true); Notice.Text = "状态已刷新。"; });
    private async void QuestionFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _busy || _updatingTopics) return;
        if (_codeJobId is not null) await CancelCurrentJobAsync();
        if (_codeJobId is not null) return;
        RenderExercise();
    }
    private async void NextQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_codeJobId is not null) await CancelCurrentJobAsync();
        if (_codeJobId is not null) return;
        RenderExercise(skipCurrent: true);
    }
    private async void RestartQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _exercise is null) return;
        if (_codeJobId is not null) await CancelCurrentJobAsync();
        if (_codeJobId is not null) return;
        _draftTimer.Stop();
        bool reloadUpdated=_exerciseCatalogStale;
        if(reloadUpdated) SavePreferences();
        else
        {
            if (!_recallOriginal) { _preferences.CodeDrafts.Remove(_exercise.Id); _preferences.Answers.Remove(_exercise.Id); }
            _preferences.ReviewDraftPassCounts.Remove(_exercise.Id);
        }
        RenderExercise(keepCurrent: true, discardCurrent: !reloadUpdated);
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (_exercise is null || _preview || _exerciseCatalogStale) return;
        if (_exercise.Kind == "code") { await StartCodeAsync(false); return; }
        var exercise = _exercise; var attempt = _attemptId;
        await RunAsync(async () =>
        {
            SaveCurrentDraft();
            var response = await SendAsync(new() { Command = "grade_exercise", ExerciseId = exercise.Id, Answers = CurrentAnswers(), AttemptId = attempt });
            if (_exercise?.Id != exercise.Id || _attemptId != attempt || response.Grade is not { } grade) return;
            foreach (var part in grade.Parts)
                if (_partFeedback.TryGetValue(part.PartId, out var text)) { text.Text = (part.Correct ? "✓ " : "再想一步 · ") + part.Feedback; text.Foreground = part.Correct ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.DarkGoldenrod; }
            Feedback.Text = grade.Passed ? "今天的收获：" + exercise.Takeaway + "\n\n" + (_snapshot?.Session.Phase == SessionPhase.Ready ? "许可已准备好，去享受你的娱乐时间吧。" : "练习已完成，已有许可时长保持不变。") : "有些地方还可以再想一想。答案会保留，修改后再提交即可。";
            if (grade.Passed) { _scenarioCompleted = true; SavePreferences(); UpdateExerciseProgress(); }
            Notice.Text = grade.Passed ? "完成记录已保存；可以点击“换一道”继续，也可以切换到轻松复习。" : "无需重新填写正确的部分。";
        });
    }

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_exercise?.SourceUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https") Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async void Finish_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => { await SendAsync(new() { Command = "finish" }); Notice.Text = "这次娱乐已结束。仍在运行的受管控窗口下一次获得焦点时会触发学习入口。"; });

    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        Notice.Text = "正在扫描安装信息、Steam 游戏目录和运行中的应用…";
        var response = await SendAsync(new() { Command = "scan" });
        _catalogEntries = response.Catalog;
        ApplyCatalogFilter();
        Notice.Text = $"扫描完成，{response.Catalog.Count} 个可选应用已缓存。输入名称搜索，添加后立即生效。";
    });

    private static AppConfig CopyConfig(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, PipeProtocol.JsonOptions), PipeProtocol.JsonOptions)!;

    private static void ValidateManualApp(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择一个存在的 EXE 文件。");
        if (AppCatalogPolicy.IsProtectedTool(path)) throw new InvalidOperationException("输入法和系统组件无需管控；Codex、Claude 和 EntLimit 开发工具不能加入管控。");
    }

    private async void ManualAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Filter = "Windows 应用 (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(async () =>
        {
            if (_editorConfig is null) throw new InvalidOperationException("后台尚未连接，请先刷新。");
            ValidateManualApp(dialog.FileName);
            var updated = CopyConfig(_editorConfig);
            if (updated.Apps.Any(a => string.Equals(a.ExecutablePath, dialog.FileName, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("这个应用已经在列表中。");
            updated.Apps.Add(new() { Name = Path.GetFileNameWithoutExtension(dialog.FileName), ExecutablePath = Path.GetFullPath(dialog.FileName), Source = "Manual", Enabled = true });
            await SaveConfigAsync(updated, false);
            Notice.Text = "已加入管控并保存，无需再点击保存。";
        });
    }
    private async void AddCandidate_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_editorConfig is null) throw new InvalidOperationException("后台尚未连接，请先刷新。");
        var selected = CatalogGrid.SelectedItems.OfType<AppCatalogEntry>().ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("先选一个候选；可按 Ctrl / Shift 多选。");
        var updated = CopyConfig(_editorConfig); int added = 0;
        foreach (var item in selected)
        {
            ValidateManualApp(item.ExecutablePath);
            if (updated.Apps.Any(a => string.Equals(a.ExecutablePath, item.ExecutablePath, StringComparison.OrdinalIgnoreCase))) continue;
            updated.Apps.Add(new() { Name = item.Name, ExecutablePath = Path.GetFullPath(item.ExecutablePath), Source = item.Source, Enabled = true }); added++;
        }
        if (added == 0) throw new InvalidOperationException("选中的应用已经在管控列表中。");
        await SaveConfigAsync(updated, false);
        Notice.Text = $"已添加并保存 {added} 个应用，管控已生效。";
    });
    private async void RemoveApp_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_editorConfig is null || ManagedGrid.SelectedItem is not ManagedApp app) throw new InvalidOperationException("先选一个已管控应用。");
        var updated = CopyConfig(_editorConfig); updated.Apps.RemoveAll(a => a.Id == app.Id);
        await SaveConfigAsync(updated, false); Notice.Text = "已移除并保存。";
    });
    private async void ManagedEnabled_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_editorConfig is null) return;
        var persisted = _persistedConfig;
        try { await SaveConfigAsync(CopyConfig(_editorConfig), false); Notice.Text = "启停设置已保存并生效。"; }
        catch { if (persisted is not null) { _editorConfig = CopyConfig(persisted); ManagedGrid.ItemsSource = _editorConfig.Apps; } throw; }
    });

    private async Task SaveConfigAsync(AppConfig config, bool reloadRules = true)
    {
        ManagedGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ManagedGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var response = await SendAsync(new() { Command = "save_config", Config = config });
        LoadEditors(response, reloadRules);
        Notice.Text = "设置已保存；本次已经获得的许可保持不变。";
    }

    private async void SaveRules_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_editorConfig is null) return;
        if (!int.TryParse(MinutesBox.Text, out int minutes) || minutes < 90 || minutes > 1440) throw new InvalidOperationException("解锁时长请填 90–1440 分钟。");
        if (!int.TryParse(WarningBox.Text, out int warning) || warning < 0 || warning >= minutes) throw new InvalidOperationException("提醒分钟请填 0 到解锁时长之间的整数。0 表示只在到期提醒。");
        var domains = DomainsBox.Text.Split(['\r', '\n', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).Distinct().ToList();
        foreach (var domain in domains)
            if (Uri.CheckHostName(domain) != UriHostNameType.Dns || !domain.Contains('.') || domain.Contains('/') || domain.Contains(':')) throw new InvalidOperationException($"域名格式不正确：{domain}。只填域名，例如 bilibili.com。");
        _editorConfig.UnlockMinutes = minutes;
        _editorConfig.WarningMinutes = warning;
        _editorConfig.SoundEnabled = SoundBox.IsChecked == true;
        _editorConfig.OverlayEnabled = OverlayBox.IsChecked == true;
        _editorConfig.MonitoringEnabled = MonitoringBox.IsChecked == true;
        _editorConfig.StartupEnabled = _editorConfig.MonitoringEnabled && StartupBox.IsChecked == true;
        _editorConfig.GuardEnabled = _editorConfig.MonitoringEnabled && GuardBox.IsChecked == true;
        _editorConfig.Domains = domains;
        await SaveConfigAsync(_editorConfig);
    });

    private void EmergencyStop_Click(object sender, RoutedEventArgs e)
    {
        // Deliberately independent of RunAsync/IPC: this is the escape route when
        // another request or the monitor is stuck. The tool owns shutdown/recovery.
        string path = Path.Combine(AppContext.BaseDirectory, "EntLimit.EmergencyStop.exe");
        if (!File.Exists(path))
        {
            Notice.Text = "缺少独立紧急工具，请使用完整安装目录中的 EntLimit.EmergencyStop.exe。";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Normal
            });
            Notice.Text = "独立紧急工具已打开，请查看工具窗口中的结果；本界面可能随后关闭。";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Notice.Text = "独立工具未能启动，请直接双击程序目录中的 EntLimit.EmergencyStop.exe，查看系统给出的结果。";
        }
    }

    private async void ResumeManagement_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_snapshot?.EmergencyStopped != true) return;
        var response = await SendAsync(new() { Command = "resume_management" });
        LoadEditors(response);
        Notice.Text = response.EmergencyStopped
            ? "紧急停用仍生效，请刷新后重试。"
            : "已解除紧急停用并恢复管控。自启与守护保持关闭；已有许可和学习记录保留。";
    });

    private async void DebugDisable_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var response = await SendAsync(new() { Command = "debug_disable" });
        LoadEditors(response);
        Notice.Text = "已关闭管控、自启、守护及提醒。后台保留连接，便于调试；网站扩展最多一分钟同步。";
    });

    private async void DebugStop_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await SendAsync(new() { Command = "debug_stop" });
        _refreshTimer.Stop();
        Close();
    });

    private async void Launch_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (ManagedGrid.SelectedItem is not ManagedApp app) throw new InvalidOperationException("先选一个已保存的受管控应用。");
        await SendAsync(new() { Command = "launch", AppId = app.Id });
        Notice.Text = "已请求启动。游戏启动器可能启动另一个 EXE，请将实际游戏程序加入列表。";
    });
}
