# Windows release 0.3.0

The installer requests Windows administrator consent and installs immutable application files under Program Files/EntLimit. It does not register startup, install browser extensions, add managed targets, enable guard, install a service, or download language runtimes automatically. The ordinary app and monitor run as the original user. Installed user data lives under `%LOCALAPPDATA%/EntLimit/data`; portable layouts continue using their own `data` unless explicitly overridden. Upgrade preserves per-user data; uninstall preserves it as well.

## Administrator window assistance

The settings page can explicitly start a narrow UAC administrator helper. It exists only to handle explicitly configured high-integrity foreground windows. It resolves the current window itself and asks the verified installation-local monitor to validate target identity, management and permit. It can only minimize the checked foreground window. It cannot terminate games, run commands, install services, execute practice code, or grant a permit. It exits on the emergency latch, paused monitoring, three connection failures, or eight hours. It requires the same Windows user and an installed Program Files layout; using another account's UAC credentials is refused. No UAC bypass is implemented.

This remains cooperative self-use control, not a tamper-proof parental security boundary. Administrative rights do not make same-user process termination impossible. Running the app from an elevated shell is discouraged; a runtime check refuses Python/Java practice execution when elevated. Practice runners are for trusted personal code, not hostile-code sandboxes. Parent passwords, separate child accounts and system services remain outside this release.

## Independent recovery

`EntLimit.EmergencyStop.exe` is a self-contained independent executable and has a Start-menu shortcut. It first persists `emergency-stop.flag` without IPC, then stops only exact installation-local EntLimit desktop/monitor/broker processes in the current Windows session. It never stops games or arbitrary caller-supplied PIDs. The normal route requires no administrator prompt. A cooperating elevated helper observes the latch and exits. If a high-privilege EntLimit process remains inaccessible, the installed tool offers an explicit UAC retry; cancelling never removes the latch. Windows access-control checks cannot be bypassed. Reopen the app and deliberately resume to clear the latch; startup and guard remain disabled until chosen again.

## Packaging and verification

Run `scripts/package.ps1 -DotnetPath <dotnet.exe> -IsccPath <ISCC.exe>`. It creates a fresh staging directory and self-contained x64 executables, generates bundled plugin hashes from published files, excludes personal configuration/drafts/history/keys/logs/debug symbols, inventories relative file hashes, then compiles the Inno installer. It never copies the live `artifacts/app/data`. The separately distributed recovery executable must remain alongside the app when used. Review the SHA256 sidecar before sharing.

This build is unsigned because no developer signing certificate was provided. Windows may show an unknown-publisher warning; no trusted publisher or SmartScreen reputation is claimed. The UAC requirement and payload are verified from the compiled installer/source manifest; an actual interactive UAC consent/install/uninstall on a clean machine still requires a person. Do not disable Windows protections to install.

References: [Inno administrator installation](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm), [Microsoft DPAPI](https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata).

## 一键打包

在项目根目录双击 `BuildInstaller.cmd`，或执行：

```powershell
./scripts/package.ps1
```

两者生成 `artifacts/installer/EntLimit-Setup-0.3.1-win-x64.exe` 和 `.sha256`。需要 .NET 10 SDK 与 Inno Setup 6+；脚本会自动寻找已安装工具。也可以传 `-DotnetPath '<dotnet.exe>' -IsccPath '<ISCC.exe>'`，或将这两个路径及可选 NugetPackages 存入不提交的 `work/build-tools.local.json`。打包不需要管理员权限；生成的安装器在实际安装时请求 UAC。不读取或打包运行中的个人 data、API 设置与密钥，不改动当前应用数据；首次恢复运行时包可能需要网络。

### 构建缓存占用

成功打包后默认自动删除本轮新建的 `work/package-<GUID>` 与 `work/emergency-<GUID>` 临时发布目录，保留安装 EXE、校验文件、SDK 和 NuGet 缓存。需要检查原始发布内容时运行 `./scripts/package.ps1 -KeepStaging`；失败的构建保留现场便于诊断。清理仅允许脚本生成的 GUID 目录，拒绝其他路径及目录链接，不触碰应用 data。
