# 配置说明

本文说明 EntLimit 在本机保存哪些配置、分别在哪个文件、默认值、校验边界，以及哪些内容**不在** `config.json`。

先回答一个常见问题：**不是所有可配置项都在 `config.json`**。`config.json` 只保存核心管控规则（应用、网站、时长、五个开关）；界面偏好与草稿、API 端点/密钥、题包、运行器插件、登录自启、浏览器桥接分别存放在其他文件或 Windows 注册表。

**一句话索引：** 管控规则 → `config.json`；题集/草稿/Python/Java → `desktop-preferences.json`；API → `api-settings.json` + `api-key.dpapi`；题包 → `data\packs\installed.json`；运行器插件 → `plugins\installed-plugins.json`；登录自启与浏览器桥接 → 注册表（布尔开关仍在 `config.json`）。

## 1. 数据目录在哪

所有“用户数据”都放在同一个数据根目录下：

| 场景 | 数据根目录 |
| --- | --- |
| 便携版 / 调试构建，exe 旁没有 `installed.flag` | `<exe 所在目录>\data` |
| 安装版，exe 旁有 `installed.flag` | `%LOCALAPPDATA%\EntLimit\data` |
| 设置了 `ENT_LIMIT_DATA_DIR` | 该环境变量的值（会经过 `Path.GetFullPath`，建议用绝对路径） |
| Monitor 带 `--data-dir <目录>` 启动 | 仅该 Monitor 进程（及其 watchdog）使用这个目录 |

`installed.flag` 只是安装布局标记，内容例如：

```text
EntLimit installed layout v1: user data is stored in LocalAppData/EntLimit/data.
```

它不是配置项，不要为了“换数据目录”去手工创建它；优先使用 `ENT_LIMIT_DATA_DIR`。

调试构建如果同时启动 Desktop 和 Monitor，两边必须拿到同一个数据目录和同一个实例名，否则会各写各的：

```powershell
$env:ENT_LIMIT_DATA_DIR = 'C:\path\to\data'
$env:ENT_LIMIT_INSTANCE = 'my-debug'
```

当前仓库的调试启动器就是一个例子：它把数据写到 `EntLimit\work\debug-data-run`，而不是真实的 `%LOCALAPPDATA%`。

## 2. 文件总览

| 文件 | 位置 | 谁写 | 用途 | 是否用户配置 |
| --- | --- | --- | --- | --- |
| `config.json` | 数据根目录 | Monitor | 应用、网站、时长、五个开关 | 是（核心规则） |
| `desktop-preferences.json` | 数据根目录 | Desktop | 题集、模式、Python/Java 路径、草稿、答案 | 是（界面偏好） |
| `api-settings.json` | 数据根目录 | Desktop | API 端点、模型 | 是 |
| `api-key.dpapi` | 数据根目录 | Desktop | DPAPI 加密的 API Key | 是（密钥） |
| `packs/installed.json` | 数据根目录\packs | Monitor | 已导入的题包 | 是（内容） |
| `plugins/installed-plugins.json` | **exe 旁** `plugins` | 安装/构建脚本 | 运行器插件索引 | 是（插件） |
| `plugins/<id>/manifest.json` | **exe 旁** `plugins` | 安装/构建脚本 | 单个插件清单与哈希 | 是（插件） |
| `session.json` | 数据根目录 | Monitor | 当前许可、计时、学习记录、活跃进程 | 否（运行时状态） |
| `catalog.json` | 数据根目录 | Monitor | 扫描到的应用缓存 | 否（缓存） |
| `emergency-stop.flag` | 数据根目录 | EmergencyStop / UI | 紧急停用闩锁 | 否（安全状态） |
| `monitor-owner.json` | 数据根目录 | Monitor | 守护用来确认 Monitor 身份 | 否（内部） |
| `desktop-material.json` | 数据根目录 | Desktop | 窗口材质诊断状态 | 否（内部） |
| `recovery.log` | 数据根目录 | LocalStore | 从备份恢复的记录 | 否（诊断） |
| `*.bak` | 数据根目录 | Monitor | 上一次有效版本的备份 | 否（备份） |
| `native-host.json` | 数据根目录 | 浏览器桥接脚本 | Chrome/Edge 的 native messaging 清单 | 否（系统注册用） |
| `installed.flag` | exe 旁 | 安装器 | 安装布局标记 | 否（安装标记） |

另外还有两类“配置”不在数据目录：

