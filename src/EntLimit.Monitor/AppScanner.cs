using System.Diagnostics;
using Microsoft.Win32;
using EntLimit.Core;
using System.Text.RegularExpressions;

namespace EntLimit.Monitor;

internal static class AppScanner
{
    public static List<AppCatalogEntry> Scan(CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, AppCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string? path, string source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim());
                if (path.StartsWith('"')) { int end = path.IndexOf('"', 1); if (end < 0) return; path = path[1..end]; }
                else { int end = path.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (end >= 0) path = path[..(end + 4)]; }
                if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
                path = Path.GetFullPath(path);
                if (!AppCatalogPolicy.IsSelectableCandidate(name, path, source)) return;
                bool hasVisibleWindow = source.Contains("运行", StringComparison.Ordinal);
                if (found.TryGetValue(path, out var existing))
                {
                    // Installed/Steam names remain friendly, but window evidence must not be
                    // lost when the same executable was discovered earlier from another source.
                    existing.HasVisibleWindow |= hasVisibleWindow;
                    if (string.IsNullOrWhiteSpace(existing.Name)) existing.Name = name;
                    if (!(existing.Source ?? "").Split('；').Contains(source, StringComparer.Ordinal))
                        existing.Source = string.IsNullOrWhiteSpace(existing.Source) ? source : existing.Source + "；" + source;
                }
                else found[path] = new AppCatalogEntry
                {
                    Name = name, ExecutablePath = path, Source = source, HasVisibleWindow = hasVisibleWindow
                };
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (string id in uninstall.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var item = uninstall.OpenSubKey(id);
                        string? name = item?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name) || item?.GetValue("SystemComponent") is int value && value == 1) continue;
                        Add(name, item?.GetValue("DisplayIcon") as string, "安装信息候选（请确认实际程序）");
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        ScanSteam(Add, cancellationToken);
        foreach (var identity in RunningWindowCandidates.Collect(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileNameWithoutExtension(identity.ExecutablePath);
            try
            {
                var info = FileVersionInfo.GetVersionInfo(identity.ExecutablePath);
                if (!string.IsNullOrWhiteSpace(info.ProductName)) name = info.ProductName;
                else if (!string.IsNullOrWhiteSpace(info.FileDescription)) name = info.FileDescription;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            Add(name, identity.ExecutablePath, "运行中的窗口程序");
        }
        // Resolving shortcuts is intentionally deferred; preserve manual add for portable apps.
        return found.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static void ScanSteam(Action<string, string?, string> add, CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var steam = registry.OpenSubKey(@"Software\Valve\Steam");
                string? path = (steam?.GetValue("SteamPath") ?? steam?.GetValue("InstallPath")) as string;
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) roots.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
        }
        foreach (var root in roots.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? vdf = ReadSmall(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
            if (vdf is null) continue;
            foreach (Match match in Regex.Matches(vdf, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (roots.Count >= 32) break;
                string library = match.Groups[1].Value.Replace("\\\\", "\\");
                try { if (Path.IsPathFullyQualified(library) && Directory.Exists(library)) roots.Add(Path.GetFullPath(library)); }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
            }
        }
        int budget = 25000;
        var scanTime = Stopwatch.StartNew();
        foreach (string library in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget <= 0 || scanTime.Elapsed > TimeSpan.FromSeconds(90)) break;
            string steamapps = Path.Combine(library, "steamapps");
            string[] manifests;
            try { manifests = Directory.GetFiles(steamapps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).Take(1000).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget <= 0 || scanTime.Elapsed > TimeSpan.FromSeconds(90)) break;
                string? text = ReadSmall(manifest); if (text is null) continue;
                string name = Value(text, "name"), directory = Value(text, "installdir");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(directory) || Path.IsPathRooted(directory)) continue;
                string common = Path.GetFullPath(Path.Combine(steamapps, "common"));
                string game;
                try { game = Path.GetFullPath(Path.Combine(common, directory)); }
                catch (ArgumentException) { continue; }
                if (!game.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(game)) continue;
                var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((game, 0)); int perGame = 0;
                while (pending.Count != 0 && budget > 0 && perGame < 1500 && scanTime.Elapsed < TimeSpan.FromSeconds(90))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    budget--; perGame++;
                    var current = pending.Dequeue();
                    try
                    {
                        foreach (string executable in Directory.EnumerateFiles(current.Path, "*.exe", SearchOption.TopDirectoryOnly))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (--budget <= 0 || ++perGame >= 1500) break;
                            string filename = Path.GetFileName(executable);
                            add(name + " · " + filename, executable, "Steam 游戏目录候选（请确认实际程序）");
                        }
                        if (current.Depth < 4)
                        {
                            foreach (string sub in Directory.EnumerateDirectories(current.Path).Take(100))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (--budget <= 0 || ++perGame >= 1500) break;
                                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                                if (new[] { "redist", "_CommonRedist", "directx", "cef", "webview", "installer" }
                                    .Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) continue;
                                pending.Enqueue((sub, current.Depth + 1));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }
    static string Value(string text, string key) => Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase).Groups[1].Value;
    static string? ReadSmall(string path)
    {
        try { if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null; return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
