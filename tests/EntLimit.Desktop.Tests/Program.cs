using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EntLimit.Core;
using EntLimit.Desktop;

internal static class Program
{
    [STAThread] static int Main()
    {
        // No window handle, Loaded event, IPC, API, real preferences or real data directory.
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow(preview:true,previewCode:true); window.PreparePreview(true);
        var type=typeof(MainWindow); const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        T Get<T>(string name)=>(T)type.GetField(name,flags)!.GetValue(window)!;
        void Set(string name,object value)=>type.GetField(name,flags)!.SetValue(window,value);
        void Call(string name,params object[] values)=>type.GetMethod(name,flags)!.Invoke(window,values);
        int count=0;
        void Check(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
        var snapshot=Get<IpcResponse>("_snapshot"); var exercise=Get<Exercise>("_exercise");
        var preferences=Get<DesktopPreferences>("_preferences"); var editor=Get<TextBox>("CodeEditor");
        const string previous="def retained_previous_answer(items): return ['history']";
        preferences.CodeDrafts[exercise.Id]=previous;
        snapshot.Session.History.Add(new(){AttemptId="fake-ui-pass",QuestionId=exercise.Id,TopicId=exercise.TopicId,CompletedAtUtc=DateTimeOffset.UtcNow});
        Call("RenderExercise",true,false,false);
        Check(editor.Text==exercise.Coding!.StarterCode,"Passed original code opens initial template without old solution");
        Check(preferences.CodeDrafts[exercise.Id]==previous,"Opening recall preserves retained historical code");
        editor.Text="temporary recall attempt";
        Set("_preview",false);Call("SaveCurrentDraft");Set("_preview",true);
        Check(preferences.CodeDrafts[exercise.Id]==previous,"Recall edits cannot overwrite implicit historical answer");
        Call("UpdateExerciseProgress");
        Check(editor.Text=="temporary recall attempt","Progress refresh does not erase current recall input");
        snapshot.Session.History.Clear(); Call("RenderExercise",true,false,false);
        Check(editor.Text==previous,"Unpassed original still restores its draft");
        const string draft="unfinished new code"; editor.Text=draft;
        Set("_preview",false);Call("SaveCurrentDraft");Set("_preview",true);
        Check(preferences.CodeDrafts[exercise.Id]==draft,"Unpassed draft still saves locally");
        var updated=JsonSerializer.Deserialize<Exercise>(JsonSerializer.Serialize(exercise))!;updated.Title+=" updated";
        snapshot.Exercises[snapshot.Exercises.FindIndex(x=>x.Id==exercise.Id)]=updated;
        Call("DetectExerciseCatalogChange",snapshot);
        Check(Get<bool>("_exerciseCatalogStale")&&!Get<Button>("CompleteButton").IsEnabled&&!Get<Button>("RunExamplesButton").IsEnabled,"Updated same-ID question blocks stale submissions and examples");
        Check(editor.Text==draft&&ReferenceEquals(Get<Exercise>("_exercise"),exercise),"Import update preserves visible question and in-progress draft");
        Call("RenderExercise",true,false,false);
        Check(ReferenceEquals(Get<Exercise>("_exercise"),updated)&&!Get<bool>("_exerciseCatalogStale"),"Explicit restart resolves latest question object and clears stale flag");
        Check(editor.Text==draft,"Explicit updated question reload preserves unfinished draft");
        var topic=Get<ComboBox>("TopicBox");
        snapshot.Topics=[new(){Id="python",Name="Python"},new(){Id="network",Name="网络"},new(){Id="english",Name="英语新题集"}];
        Call("UpdateTopicChoices",snapshot);
        Check(topic.Items.OfType<ComboBoxItem>().Any(x=>x.Tag?.ToString()=="english"),"Imported topic becomes a dynamic topic choice");
        Check(editor.Text==draft&&Get<Exercise>("_exercise").Id==exercise.Id,"Topic list refresh does not resample or overwrite current input");
        window.SetGatePresentation();
        Check(window.Topmost&&window.WindowState==WindowState.Maximized,"Denied-use learning entry becomes maximized and topmost");
        Check(Get<TabControl>("Tabs").SelectedIndex==0&&editor.Text==draft,"Gate presentation selects learning page without replacing draft");
        Call("UpdateGatePresentation",new IpcResponse());
        Check(window.Topmost,"Locked status preserves explicit gate presentation");
        Call("UpdateGatePresentation",new IpcResponse {Session=new(){Phase=SessionPhase.Ready}});
        Check(!window.Topmost,"Earned permit releases topmost without restoring old answers");
        window.SetGatePresentation();
        Call("UpdateGatePresentation",new IpcResponse {Config=new(){MonitoringEnabled=false}});
        Check(!window.Topmost,"Pausing monitoring releases topmost");
        window.SetGatePresentation();window.ClearGatePresentation();
        Check(!window.Topmost,"Normal explicit open can release gate topmost");
        Console.WriteLine($"{count} offline Desktop checks passed.");
        app.Shutdown();return 0;
    }
}