- **Windows 注册表**：登录自启、Chrome/Edge native messaging host 注册。
- **环境变量 / 命令行参数**：数据目录、实例隔离、调试/测试开关。

## 3. `config.json`：核心管控规则

Monitor 是唯一写入者。正常修改入口是 UI（规则页、受管控应用页）经 IPC 的 `save_config`；“规则与网站”页顶部现在有“管控总开关”和“保存规则与域名”按钮，保存后立即生效。手工编辑前应先退出 Monitor，否则内存中的旧配置可能在下次保存时覆盖你的修改。

### 3.1 字段

| 字段 | 类型 | 默认 | 边界/说明 |
| --- | --- | --- | --- |
| `SchemaVersion` | int | `1` | 必须为 `1`；不要手工改版本号 |
| `Apps` | array | `[]` | 受管控应用；保存时最多 1000 条 |
| `Domains` | array<string> | `[]` | 受管控网站域名；保存时最多 1000 条 |
| `UnlockMinutes` | int | `90` | 娱乐许可分钟数，`90–1440` |
| `WarningMinutes` | int | `10` | 到期前提醒分钟数，`0–(UnlockMinutes-1)`；`0` 表示只在到期时提醒 |
| `SoundEnabled` | bool | `true` | 到期/提醒声音 |
| `OverlayEnabled` | bool | `false` | 可选的不激活浮层；独占全屏下不保证可见 |
| `StartupEnabled` | bool | `false` | 登录 Windows 后自启 Monitor；要求 `MonitoringEnabled=true` |
| `GuardEnabled` | bool | `false` | 轻量守护，Monitor 被结束后尝试恢复；要求 `MonitoringEnabled=true` |
| `MonitoringEnabled` | bool | `true` | **总开关**；`false` 表示“调试中 · 管控已暂停”，不做窗口管控、不发放新许可 |

### 3.2 `Apps` 里的每一项

| 字段 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `Id` | string | 新 GUID | 稳定标识；非空、唯一、最多 128 字符。手工编辑时务必显式给出，否则每次加载可能生成新 ID |
| `Name` | string | `""` | 显示名；非空、最多 512 字符 |
| `ExecutablePath` | string | `""` | 绝对路径的 `.exe` 文件，必须存在；不能是 EntLimit 自身 |
| `Source` | string | `"Manual"` | 来源标记（Manual/Steam/安装目录等），只用于展示，不参与匹配 |
| `Enabled` | bool | `true` | 只有启用的应用才会被前台窗口匹配 |

`Domains` 的每一项应当是纯域名，例如 `bilibili.com`、`www.youtube.com`：

- 不要带 `https://`、路径、端口或查询参数；
- 建议小写、去重；
- 保存时用 `Uri.CheckHostName` 校验为 DNS 名称，且长度不超过 253；
- 浏览器扩展会为域名匹配其子域；IP、localhost 不会作为可管控域名保存。

### 3.3 校验与写入

保存时有两层校验：

1. **加载/落盘校验**（`LocalStore.ValidateConfig`）：`SchemaVersion=1`、`Apps`/`Domains` 不能为 `null`、时长范围、应用 ID/名称/路径非空、应用 ID 唯一、域名非空。
2. **IPC `save_config` 校验**（Monitor）：在上一层之上，再检查应用/域名数量上限、ID/名称长度、域名 DNS 格式、EXE 路径为绝对路径且文件存在且以 `.exe` 结尾、不是 `EntLimit.*`；并且 `StartupEnabled`/`GuardEnabled` 不能在没有 `MonitoringEnabled` 时打开。紧急停用闩锁存在时，也不能通过保存重新打开管控/自启/守护/声音/浮层，必须先走“解除紧急停用并恢复管控”。

保存行为：

- 原子写入：先写 `.config.json.<guid>.tmp`，再替换 `config.json`；
- 已有文件时生成/更新 `config.json.bak`；
- 如果 `config.json` 损坏，会尝试 `config.json.bak`；存在有效备份时恢复到内存并写入 `recovery.log`。两者都无效时抛出加载错误、保留原文件，Monitor 启动失败会写 `monitor-startup-error.log`；
- 手工编辑绕过 UI 的严格校验；下一次通过 UI 保存时会重新校验，可能被拒绝。

### 3.4 示例

