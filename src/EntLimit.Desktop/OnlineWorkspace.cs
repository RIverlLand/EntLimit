using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using EntLimit.Core;
using EntLimit.Online;
using Microsoft.Win32;

namespace EntLimit.Desktop;

public partial class MainWindow
{
    private ProtectedApiStore? _apiStore;
    private CompatibleApiClient? _apiClient;
    private CancellationTokenSource? _onlineCancellation;
    private string? _cloudExerciseId;
    private TextBlock? _cloudVersion;
    private TextBox _endpointInput = null!, _modelInput = null!, _topicInput = null!, _onlineOutput = null!, _packInput = null!, _javaInput = null!;
    private PasswordBox _keyInput = null!;
    private TextBlock _apiStatus = null!;
    private StackPanel _packRows = null!, _extensionRows = null!;
    private string? _previewPackJson, _previewCatalogRevision;
    private readonly Dictionary<string,ComboBox> _importChoices = [];
    private bool _updatingTopics, _previewExtensions;

    private void InitializeOnlineUi(bool forPreview=false)
    {
        if ((_preview && !forPreview) || _apiStore is not null) return;
        _apiStore = new ProtectedApiStore(forPreview ? Path.Combine(Path.GetTempPath(),"EntLimit-offline-preview-"+Guid.NewGuid().ToString("N")) : DataPaths.GetRoot()); _apiClient = new CompatibleApiClient();
        ApiSettings settings; try { settings=forPreview ? new() : _apiStore.LoadSettings(); } catch { settings=new(); }
        var body=new StackPanel { Margin=new Thickness(24) };
        body.Children.Add(OnlineHeading("API 与扩展",22));
        body.Children.Add(OnlineParagraph("本地规则分析保持免费。只有点击连接测试、在线分析或在线生成时才访问配置的服务；在线建议不授予许可。"));
        _endpointInput=OnlineInput(settings.Endpoint); body.Children.Add(OnlineParagraph("兼容 Chat Completions 端点（例如 https://服务地址/v1）；HTTP 仅限本机无密钥服务。请求直连，不使用系统代理。")); body.Children.Add(_endpointInput);
        _modelInput=OnlineInput(settings.Model); body.Children.Add(OnlineParagraph("模型名称")); body.Children.Add(_modelInput);
        _keyInput=new PasswordBox { Padding=new Thickness(8),MaxLength=16000,Margin=new Thickness(0,6,0,8) }; body.Children.Add(OnlineParagraph("API Key（输入为遮罩；留空保留当前端点的已保存密钥，本地无认证服务可留空）")); body.Children.Add(_keyInput);
        _apiStatus=OnlineParagraph(_apiStore.HasKey ? "已保存当前用户 DPAPI 加密密钥；不会将密钥填回界面。" : "尚未保存密钥。"); body.Children.Add(_apiStatus);
        var apiActions=new WrapPanel();
        apiActions.Children.Add(OnlineButton("保存 API 设置",SaveApiSettings)); apiActions.Children.Add(OnlineButton("删除已保存密钥",()=>{ try { _apiStore.DeleteKey(); _keyInput.Clear(); _apiStatus.Text="密钥已删除，端点和模型仍保留。"; } catch { _apiStatus.Text="密钥未能删除，请检查数据目录权限。"; } }));
        apiActions.Children.Add(OnlineButton("连接测试（发送短问候）",()=>_ = OnlineRequestAsync(async token=>{ string text=await CallApiAsync("你是连通性测试助手。","仅回复 OK，不需要其他信息。",token); _apiStatus.Text="连接成功，已收到兼容文本回复。"; })));
        apiActions.Children.Add(OnlineButton("取消在线请求",()=>_onlineCancellation?.Cancel())); body.Children.Add(apiActions);
        body.Children.Add(OnlineHeading("生成与导入题包"));
        body.Children.Add(OnlineParagraph("可以导出题包生成提示词交给其他工具，也可以明确点击在线生成。生成内容仅是 AI 待审草稿，未经过网络检索；必须先预览、逐题确认再导入。导入不会执行题目代码。"));
        _topicInput=OnlineInput("网络基础：IP 地址与默认网关，入门，生成 3 道场景题"); body.Children.Add(_topicInput);
        var generateActions=new WrapPanel(); generateActions.Children.Add(OnlineButton("导出生成提示词",()=>_ = ExportPackPromptAsync())); generateActions.Children.Add(OnlineButton("在线生成草稿",()=>_ = GeneratePackAsync()));
        generateActions.Children.Add(OnlineButton("读取 JSON 文件",ReadPackFile)); generateActions.Children.Add(OnlineButton("预览题包",()=>_ = PreviewPackAsync())); body.Children.Add(generateActions);
        _packInput=OnlineInput(""); _packInput.AcceptsReturn=true; _packInput.TextWrapping=TextWrapping.Wrap; _packInput.VerticalScrollBarVisibility=ScrollBarVisibility.Auto; _packInput.Height=170; _packInput.MaxLength=60000; body.Children.Add(_packInput);
        _packInput.TextChanged+=(_,_)=>{ _previewPackJson=null; _previewCatalogRevision=null; _packRows?.Children.Clear(); _importChoices.Clear(); };
        _packRows=new StackPanel(); body.Children.Add(_packRows); body.Children.Add(OnlineButton("确认所选项并导入",()=>_ = ImportPackAsync()));
        _onlineOutput=OnlineInput(""); _onlineOutput.IsReadOnly=true; _onlineOutput.AcceptsReturn=true; _onlineOutput.Height=170; _onlineOutput.TextWrapping=TextWrapping.Wrap; _onlineOutput.VerticalScrollBarVisibility=ScrollBarVisibility.Auto; body.Children.Add(_onlineOutput);
        body.Children.Add(OnlineHeading("已安装内容与运行器")); body.Children.Add(OnlineParagraph("扩展管理只展示经过验证的内容包和显式安装的运行器，不下载或自动运行未知可执行插件。"));
        _extensionRows=new StackPanel(); body.Children.Add(_extensionRows);
        body.Children.Add(OnlineParagraph("Java JDK 目录（必须含 bin/java.exe 和 bin/javac.exe；练习进程保持普通用户权限）"));
        _javaInput=OnlineInput(_preferences.JavaPath ?? ""); body.Children.Add(_javaInput);
        body.Children.Add(OnlineButton("选择 Java JDK 文件夹",()=>{ var picker=new OpenFolderDialog { Title="选择已安装的 JDK 目录" }; if(picker.ShowDialog(this)==true) { _preferences.JavaPath=picker.FolderName; _javaInput.Text=picker.FolderName; SavePreferences(); UpdateRuntimeLabel(); } }));
        body.Children.Add(OnlineButton("保存 Java 路径",()=>{ _preferences.JavaPath=_javaInput.Text.Trim(); SavePreferences(); UpdateRuntimeLabel(); _apiStatus.Text="Java 路径已保存；运行器会在练习时校验。"; }));
        IntegrationHost.Child=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
    }
    public void PreparePreview(string mode)
    {
        PreparePreview(mode=="code");
        if(mode is not ("online" or "extensions"))return;
        InitializeOnlineUi(forPreview:true); Navigate_Click(NavIntegrations,new RoutedEventArgs()); _previewExtensions=mode=="extensions";
        if(_snapshot is not null) { _snapshot.ContentPacks=[new(){PackId="builtin",Name="内置原创题包（离线预览）",Version="1.0",ExerciseCount=_snapshot.Exercises.Count}]; _snapshot.RunnerPlugins=[new(){Id="python",Name="Python 个人练习运行器",LanguageId="python",Trusted=true,Message="仅使用本机选择的解释器"}]; UpdateExtensionList(_snapshot); }
    }
    private static TextBlock OnlineHeading(string text,double size=18)=>new(){Text=text,FontSize=size,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,18,0,9)};
    private static TextBlock OnlineParagraph(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,LineHeight=23,Margin=new Thickness(0,4,0,6)};
    private static TextBox OnlineInput(string text)=>new(){Text=text,Margin=new Thickness(0,5,0,8),Padding=new Thickness(8)};
    private static Button OnlineButton(string text,Action click) { var button=new Button { Content=text,HorizontalAlignment=HorizontalAlignment.Left }; button.Click+=(_,_)=>click(); return button; }
    private ApiSettings CurrentApiSettings()=>new(){Endpoint=ApiEndpoint.Canonical(_endpointInput.Text),Model=_modelInput.Text.Trim()};
    private void SaveApiSettings()
    {
        if(_preview)return;
        try
        {
            var settings=CurrentApiSettings(); ApiEndpoint.ValidateModel(settings.Model);
            if (_keyInput.Password.Length>0) { _apiStore!.SaveKey(settings.Endpoint,_keyInput.Password); _keyInput.Clear(); }
            else if (_apiStore!.HasKey) _ = _apiStore.ReadKey(settings.Endpoint); // Endpoint-bound; cannot silently reuse a key at a new URL.
            _apiStore.SaveSettings(settings); _apiStatus.Text="API 设置已保存；密钥仅以当前用户加密文件保存。";
        }
        catch(InvalidOperationException ex) { _apiStatus.Text=ex.Message; }
        catch { _apiStatus.Text="API 设置未能保存，请检查数据目录权限。"; }
    }
    private async Task<string> CallApiAsync(string system,string prompt,CancellationToken token)
    {
        var settings=CurrentApiSettings(); string key=_keyInput.Password.Length>0 ? _keyInput.Password : _apiStore!.ReadKey(settings.Endpoint);
        return await _apiClient!.CompleteAsync(settings,key,system,prompt,token);
    }
    private async Task OnlineRequestAsync(Func<CancellationToken,Task> operation)
    {
        if(_preview) { _apiStatus.Text="离线预览不会发送请求。"; return; }
        if (_onlineCancellation is not null) { _apiStatus.Text="已有在线请求，请等待或取消。"; return; }
        using var cancellation=new CancellationTokenSource(); _onlineCancellation=cancellation;
        try { _apiStatus.Text="正在请求配置的服务…"; await operation(cancellation.Token); }
        catch(OperationCanceledException) { _apiStatus.Text="在线请求已取消。"; }
        catch(InvalidOperationException ex) { _apiStatus.Text=ex.Message; }
        catch { _apiStatus.Text="在线请求未完成；服务端错误详情不会显示。"; }
        finally { _onlineCancellation=null; _cloudExerciseId=null; SetLearningControls(); }
    }
    private async Task<string> GetPackPromptAsync() { var response=await SendAsync(new(){Command="export_pack_prompt"}); return response.PromptText ?? ""; }
    private async Task ExportPackPromptAsync()
    {
        await RunAsync(async()=>{ string prompt=await GetPackPromptAsync(); var save=new SaveFileDialog { Filter="文本文件|*.txt",FileName="EntLimit-题包生成提示词.txt" }; if(save.ShowDialog(this)==true) { await File.WriteAllTextAsync(save.FileName,prompt+"\n\n用户要求："+_topicInput.Text); _onlineOutput.Text="提示词已导出，不包含 API 密钥、学习记录或草稿。"; } });
    }
    private async Task GeneratePackAsync()=>await OnlineRequestAsync(async token=>
    {
        string prompt=await GetPackPromptAsync();
        string answer=await CallApiAsync("你是原创练习题编写助手。只返回符合用户给定 schema 的 JSON 题包，不加代码围栏。所有题目 SourceType=original，SourceLabel 明确写 AI 生成待人工审核；不要声称检索过网页，不要复制课程原题，不执行代码。",prompt+"\n用户要求："+_topicInput.Text,token);
        _packInput.Text=MarkGeneratedPack(answer.Trim()); _onlineOutput.Text="已生成待审草稿，尚未导入。请预览并核对题目、答案和来源。"; _apiStatus.Text="在线生成完成，等待本地预览。";
    });
    private void ReadPackFile()
    {
        var open=new OpenFileDialog {Filter="题包 JSON|*.json",CheckFileExists=true}; if(open.ShowDialog(this)!=true)return;
        try { if(new FileInfo(open.FileName).Length>48*1024)throw new InvalidOperationException("题包最多 48 KiB。"); _packInput.Text=File.ReadAllText(open.FileName,Encoding.UTF8); }
        catch(InvalidOperationException ex){_onlineOutput.Text=ex.Message;} catch{_onlineOutput.Text="题包文件无法读取。";}
    }
    private async Task PreviewPackAsync()=>await RunAsync(async()=>
    {
        string json=_packInput.Text; if(Encoding.UTF8.GetByteCount(json)>48*1024)throw new InvalidOperationException("题包最多 48 KiB。");
        var response=await SendAsync(new(){Command="preview_pack",PackJson=json}); var preview=response.PackPreview ?? throw new InvalidOperationException("未收到题包预览。");
        _previewPackJson=json; _previewCatalogRevision=preview.CatalogRevision; _packRows.Children.Clear(); _importChoices.Clear();
        _packRows.Children.Add(OnlineParagraph($"题包：{preview.Name} · {preview.Rows.Count} 条。相同题默认跳过，更新和相似项请人工决定。"));
        foreach(var row in preview.Rows)
        {
            var panel=new DockPanel {Margin=new Thickness(0,6,0,6)}; var choice=new ComboBox {Width=130,Margin=new Thickness(0,0,12,0)};
            choice.Items.Add(new ComboBoxItem {Content="跳过",Tag="skip"});
            if(row.Status is "new" or "similar")choice.Items.Add(new ComboBoxItem {Content="新增",Tag="add"});
            if(row.Status=="update")choice.Items.Add(new ComboBoxItem {Content="更新",Tag="update"});
            choice.SelectedIndex=row.Status=="new"?1:0; DockPanel.SetDock(choice,Dock.Right); panel.Children.Add(choice);
            panel.Children.Add(OnlineParagraph($"[{row.Status}] {row.Title} · {row.TopicId}"+(string.IsNullOrEmpty(row.SimilarToId)?"":$" · 相似于 {row.SimilarToId}")));
            _packRows.Children.Add(panel);
            string? details=PackExerciseDetails(json,row.ExerciseId);
            if(details is not null)_packRows.Children.Add(new Expander {Header="查看本题题干、答案与测试",Content=new TextBox {Text=details,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=240,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},Margin=new Thickness(0,0,0,8)});
            _importChoices[row.ExerciseId]=choice;
        }
    });
    private async Task ImportPackAsync()=>await RunAsync(async()=>
    {
        if(_previewPackJson is null || _previewCatalogRevision is null || _previewPackJson!=_packInput.Text)throw new InvalidOperationException("请先预览当前 JSON，再确认导入。");
        var choices=_importChoices.ToDictionary(x=>x.Key,x=>(x.Value.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"skip");
        var response=await SendAsync(new(){Command="import_pack",PackJson=_previewPackJson,PackChoices=choices,ExpectedCatalogRevision=_previewCatalogRevision});
        _previewPackJson=null; _previewCatalogRevision=null; _onlineOutput.Text=_exerciseCatalogStale ? "导入完成。"+CatalogChangedNotice : "导入完成。动态题集已更新；现有练习、草稿、许可和学习记录保持。"; UpdateExtensionList(response);
    });
    private void UpdateExtensionList(IpcResponse response)
    {
        if(_extensionRows is null)return; _extensionRows.Children.Clear();
        foreach(var pack in response.ContentPacks)_extensionRows.Children.Add(OnlineParagraph($"内容包 · {pack.Name} · v{pack.Version} · {pack.ExerciseCount} 题"));
        foreach(var runner in response.RunnerPlugins)_extensionRows.Children.Add(OnlineParagraph($"运行器 · {runner.Name} · {runner.LanguageId} · {(runner.Trusted ? "已验证" : "不可运行")} · {runner.Message}"));
        foreach(var warning in response.ContentWarnings)_extensionRows.Children.Add(OnlineParagraph("内容提示："+warning));
    }
    private async void CloudAnalyze_Click(object sender,RoutedEventArgs e)
    {
        if(_preview || _exercise?.Kind!="code" || _busy || _codeJobId is not null || _exerciseCatalogStale)return;
        var exercise=_exercise; string code=CodeEditor.Text,hash=HashCode(code);
        await OnlineRequestAsync(async token=>
        {
            _cloudExerciseId=exercise.Id; SetLearningControls();
            string answer=await CallApiAsync("你是编程学习助手。仅根据用户给出的题目和代码提供条件性的分析建议、复杂度、改进思路及短练习。不要声称运行过代码、证明正确或检索过网页。输出纯文本，不执行代码。",$"题目：{exercise.Title}\n要求：{exercise.Scenario}\n代码：\n{code}",token);
            if(_exercise?.Id!=exercise.Id || token.IsCancellationRequested || _exerciseCatalogStale)return;
            AnalysisContent.Children.Clear(); _analysisView=null; _analysisCodeHash=hash;
            AnalysisContent.Children.Add(OnlineHeading("在线建议 · 未运行、未判题"));
            _cloudVersion=OnlineParagraph(""); AnalysisContent.Children.Add(_cloudVersion); UpdateAnalysisStaleness();
            AnalysisContent.Children.Add(new TextBox {Text=answer,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent});
            _onlineOutput.Text="在线代码分析已完成，详见练习页的“解答分析”。"; UpdateAnalysisStaleness(); CodeOutputTabs.SelectedIndex=1;
            _apiStatus.Text="在线建议已返回，未授予许可。";
        });
    }
    private static string MarkGeneratedPack(string answer)
    {
        try
        {
            var pack=JsonSerializer.Deserialize<ExerciseContentPack>(answer,PipeProtocol.JsonOptions);
            if(pack?.Exercises is null || pack.Exercises.Count==0)throw new InvalidOperationException("API 未返回有效 JSON 题包，请调整要求后重新生成。");
            foreach(var exercise in pack.Exercises) { exercise.SourceType="original"; exercise.SourceLabel="AI 生成待人工审核；未检索或验证外部来源"; }
            return JsonSerializer.Serialize(pack,new JsonSerializerOptions(PipeProtocol.JsonOptions){WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
        }
        catch(JsonException) { throw new InvalidOperationException("API 草稿不是有效 JSON 题包；没有自动导入任何内容，请调整要求后重试。"); }
    }
    private static string? PackExerciseDetails(string json,string id)
    {
        try
        {
            var pack=JsonSerializer.Deserialize<ExerciseContentPack>(json,PipeProtocol.JsonOptions);
            var item=pack?.Exercises.FirstOrDefault(x=>x.Id==id);
            if(item is not null)return JsonSerializer.Serialize(item,new JsonSerializerOptions(PipeProtocol.JsonOptions){WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException) { }
        return null;
    }
    private string TopicName(string id)=>_snapshot?.Topics.FirstOrDefault(x=>x.Id==id)?.Name ?? (id=="network"?"网络":id=="python"?"Python":id);
    private void UpdateTopicChoices(IpcResponse response)
    {
        var topics=response.Topics.Select(x=>(x.Id,x.Name)).ToList();
        if(topics.Count==0)topics=response.Exercises.Select(x=>(x.TopicId,x.TopicId=="network"?"网络基础":x.TopicId=="python"?"Python 编程":x.TopicId)).Distinct().ToList();
        topics.Add(("all","全部题集"));
        string desired=_loaded ? (TopicBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()??_preferences.TopicId : _preferences.TopicId;
        if(!topics.Any(x=>x.Id==desired))desired=topics.Any(x=>x.Id=="network")?"network":topics[0].Id;
        var existing=TopicBox.Items.OfType<ComboBoxItem>().Select(x=>(x.Tag?.ToString(),x.Content?.ToString())).ToList();
        if(existing.SequenceEqual(topics.Select(x=>((string?)x.Id,(string?)x.Name))))return;
        _updatingTopics=true;
        try { TopicBox.Items.Clear(); foreach(var topic in topics)TopicBox.Items.Add(new ComboBoxItem {Tag=topic.Id,Content=topic.Name}); SelectTagged(TopicBox,desired); }
        finally { _updatingTopics=false; }
    }
    private void StartAdminBroker_Click(object sender,RoutedEventArgs e)
    {
        if(!File.Exists(Path.Combine(AppContext.BaseDirectory,"installed.flag"))) { Notice.Text="管理员窗口协助仅在正式安装版可用；便携版不提升运行器或练习代码权限。"; return; }
        string path=Path.Combine(AppContext.BaseDirectory,"EntLimit.AdminBroker.exe");
        if(!File.Exists(path)) { Notice.Text="未找到管理员窗口协助程序，请修复安装。"; return; }
        try
        {
            using var identity=WindowsIdentity.GetCurrent();
            var start=new ProcessStartInfo(path) {UseShellExecute=true,Verb="runas",WorkingDirectory=AppContext.BaseDirectory};
            start.ArgumentList.Add("--owner-sid"); start.ArgumentList.Add(identity.User!.Value); Process.Start(start);
            Notice.Text="已请求 Windows UAC 授权；管理员协助只处理窗口，练习仍由普通用户进程执行。本次最多 8 小时，后台断连会退出；之后需手动重新授权。";
        }
        catch(System.ComponentModel.Win32Exception) { Notice.Text="管理员协助未启动；可能已取消 Windows UAC 授权。"; }
    }

}
