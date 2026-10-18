using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EntLimit.Core;

if(args.Length != 3) throw new ArgumentException("payload installer repo");
string payload=Path.GetFullPath(args[0]), installer=Path.GetFullPath(args[1]), repo=Path.GetFullPath(args[2]);
int passed=0;
void Check(bool condition,string name) { if(!condition) throw new Exception(name); Console.WriteLine("PASS "+name); passed++; }
string Manifest(string file)
{
    var module=Native.LoadLibraryEx(file,IntPtr.Zero,0x22);
    if(module==IntPtr.Zero) throw new IOException("Cannot read PE resource");
    try
    {
        var resource=Native.FindResource(module,new IntPtr(1),new IntPtr(24));
        if(resource==IntPtr.Zero)throw new IOException("Missing embedded manifest");
        var size=Native.SizeofResource(module,resource);
        var bytes=new byte[size];
        Marshal.Copy(Native.LockResource(Native.LoadResource(module,resource)),bytes,0,bytes.Length);
        return Encoding.UTF8.GetString(bytes);
    }
    finally {Native.FreeLibrary(module);}
}
// Inno's bootstrapper intentionally remains asInvoker to retain the original user's
// token. PrivilegesRequired is compiled into setup data and the loader requests UAC.
Check(Manifest(installer).Contains("JR.Inno.Setup")
    && File.ReadAllText(Path.Combine(repo,"installer/EntLimit.iss")).Contains("PrivilegesRequired=admin")
    && !File.ReadAllText(Path.Combine(repo,"installer/EntLimit.iss")).Contains("PrivilegesRequiredOverridesAllowed"),
    "Inno administrator install policy has no non-admin override; original-user bootstrapper retained");
Check(Manifest(Path.Combine(payload,"EntLimit.AdminBroker.exe")).Contains("requireAdministrator"),"Broker requests administrator consent");
Check(Manifest(Path.Combine(payload,"EntLimit.EmergencyStop.exe")).Contains("asInvoker"),"Emergency executable starts without administrative rights");
foreach(var file in new[]{"EntLimit.Desktop.exe","EntLimit.Monitor.exe","EntLimit.NativeHost.exe"})
    Check(!Manifest(Path.Combine(payload,file)).Contains("requireAdministrator"),file+" does not request elevation");
Check(!Directory.Exists(Path.Combine(payload,"data")),"Package has no personal data directory");
Check(!Directory.GetFiles(payload,"*.dpapi",SearchOption.AllDirectories).Any(),"No encrypted or plaintext API credentials packaged");
Check(!Directory.GetFiles(payload,"*.pdb",SearchOption.AllDirectories).Any(),"Debug symbols excluded");
Check(File.Exists(Path.Combine(payload,"release-manifest.json")),"Relative release inventory exists");
string text=File.ReadAllText(Path.Combine(repo,"installer/EntLimit.iss"));
Check(text.Contains("runasoriginaluser"),"Installer launches normal app as original user");
Check(!text.Contains("[Registry]"),"Installer does not auto-register startup or browser permissions");
Check(!ExecutionSafety.IsElevated,"Regression harness and practice tests run unelevated");
// Real recovery smoke in an isolated pipe/data namespace. No real production latch or Run key.
string testRoot=Path.Combine(repo,"work","distribution-smoke-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
new LocalStore(testRoot).SaveConfig(new AppConfig());
string instance="dist-"+Guid.NewGuid().ToString("N");
ProcessStartInfo StartInfo(string name)
{
    var info=new ProcessStartInfo(Path.Combine(payload,name)){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=payload};
    info.Environment["ENT_LIMIT_DATA_DIR"]=testRoot;
    info.Environment["ENT_LIMIT_INSTANCE"]=instance;
    return info;
}
using var monitor=Process.Start(StartInfo("EntLimit.Monitor.exe"))!;
try
{
    Thread.Sleep(1200);
    Check(!monitor.HasExited,"Clean installed-layout monitor starts offline");
    string config=Path.Combine(testRoot,"config.json"), session=Path.Combine(testRoot,"session.json");
    Check(File.Exists(config)&&File.Exists(session),"Installed monitor creates isolated user data");
    string beforeConfig=File.ReadAllText(config),beforeSession=File.ReadAllText(session);
    // Halt the message loop completely: the independent escape hatch cannot depend on IPC.
    using var thread = new FrozenProcess(monitor.Id);
    var stopInfo=StartInfo("EntLimit.EmergencyStop.exe");stopInfo.ArgumentList.Add("--quiet");
    stopInfo.ArgumentList.Add("--test");
    stopInfo.Environment["ENT_LIMIT_TEST_PIDS"]=monitor.Id.ToString();
    using var stop=Process.Start(stopInfo)!;
    Check(stop.WaitForExit(20000),"Standalone emergency executable finishes against frozen monitor");
    Check(stop.ExitCode==0&&monitor.WaitForExit(5000),"Recovery kills only allowlisted installation monitor without IPC");
    Check(EmergencyStop.IsActive(testRoot),"Persistent emergency latch survives recovery executable exit");
    Check(File.ReadAllText(config)==beforeConfig&&File.ReadAllText(session)==beforeSession,"Recovery preserves configuration and learning state bytes");
    using var restarted=Process.Start(StartInfo("EntLimit.Monitor.exe"))!;
    Thread.Sleep(900);
    Check(!restarted.HasExited&&EmergencyStop.IsActive(testRoot),"Reopening monitor does not clear emergency latch");
    restarted.Kill();restarted.WaitForExit();
}
finally {if(!monitor.HasExited){monitor.Kill();monitor.WaitForExit();}}
Console.WriteLine($"{passed} distribution checks passed.");

sealed class FrozenProcess : IDisposable
{
    readonly IntPtr handle;
    public FrozenProcess(int pid){handle=Native.OpenProcess(0x0800,false,pid);if(handle==IntPtr.Zero||Native.NtSuspendProcess(handle)!=0)throw new IOException("Suspend test fixture failed");}
    public void Dispose(){if(handle!=IntPtr.Zero){Native.NtResumeProcess(handle);Native.CloseHandle(handle);}}
}
static class Native
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr FindResource(IntPtr module,IntPtr name,IntPtr type);
    [DllImport("kernel32.dll")]public static extern uint SizeofResource(IntPtr module,IntPtr resource);
    [DllImport("kernel32.dll")]public static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
    [DllImport("kernel32.dll")]public static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")]public static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll")]public static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")]public static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")]public static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")]public static extern int NtResumeProcess(IntPtr handle);
}