```json
{
  "SchemaVersion": 1,
  "Apps": [
    {
      "Id": "steam-dota2",
      "Name": "Dota 2",
      "ExecutablePath": "D:\\Steam\\steamapps\\common\\dota 2 beta\\game\\bin\\win64\\dota2.exe",
      "Source": "Steam",
      "Enabled": true
    }
  ],
  "Domains": [
    "bilibili.com",
    "www.youtube.com"
  ],
  "UnlockMinutes": 90,
  "WarningMinutes": 10,
  "SoundEnabled": true,
  "OverlayEnabled": false,
  "StartupEnabled": false,
  "GuardEnabled": false,
  "MonitoringEnabled": true
}
```

## 4. `desktop-preferences.json`：界面偏好与草稿

Desktop 独占写入，Monitor 不读。它没有 `SchemaVersion`；JSON 解析失败时回退默认值并提示；`TopicId`/`Mode`/`SelectionStrategy` 会做规范化，`PythonPath`/`JavaPath` 只保存路径、不校验存在性，运行时才报错。它不会影响管控配置。

| 字段 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `PythonPath` | string/null | `null` | 选中的 `python.exe` 绝对路径；为空或路径已不存在时，Desktop 会用 Monitor 自动发现的解释器填入内存，随后保存。搜索顺序见 4.1 |
| `JavaPath` | string/null | `null` | JDK 目录，必须含 `bin\java.exe` 和 `bin\javac.exe` |
| `TopicId` | string | `"network"` | 当前题集；内置 `network`/`python`，也可为导入题包的主题 ID |
| `Mode` | string | `"standard"` | `standard`（正常）或 `light`（轻松复习） |
| `SelectionStrategy` | string | `"recommended"` | `recommended`（推荐）或 `random`（随机） |
| `LastExerciseId` | string/null | `null` | 上次题目；随机模式用来避免立即重复 |
| `CodeDrafts` | object | `{}` | `exerciseId -> 代码字符串` |
| `ReviewDraftPassCounts` | object | `{}` | `exerciseId -> 已通过次数`，用于复习草稿失效判断 |
| `Answers` | object | `{}` | `exerciseId -> { partId -> 答案字符串 }` |

注意：

- 这个文件包含你写的代码、草稿和答案，属于个人数据，不要随意外发；
- Desktop 在关闭、切题、保存草稿、判题通过等时机会写入；先退出 Desktop 再手工编辑；
- 没有 `.bak`；写入采用 `desktop-preferences.json.tmp` + `File.Move(..., true)`，如果写入失败会在界面提示“草稿未能写入本机”；
- 删除它可以重置界面偏好和草稿，但会丢失未提交的答案。

### 4.1 Python 解释器怎么找

运行时按以下顺序寻找 Python 3.8+：

1. `desktop-preferences.json` 里显式选择的 `PythonPath`（只要它可用，就优先使用；显式选择不会被静默替换）；
2. `PATH` 中的 `python.exe` / `python3.exe`（会跳过 Windows Store 的 `WindowsApps` 别名）；
3. 托管运行时 `%LOCALAPPDATA%\EntLimit\runtime\python\python.exe`；
4. 便携运行时 `<exe 所在目录>\runtime\python\python.exe`；
5. 注册表 `HKCU/HKLM\Software\Python\PythonCore\*\InstallPath`；
6. `%LOCALAPPDATA%\Programs\Python\*\python.exe`。

如果 `PythonPath` 为空或指向的文件已不存在，Desktop 会用自动发现的结果填入；如果显式选择的路径存在但不可执行/版本低于 3.8，运行会报“选定的 Python 无法使用”，不会悄悄换成另一个解释器。

**插件下载约定：** 后续的 Python 运行时插件应把完整的解释器解压到 `%LOCALAPPDATA%\EntLimit\runtime\python`（确保该目录下直接有 `python.exe`），应用会自动发现并把路径填入偏好；核心程序目前不静默联网下载解释器，下载/校验/许可证确认由运行时插件负责。若插件安装在便携版，也可以放到 `<exe>\runtime\python`。

运行器会优先把临时文件写到 `<数据根目录>\tmp`，只有那里不可写时才回退到 `%TEMP%`；这样在低完整性/受限环境中，即使用户的 `%TEMP%` 不可写，判题也能运行。

## 5. `api-settings.json` 与 `api-key.dpapi`

这两个文件由 Desktop 的“API 与扩展”页维护，Monitor 不读。

### `api-settings.json`

| 字段 | 类型 | 默认 | 边界 |
| --- | --- | --- | --- |
| `Endpoint` | string | `""` | 完整 URL；远程必须 HTTPS；HTTP 仅允许 localhost/回环；不能含用户名、查询参数或片段；最多 2048 字符 |
| `Model` | string | `""` | 非空、最多 200 字符、不能含控制字符 |

