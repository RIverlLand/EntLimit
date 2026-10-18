using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace EntLimit.Online;

public sealed class ProtectedApiStore(string root)
{
    public string SettingsPath => Path.Combine(root,"api-settings.json");
    public string SecretPath => Path.Combine(root,"api-key.dpapi");
    public bool HasKey => File.Exists(SecretPath);
    public ApiSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return new();
        if (new FileInfo(SettingsPath).Length > 8192) throw new InvalidOperationException("API 设置文件过大。");
        return JsonSerializer.Deserialize<ApiSettings>(File.ReadAllText(SettingsPath)) ?? new();
    }
    public void SaveSettings(ApiSettings settings)
    {
        settings.Endpoint = ApiEndpoint.Canonical(settings.Endpoint); ApiEndpoint.ValidateModel(settings.Model);
        Directory.CreateDirectory(root); AtomicWrite(SettingsPath,Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings)));
    }
    public void SaveKey(string endpoint,string key)
    {
        key=key.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 16000 || key.Any(c=>c<'!' || c>'~')) throw new InvalidOperationException("请填写有效密钥。");
        if(ApiEndpoint.Validate(endpoint).Scheme=="http")throw new InvalidOperationException("本机 HTTP 仅支持无密钥服务；携带密钥请使用 HTTPS。");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new SecretEnvelope(ApiEndpoint.Canonical(endpoint),key));
        try { byte[] cipher=Dpapi(plain,false); Directory.CreateDirectory(root); AtomicWrite(SecretPath,cipher); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public string ReadKey(string endpoint)
    {
        if (!File.Exists(SecretPath)) return "";
        if (new FileInfo(SecretPath).Length > 65536) throw new InvalidOperationException("加密密钥文件无效，请删除后重新保存。");
        byte[] plain=Dpapi(File.ReadAllBytes(SecretPath),true);
        try
        {
            var envelope=JsonSerializer.Deserialize<SecretEnvelope>(plain) ?? throw new InvalidOperationException("密钥无法读取。");
            if (envelope.Endpoint != ApiEndpoint.Canonical(endpoint)) throw new InvalidOperationException("端点已变化，请为这个端点重新输入并保存密钥。");
            return envelope.Key;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void DeleteKey() { if (File.Exists(SecretPath)) File.Delete(SecretPath); }
    private static void AtomicWrite(string path,byte[] value)
    {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            FileStream stream;
            if (OperatingSystem.IsWindows() && path.EndsWith(".dpapi",StringComparison.Ordinal))
            {
                using var identity=WindowsIdentity.GetCurrent();
                var security=new FileSecurity(); security.SetAccessRuleProtection(true,false); security.SetOwner(identity.User!);
                security.AddAccessRule(new FileSystemAccessRule(identity.User!,FileSystemRights.FullControl,AccessControlType.Allow));
                stream=new FileInfo(temp).Create(FileMode.CreateNew,FileSystemRights.FullControl,FileShare.None,4096,FileOptions.WriteThrough,security);
            }
            else stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
            using(stream) { stream.Write(value); stream.Flush(flushToDisk:true); }
            File.Move(temp,path,true);
        }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }

    private static byte[] Dpapi(byte[] bytes,bool decrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("密钥加密仅支持当前 Windows 用户。");
        IntPtr input=Marshal.AllocHGlobal(bytes.Length); Blob output=default;
        try
        {
            Marshal.Copy(bytes,0,input,bytes.Length); var data=new Blob { Size=bytes.Length,Data=input };
            bool ok=decrypt ? CryptUnprotectData(ref data,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output) : CryptProtectData(ref data,null,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if (!ok) throw new InvalidOperationException("当前 Windows 用户无法加密或解密密钥，请重新保存。");
            var result=new byte[output.Size]; Marshal.Copy(output.Data,result,0,result.Length); return result;
        }
        finally
        {
            for (int i=0;i<bytes.Length;i++) Marshal.WriteByte(input,i,0);
            Marshal.FreeHGlobal(input);
            if (output.Data!=IntPtr.Zero) { for (int i=0;i<output.Size;i++) Marshal.WriteByte(output.Data,i,0); LocalFree(output.Data); }
        }
    }
    private sealed record SecretEnvelope(string Endpoint,string Key);
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
}
