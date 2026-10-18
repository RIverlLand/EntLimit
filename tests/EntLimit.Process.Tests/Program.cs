using EntLimit.Core;

var epoch = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
var app = new ManagedApp { Name = "百度网盘", ExecutablePath = @"D:\Apps\BaiduNetdisk\BaiduNetdisk.exe" };
ProcessIdentity P(int pid, string path) => new(pid, epoch.AddSeconds(pid), path);
var main = P(10, app.ExecutablePath);
var intermediate = P(11, @"D:\Apps\BaiduNetdisk\helpers\BaiduNetdiskHost.exe");
var child = P(12, @"D:\Apps\BaiduNetdisk\BaiduNetdiskUnite.exe");
int passed = 0;
void Check(string name, ManagedApp target, ProcessIdentity candidate, ProcessIdentity[] ancestors, bool expected)
{
    bool actual = ApplicationProcessMatcher.Matches(target, candidate, ancestors);
    if (actual != expected) throw new Exception($"{name}: expected {expected}, got {actual}");
    passed++;
}
Check("Direct main match without ancestry", app, main, [], true);
Check("Direct main match ignores case", app, P(20, @"d:\APPS\BAIDUNETDISK\baidunetdisk.EXE"), [], true);
Check("Direct path canonicalized", app, P(20, @"D:\Apps\BaiduNetdisk\helpers\..\BaiduNetdisk.exe"), [], true);
Check("Real child with main ancestor", app, child, [main], true);
Check("Child in nested directory", app, intermediate, [main], true);
Check("Grandchild validated chain", app, child, [intermediate, main], true);
Check("Child ancestry case insensitive", app, child, [P(10, @"d:\apps\baidunetdisk\BAIDUNETDISK.EXE")], true);
Check("Same folder without ancestry", app, child, [], false);
Check("Same filename prefix without ancestry", app, P(20, @"D:\Apps\BaiduNetdisk\BaiduNetdiskOther.exe"), [], false);
Check("Wrong ancestor same folder", app, child, [intermediate], false);
Check("Wrong ancestor same filename elsewhere", app, child, [P(10, @"C:\Apps\BaiduNetdisk\BaiduNetdisk.exe")], false);
Check("Sibling directory is not installation", app, P(20, @"D:\Apps\BaiduNetdisk2\child.exe"), [main], false);
Check("Child outside installation", app, P(20, @"C:\Tools\child.exe"), [main], false);
Check("Canonical path cannot escape installation", app, P(20, @"D:\Apps\BaiduNetdisk\..\Other\child.exe"), [main], false);
Check("Renamed main without ancestry", app, P(20, @"D:\Apps\BaiduNetdisk\renamed.exe"), [], false);
Check("Unrelated same-folder executable without ancestry", app, P(20, @"D:\Apps\BaiduNetdisk\Unrelated.exe"), [], false);
Check("Relative candidate rejected", app, P(20, "BaiduNetdisk.exe"), [main], false);
Check("Relative registration rejected", new ManagedApp { ExecutablePath = "BaiduNetdisk.exe" }, main, [], false);
Check("Disabled app rejected", new ManagedApp { ExecutablePath = app.ExecutablePath, Enabled = false }, main, [], false);
Check("Empty registration rejected", new ManagedApp(), child, [main], false);
Check("Malformed registration rejected", new ManagedApp { ExecutablePath = "D:\\bad\0\\app.exe" }, child, [main], false);
var rootApp = new ManagedApp { ExecutablePath = @"D:\main.exe" };
var rootMain = P(10, rootApp.ExecutablePath);
Check("Volume-root main direct match", rootApp, rootMain, [], true);
Check("Volume-root main cannot claim child", rootApp, P(20, @"D:\Other\child.exe"), [rootMain], false);
Check("Volume-root main cannot claim sibling", rootApp, P(20, @"D:\child.exe"), [rootMain], false);
var shareApp = new ManagedApp { ExecutablePath = @"\\server\share\main.exe" };
var shareMain = P(10, shareApp.ExecutablePath);
Check("UNC-root direct match", shareApp, shareMain, [], true);
Check("UNC-root cannot claim share contents", shareApp, P(20, @"\\server\share\Other\child.exe"), [shareMain], false);
var nestedShareApp = new ManagedApp { ExecutablePath = @"\\server\share\Application\main.exe" };
Check("Nested UNC installation child", nestedShareApp, P(20, @"\\server\share\Application\child.exe"), [P(10, nestedShareApp.ExecutablePath)], true);
// Matching classifies processes only. A previous grant for another identity is irrelevant;
// SessionEngine separately decides whether this new identity may use the current permit.
var earlierIdentity = main with { Pid = 999, StartedAtUtc = epoch.AddDays(-1) };
Check("Executable match is independent of saved grant identity", app, main, [earlierIdentity], true);
Console.WriteLine($"PASS {passed} application-process matching checks");