`Endpoint` 可以写基础地址（如 `https://api.example.com/v1`），也可以写完整的 `/chat/completions` 地址；保存时会规范化并去掉末尾 `/`。

### `api-key.dpapi`

- 二进制文件，不是 JSON；
- 内容是当前 Windows 用户 DPAPI 加密的 `{Endpoint, Key}` 信封；
- 文件 ACL 只给当前用户，创建时使用随机临时文件 + 原子替换；
- 密钥与端点绑定：端点改变后必须重新输入密钥，不能静默复用；
- 不要手工编辑、跨用户复制或跨机器复制；DPAPI 解密失败时会要求重新配置；
- HTTP 回环服务只允许无密钥；带密钥必须 HTTPS；
- Key 最多 16000 个可见 ASCII 字符。

`api-settings.json` 没有 `.bak`；修改端点/模型优先通过 UI。`ONLINE.md` 有更完整的在线安全说明。

## 6. `packs/installed.json`：题包

- 位置：`<数据根目录>\packs\installed.json`；
- 写入者：Monitor 的 `ContentPackStore`；
- 结构：`{ "SchemaVersion": 1, "Packs": [...] }`；
- 边界：最多 30 个包、合计最多 300 道题、序列化后最多 512 KiB；读取文件上限 2 MiB；
- 每个包必须至少有一道题；包 ID、题目 ID 不能重复；同一主题 ID 的名称必须一致；
- 备份：`installed.json.bak`；如果从损坏文件恢复，会额外保留 `.corrupt`；
- 内置题目来自 exe 旁的 `content/sample-exercises.json`（嵌入资源兜底），**不是**用户题包；不要通过改它来加题，使用题包导入。

题包格式、预览/逐题确认、ID 更新规则见 [题包格式与导入](CONTENT-PACKS.md)。正常入口是“API 与扩展”页的“预览题包 / 确认所选项并导入”，不建议手工编辑 `installed.json`。

## 7. `plugins/`：运行器插件

插件位于 **exe 旁**的 `plugins` 目录，不是用户数据目录。它随应用安装位置共享，不按用户隔离。

- `plugins/installed-plugins.json`：索引数组，每项 `{ Id, ManifestPath, ManifestSha256 }`；
  - 最多 32 项；
  - `ManifestPath` 必须是 `plugins` 下的相对路径，不能含 `..`、`.`、空段或盘符；
  - `ManifestSha256` 必须是 64 位十六进制；
  - 索引和清单都不能是链接/重解析点。
- `plugins/<id>/manifest.json`：
  - `ProtocolVersion` 必须为 `1`；
  - `Id`、`Name`、`LanguageId`、`Executable`、`Files`；
  - `Executable` 必须是 `.exe`，不能是 `cmd/powershell/wscript/mshta` 等解释器；
  - `Files` 必须把可执行文件及其所有运行时依赖按 SHA256 固定下来；未固定的文件会导致校验失败；
  - 单个清单最多 1024 个文件，单文件最多 1 MiB。
- 安装可执行插件需要显式授权；Java 运行器是可选插件，Python 运行器内置于 `EntLimit.Runner`。
- 不要手工改 `installed-plugins.json` 或清单哈希；重新安装/重新打包。协议细节见 [插件协议](PLUGINS.md)。

## 8. Windows 注册表与浏览器桥接

这些是**系统级配置**，不在 `config.json` 里。

### 登录自启

- 位置：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名 `EntLimit`；
- 命令形如：`"<Monitor.exe 的绝对路径>" --data-dir "<数据根目录>"`；
- 由 `config.json` 的 `StartupEnabled` 控制；关闭时只删除与本安装完全匹配的值，不碰其他安装；
- 如果 Run 值被另一个安装位置占用，启用会拒绝，而不是覆盖；
- 使用 `ENT_LIMIT_INSTANCE` 的隔离实例**不能**注册登录自启（会明确报错）。

### 浏览器 native messaging

`scripts/register-browser-host.ps1` 会在当前用户下写：

- `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.entlimit.bridge`
- `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.entlimit.bridge`

指向的清单是数据目录下的 `native-host.json`：

```json
{
  "name": "com.entlimit.bridge",
  "description": "EntLimit local learning companion",
  "path": "C:\\...\\EntLimit.NativeHost.exe",
  "type": "stdio",
  "allowed_origins": ["chrome-extension://<32 位 a-p 扩展 ID>/"]
}
```

