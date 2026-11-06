# 桌面便笺（DesktopNotes）

这是一个参考「小孩桌面便签」体验的开源桌面便笺项目，使用 C# + Avalonia UI 11 实现，支持**嵌入桌面（WorkerW）**、**时间轴**、**待办事项**三种便笺类型，可**开机自启**，并能**导入导出 Windows 自带便笺**的内容，以便通过 Windows 自带便笺实现**云同步**。数据保存在本机 JSON 文件里。

## 致谢

感谢「小孩桌面便签」带来的灵感。本项目是独立实现，并非「小孩桌面便签」的官方版本或附属项目；如果你需要更全面、成熟的功能，推荐使用「小孩桌面便签」。

## 许可证

本项目采用 [MIT 许可证](./LICENSE)。

## 功能

| 功能 | 说明 |
| --- | --- |
| 三种便笺 | 📄 普通便签（多行文本）、✅ 待办事项（勾选/新增/删除，显示完成数）、🕐 时间轴（带时间戳的记录流） |
| 嵌入桌面 | 便笺可嵌入桌面壁纸层：位于壁纸之上、桌面图标之下；支持单张切换与「全部嵌入 / 全部还原」 |
| 开机自启 | 勾选「开机自启」即写入当前用户 Run 注册表项；登录后静默启动到托盘，只恢复桌面便笺、不弹主面板 |
| 导入 Windows 便笺 | 把系统自带便笺（Microsoft Sticky Notes）的内容转成本应用的便笺；首次运行自动导入，之后可随时手动同步 |
| 导出到 Windows 便笺 | 把本应用的便笺写回系统自带便笺（自动备份数据库、按来源去重、重复导出只更新不新增） |
| 提醒 | 标题栏 ⏰ 可设置自定义未来日期与时间，也可清除提醒；到点自动打开窗口、置顶并闪烁任务栏 |
| 窗口操作 | 无边框便笺、拖动标题栏移动、右下角拖动缩放、📌 置顶、🎨 换色（6 种便签色）、— 隐藏 |
| 托盘 | 关闭主面板 = 收起到托盘；托盘菜单：显示主面板 / 新建便签 / 导入 Windows 便笺 / 开机自启 / 退出 |
| 持久化 | 位置、尺寸、颜色、内容、待办、时间轴、提醒全部即时保存到 `%AppData%\DesktopNotes\notes.json` |

## 环境要求

- Windows 10/11（嵌入桌面依赖 Explorer 的 WorkerW 层）
- .NET SDK 8.0（`dotnet --version` 应 ≥ 8.0）
- VS Code（可选扩展：C# Dev Kit / C#）

## 构建与运行

```powershell
# 构建
dotnet build DesktopNotes.csproj -c Debug

# 运行
.\bin\Debug\net8.0-windows\DesktopNotes.exe
```

VS Code：直接按 <kbd>F5</kbd>（`.vscode/launch.json` 已配置好，会先自动构建）；
<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>B</kbd> 执行构建任务，或运行「run test」任务执行自动化测试。

## 体积与发布

