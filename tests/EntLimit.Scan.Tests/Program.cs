using EntLimit.Core;

var cases = new (string Name, string Path, string? Source, bool Expected)[]
{
    ("Windows system process", @"C:\Windows\System32\ctfmon.exe", null, false),
    ("Windows helper on another drive", @"D:\Windows\System32\taskhostw.exe", "运行中的程序", false),
    ("Third-party IME", @"C:\Program Files\SogouInput\15\SogouPY.exe", "安装信息候选", false),
    ("Rime IME server", @"C:\Program Files\Rime\weasel-0.17.4\WeaselServer.exe", "运行中的程序", false),
    ("Rime IME deployer", @"C:\Program Files\Rime\weasel-0.17.4\WeaselDeployer.exe", "安装信息候选", false),
    ("RiME game remains selectable", @"D:\SteamLibrary\steamapps\common\RiME\RiME.exe", "Steam", true),
    ("Alternate IME executable", @"C:\Program Files\InputMethod\agent.exe", "运行中的程序", false),
    ("Codex", @"C:\Apps\Codex\app.exe", "安装信息候选", false),
    ("Codex Store package", @"C:\Program Files\WindowsApps\OpenAI.Codex_1.2_x64__xyz\app\codex.exe", "运行中的程序", false),
    ("Claude CLI", @"C:\Tools\claude.exe", null, false),
    ("Codex code host outside named installation", @"C:\Users\User\.codex\packages\runtime\codex-code-mode-host.exe", "运行中的程序", false),
    ("Codex app daemon", @"C:\Tools\codex-app-server.exe", "运行中的程序", false),
    ("Monitor", @"D:\Apps\EntLimit.Monitor.exe", "Manual", false),
    ("Self installation with generic executable", @"D:\Apps\EntLimit\app.exe", "Manual", false),
    ("Terminal", @"C:\Tools\pwsh.exe", "运行中的程序", false),
    ("Crash helper", @"D:\Games\One\crashpad_handler.exe", "Steam", false),
    ("Uninstaller", @"D:\Apps\One\unins000.exe", "安装信息候选", false),
    ("Helper suffix", @"D:\Games\One\renderer_helper.exe", "Steam", false),
    ("Browser subprocess", @"C:\Apps\Edge\msedgewebview2.exe", "运行中的程序", false),
    ("Chrome stays selectable", @"C:\Program Files\Google\Chrome\Application\chrome.exe", null, true),
    ("Edge stays selectable", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "安装信息候选", true),
    ("BaiduNetdisk stays selectable", @"D:\Apps\BaiduNetdisk\BaiduNetdisk.exe", "运行中的窗口程序", true),
    ("BaiduNetdisk app host", @"D:\Apps\BaiduNetdisk\app.exe", "运行中的窗口程序", true),
    ("BaiduNetdisk real window host", @"D:\Apps\BaiduNetdisk\BaiduNetdiskHost.exe", "运行中的窗口程序", true),
    ("BaiduNetdisk updater", @"D:\Apps\BaiduNetdisk\kernelUpdate.exe", "运行中的程序", false),
    ("BaiduNetdisk background service", @"D:\Apps\BaiduNetdisk\YunDetectService.exe", "运行中的程序", false),
    ("Not a substring filter", @"D:\Games\WindowsAdventure\ServiceQuest.exe", "Steam", true),
    ("Codex-like name", @"D:\Games\CodexQuest\codexquest.exe", "Steam", true),
    ("Game with crash in its name", @"D:\Games\CrashBandicoot\CrashBandicoot.exe", "Steam", true),
    ("Canonical path leaves tooling folder", @"D:\Apps\Codex\..\game.exe", "Steam", true),
    ("Canonical path enters tooling folder", @"D:\Apps\Games\..\Codex\app.exe", "Steam", false),
    ("Manually recover helper", @"D:\Apps\One\renderer_helper.exe", "Manual", true),
    ("Relative candidate rejected", @"game.exe", null, false),
    ("Non-executable rejected", @"D:\Games\game.dll", null, false),
};
foreach (var c in cases)
{
    bool actual = AppCatalogPolicy.IsSelectableCandidate(c.Name, c.Path, c.Source);
    if (actual != c.Expected) throw new Exception($"{c.Name}: expected {c.Expected}, got {actual}");
}
if (AppCatalogPolicy.IsSelectableCandidate(null, null, null)) throw new Exception("Null cache entry allowed");
if (AppCatalogPolicy.IsSelectableCandidate("Codex", @"D:\Vendor\renamed.exe", "Manual")) throw new Exception("Product classification ignored");
Console.WriteLine($"PASS {cases.Length + 2} catalog policy checks");