扩展 ID 必须是 32 位 a–p 字母；`allowed_origins` 限制哪些扩展可以连接本地桥接。扩展自身在 `chrome.storage.local` 里缓存 `domains`、`granted`、`snapshot`、`lastSync`、`error`、`finishingDeadline`，这些是运行时缓存，不是用户配置；域名列表来自 `config.json`，浏览器权限由扩展弹窗授权。

## 9. 运行时/内部文件（不是配置）

| 文件 | 说明 |
| --- | --- |
| `session.json` | Monitor 的许可状态：`SchemaVersion=1`、`Phase`（0=Locked、1=Ready、2=Active、3=Draining）、`GrantedMinutes`、时间戳、`ActiveProcesses`、`DrainingProcesses`、`History`、提醒标记。由 Monitor 持续写入；不要手工编辑。有 `session.json.bak` |
| `catalog.json` | 扫描缓存（`AppCatalogEntry` 数组）。删掉后重新扫描即可；不影响已配置的 `Apps` |
| `emergency-stop.flag` | 紧急停用闩锁。存在时快照会关闭管控/自启/守护/声音/浮层；通过 UI“解除紧急停用并恢复管控”清除。不要手工删，除非在做恢复 |
| `monitor-owner.json` | Monitor 的 PID/启动时间/路径/实例名，守护用来确认身份；不要编辑 |
| `desktop-material.json` | 窗口材质（Acrylic/实色）诊断状态；不是用户配置 |
| `recovery.log` | 从 `.bak` 恢复配置/会话/扫描缓存时追加的诊断记录 |
| `*.bak`、`*.corrupt` | 上一次有效版本/损坏证据；排障时先保留，不要急着删 |
| `native-host.json` | 浏览器桥接清单（见上一节），不是应用配置 |
| `emergency-verify.json` | `EmergencyStop --test` 的诊断输出，仅测试用 |
| `installed.flag` | 安装布局标记，决定数据目录；不是用户配置 |

`session.json` 的 `Phase` 在 JSON 里是数字而不是字符串；手工改成 `"Active"` 会导致校验失败。`Active` 状态还要求 `StartedAtUtc`/`DeadlineUtc` 有效。正常修改许可请通过完成练习/结束本次娱乐，不要手改状态。

## 10. 环境变量与命令行

### 环境变量

| 变量 | 作用 | 边界/注意 |
| --- | --- | --- |
| `ENT_LIMIT_DATA_DIR` | 覆盖数据根目录 | 建议绝对路径；Desktop 和 Monitor 必须一致 |
| `ENT_LIMIT_INSTANCE` | 隔离实例：改变命名管道、Mutex、Desktop 单例名 | 1–64 个 ASCII 字母/数字/`-`；两边必须一致；隔离实例不能注册登录自启 |
| `ENT_LIMIT_TEST_PYTHON` | 测试用 Python 路径 | 仅测试脚本使用，不是应用配置 |
| `ENT_LIMIT_TEST_JDK` | 测试用 JDK 目录 | 仅测试 |
| `ENT_LIMIT_TEST_PIDS` | EmergencyStop `--test` 用 PID 列表 | 仅测试 |
| `DOTNET_ROOT` / `DOTNET_ROOT_X64` | 让 apphost 找到本地 .NET 10 runtime | 仅用于当前仓库的框架依赖调试构建；正式安装包自带运行库 |

### 命令行

| 程序 | 参数 | 说明 |
| --- | --- | --- |
| `EntLimit.Monitor.exe` | `--data-dir <目录>` | 设置本次 Monitor（及其 watchdog）的数据目录；Desktop 仍需通过环境变量拿到同一目录 |
| `EntLimit.Monitor.exe` | `--watchdog` | 内部守护模式，不应手工启动 |
| `EntLimit.Desktop.exe` | `--gate` | 以“被管控窗口触发的学习入口”形式启动（最大化置顶） |
| `EntLimit.Desktop.exe` | `--preview <png> [network\|code\|online\|extensions] [宽] [高]` | 离线渲染界面到 PNG；不连接 Monitor、不写许可/配置 |
| `EntLimit.EmergencyStop.exe` | `--test` | 仅测试；配合 `ENT_LIMIT_INSTANCE` 等变量 |

## 11. 常见修改场景

