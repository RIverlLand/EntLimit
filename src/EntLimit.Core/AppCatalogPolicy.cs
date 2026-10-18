namespace EntLimit.Core;

/// <summary>Small exclusion policy for discovered candidates, not an application allowlist.</summary>
public static class AppCatalogPolicy
{
    static readonly HashSet<string> Infrastructure = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "cmd", "powershell", "pwsh", "windowsterminal", "wt", "conhost", "openconsole",
        "svchost", "services", "lsass", "dwm", "sihost", "taskhostw", "runtimebroker",
        "applicationframehost", "shellexperiencehost", "startmenuexperiencehost", "searchhost",
        "ctfmon", "textinputhost", "tabtip", "inputpersonalization", "weaselserver", "weaseldeployer",
        "sogoupy", "sogoucloud", "sogouime", "sogouimemanager", "qqpinyin", "qqpinyinbroker",
        "baidupinyin", "baiduime", "baiduplatform", "msedgewebview2", "crashpad_handler",
        "crashreporter", "dxsetup", "vcredist_x86", "vcredist_x64", "vc_redist.x86", "vc_redist.x64",
        "kernelupdate", "yundetectservice"
    };
    static readonly HashSet<string> Tools = new(StringComparer.OrdinalIgnoreCase)
        { "codex", "codex-code-mode-host", "codex-app-server", "codex-responses-api-proxy", "openai codex", "claude", "claude code", "claude-code", "entlimit", "entlimit.desktop", "entlimit.monitor", "entlimit.nativehost" };
    static readonly HashSet<string> ExcludedComponents = new(StringComparer.OrdinalIgnoreCase)
        { "System32", "SysWOW64", "WinSxS", "IME", "InputMethod", "InputMethods", "SogouInput", "QQPinyin", "BaiduPinyin", "Redist", "_CommonRedist" };

    public static bool IsProtectedTool(string? path, string? name = null)
    {
        string file = FileStem(path);
        if (Tools.Contains(file) || Tools.Contains(name?.Trim() ?? "")) return true;
        // Package and installation directory names are matched as components, never substrings.
        return Components(path).Any(p => p.Equals("EntLimit", StringComparison.OrdinalIgnoreCase)
            || p.Equals("Codex", StringComparison.OrdinalIgnoreCase)
            || p.Equals("Claude", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("Anthropic.Claude_", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSelectableCandidate(string? name, string? path, string? source)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(path)) return false;
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
        if (IsProtectedTool(path, name)) return false;
        // Manual selection can recover uncommon applications hidden by the discovery heuristic.
        if (string.Equals(source, "Manual", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "手动添加", StringComparison.OrdinalIgnoreCase)) return true;
        string file = FileStem(path);
        if (Infrastructure.Contains(file) || IsHelper(file)) return false;
        string[] components = Components(path);
        if (components.Any(ExcludedComponents.Contains)) return false;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && IsWithin(path, windows)) return false;
        // Old caches may have been produced on a different Windows installation drive.
        if (components.Length > 1 && components[1].Equals("Windows", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    static bool IsHelper(string file) => file.Equals("uninstall", StringComparison.OrdinalIgnoreCase)
        || file.Equals("setup", StringComparison.OrdinalIgnoreCase)
        || file.StartsWith("unins", StringComparison.OrdinalIgnoreCase) && file[5..].All(char.IsDigit)
        || new[] { "_helper", "-helper", ".helper", "_service", "-service", "_crashhandler", "_crashreporter" }
            .Any(suffix => file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    static string FileStem(string? path) => Path.GetFileNameWithoutExtension((path ?? "").Replace('\\', Path.DirectorySeparatorChar));
    static string[] Components(string? path) => (path ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
    static bool IsWithin(string path, string directory) => path.StartsWith(directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
