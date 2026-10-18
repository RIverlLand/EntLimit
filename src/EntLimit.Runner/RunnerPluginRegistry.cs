using System.Security.Cryptography;
using System.Text.Json;
using EntLimit.Core;

namespace EntLimit.Runner;

/// <summary>Explicitly installed executable code, separate from passive exercise packs. Same-user trust, not anti-tampering.</summary>
public static class RunnerPluginRegistry
{
    const int MaxFileBytes = 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static List<RunnerPluginInfo> GetInstalled(string appRoot)
    {
        var result = new List<RunnerPluginInfo>();
        List<InstalledRunnerPlugin> entries;
        try { entries = ReadIndex(appRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or System.ComponentModel.Win32Exception)
        { return [new() { Id = "invalid-registry", Trusted = false, Message = "插件安装索引损坏；Python 内置运行器仍可用。" }]; }
        foreach (var entry in entries)
        {
            try { var resolved = Resolve(appRoot, entry); result.Add(new() { Id = resolved.Manifest.Id, Name = resolved.Manifest.Name, LanguageId = resolved.Manifest.LanguageId, Trusted = true }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or System.ComponentModel.Win32Exception)
            { result.Add(new() { Id = entry.Id, Trusted = false, Message = "插件校验失败，需要重新检查安装和授权。" }); }
        }
        return result;
    }
    public static (RunnerPluginManifest Manifest, string Executable) ResolveLanguage(string appRoot, string language)
    {
        foreach (var entry in ReadIndex(appRoot))
        {
            try
            {
                var resolved = Resolve(appRoot, entry);
                if (resolved.Manifest.LanguageId == language) return resolved;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or System.ComponentModel.Win32Exception) { }
        }
        throw new InvalidDataException("未安装或未授权该语言运行器插件。");
    }
    public static void RegisterInstalled(string appRoot, string manifestPath, bool explicitConsent)
    {
        if (!explicitConsent) throw new InvalidOperationException("安装可执行插件需要明确授权。");
        var root = Path.Combine(Path.GetFullPath(appRoot), "plugins");
        manifestPath = Path.GetFullPath(manifestPath);
        EnsureInside(root, manifestPath);
        EnsureNoLinks(root, manifestPath);
        string relative = Path.GetRelativePath(root, manifestPath);
        var bytes = ReadBounded(manifestPath);
        var manifest = JsonSerializer.Deserialize<RunnerPluginManifest>(bytes, Json) ?? throw new InvalidDataException("Empty manifest.");
        var entry = new InstalledRunnerPlugin { Id = manifest.Id, ManifestPath = relative.Replace('\\', '/'), ManifestSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        _ = Resolve(appRoot, entry);
        var entries = ReadIndex(appRoot).Where(e => e.Id != entry.Id).ToList(); entries.Add(entry);
        Directory.CreateDirectory(root);
        string index = Path.Combine(root, "installed-plugins.json"); string temp = index + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json)); File.Move(temp, index, true);
    }
    public static void CreateManifest(string pluginDirectory, string id, string name, string languageId, string executable)
    {
        var files = EnumeratePluginFiles(pluginDirectory)
            .Where(f => !Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetRelativePath(pluginDirectory, f).Replace('\\', '/'), f => Hash(f), StringComparer.OrdinalIgnoreCase);
        var manifest = new RunnerPluginManifest { Id = id, Name = name, LanguageId = languageId, Executable = executable, Files = files };
        File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
    }
    static List<InstalledRunnerPlugin> ReadIndex(string appRoot)
    {
        string root = Path.Combine(Path.GetFullPath(appRoot), "plugins"); string path = Path.Combine(root, "installed-plugins.json");
        if (!File.Exists(path)) return [];
        EnsureNoLinks(root, path);
        var entries = JsonSerializer.Deserialize<List<InstalledRunnerPlugin>>(ReadBounded(path), Json) ?? throw new InvalidDataException("Invalid installed index.");
        if (entries.Count > 32 || entries.Any(e => e is null || !ValidId(e.Id) || string.IsNullOrWhiteSpace(e.ManifestPath) || e.ManifestPath.Length > 512 || e.ManifestSha256 is null || e.ManifestSha256.Length != 64)) throw new InvalidDataException("Malformed installed index.");
        return entries;
    }
    static (RunnerPluginManifest Manifest, string Executable) Resolve(string appRoot, InstalledRunnerPlugin entry)
    {
        string root = Path.Combine(Path.GetFullPath(appRoot), "plugins");
        string path = ResolveRelative(root, entry.ManifestPath); var bytes = ReadBounded(path);
        if (!HashEquals(Convert.ToHexString(SHA256.HashData(bytes)), entry.ManifestSha256)) throw new InvalidDataException("Manifest hash mismatch.");
        var manifest = JsonSerializer.Deserialize<RunnerPluginManifest>(bytes, Json) ?? throw new InvalidDataException("Invalid manifest.");
        if (manifest.ProtocolVersion != 1 || manifest.Id != entry.Id || !ValidId(manifest.Id) || !ValidId(manifest.LanguageId) || manifest.Name is null || manifest.Name.Length > 128 || manifest.Files is null || manifest.Files.Count is < 1 or > 1024) throw new InvalidDataException("Unsupported plugin identity/protocol.");
        string folder = Path.GetDirectoryName(path)!; string executable = ResolveRelative(folder, manifest.Executable);
        var shells = new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "bash.exe", "sh.exe", "rundll32.exe", "mshta.exe", "regsvr32.exe" };
        if (shells.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Command interpreters cannot be plugin entry points.");
        if (!string.Equals(Path.GetExtension(executable), OperatingSystem.IsWindows() ? ".exe" : "", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only direct executable plugins are supported; no shell scripts.");
        if (OperatingSystem.IsWindows())
        {
            string originalName = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).OriginalFilename ?? "";
            if (originalName.EndsWith(".mui", StringComparison.OrdinalIgnoreCase)) originalName = originalName[..^4];
            if (shells.Contains(originalName, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Renamed command interpreters cannot be plugin entry points.");
        }
        var pinned = manifest.Files.ToDictionary(f => ResolveRelative(folder, f.Key), f => f.Value, StringComparer.OrdinalIgnoreCase);
        if (!pinned.ContainsKey(executable)) throw new InvalidDataException("Executable must be hash-pinned.");
        foreach (var file in pinned)
            if (!HashEquals(Hash(file.Key), file.Value)) throw new InvalidDataException("Plugin dependency hash mismatch.");
        // Every shipped DLL/runtime config must be pinned, not only the launcher EXE.
        foreach (var file in EnumeratePluginFiles(folder).Where(f => Path.GetFileName(f) != "manifest.json" && !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            if (!pinned.ContainsKey(Path.GetFullPath(file))) throw new InvalidDataException("Unpinned executable dependency.");
        return (manifest, executable);
    }
    public static string ResolveRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(s => s is ".." or "." or "")) throw new InvalidDataException("Unsafe plugin relative path.");
        string path = Path.GetFullPath(Path.Combine(root, relative)); EnsureInside(root, path); EnsureNoLinks(root, path); return path;
    }
    static IEnumerable<string> EnumeratePluginFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root); int count = 0;
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > 4096) throw new InvalidDataException("Plugin directory contains too many entries.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked plugin directory entries are forbidden.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry); else yield return entry;
            }
        }
    }
    static void EnsureInside(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Plugin path escapes its directory.");
    }
    static void EnsureNoLinks(string root, string path)
    {
        var current = new FileInfo(path) as FileSystemInfo;
        string boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked plugin paths are not supported.");
            if (current.FullName.TrimEnd(Path.DirectorySeparatorChar).Equals(boundary, StringComparison.OrdinalIgnoreCase)) break;
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
    }
    static byte[] ReadBounded(string path) { if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("Plugin metadata too large."); return File.ReadAllBytes(path); }
    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static bool HashEquals(string actual, string? expected) => expected is { Length: 64 } && actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    static bool ValidId(string? value) => value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
