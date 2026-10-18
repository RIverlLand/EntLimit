using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EntLimit.Core;
using Microsoft.Win32;

namespace EntLimit.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<string, FrameworkElement> _answerInputs = [];
    private readonly Dictionary<string, TextBlock> _partFeedback = [];
    private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _codePoll = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private DesktopPreferences _preferences = new();
    private bool _preview, _polling, _compactLayout;
    private PythonHighlightAdorner? _highlight;
    private void AttachCodeHighlight() { if (_highlight is null && System.Windows.Documents.AdornerLayer.GetAdornerLayer(CodeEditor) is { } layer) { _highlight = new PythonHighlightAdorner(CodeEditor); layer.Add(_highlight); } }
    private string? _codeJobId;
    private Exercise? _codeExercise;
    private string? _submittedCode, _submittedExerciseId, _submittedJobId;
    private bool _submittedExamplesOnly;
    private string? _analysisCodeHash;
    private CodeAnalysisView? _analysisView;
    private Expander? _referenceSolution;
    private Button? _referenceButton;
    private Button? _selectedNavigation;
    private int _hintIndex;
    private bool _scenarioCompleted;
    private bool _exerciseCatalogStale;
    private const string CatalogChangedNotice="当前题已更新，换一道或重新开始加载新版；草稿保留。";
    private bool _recallOriginal;
    private int _reviewDraftPassCount;
    private string PreferencesPath => Path.Combine(DataPaths.GetRoot(), "desktop-preferences.json");

    public void FinalizePreview() { AttachCodeHighlight(); _highlight?.InvalidateVisual(); if(_previewExtensions && IntegrationHost.Child is ScrollViewer scroll)scroll.ScrollToEnd(); }

    public void ApplyCompactLayout(double height)
    {
        _compactLayout = height < 800;
        var visibility = height < 800 ? Visibility.Collapsed : Visibility.Visible;
        LearningHeading.Visibility = LearningSubtitle.Visibility = WorkspaceSubtitle.Visibility = visibility;
        WorkspaceHeader.Visibility = visibility;
        SelectionNote.Visibility = visibility;
        if (_referenceButton is not null) _referenceButton.Visibility = !_compactLayout && _exercise?.Kind == "code" ? Visibility.Visible : Visibility.Collapsed;
    }

    public void PreparePreview(bool code)
    {
        ((System.Windows.Controls.Grid)Content).Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(241,245,250));
        _snapshot = new IpcResponse { Exercises = ExerciseCatalog.All.ToList() };
        _loaded = true; TopicBox.SelectedIndex = code ? 0 : 1; SelectQuestion();
        StatusTitle.Text = "先学一点，再安心玩";
        StatusDetail.Text = "完成一个小练习，获得 90 分钟娱乐许可。";
        Notice.Text = "离线界面预览 · 不连接监控，不写入许可或配置。";
    }

    private void RenderExercise(bool keepCurrent = false, bool discardCurrent = false, bool skipCurrent = false)
    {
        if (!_loaded || _snapshot is null) return;
        if (!discardCurrent) SaveCurrentDraft();
        string mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "standard";
        string topic = (TopicBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "network";
        var pool = ExerciseSelection.Rank(_snapshot.Exercises, _snapshot.Session.History, topic, mode);
        if (pool.Count == 0) { QuestionTitle.Text = "这个主题暂时没有练习"; return; }
        var current = _exercise;
        bool random = (StrategyBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "random";
        string? avoid = skipCurrent ? current?.Id : random ? _preferences.LastExerciseId : null;
        _exercise = keepCurrent && current is not null ? _snapshot.Exercises.FirstOrDefault(x=>x.Id==current.Id)
            : ExerciseSelection.Pick(_snapshot.Exercises, _snapshot.Session.History, topic, mode, random, avoid);
        if (_exercise is null) { QuestionTitle.Text = "当前范围暂时没有练习"; return; }
        _exerciseCatalogStale=false;
        _preferences.LastExerciseId = _exercise.Id;
        _scenarioCompleted = false;
        ClearCodeAnalysis();
        _submittedCode = _submittedExerciseId = _submittedJobId = null;
        var progress = ExerciseSelection.Progress(_exercise, _snapshot.Session.History, _snapshot.Exercises);
        _reviewDraftPassCount = progress.PassedCount;
        _recallOriginal = progress.Passed && string.IsNullOrEmpty(_exercise.ReviewOfId);
        if (!string.IsNullOrEmpty(_exercise.ReviewOfId) && (!_preferences.ReviewDraftPassCounts.TryGetValue(_exercise.Id, out int draftCount) || draftCount != progress.PassedCount))
        {
            _preferences.Answers.Remove(_exercise.Id); _preferences.ReviewDraftPassCounts.Remove(_exercise.Id);
        }
        _attemptId = Guid.NewGuid().ToString("N"); _hintIndex = 0;
        QuestionTitle.Text = _exercise.Title;
        QuestionMeta.Text = $"{TopicName(_exercise.TopicId)} · {(!string.IsNullOrEmpty(_exercise.ReviewOfId) ? "回忆复习" : _exercise.Kind == "code" ? "编程练习" : "场景练习")}  /  预计 {_exercise.EstimatedMinutes} 分钟  /  原创练习";
        QuestionPrompt.Text = _exercise.Scenario;
        HintText.Text = "先试一试；需要时，可以逐步展开提示。";
        AnswerPanel.Children.Clear(); _referenceSolution = null; NetworkAnswers.Children.Clear(); _answerInputs.Clear(); _partFeedback.Clear();
        bool coding = _exercise.Kind == "code";
        CodeLanguageTitle.Text=_exercise.LanguageId=="java"?"Java 工作台":"Python 工作台";
        SelectRuntimeButton.Content=_exercise.LanguageId=="java"?"选择 JDK…":"选择 Python…";
        if (_referenceButton is null && RunExamplesButton.Parent is Panel actions)
        {
            _referenceButton = new Button { Content = "参考解与优化", ToolTip = "手动展开参考解；不会改动草稿或自动解锁。" };
            _referenceButton.Click += (_,_) =>
            {
                if (_referenceSolution is null) return;
                _referenceSolution.IsExpanded = true;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => _referenceSolution?.BringIntoView(new Rect(0,0,Math.Max(1,_referenceSolution.ActualWidth),40))));
            };
            actions.Children.Add(_referenceButton);
        }
        if (_referenceButton is not null) { _referenceButton.Visibility = coding && !_compactLayout ? Visibility.Visible : Visibility.Collapsed; _referenceButton.Content = "参考解与优化"; }
        bool hasTopology = _exercise.Id is "network-subnet-gateway-v1" or "network-dns-incident-v1";
        TopologyPanel.Children.Clear(); TopologyPanel.Visibility = hasTopology ? Visibility.Visible : Visibility.Collapsed;
        string[] nodes = _exercise.Id == "network-subnet-gateway-v1" ? ["电脑 192.168.10.20/24", "⇄  二层交换机  ⇄", "打印机 192.168.10.80/24", "NAS 192.168.20.30/24 · 不同子网", "跨网段下一跳 → 网关 192.168.10.1 · 后续路由待确认"] : ["浏览器输入域名", "→ DNS 查询超时", "对照：已知站点 IP 的 TCP 连接可达", "定位失败环节，再验证网页服务"];
        foreach (string node in hasTopology ? nodes : []) TopologyPanel.Children.Add(new Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(95,236,243,252)), CornerRadius = new CornerRadius(8), Padding = new Thickness(10,8,10,8), Margin = new Thickness(0,0,8,8), Child = new TextBlock { Text = node, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 250, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62,94,138)) } });
        CodePanel.Visibility = RunExamplesButton.Visibility = AnalyzeCodeButton.Visibility = CloudAnalyzeButton.Visibility = coding ? Visibility.Visible : Visibility.Collapsed;
        AnalyzeCodeButton.Content = _exercise.LanguageId == "python" ? "分析当前解答" : "本地分析仅支持 Python";
        NetworkPanel.Visibility = coding ? Visibility.Collapsed : Visibility.Visible;
        CodeSplitter.Visibility = Visibility.Visible;
        ExerciseDivider.Width = new GridLength(12);
        CodeColumn.Width = new GridLength(1.15, GridUnitType.Star);
        CodeColumn.MinWidth = 310;
        if (coding)
        {
            if (_exercise.Coding is { } codingDetails && !string.IsNullOrWhiteSpace(codingDetails.ReferenceCode))
            {
                _referenceSolution = ReferenceSolutionView.Create(codingDetails, message => Notice.Text = message);
                AnswerPanel.Children.Add(_referenceSolution);
            }
            CodeEditor.Text = (_recallOriginal ? null : _preferences.CodeDrafts.GetValueOrDefault(_exercise.Id)) ?? _exercise.Coding?.StarterCode ?? "";
            CodeResults.Text = _recallOriginal ? "复习练习：历史解答已保留，本次从初始模板开始。运行示例只检查公开用例；提交检查运行全部用例。" : "运行示例只检查公开用例；提交检查会运行全部用例。";
            UpdateRuntimeLabel();
        }
        else foreach (var part in _exercise.Parts)
        {
            var section = new StackPanel { Margin = new Thickness(0,0,0,20) };
            section.Children.Add(new TextBlock { Text = part.Prompt, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, LineHeight = 24 });
            if (part.Kind == "choice")
            {
                var choices = new StackPanel { Margin = new Thickness(0,8,0,0) };
                for (int i=0; i<part.Options.Length; i++)
                {
                    var radio = new RadioButton { GroupName = part.Id, Tag = i.ToString(), Margin = new Thickness(0,6,0,6), Content = new TextBlock { Text = part.Options[i], TextWrapping = TextWrapping.Wrap, MaxWidth = 710 } };
                    if (!_recallOriginal && _preferences.Answers.TryGetValue(_exercise.Id, out var saved)) radio.IsChecked = saved.GetValueOrDefault(part.Id) == i.ToString();
                    radio.Checked += (_,_) => ScheduleDraft(); choices.Children.Add(radio);
                }
                section.Children.Add(choices); _answerInputs[part.Id] = choices;
            }
            else
            {
                var input = new TextBox { MinWidth = 180, MaxWidth = 430, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "填写你的答案，支持首尾空格与大小写的合理差异。" };
                if (!_recallOriginal && _preferences.Answers.TryGetValue(_exercise.Id, out var saved)) input.Text = saved.GetValueOrDefault(part.Id) ?? "";
                input.TextChanged += (_,_) => ScheduleDraft(); section.Children.Add(input); _answerInputs[part.Id] = input;
            }
            var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 23, Margin = new Thickness(0,8,0,0) };
            section.Children.Add(feedback); _partFeedback[part.Id] = feedback; NetworkAnswers.Children.Add(section);
        }
        Feedback.Text = "";
        UpdateExerciseProgress();
        SetLearningControls();
        SavePreferences();
    }

    private void DetectExerciseCatalogChange(IpcResponse response)
    {
        if(_exercise is null)return;
        var latest=response.Exercises.FirstOrDefault(x=>x.Id==_exercise.Id);
        if(latest is null || JsonSerializer.Serialize(latest,PipeProtocol.JsonOptions)!=JsonSerializer.Serialize(_exercise,PipeProtocol.JsonOptions))_exerciseCatalogStale=true;
        if(_exerciseCatalogStale) { Notice.Text=CatalogChangedNotice; SetLearningControls(); }
    }

    private void UpdateExerciseProgress()
    {
        if (_snapshot is null || LearningProgress is null || QuestionProgress is null) return;
        string topic = (TopicBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "network";
        string mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "standard";
        var history = _snapshot.Session.History;
        var originals = _snapshot.Exercises.Where(x=>(topic == "all" || x.TopicId == topic) && string.IsNullOrEmpty(x.ReviewOfId)).ToList();
        int passed = originals.Count(x=>ExerciseSelection.Progress(x,history,_snapshot.Exercises).Passed);
        int reviewCount = originals.Sum(x=>ExerciseSelection.Progress(x,history,_snapshot.Exercises).ReviewCount);
        string scope = topic == "all" ? "全部题集" : TopicName(topic);
        bool random = (StrategyBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "random";
        var eligible = ExerciseSelection.Rank(_snapshot.Exercises, history, topic, mode);
        LearningProgress.Text = $"{scope} · {(random ? "随机" : "推荐")}（候选 {eligible.Count}） · 原题进度 {passed} / {originals.Count} · 复习 {reviewCount} 次";
        bool eligibleReview = _snapshot.Exercises.Any(x=>(topic == "all" || x.TopicId == topic) && !string.IsNullOrEmpty(x.ReviewOfId) && ExerciseSelection.Progress(x,history,_snapshot.Exercises).SourcePassed);
        SelectionNote.Text = mode == "light" ? eligibleReview ? "优先回忆已经通过的知识；换一道会在当前复习池中切换。" : "还没有可复习的已通过原题，先从入门练习开始。"
            : passed == originals.Count && originals.Count > 0 ? "当前范围的原题已全部通过，建议切换到轻松复习；继续换题会回顾较久未练的内容。"
            : originals.Where(x=>x.Mode == "standard").All(x=>ExerciseSelection.Progress(x,history,_snapshot.Exercises).Passed) ? "标准题已通过，接下来优先完成尚未通过的入门题。" : "优先选择尚未通过的原题；完成后点击“换一道”继续，不会自动切走。";
        SelectionNote.Text = (random ? $"在{scope}当前优先的 {eligible.Count} 道候选中随机抽取，优先避开刚练的题。" : $"在{scope}按学习进度推荐。") + " " + SelectionNote.Text;
        if (eligible.Count == 1) SelectionNote.Text += " 当前优先池只有这一题；换一道仍会回到本题，可以切换题集 / 模式。";
        LearningProgress.ToolTip = SelectionNote.Text;
        if (_exercise is null) return;
        var progress = ExerciseSelection.Progress(_exercise,history,_snapshot.Exercises);
        string when = progress.LastPassedAt is { } last ? $" · 上次 {last.ToLocalTime():yyyy-MM-dd HH:mm}" : "";
        QuestionProgress.Text = string.IsNullOrEmpty(_exercise.ReviewOfId)
            ? progress.Passed ? $"✓ 已通过 {progress.PassedCount} 次{when} · 关联复习 {progress.ReviewCount} 次" : "○ 尚未通过 · 试着完成一次，留下一点积累"
            : $"{(progress.SourcePassed ? "✓ 源题已通过" : "源题尚未通过")} · 本题复习 {progress.ReviewCount} 次{when}";
        LearningProgress.ToolTip = SelectionNote.Text;
    }

    private Dictionary<string,string> CurrentAnswers() => _answerInputs.ToDictionary(x=>x.Key, x=> x.Value is TextBox box ? box.Text : ((StackPanel)x.Value).Children.OfType<RadioButton>().FirstOrDefault(r=>r.IsChecked == true)?.Tag?.ToString() ?? "");
    private void SaveCurrentDraft()
    {
        // Recalled originals start fresh; never overwrite their retained historical answer.
        if (_exercise is null || _preview || _recallOriginal) return;
        if (_exercise.Kind == "code") _preferences.CodeDrafts[_exercise.Id] = CodeEditor.Text;
        else if (!string.IsNullOrEmpty(_exercise.ReviewOfId) && _scenarioCompleted)
        {
            _preferences.Answers.Remove(_exercise.Id); _preferences.ReviewDraftPassCounts.Remove(_exercise.Id);
        }
        else
        {
            _preferences.Answers[_exercise.Id] = CurrentAnswers();
            if (!string.IsNullOrEmpty(_exercise.ReviewOfId)) _preferences.ReviewDraftPassCounts[_exercise.Id] = _reviewDraftPassCount;
        }
    }
    private void ScheduleDraft() { if (_preview || !_loaded) return; _draftTimer.Stop(); _draftTimer.Start(); }
    private void ReadPreferences()
    {
        try { if (File.Exists(PreferencesPath)) _preferences = JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(PreferencesPath)) ?? new(); } catch { Notice.Text = "草稿文件暂时无法读取，许可与管控配置不受影响。"; }
        _preferences.CodeDrafts ??= []; _preferences.Answers ??= []; _preferences.ReviewDraftPassCounts ??= [];
        foreach (var key in _preferences.Answers.Where(x=>x.Value is null).Select(x=>x.Key).ToArray()) _preferences.Answers[key] = [];
        _draftTimer.Tick += (_,_) => { _draftTimer.Stop(); SavePreferences(); };
        _codePoll.Tick += async (_,_) => await PollCodeAsync();
    }
    private void RestoreFilterPreferences()
    {
        // Called once before _loaded=true, so restoring controls neither selects a
        // question nor sends IPC. Unknown settings fall back to existing defaults.
        _preferences.TopicId = string.IsNullOrWhiteSpace(_preferences.TopicId) ? "network" : _preferences.TopicId;
        _preferences.Mode = _preferences.Mode is "standard" or "light" ? _preferences.Mode : "standard";
        _preferences.SelectionStrategy = _preferences.SelectionStrategy is "recommended" or "random" ? _preferences.SelectionStrategy : "recommended";
        if (TopicBox.Items.OfType<ComboBoxItem>().Any(x=>x.Tag?.ToString()==_preferences.TopicId)) SelectTagged(TopicBox, _preferences.TopicId);
        SelectTagged(ModeBox, _preferences.Mode);
        SelectTagged(StrategyBox, _preferences.SelectionStrategy);
    }
    private static void SelectTagged(ComboBox control, string tag)
    {
        control.SelectedItem = control.Items.OfType<ComboBoxItem>().First(x=>x.Tag?.ToString() == tag);
    }
    private void CaptureFilterPreferences()
    {
        _preferences.TopicId = (TopicBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "network";
        _preferences.Mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "standard";
        _preferences.SelectionStrategy = (StrategyBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "recommended";
    }
    private void SavePreferences()
    {
        if (_preview) return;
        CaptureFilterPreferences();
        SaveCurrentDraft();
        try { Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!); string temp = PreferencesPath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(_preferences)); File.Move(temp, PreferencesPath, true); }
        catch { Notice.Text = "草稿未能写入本机，请先保留你编辑的代码。"; }
    }
    private void UpdateRuntimeLabel()
    {
        if (RuntimeLabel is null) return;
        bool java = _exercise?.LanguageId == "java";
        RuntimeLabel.Text = java ? "Java JDK · " + (_preferences.JavaPath ?? "未选择；请到 API 与扩展页设置已安装 JDK")
            : string.IsNullOrWhiteSpace(_preferences.PythonPath) ? "未找到 Python。请选择已有 Python 3；不自动下载。" : "Python · " + _preferences.PythonPath;
    }
    private void SelectPython_Click(object sender, RoutedEventArgs e)
    {
        if (_codeJobId is not null) return;
        if (_exercise?.LanguageId == "java") { var folder=new OpenFolderDialog { Title="选择 Java JDK 目录" }; if(folder.ShowDialog(this)==true) { _preferences.JavaPath=folder.FolderName; if(_javaInput is not null)_javaInput.Text=folder.FolderName; SavePreferences(); UpdateRuntimeLabel(); } return; }
        var dialog = new OpenFileDialog { Filter = "Python 解释器 (python.exe)|python.exe|Windows 应用 (*.exe)|*.exe", CheckFileExists = true, Title = "选择已安装的 Python 3 解释器" };
        if (dialog.ShowDialog(this) != true) return;
        _preferences.PythonPath = dialog.FileName; UpdateRuntimeLabel(); SavePreferences();
    }
    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag?.ToString(), out int index)) return;
        Tabs.SelectedIndex = index;
        _selectedNavigation?.ClearValue(Control.BackgroundProperty);
        if (_selectedNavigation is not null) _selectedNavigation.FontWeight = FontWeights.Normal;
        _selectedNavigation = button; button.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(125,214,229,250)); button.FontWeight = FontWeights.SemiBold;
    }
    private void Hint_Click(object sender, RoutedEventArgs e)
    {
        if (_exercise is null) return;
        var hints = _exercise.Kind == "code" ? _exercise.Coding?.Hints ?? [] : _exercise.Parts.SelectMany(x=>x.Hints).ToArray();
        if (hints.Length == 0) { HintText.Text = "先把场景中的条件分别写下来，再对照问题推理。"; return; }
        _hintIndex = Math.Min(_hintIndex + 1, hints.Length);
        HintText.Text = string.Join("\n\n", hints.Take(_hintIndex).Select((h,i)=>$"提示 {i+1} · {h}"));
    }
    private async void RunExamples_Click(object sender, RoutedEventArgs e) => await StartCodeAsync(true);
    private async void AnalyzeCode_Click(object sender, RoutedEventArgs e) { if(_exercise?.LanguageId!="python") { Notice.Text="本地 AST 分析目前只支持 Python；Java 可主动选择在线建议。"; return; } await StartCodeAsync(false, analysisOnly:true); }
    private async Task StartCodeAsync(bool examplesOnly, bool analysisOnly = false)
    {
        if (_exercise is null || _codeJobId is not null || _preview || _busy || _exerciseCatalogStale) return;
        var exercise = _exercise; string attempt = _attemptId; string code = CodeEditor.Text;
        await RunAsync(async () =>
        {
            SavePreferences();
            var response = await SendAsync(new() { Command = analysisOnly ? "start_analysis" : "start_code", ExerciseId = exercise.Id, Code = code, PythonPath = _preferences.PythonPath, RuntimePath = exercise.LanguageId == "java" ? _preferences.JavaPath : _preferences.PythonPath, ExamplesOnly = examplesOnly, AttemptId = attempt });
            _codeJobId = response.CodeJob?.JobId; _codeExercise = exercise;
            if (_codeJobId is null) throw new InvalidOperationException("任务未能启动，请稍后再试。");
            _submittedCode = code; _submittedExerciseId = exercise.Id; _submittedJobId = _codeJobId; _submittedExamplesOnly = examplesOnly;
            if (!examplesOnly) ClearCodeAnalysis();
            CodeResults.Text = analysisOnly ? "正在分析提交时的代码快照…\n本地结构分析不执行代码，也不授予许可。" : "正在独立进程中运行…\n只运行你自己编写或信任的练习代码；这不是安全沙箱。";
            if (analysisOnly) { AnalysisContent.Children.Clear(); AnalysisContent.Children.Add(new TextBlock { Text = "正在按需分析当前代码结构…", TextWrapping = TextWrapping.Wrap }); CodeOutputTabs.SelectedIndex = 1; }
            else CodeOutputTabs.SelectedIndex = 0;
            SetCodeRunning(true); _codePoll.Start();
        });
    }
    private void SetLearningControls() { bool free = !_busy && _codeJobId is null && _cloudExerciseId is null; AnalyzeCodeButton.IsEnabled = RunExamplesButton.IsEnabled = CompleteButton.IsEnabled = ModeBox.IsEnabled = TopicBox.IsEnabled = StrategyBox.IsEnabled = RestartButton.IsEnabled = NextExerciseButton.IsEnabled = free; NetworkAnswers.IsEnabled = !_busy; CloudAnalyzeButton.IsEnabled=free && _onlineCancellation is null && !_exerciseCatalogStale; AnalyzeCodeButton.IsEnabled=free && _exercise?.LanguageId=="python" && !_exerciseCatalogStale; if(_exerciseCatalogStale) { CompleteButton.IsEnabled=false; RunExamplesButton.IsEnabled=false; } if(ExecutionSafety.IsElevated) { CompleteButton.IsEnabled=false; RunExamplesButton.IsEnabled=false; AnalyzeCodeButton.IsEnabled=false; RuntimeLabel.Text="管理员进程不执行练习，请普通方式启动 EntLimit。"; } }
    private void SetCodeRunning(bool running) { CancelCodeButton.IsEnabled = running; CodeEditor.IsReadOnly = running; SetLearningControls(); }
    private async Task PollCodeAsync()
    {
        if (_polling || _codeJobId is null) return;
        _polling = true;
        string jobId = _codeJobId;
        string? submittedCode = _submittedCode, exerciseId = _submittedExerciseId;
        try
        {
            var response = await SendAsync(new() { Command = "code_status", JobId = jobId });
            if (_codeJobId != jobId || _submittedJobId != jobId || _submittedExerciseId != exerciseId || _exercise?.Id != exerciseId
                || response.CodeJob is not { } job || job.JobId != jobId || job.ExerciseId != exerciseId || job.State == "running") return;
            _codePoll.Stop(); _codeJobId = null; SetCodeRunning(false);
            if (job.State != "completed" || job.Result is not { } result)
            {
                CodeResults.Text = job.Message;
                if (job.AnalysisOnly) SetAnalysisMessage(job.Message);
                return;
            }
            string resultDetails = string.Join("\n\n", result.Cases.Select(c => (c.Passed ? "✓ " : "× ") + c.Name + "\n预期：" + c.Expected + "\n实际：" + c.Actual + (string.IsNullOrEmpty(c.Error) ? "" : "\n" + c.Error))) + (string.IsNullOrEmpty(result.StandardOutput) ? "" : "\n\n程序输出\n" + result.StandardOutput);
            CodeResults.Text = result.Message + "\n\n" + resultDetails;
            if (!job.ExamplesOnly && result.Analysis is { } analysis && submittedCode is not null)
            {
                string expectedHash = HashCode(submittedCode);
                if (!string.Equals(expectedHash, analysis.CodeHash, StringComparison.OrdinalIgnoreCase)) SetAnalysisMessage("分析结果与本次提交的代码版本不一致，已隐藏；请重新分析当前解答。");
                else
                {
                    _analysisCodeHash = expectedHash;
                    _analysisView = new CodeAnalysisView(analysis, result.Message);
                    AnalysisContent.Children.Clear(); AnalysisContent.Children.Add(_analysisView);
                    UpdateAnalysisStaleness();
                    if (!job.AnalysisOnly) CodeResults.Text = result.Message + "\n\n解答分析摘要\n" + (analysis.AstAvailable ? analysis.Summary : "未完成 AST 识别，仅有通用学习参考。" + analysis.Summary) + "\n更多内容见“解答分析”页。\n\n" + resultDetails;
                }
            }
            else if (!job.ExamplesOnly) SetAnalysisMessage("本次未获得结构分析。可以手动重试“分析当前解答”，参考解仍可主动展开。");
            if (job.AnalysisOnly)
            {
                Notice.Text = result.Analysis?.AstAvailable == true ? "结构分析已完成；仅提供有限本地规则建议，未运行代码、未授予许可。" : "本次未完成 AST 识别，请查看分析页中的环境或语法提示；仍可展开通用参考，未授予许可。";
                return;
            }
            if (result.Passed && !job.ExamplesOnly && !job.AnalysisOnly)
            {
                if (_referenceSolution is not null && _exercise?.Id == _codeExercise?.Id) { _referenceSolution.Header = "测试通过 · 展开参考解，对照思路与复杂度"; if (_referenceButton is not null) _referenceButton.Content = "对照参考解"; }
                Feedback.Text = "今天的收获：" + _codeExercise?.Takeaway + "\n\n" + (response.Session.Phase == SessionPhase.Ready ? "许可已准备好，去安心玩吧。" : "完成记录已保存，现有许可保持不变。");
                Notice.Text = "全部测试通过，完成记录已保存；“解答分析”中可查看这份代码的建议。"; UpdateExerciseProgress();
            }
            else if (!job.ExamplesOnly) Notice.Text = "请先看失败用例；“解答分析”提供结构建议与教程，不能替代正确性检查。";
        }
        catch { if (_codeJobId == jobId) { _codePoll.Stop(); Notice.Text = "任务状态暂时无法连接；点击停止可尝试取消，然后刷新重试。"; } }
        finally { _polling = false; }
    }

    private static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    private void ClearCodeAnalysis()
    {
        if (_cloudExerciseId is not null) _onlineCancellation?.Cancel();
        _cloudVersion=null;
        _analysisView = null; _analysisCodeHash = null;
        if (AnalysisContent is null) return;
        AnalysisContent.Children.Clear(); AnalysisContent.Children.Add(new TextBlock { Text = "点击“分析当前解答”，或提交全部检查后查看结构建议与教程。运行示例不触发分析。", TextWrapping = TextWrapping.Wrap });
        AnalysisTab.Header = "解答分析";
    }
    private void SetAnalysisMessage(string message)
    {
        _analysisView = null; _analysisCodeHash = null;
        AnalysisContent.Children.Clear(); AnalysisContent.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }); AnalysisTab.Header = "解答分析";
    }
    private void UpdateAnalysisStaleness()
    {
        if (_analysisCodeHash is null) return;
        if (_cloudVersion is not null)
        {
            bool cloudStale=!string.Equals(HashCode(CodeEditor.Text),_analysisCodeHash,StringComparison.OrdinalIgnoreCase);
            _cloudVersion.Text=cloudStale?"代码已修改：以下在线建议对应发送时的旧代码，请重新发送分析。":"对应发送时的当前代码；模型建议未执行代码，需要自行验证。";
            AnalysisTab.Header=cloudStale?"在线建议 · 代码已修改":"在线建议";
            return;
        }
        if (_analysisView is null) return;
        bool stale = !string.Equals(HashCode(CodeEditor.Text), _analysisCodeHash, StringComparison.OrdinalIgnoreCase);
        _analysisView.SetStale(stale); AnalysisTab.Header = stale ? "分析 · 代码已修改" : "解答分析";
    }

    private async Task CancelCurrentJobAsync()
    {
        if (_codeJobId is null) return;
        string jobId = _codeJobId;
        try { await SendAsync(new() { Command = "cancel_code", JobId = jobId }); if (_codeJobId != jobId) return; _codeJobId = null; _codePoll.Stop(); SetCodeRunning(false); CodeResults.Text = "本次任务已停止，代码草稿保留。"; if (!_submittedExamplesOnly) SetAnalysisMessage("任务已停止，未产生新的分析；可以重新分析当前解答。"); }
        catch { Notice.Text = "取消请求暂未送达，后台运行仍有超时限制。"; }
    }
    private async void CancelCode_Click(object sender, RoutedEventArgs e) => await CancelCurrentJobAsync();
    private void CodeEditor_Changed(object sender, TextChangedEventArgs e) { UpdateAnalysisStaleness(); _highlight?.InvalidateVisual(); if (LineNumbers is not null) LineNumbers.Text = string.Join("\n", Enumerable.Range(1, Math.Max(1, CodeEditor.Text.Count(c=>c=='\n') + 1))); ScheduleDraft(); }
    private void CodeEditor_Scrolled(object sender, ScrollChangedEventArgs e) { _highlight?.InvalidateVisual(); if (LineScroll is not null) LineScroll.ScrollToVerticalOffset(e.VerticalOffset); }
    private void CodeEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (CodeEditor.IsReadOnly) return;
        if (e.Key == Key.Tab) { int caret = CodeEditor.SelectionStart; CodeEditor.SelectedText = "    "; CodeEditor.CaretIndex = caret + 4; e.Handled = true; }
        if (e.Key == Key.Return)
        {
            int caret = CodeEditor.CaretIndex; string before = CodeEditor.Text[..caret]; string line = before[(before.LastIndexOf('\n')+1)..]; string indent = new(line.TakeWhile(c=>c==' ').ToArray());
            if (line.TrimEnd().EndsWith(':')) indent += "    ";
            CodeEditor.SelectedText = Environment.NewLine + indent; CodeEditor.CaretIndex = caret + Environment.NewLine.Length + indent.Length; e.Handled = true;
        }
    }
}

public sealed class DesktopPreferences
{
    public string? PythonPath { get; set; }
    public string? JavaPath { get; set; }
    public string TopicId { get; set; } = "network";
    public string Mode { get; set; } = "standard";
    public string SelectionStrategy { get; set; } = "recommended";
    public string? LastExerciseId { get; set; }
    public Dictionary<string,string> CodeDrafts { get; set; } = [];
    public Dictionary<string,int> ReviewDraftPassCounts { get; set; } = [];
    public Dictionary<string, Dictionary<string,string>> Answers { get; set; } = [];
}