- 源码本身只有 ~130 KB；体积都来自构建产物，`bin/`、`obj/`、`.sbx/` 已在 `.gitignore` 里排除。
- **关键**：不指定运行标识（RID）时，NuGet 会把 SkiaSharp / Avalonia / SQLite 等 **25 个平台**的原生库全部复制进
  `bin`，单个配置就有 141 MB（实测 Debug 152 MB、Release 216 MB，整个目录 391 MB）。`csproj` 里已固定成本机平台：

  ```xml
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>false</SelfContained>
  <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
  ```

  之后 Debug 28 MB、Release 27 MB，且输出路径保持不变（`bin\Debug\net8.0-windows\`）。
  换到 ARM64 机器时把 `win-x64` 改成 `win-arm64`。

- 发布到其他机器（目标机需装 .NET 8 桌面运行时，`publish` 约 28 MB）：

  ```powershell
  dotnet publish DesktopNotes.csproj -c Release -r win-x64 --self-contained false -o publish
  ```

  需要免安装自包含版本：追加 `--self-contained true -p:PublishSingleFile=true`（约 90–150 MB）。

## 交互细节

- **拖动**：按住标题栏（含标题文字）拖动窗口，位移按屏幕光标计算，1:1 跟随、不抖动；移动超过 4px 才判定为拖动，单击标题仍然是编辑标题。
- **缩放**：右下角 ◢ 手柄拖动缩放，同样按屏幕光标位移计算。
- **聚焦**：点击便笺任意位置都会把窗口带到前台；点击正文即可直接输入。
- **嵌入便笺的点击限制**：嵌到桌面图标层下面的窗口收不到鼠标（图标层 `SysListView32` 盖在桌面整幅区域上，这是系统行为）。因此：
  - 在主面板点「打开」→ 该便笺会**临时浮出**到普通窗口层并激活，可直接编辑；
  - 点到别处（便笺失去焦点）→ **自动回到桌面层**，恢复"壁纸之上、图标之下"的观感；
  - 提醒到点时同样会自动浮出，避免提醒看不见。
## 开机自启

- 开关位置：主面板底部「开机自启」复选框，或托盘菜单「开机自启」。
- 实现：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `DesktopNotes` 值，内容形如
  `"<程序路径>" --autostart`；关闭时删除该值。**不需要管理员权限**，也不会创建计划任务。
- 启动参数 `--autostart` 时：不显示主面板（`MainWindow` 不注册为 lifetime 主窗口，避免被自动显示），
  只恢复桌面便笺并常驻托盘；从托盘「显示主面板」即可打开。
- 若程序是以 `dotnet DesktopNotes.dll` 方式启动，注册的是 `dotnet "<dll>" --autostart`。

## 与 Windows 自带便笺互导

Windows 便笺把内容存在 SQLite 数据库：

```
%LocalAppData%\Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\LocalState\plum.sqlite
```

导入流程（`Services/StickyNotesImporter.cs`）：

1. 把 `plum.sqlite` 连同 `-wal`/`-shm` 复制到临时目录（便笺程序运行时数据库被占用，直接读会失败）；
2. 用 `Microsoft.Data.Sqlite` 打开副本，查询 `Note` 表中未删除且有文字的行；
3. 去掉每行的 `\id=<guid>` 段落标记，还原纯文本；
4. 首行作便笺标题、其余作正文（单行内容整段放进正文，避免丢字）；
5. 主题色（Yellow/Green/Blue/Pink/Purple）映射到便签颜色，`WindowPosition` 里的坐标直接沿用。

去重与同步：

- 每条导入的便笺带 `SourceId = stickynotes:<guid>`；
- 再次导入时按 `SourceId` 匹配：内容有变就**更新**，没变就跳过，**不会重复创建**；
- 首次运行（`notes.json` 不存在）会自动导入一次，之后可通过主面板按钮或托盘菜单手动同步；
- 在本应用里删除某张导入的便笺后，重启不会自动把它加回来（自动导入只在首次运行发生）。


### 导出到系统便笺

主面板「导出便笺」或托盘菜单「导出到 Windows 便笺」会把本应用的便笺写回上面那个 `plum.sqlite`
（`Services/StickyNotesExporter.cs`）。系统便笺没有公开 API，只能直接写它的数据库，因此做了这些保护：

- **先备份**：每次导出前把 `plum.sqlite`（含 `-wal`/`-shm`）复制到 `%AppData%\DesktopNotes\backups\`，只保留最近 10 份；
- **只增改、不删除**：从不删除系统便笺；
- **双向去重**：导出成功后给便笺写入 `SourceId`；带 `SourceId` 的便笺再次导出是**更新**同一张系统便笺，
  没有的才新增，所以重复点「导出便笺」不会堆出一堆重复便签；
- **格式转换**：标题 + 正文按段落写入，待办写成 `☐/☑` 清单文本，时间轴带上时间戳；颜色映射成便笺主题色；
- 若系统便笺程序正在运行，新内容可能要**重启便笺程序**后才显示（界面会给出提示）。

### 触发系统便笺的云同步

便笺的云端同步由它自己的同步引擎完成：程序启动后读取 `plum.sqlite`，把新增/修改过的便笺上传到微软账号。
直接改库不会让运行中的便笺感知到变化，反而可能被它的内存缓存覆盖，所以导出按这个顺序做
（`Services/StickyNotesApp.cs`）：

1. 便笺程序在运行 → 先关闭（`WM_CLOSE` → `taskkill` → 强杀兜底）；
2. 备份并写入 `plum.sqlite`；
3. 重新打开便笺程序：`explorer shell:AppsFolder\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe!App`。
   它读到新数据后自行把便笺同步到云端。

状态栏会提示「已重新打开便笺，正在同步到云端」；若关闭失败会提示「内容可能被其覆盖」，请手动重开便笺。

**前提**：便笺里已登录微软账号（第一次用便笺时登录过就行）；否则只在本地可见。
不需要自动开关便笺程序时（例如只想写库），设置环境变量 `DESKTOPNOTES_NO_SYNC=1`。

## 主面板


## 自动化测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-app.ps1
```

测试脚本会：

