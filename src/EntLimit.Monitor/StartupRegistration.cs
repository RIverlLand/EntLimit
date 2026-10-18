using Microsoft.Win32;

namespace EntLimit.Monitor;

internal interface IStartupValue
{
    string? Read();
    void Write(string? command);
}

internal sealed class StartupRegistration
{
    readonly IStartupValue value;
    readonly string expectedCommand;
    readonly bool isolated = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ENT_LIMIT_INSTANCE"));
    public StartupRegistration(string executable, string dataRoot, IStartupValue? value = null)
    { expectedCommand = FormatCommand(executable, dataRoot); this.value = value ?? new RegistryStartupValue(); }

    public static string FormatCommand(string executable, string dataRoot)
        => Quote(Path.GetFullPath(executable)) + " --data-dir " + Quote(Path.GetFullPath(dataRoot));
    static string Quote(string argument)
    {
        if (argument.Contains('"') || argument.Contains('\r') || argument.Contains('\n'))
            throw new ArgumentException("Invalid startup path.");
        // Windows command-line quoting must double backslashes before a closing quote.
        int trailing = argument.Length - argument.TrimEnd('\\').Length;
        return "\"" + argument + new string('\\', trailing) + "\"";
    }
    public string? Capture() => isolated ? null : value.Read();
    public void Restore(string? previous) { if (!isolated) value.Write(previous); }
    public void Apply(bool enabled)
    {
        if (isolated)
        {
            if (enabled) throw new InvalidOperationException("隔离调试实例不能注册登录自启。");
            return; // Tests cannot read or mutate the real Run key, including debug stop.
        }
        string? current = value.Read();
        if (enabled)
        {
            if (current is not null && current != expectedCommand)
                throw new InvalidOperationException("登录自启项已由另一个安装位置使用。");
            value.Write(expectedCommand);
        }
        else if (current == expectedCommand) value.Write(null);
    }
    sealed class RegistryStartupValue : IStartupValue
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "EntLimit";
        public string? Read()
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            object? raw = key?.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null) return null;
            return raw as string ?? throw new InvalidOperationException("登录自启项格式不兼容。");
        }
        public void Write(string? command)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
            if (command is null) key.DeleteValue(Name, throwOnMissingValue: false);
            else key.SetValue(Name, command, RegistryValueKind.String);
        }
    }
}
