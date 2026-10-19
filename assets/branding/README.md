# EntLimit Logo

`entlimit-logo-v1.png` 是应用图标候选素材：书页与播放键融合，表达知识积累与安心娱乐，使用与当前界面相配的蓝绿渐变。

- 1254 × 1254 PNG，RGBA，外部背景透明。
- 使用 imagegen 生成，版本 v1。
- 本次仅保存素材，尚未接入窗口、任务栏、托盘或安装器。
- 下次编译接入时，再从确认后的 PNG 导出包含小尺寸图层的 Windows ICO，并检查 16/24/32 像素下的辨识度。

## 后续接入位置（尚未实现）

当前仅保存 PNG 原图；下面是下一次集成时的工作，不代表现有 EXE 或安装包已经使用此图标。

1. 从 PNG 导出一份 Windows 多尺寸 ICO，至少包含 16、24、32、48、64、128、256 像素，并单独检查小尺寸的轮廓与透明边缘。小图模糊时应简化图形，避免只依赖系统缩放。
2. 在 EntLimit.Desktop、EntLimit.Monitor 等 EXE 项目的 csproj 中配置 ApplicationIcon，编译到程序资源中。WPF MainWindow 再设置 Icon，确保窗口标题栏、Alt-Tab 与运行中的任务栏显示一致。
3. EntLimit.Monitor/Program.cs 的 NotifyIcon 目前使用 SystemIcons.Information，需改成新的应用图标或独立小尺寸托盘图标；保留图标对象，并在托盘退出时释放。
4. installer/EntLimit.iss 设置 SetupIconFile，让安装器 EXE 使用 ICO。开始菜单、桌面快捷方式和当前 UninstallDisplayIcon 都指向应用 EXE，嵌入图标后可沿用。安装向导大图属于可选素材，与 EXE 图标是两回事。

仅替换 PNG 不会改变已经编译的程序。接入完成后需要重新编译各 EXE 并重打安装包；固定到任务栏的旧快捷方式若仍显示旧图标，可重新固定进行检查，不需要修改显示分辨率或清空系统缓存。