1. 备份 `%AppData%\DesktopNotes\notes.json`，写入 4 张种子便笺（含 1 张嵌入式、1 张到期提醒）；
2. 启动应用，通过 **UI Automation** 点击真实按钮，验证 37 项行为；
3. 结束时还原用户数据、清除测试写入的「开机自启」注册表项，截图与结果输出到 `.sbx\shots\`
   （`test-result.json`、`test-log.txt`）。

覆盖范围：启动恢复、位置/尺寸还原、WorkerW 嵌入与 `WS_CHILD`、提醒置顶、新建便笺、状态栏、
全部嵌入/还原、单张嵌入/还原、待办删除、换色、隐藏与重新打开、删除便笺、重启持久化、
**拖动 1:1 跟随且无抖动**、**点击正文即可聚焦**、**嵌入便笺浮出编辑/失焦自动归位**、
导入 Windows 便笺（含重复导入去重）、**导出到 Windows 便笺（含自动备份、重复导出只更新）**、
开机自启开关与 `--autostart` 静默启动。

> 导入/导出测试使用 `plum.sqlite` 的**副本**（`DESKTOPNOTES_STICKY_DB` 环境变量指向），不会改动真实的系统便笺数据；
> 备份目录同样可用 `DESKTOPNOTES_BACKUP_DIR` 重定向到 `.sbx\shots\backups`。


## 实现要点

### 嵌入桌面（WorkerW）

不同 Windows 版本的桌面层结构不同，`Services/Win32.cs` 依次尝试：

1. **新版（Win11 22H2+，实测 10.0.26300）**：向 `Progman` 发送 `0x052C` 后，`WorkerW` 是 `Progman` 的**直接子窗口**，
   与图标层 `SHELLDLL_DefView` 同级、位于其下方 —— 直接 `SetParent` 到该 `WorkerW`；
2. **经典布局**：`SHELLDLL_DefView` 位于某个顶层 `WorkerW` 内，取其后一个同级 `WorkerW`；
3. **兜底**：把 `Progman` 作为宿主。

嵌入时保留屏幕坐标（转换成宿主客户区坐标后再 `SetWindowPos`），拖动/缩放统一走 `Win32.MoveWindowTo`，对嵌入态同样有效。


### 数据与视图

- `Models/Note.cs`：便笺模型（含 `TodoItem`、`TimelineEntry`、`SourceId`）；
- `Services/NoteStore.cs`：JSON 持久化，读取失败不会覆盖原文件；
- `Services/AutoStart.cs`：开机自启（注册表 Run 项）；
- `Services/StickyNotesImporter.cs`：读取 Windows 便笺的 SQLite 数据库；
- `ViewModels/NoteViewModel.cs`：包装模型，任何修改即时保存；待办/时间轴条目各有子 ViewModel，编辑即存；
- `Views/NoteWindow.axaml(.cs)`：无边框便笺窗口，按类型切换内容区。

## 目录结构

```
DesktopNotes.csproj
App.axaml / App.axaml.cs        应用样式、托盘、启动流程、--autostart
Program.cs                      Avalonia 入口
Models/Note.cs                  便笺模型（便签/待办/时间轴）
Services/NoteStore.cs           JSON 存储
Services/Win32.cs               WorkerW 嵌入、窗口移动等 Win32 互操作
Services/AutoStart.cs           开机自启注册表项
Services/StickyNotesImporter.cs Windows 便笺数据导入
Services/StickyNotesExporter.cs Windows 便笺数据导出（含备份）
ViewModels/                     主面板与便笺视图模型
Views/                          MainWindow / NoteWindow
Converters/                     颜色字符串 → 画刷
Assets/app.ico                  应用图标
tools/test-app.ps1              UI 自动化测试
```

## 已知限制

- 仅 Windows 实现（`net8.0-windows`，依赖 User32 / WorkerW）；
- 嵌入桌面后，便笺会随壁纸层一起被「显示桌面」「Win+D」影响；
- 桌面层结构依赖 Explorer，若系统版本变化导致找不到 `WorkerW`，会退回 `Progman` 宿主（仍可显示，但可能位于图标之上）；
- 嵌在图标层下面的便笺**点不到**（图标层 `SysListView32` 覆盖整个桌面），这是系统行为；
  编辑请从主面板「打开」（会自动浮出，失焦归位）；
- 导入/导出只处理便笺**文字**，不包含图片、墨迹；导入时不还原清单勾选状态（写回时按 `☐/☑` 文本处理）；
- 导出是直接写系统便笺的数据库，绕过它的官方接口；若便笺程序正在运行，需要重启它才能看到新内容；
- 便笺数据是单文件全量写入，便笺数量很多时保存开销会上升（当前规模无感）。