| 想改什么 | 首选位置 | 备注 |
| --- | --- | --- |
| 受管控应用/网站、90 分钟时长、提醒、五个开关 | UI“规则与网站”“受管控应用” → `config.json` | 保存即生效；手工改要退出 Monitor |
| Python 解释器/Java JDK | UI“API 与扩展”或代码题工作台 → `desktop-preferences.json` | 不要填不存在的路径 |
| 草稿、答案、题集/模式/随机策略 | UI → `desktop-preferences.json` | 属于个人数据 |
| API 端点/模型/Key | UI“API 与扩展” → `api-settings.json` + `api-key.dpapi` | 不要手工编辑 DPAPI 文件 |
| 题包 | UI 预览并导入 → `packs/installed.json` | 先预览、逐题确认 |
| Java 运行器插件 | `plugins/` + 清单/索引 | 需要显式授权和哈希固定 |
| 登录自启 | UI 规则页 `StartupEnabled` | 实际写入 HKCU Run；隔离实例不可用 |
| 浏览器桥接 | `scripts/register-browser-host.ps1` | 写 HKCU native messaging 键 |
| 数据目录 | `ENT_LIMIT_DATA_DIR` 或安装布局 | 不要手工伪造 `installed.flag` |
| 扫描缓存 | 删除 `catalog.json` 后重新扫描 | 不影响管控列表 |
| 紧急停用/恢复 | UI 或 `EntLimit.EmergencyStop.exe` | 不要手工删闩锁 |

## 12. 手工编辑规则与安全

1. **先退出应用和 Monitor**：任务管理器确认没有 `EntLimit.Monitor.exe` / `EntLimit.Desktop.exe`；否则会在下次保存时覆盖你的修改。
2. **先备份数据目录**；应用自己的 `.bak` 只覆盖部分文件。
3. **UTF-8、无 BOM、无注释、无尾逗号**；保持 `SchemaVersion` 不变。
4. 改完用 `Get-Content ... | ConvertFrom-Json` 做一次语法检查。
5. 重启后看 `<数据根目录>\monitor-errors.log` 或 `monitor-startup-error.log`；配置/会话损坏时看 `recovery.log`。
6. 不要手工编辑：`session.json`、`api-key.dpapi`、`monitor-owner.json`、`emergency-stop.flag`、`plugins/*` 的哈希；这些要么由程序维护，要么需要专用工具/授权。
7. 手工改 `config.json` 绕过 UI 的严格校验；即使能加载，下一次 UI 保存会重新校验，可能被拒绝。
8. 数据目录可能包含学习记录、代码草稿、答案、应用路径和 API 配置；不要把它发给别人。`api-key.dpapi` 受 DPAPI 保护，但同一 Windows 用户下的恶意程序仍可能解密；插件是可执行代码，只安装可信来源。

## 13. 构建期配置（不属于运行配置）

以下文件只影响构建/打包，应用运行时不读取：

| 文件/位置 | 用途 |
| --- | --- |
| `work/build-tools.local.json` | 可选，gitignored；给 `scripts/package.ps1` 提供 `DotnetPath`、`IsccPath`、`NugetPackages` |
| `NuGet.Config` | 构建/测试的包源配置（当前清空，不依赖外部包） |
| `Publish.NuGet.Config` | 自包含发布时使用 nuget.org 的包源配置 |
| `Directory.Build.props` | 版本号、`LangVersion`、`Deterministic` 等编译属性 |
| `work/tools/dotnet`、`work/tools/inno` | `package.ps1` 查找本地工具时的回退路径 |

这些不是用户配置；改它们只影响源码构建或安装包，不影响已经运行的应用。

## 14. 本仓库调试实例

当前仓库的调试启动器 `EntLimit\work\run-debug.cmd` 使用隔离的环境变量（具体值以脚本为准）：

```text
ENT_LIMIT_INSTANCE=<隔离实例名>
ENT_LIMIT_DATA_DIR=EntLimit\work\<调试数据目录>
DOTNET_ROOT=EntLimit\work\dotnet-sdk
```

它启动的是 `artifacts\app-debug*` 里的 Debug 构建，不碰 `%LOCALAPPDATA%\EntLimit\data`。如果看到“调试中 · 管控已暂停”，检查该调试数据目录下的 `config.json`。

其中 `MonitoringEnabled` 是否为 `false`。这是核心管控开关，不是计时器状态；在“规则与网站”页勾选“启用应用与网站管控”并保存即可恢复。调试实例使用 `ENT_LIMIT_INSTANCE`，因此不能注册登录自启；需要测试自启时，用非隔离实例从普通安装布局启动。
