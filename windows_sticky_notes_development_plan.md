# Windows 轻量桌面便签开发方案

> 技术栈：**C# + .NET + WPF + Win32 P/Invoke**  
> 数据方案：**Markdown 文件为唯一真实数据源（Source of Truth）**  
> 目标平台：**Windows 10 / Windows 11**  
> 产品定位：类似 Microsoft Sticky Notes 的轻量桌面便签，但更强调 Markdown、文件可控、本地优先、窗口行为可定制。

---

# 1. 项目目标

开发一个轻量、稳定、长期常驻 Windows 桌面的便签应用。

核心目标：

- 每张便签为独立桌面窗口。
- 多个便签不会在任务栏生成大量图标。
- 尽量避免 Alt+Tab 中出现大量便签窗口。
- `Win + D` 显示桌面时，便签仍保持可见，行为尽量接近 Microsoft Sticky Notes。
- 正常应用窗口可以覆盖便签，默认不做“永久置顶”。
- 支持可选“始终置顶”模式。
- 使用 Markdown 文件存储便签内容。
- 用户可指定便签保存目录。
- 不依赖数据库作为主数据源。
- 不需要 GitHub / Git 同步。
- 应用配置和机器相关 UI 状态与 Markdown 内容分离。
- 支持外部编辑器（VS Code、Obsidian、Typora 等）直接修改 Markdown。
- 尽量减少后台服务、子进程和 IPC 层级。
- 单进程、低内存、低 CPU 占用。

---

# 2. 技术选型

## 2.1 核心技术

```text
C#
.NET
WPF
Win32 P/Invoke
```

推荐：

```text
.NET 10 或项目开发时最新稳定 LTS
```

如果项目正式启动时 .NET LTS 版本有变化，优先选择最新稳定 LTS。

---

## 2.2 推荐依赖

### MVVM

```text
CommunityToolkit.Mvvm
```

用途：

- ObservableObject
- RelayCommand
- AsyncRelayCommand
- ViewModel 基础设施

---

### Markdown

推荐：

```text
Markdig
```

用途：

- Markdown 解析
- Markdown -> HTML / AST
- 扩展 Markdown 语法

如果编辑器第一版仅使用纯文本编辑，可暂时只用于预览。

---

### YAML Front Matter

推荐：

```text
YamlDotNet
```

用途：

解析 Markdown 文件开头的 metadata：

```yaml
---
id: ...
title: ...
color: yellow
tags:
  - work
---
```

---

### JSON

使用 .NET 自带：

```text
System.Text.Json
```

用于：

- settings.json
- layout.json
- cache
- app state

---

### 日志

推荐：

```text
Microsoft.Extensions.Logging
```

可选：

```text
Serilog
```

第一版可以使用简单 Rolling File Logger，避免日志系统过重。

---

# 3. 不采用的技术

第一版明确不引入：

```text
SQLite
EF Core
Electron
Node.js
React
Tauri
Go Service
HTTP Server
localhost API
Git Sync
Cloud Sync
独立后台 Service
```

设计原则：

> 只有实际需求出现时才增加基础设施。

避免为了未来可能存在的需求过早复杂化。

---

# 4. 总体架构

建议架构：

```text
┌───────────────────────────────────────────┐
│                 WPF UI                    │
│                                           │
│ NoteWindow / ManagerWindow / Settings     │
└─────────────────────┬─────────────────────┘
                      │
                  ViewModel
                      │
┌─────────────────────▼─────────────────────┐
│                Application                │
│                                           │
│ NoteService                               │
│ SearchService                             │
│ WindowManager                             │
│ SettingsService                           │
│ FileWatchService                          │
└─────────────────────┬─────────────────────┘
                      │
┌─────────────────────▼─────────────────────┐
│                   Core                    │
│                                           │
│ Note                                      │
│ NoteMetadata                              │
│ NoteLayout                                │
│ AppSettings                               │
└─────────────────────┬─────────────────────┘
                      │
┌─────────────────────▼─────────────────────┐
│              Infrastructure               │
│                                           │
│ MarkdownStorage                           │
│ JsonSettingsStorage                       │
│ FileSystemWatcher                         │
│ Win32                                     │
└─────────────────────┬─────────────────────┘
                      │
                      ▼
                用户指定目录
```

---

# 5. 推荐解决方案结构

建议：

```text
StickyNotes.sln

src/
├── StickyNotes.App/
│   ├── App.xaml
│   ├── App.xaml.cs
│   │
│   ├── Views/
│   │   ├── NoteWindow.xaml
│   │   ├── ManagerWindow.xaml
│   │   ├── SettingsWindow.xaml
│   │   ├── SearchWindow.xaml
│   │   └── Dialogs/
│   │
│   ├── ViewModels/
│   │   ├── NoteViewModel.cs
│   │   ├── ManagerViewModel.cs
│   │   ├── SettingsViewModel.cs
│   │   └── SearchViewModel.cs
│   │
│   ├── Controls/
│   │   ├── NoteToolbar.xaml
│   │   ├── TagChip.xaml
│   │   └── MarkdownEditor.xaml
│   │
│   ├── Themes/
│   │   ├── Colors.xaml
│   │   ├── Typography.xaml
│   │   ├── Buttons.xaml
│   │   └── NoteTheme.xaml
│   │
│   └── Resources/
│
├── StickyNotes.Core/
│   ├── Models/
│   │   ├── Note.cs
│   │   ├── NoteMetadata.cs
│   │   ├── NoteLayout.cs
│   │   ├── AppSettings.cs
│   │   └── NoteColor.cs
│   │
│   ├── Interfaces/
│   │   ├── INoteRepository.cs
│   │   ├── INoteService.cs
│   │   ├── ISettingsService.cs
│   │   ├── IWindowManager.cs
│   │   └── ISearchService.cs
│   │
│   └── Events/
│
├── StickyNotes.Infrastructure/
│   ├── Storage/
│   │   ├── MarkdownNoteRepository.cs
│   │   ├── MarkdownParser.cs
│   │   ├── MarkdownSerializer.cs
│   │   └── AtomicFileWriter.cs
│   │
│   ├── Settings/
│   │   ├── JsonSettingsService.cs
│   │   └── LayoutStore.cs
│   │
│   ├── FileWatching/
│   │   └── NoteFileWatcher.cs
│   │
│   ├── Windows/
│   │   ├── NativeMethods.cs
│   │   ├── WindowStyleManager.cs
│   │   ├── ShowDesktopManager.cs
│   │   ├── ZOrderManager.cs
│   │   ├── MonitorManager.cs
│   │   └── SnapManager.cs
│   │
│   └── Logging/
│
└── StickyNotes.Tests/
    ├── Storage/
    ├── Parsing/
    ├── Search/
    └── WindowLogic/
```

---

# 6. 数据设计

## 6.1 Markdown 为唯一真实数据源

每个便签对应一个 Markdown 文件：

```text
Notes/
├── 工作/
│   ├── Docker.md
│   └── 项目计划.md
│
├── 生活/
│   └── 购物清单.md
│
└── 临时.md
```

用户可直接：

- 复制
- 备份
- 同步
- 使用 Obsidian 打开
- 使用 VS Code 编辑
- 使用 Typora 编辑

应用不应锁定 Markdown 文件格式。

---

# 7. Markdown 文件格式

建议：

```markdown
---
id: "c88988a6-5f9b-4df4-8bea-8b28732a4d1b"
title: "Docker 常用命令"
color: "yellow"
createdAt: "2026-09-19T00:00:00+08:00"
updatedAt: "2026-09-19T00:20:00+08:00"
tags:
  - docker
  - linux
---

# Docker

启动：

```bash
docker compose up -d
```

查看：

- `docker ps`
- `docker logs`
```

---

## 7.1 Front Matter 只保存“便签自身属性”

建议保存：

```text
id
title
color
createdAt
updatedAt
tags
```

可以保存：

```text
locked
```

但第一版不建议把机器相关坐标写进 Markdown。

---

# 8. 窗口坐标不要写进 Markdown

原因：

电脑 A：

```text
2560x1440
+
1920x1080
```

电脑 B：

```text
1920x1080
```

窗口坐标属于：

> 当前设备 UI 状态

而不是：

> 笔记内容。

因此使用独立：

```text
.pinslip/layout.json
```

例如：

```json
{
  "version": 1,
  "notes": {
    "c88988a6-5f9b-4df4-8bea-8b28732a4d1b": {
      "monitor": "\\\\.\\DISPLAY1",
      "x": 1260,
      "y": 320,
      "width": 380,
      "height": 460,
      "collapsed": false,
      "alwaysOnTop": false
    }
  }
}
```

---

# 9. 应用级设置

建议：

```text
.pinslip/settings.json
```

示例：

```json
{
  "theme": "system",
  "defaultColor": "yellow",
  "defaultWidth": 360,
  "defaultHeight": 420,
  "startWithWindows": true,
  "showTrayIcon": true,
  "singleClickTrayAction": "toggleNotes",
  "globalNewNoteHotkey": "Ctrl+Alt+N",
  "autoSaveDelayMs": 500,
  "keepNotesVisibleOnShowDesktop": true,
  "hideNotesFromTaskbar": true,
  "hideNotesFromAltTab": true
}
```

---

# 10. 最终目录建议

```text
用户选择目录/
│
├── 工作/
│   ├── Docker.md
│   └── 项目计划.md
│
├── 生活/
│   └── 购物.md
│
├── attachments/
│   ├── 20260919_001.png
│   └── 20260919_002.png
│
└── .pinslip/
    ├── settings.json
    ├── layout.json
    ├── trash/
    └── logs/
```

如果不希望配置跟随用户目录，也可以把 application settings 放到：

```text
%LOCALAPPDATA%\ApplicationName\
```

推荐方案：

- 笔记数据跟随 Notes Folder。
- 机器相关 UI 配置存在 `%LOCALAPPDATA%`。
- 回收站放在 Notes Folder 内。

具体可在开发时根据 portable 需求决定。

---

# 11. Note 模型

建议：

```csharp
public sealed class Note
{
    public Guid Id { get; init; }

    public string FilePath { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public NoteColor Color { get; set; }

    public List<string> Tags { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
```

---

# 12. Window Layout 模型

```csharp
public sealed class NoteLayout
{
    public Guid NoteId { get; init; }

    public string? MonitorId { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 360;

    public double Height { get; set; } = 420;

    public bool Collapsed { get; set; }

    public bool AlwaysOnTop { get; set; }

    public bool Locked { get; set; }
}
```

---

# 13. 内存仓库

启动时扫描一次 Markdown：

```text
*.md
 ↓
Parse
 ↓
Note
 ↓
Dictionary<Guid, Note>
```

推荐：

```csharp
Dictionary<Guid, Note>
```

或线程安全需求较高时：

```csharp
ConcurrentDictionary<Guid, Note>
```

但 UI 主线程模型下没必要过度使用 Concurrent。

---

# 14. 搜索设计

第一版不需要 SQLite。

应用启动后所有 Markdown 已经在内存：

```csharp
IEnumerable<Note> Search(string query)
{
    return _notes.Values.Where(note =>
        note.Title.Contains(
            query,
            StringComparison.OrdinalIgnoreCase)
        ||
        note.Content.Contains(
            query,
            StringComparison.OrdinalIgnoreCase)
        ||
        note.Tags.Any(tag =>
            tag.Contains(
                query,
                StringComparison.OrdinalIgnoreCase)));
}
```

对于：

```text
几十
几百
上千
```

普通短便签已经足够。

---

# 15. 什么时候才需要 SQLite FTS5

仅当实际测试出现：

```text
5000+
10000+
```

甚至更大量 Markdown 后，搜索开始出现明显延迟，再引入 SQLite FTS5。

SQLite 只能作为：

> 可删除、可重建的搜索索引。

禁止把 SQLite 变成唯一主数据源。

即：

```text
Markdown
   ↓
Index Builder
   ↓
SQLite FTS5
```

数据库删除：

```text
重新扫描 Markdown
↓
恢复全部索引
```

---

# 16. FileSystemWatcher

使用：

```text
System.IO.FileSystemWatcher
```

监听：

```text
Created
Changed
Deleted
Renamed
```

建议：

```csharp
var watcher = new FileSystemWatcher(notesFolder)
{
    Filter = "*.md",
    IncludeSubdirectories = true,
    NotifyFilter =
        NotifyFilters.FileName |
        NotifyFilters.LastWrite |
        NotifyFilters.Size
};
```

---

# 17. FileSystemWatcher 注意事项

FileSystemWatcher 事件不是严格一文件一次。

一次保存可能触发：

```text
Changed
Changed
Changed
Renamed
```

因此必须增加：

```text
Debounce
```

建议：

```text
200~500ms
```

按照文件路径 debounce。

例如：

```text
Dictionary<string, CancellationTokenSource>
```

收到新事件：

1. 取消旧任务。
2. 等待 300ms。
3. 再重新读取文件。

---

# 18. 避免自己写文件触发自己

场景：

```text
用户编辑
↓
应用 Save
↓
FileSystemWatcher Changed
↓
应用又 Reload
```

容易产生：

- 光标跳动
- 内容闪烁
- 无限循环
- 重复保存

解决方案：

维护：

```text
LastWrittenHash
```

或者：

```text
LastInternalWriteTime
```

更可靠的方案：

每次内部写文件时计算：

```text
SHA256 / xxHash / content hash
```

FileSystemWatcher 触发后：

```text
新内容 Hash == 刚才写入 Hash
```

则忽略。

---

# 19. 自动保存

推荐：

```text
输入
↓
500ms debounce
↓
保存
```

不要每输入一个字符写一次文件。

建议默认：

```text
300~800ms
```

可设：

```text
500ms
```

---

# 20. 原子保存

避免崩溃导致 Markdown 变成空文件。

禁止直接：

```csharp
File.WriteAllText(path, content);
```

推荐：

```text
write temp
↓
flush
↓
replace original
```

例如：

```text
Docker.md.tmp
↓
成功写入
↓
File.Replace / Move
↓
Docker.md
```

注意：

- 文件必须先完整写完。
- 必要时使用 backup。
- Windows Defender/同步软件可能短暂占用文件，应支持有限次数重试。

---

# 21. 外部编辑冲突

场景：

```text
便签程序中修改
+
VS Code 同时修改
```

第一版采用简单策略：

### 未修改状态

外部文件变化：

```text
直接 reload
```

### 本地存在未保存修改

外部文件变化：

弹出：

```text
文件已在外部修改
```

提供：

```text
重新加载
覆盖外部版本
查看差异
```

第一版甚至可以先只做：

```text
重新加载
保留当前内容
```

以后再增加 diff。

---

# 22. 回收站

不直接删除 Markdown。

例如：

```text
工作.md
```

删除后：

```text
.pinslip/trash/
    20260919_001_工作.md
```

实现：

```text
File.Move
```

记录原路径可以：

1. 写入文件 metadata。
2. 单独 `trash-index.json`。

推荐：

```json
{
  "files": {
    "20260919_001_工作.md": {
      "originalPath": "工作/工作.md",
      "deletedAt": "2026-09-19T00:00:00+08:00"
    }
  }
}
```

---

# 23. 附件设计

粘贴图片：

```text
Clipboard
↓
PNG
↓
attachments/
↓
Markdown image syntax
```

生成：

```text
attachments/
20260919_001.png
```

Markdown：

```markdown
![image](../attachments/20260919_001.png)
```

实际相对路径需要根据 Markdown 所在目录计算。

---

# 24. 窗口设计目标

每个便签：

```text
┌───────────────────────────┐
│ 标题                    × │
├───────────────────────────┤
│                           │
│ Markdown 内容             │
│                           │
│                           │
├───────────────────────────┤
│ B  ☑  📌  🔍  ⋯          │
└───────────────────────────┘
```

建议：

- 无系统边框
- 自定义标题栏
- 圆角
- 阴影
- resize
- 自定义颜色
- 可折叠
- 可锁定
- 可置顶
- toolbar 自动隐藏/弱显示

---

# 25. WPF Window 推荐设置

```xml
<Window
    WindowStyle="None"
    ResizeMode="CanResizeWithGrip"
    ShowInTaskbar="False"
    Background="Transparent"
    AllowsTransparency="False">
</Window>
```

注意：

`AllowsTransparency=True` 可能导致：

- 性能下降
- 某些 GPU 合成问题
- 部分 DWM 特性失效

因此尽量：

> 使用 DWM + Win32 实现圆角/阴影，而不是依赖整个窗口透明。

---

# 26. 任务栏隐藏

WPF 第一层：

```csharp
ShowInTaskbar = false;
```

但为了更稳定控制 Shell 行为，可配合 Win32：

```text
WS_EX_TOOLWINDOW
WS_EX_APPWINDOW
```

目标：

```text
普通便签窗口
→ 不显示任务栏按钮
```

---

# 27. Alt+Tab 隐藏

大量便签不应该污染 Alt+Tab。

可使用：

```text
WS_EX_TOOLWINDOW
```

并移除：

```text
WS_EX_APPWINDOW
```

注意：

不同 Owner Window 关系也会影响 Alt+Tab。

因此建议建立一个隐藏的：

```text
Owner Window
```

所有便签可以统一归属一个 owner。

开发时需要测试：

- Windows 10
- Windows 11
- 多显示器
- Explorer 重启后

---

# 28. 隐藏 Owner Window

可以创建一个不可见 WPF Window：

```text
ApplicationHostWindow
```

用途：

- 提供 HWND owner
- 托盘生命周期
- Shell Hook
- 全局消息接收
- 管理便签窗口

它本身：

```text
不显示
不在任务栏
不在 Alt+Tab
```

---

# 29. Win + D 的目标行为

目标不是：

```text
AlwaysOnTop
```

也不是：

```text
嵌入 Wallpaper WorkerW
```

而是接近 Sticky Notes：

### 普通状态

```text
Chrome
↓
可以覆盖 Note
```

### Win + D

```text
Chrome / Explorer
隐藏/最小化
↓
Note 保留
```

### 恢复窗口

```text
普通程序重新恢复
↓
仍然可以覆盖 Note
```

---

# 30. Show Desktop 设计

建议单独模块：

```text
ShowDesktopManager
```

职责：

- 监听 Shell 窗口变化。
- 判断 Windows 是否执行 Show Desktop。
- 记录便签当前 Z-order。
- 必要时恢复 Note 窗口。
- 避免永久 TopMost。

可能涉及：

```text
RegisterShellHookWindow
RegisterWindowMessage("SHELLHOOK")
SetWinEventHook
SetWindowPos
GetWindow
EnumWindows
```

必要时可以补充 Explorer 桌面窗口检测。

---

# 31. 不建议简单 Hook Win+D 键盘

原因：

用户触发显示桌面的方式不止：

```text
Win + D
```

还有：

- 点击任务栏最右侧 Show Desktop
- Shell 操作
- 部分触摸板手势
- 第三方快捷方式

因此应该监听：

> Shell / Window 状态变化

而不是只监听键盘快捷键。

---

# 32. Always On Top

可选功能。

WPF：

```csharp
Topmost = true;
```

或者 Win32：

```text
SetWindowPos(HWND_TOPMOST)
```

关闭：

```text
SetWindowPos(HWND_NOTOPMOST)
```

必须明确：

> Show Desktop 保留 != Always On Top。

两者完全独立。

---

# 33. 鼠标穿透

后期功能：

```text
锁定便签
```

锁定后可以提供：

```text
点击穿透
```

可能涉及：

```text
WS_EX_TRANSPARENT
WS_EX_LAYERED
```

注意不要默认开启，否则用户无法重新操作窗口。

建议：

- Tray 菜单提供“取消全部穿透”
- 全局快捷键可以恢复编辑状态

---

# 34. 窗口磁吸

建议自己实现，不依赖大框架。

常量：

```text
SnapDistance = 10~16 px
```

窗口 A：

```text
left
right
top
bottom
```

窗口 B：

比较：

```text
abs(A.right - B.left)
```

小于阈值：

```text
A.x = B.left - A.width
```

同理处理：

- left ↔ right
- top ↔ bottom
- screen edges

---

# 35. 窗口分组

后期：

```text
Note A
Note B
Note C
```

吸附后可以建立：

```text
NoteGroup
```

示例：

```csharp
public sealed class NoteGroup
{
    public Guid Id { get; init; }

    public HashSet<Guid> NoteIds { get; init; } = [];
}
```

移动主窗口时可整体移动。

第一版不要做自动复杂分组。

先实现：

```text
Snap
```

再逐步扩展。

---

# 36. 多显示器

必须从一开始支持。

不要假设：

```text
Primary Screen == 唯一屏幕
```

应保存：

```text
Monitor ID
工作区 bounds
DPI
```

显示器移除：

```text
原便签位置不存在
↓
将便签移动到主显示器可见区域
```

启动时必须检查：

```text
窗口是否完全在屏幕外
```

如果是：

```text
自动纠正
```

---

# 37. DPI

WPF 自身支持 DPI，但 Win32 坐标与 WPF DIP 需要注意换算。

WPF：

```text
Device Independent Pixel
1 DIP = 1/96 inch
```

Win32 可能返回：

```text
physical pixels
```

所有 WindowManager 代码必须明确：

```text
DIP
vs
Pixel
```

避免：

```text
125%
150%
200%
```

缩放时窗口漂移。

---

# 38. 自定义标题栏拖动

WPF 可以：

```csharp
DragMove();
```

但如果需要高级体验：

- 边缘 resize
- 双击折叠
- snap preview
- 自定义 hit test

建议处理：

```text
WM_NCHITTEST
```

返回：

```text
HTCAPTION
HTLEFT
HTRIGHT
HTTOP
HTBOTTOM
HTTOPLEFT
...
```

这样系统级 resize 体验更自然。

---

# 39. UI 风格

设计目标：

```text
轻
柔和
低干扰
桌面长期驻留
```

不要做：

```text
复杂 Ribbon
大量 Material 卡片
过强阴影
高饱和色
过多按钮
```

推荐：

- 8~14 px 圆角
- 轻微 shadow
- toolbar hover 出现
- 窗口失焦后减少视觉噪音
- 默认淡黄色
- 支持蓝 / 绿 / 粉 / 灰等

---

# 40. Design Tokens

建议统一：

```xml
<Color x:Key="NoteYellow">#FFF5B8</Color>
<Color x:Key="NoteBlue">#DDEEFF</Color>
<Color x:Key="NoteGreen">#DCF4D7</Color>

<sys:Double x:Key="NoteCornerRadius">12</sys:Double>
```

不要在控件里硬编码颜色。

---

# 41. Markdown 编辑器

第一版建议：

```text
纯文本编辑 + Markdown 快捷格式化
```

例如：

- Ctrl+B → `**text**`
- Ctrl+I → `*text*`
- checkbox
- bullet list
- link
- code

不要第一版就做复杂 Rich Markdown WYSIWYG。

这是最容易把项目复杂度拉高的部分之一。

---

# 42. 编辑模式方案

推荐第一阶段：

```text
TextBox / 自定义 Text Editor
+
Markdown Preview 可选
```

可以做到：

```text
编辑时 Markdown
失焦后 Preview
```

或：

```text
用户手动切换 Preview
```

---

# 43. Rich Markdown 后续方案

如果未来必须做类似：

```text
Markdown 所见即所得
```

可以考虑：

- AvalonEdit
- 自定义 FlowDocument
- WebView2 内嵌 Markdown editor（仅编辑器，不作为整个 App 技术栈）

注意：

> 即使主程序是 WPF，也完全可以只在编辑区域使用 WebView2。

但第一版不建议。

---

# 44. 托盘

推荐：

```text
NotifyIcon
```

功能：

```text
新建便签
显示全部
隐藏全部
搜索
打开管理器
设置
退出
```

退出必须区分：

```text
关闭便签
```

和：

```text
退出应用
```

便签关闭只影响窗口，不应该杀进程。

---

# 45. 应用生命周期

应用启动：

```text
App Start
↓
加载 Settings
↓
加载 Layout
↓
扫描 Notes Folder
↓
解析 Markdown
↓
建立内存索引
↓
恢复需要显示的窗口
↓
启动 FileSystemWatcher
↓
启动 Tray
↓
注册 Hotkey / Shell Hook
```

退出：

```text
停止 watcher
↓
flush pending save
↓
保存 layout
↓
释放 shell hooks
↓
释放 hotkeys
↓
关闭 windows
↓
退出
```

---

# 46. 单实例

建议必须单实例。

避免：

```text
启动两次
↓
两个进程同时修改 Markdown
```

可以：

```text
Mutex
```

例如：

```csharp
new Mutex(
    initiallyOwned: true,
    name: @"Local\StickyNotes_App",
    createdNew: out bool createdNew);
```

第二实例：

- 激活第一实例
- 或请求新建便签
- 然后退出

后续可以用：

```text
Named Pipe
```

做简单实例通信。

---

# 47. 全局快捷键

例如：

```text
Ctrl + Alt + N
```

新建便签。

Win32：

```text
RegisterHotKey
UnregisterHotKey
```

配置项允许修改。

冲突时：

```text
提示快捷键已被占用
```

不能静默失败。

---

# 48. 新建便签

流程：

```text
New Note
↓
生成 Guid
↓
生成 Markdown 文件
↓
加入 Memory Repository
↓
创建 NoteWindow
↓
记录 Layout
↓
聚焦 Editor
```

默认文件名：

```text
Untitled.md
```

不推荐。

更推荐：

```text
2026-09-19-001.md
```

或：

```text
UUID.md
```

显示标题可以独立于文件名。

推荐稳定文件名：

```text
<guid>.md
```

优点：

- 标题变化不需要重命名。
- 避免文件冲突。
- 文件 watcher 更稳定。

缺点：

- 用户直接看目录不直观。

折中：

```text
20260919-标题摘要-短ID.md
```

建议开发时根据“用户直接操作文件”的重要性决定。

---

# 49. 标题策略

Markdown：

```markdown
---
title: Docker
---
```

标题优先级：

```text
Front Matter title
↓
第一行 H1
↓
文件名
↓
“无标题”
```

建议只保留一个 canonical title：

> Front Matter title

UI 修改标题后同时更新 front matter。

---

# 50. 文件夹

文件夹直接等于实际目录。

例如：

```text
Notes/
工作/
生活/
学习/
```

不再额外做数据库 folder table。

移动便签到文件夹：

```text
File.Move
```

即可。

---

# 51. 标签

标签写入 Front Matter：

```yaml
tags:
  - work
  - docker
```

启动时建立：

```csharp
Dictionary<string, HashSet<Guid>>
```

作为内存索引。

无需数据库。

---

# 52. 内存搜索索引

可以维护：

```text
TitleIndex
TagIndex
PlainTextCache
```

第一版其实只需要：

```text
Dictionary<Guid, Note>
```

搜索时 LINQ 即可。

只有性能测试证明有问题时再优化。

---

# 53. UI 更新

外部文件变化后：

```text
NoteRepository.Update
↓
event
↓
ViewModel 更新
↓
WPF Binding
↓
UI 更新
```

推荐：

```text
event aggregator
```

但不要上复杂框架。

可以自己定义：

```csharp
event EventHandler<NoteChangedEventArgs>? NoteChanged;
```

---

# 54. WindowManager

这是项目最重要的服务之一。

建议：

```csharp
public interface IWindowManager
{
    void ShowNote(Guid noteId);
    void HideNote(Guid noteId);
    void CloseNote(Guid noteId);

    void ShowAllNotes();
    void HideAllNotes();

    void SetTopMost(Guid noteId, bool value);
    void SetLocked(Guid noteId, bool value);

    void RestoreLayout(Guid noteId);
    void SaveLayout(Guid noteId);
}
```

---

# 55. WindowManager 不应该包含业务数据

错误：

```text
WindowManager
→ 直接读写 Markdown
```

正确：

```text
WindowManager
→ NoteId
→ Window State
```

Note 内容由：

```text
NoteService
```

管理。

---

# 56. Win32 封装

不要让 P/Invoke 散落全项目。

集中：

```text
Infrastructure/Windows/NativeMethods.cs
```

例如：

```csharp
internal static partial class NativeMethods
{
    // User32
    // DwmApi
    // Shell32
}
```

建议开发时优先考虑：

```text
CsWin32
```

自动生成 Windows API Interop。

NuGet：

```text
Microsoft.Windows.CsWin32
```

相比大量手写 `[DllImport]`：

- 类型更安全
- 定义更准确
- 减少签名错误

---

# 57. 可使用 CsWin32

如果使用：

```text
Microsoft.Windows.CsWin32
```

则项目可以维护：

```text
NativeMethods.txt
```

例如：

```text
SetWindowLongPtr
GetWindowLongPtr
SetWindowPos
RegisterHotKey
UnregisterHotKey
RegisterShellHookWindow
RegisterWindowMessage
SetWinEventHook
UnhookWinEvent
```

然后自动生成 Interop。

---

# 58. 错误处理

保存失败必须提示：

```text
无法保存
原因
文件路径
```

同时：

- 内存内容不能丢。
- 不应该把窗口直接关掉。
- 支持重新保存。

常见错误：

```text
文件只读
目录无权限
文件被占用
磁盘满
路径过长
文件被删除
```

---

# 59. 崩溃恢复

自动保存 debounce 会存在：

```text
最后 500ms 内容未写磁盘
```

可以选择：

```text
autosave recovery file
```

例如：

```text
%LOCALAPPDATA%\AppName\recovery\
```

但第一版可以不做。

关键是：

> 正常输入后尽快自动保存。

---

# 60. 日志

日志建议：

```text
%LOCALAPPDATA%\AppName\logs\
```

每天：

```text
app-2026-09-19.log
```

日志内容：

```text
启动
退出
文件读取异常
文件保存异常
FileSystemWatcher 异常
Shell Hook 初始化异常
布局恢复异常
```

不要记录用户完整便签内容。

---

# 61. 性能目标

建议设定：

### 空闲 CPU

```text
接近 0%
```

### 空闲状态

禁止：

```text
while(true)
timer 16ms
不停扫描窗口
不停扫目录
```

全部事件驱动。

### 启动

目标：

```text
数百 Markdown：
< 1 秒~2 秒可交互
```

根据机器性能调整。

---

# 62. 内存目标

应用为单进程 WPF。

目标：

```text
基础运行
尽量控制在较低水平

20~50 个普通便签
仍保持合理内存
```

关键：

- 不为每个便签创建昂贵后台服务。
- 不为每个窗口创建 WebView。
- 图片按需加载。
- 隐藏窗口必要时可释放重建。

---

# 63. 大量窗口优化

不要默认所有 Markdown 都创建 Window。

推荐：

```text
所有 Note
→ 内存中存在

只有 Visible Notes
→ 创建 WPF Window
```

管理器列表里存在但关闭的便签：

```text
没有 HWND
```

这会显著降低窗口数量。

---

# 64. “关闭”定义

建议：

### X 按钮

```text
隐藏窗口
```

或：

```text
关闭窗口实例，但不删除 Markdown
```

### 删除

独立菜单：

```text
移到回收站
```

不要让用户误以为关闭窗口就是删除数据。

---

# 65. 管理器窗口

除了桌面便签，建议有一个管理界面：

```text
搜索
所有便签
文件夹
标签
回收站
设置
```

管理器可以显示在任务栏。

桌面便签则：

```text
ShowInTaskbar = false
```

这样任务栏最多只有：

```text
一个主程序窗口
```

或者完全只保留托盘。

---

# 66. 产品窗口类型

建议分三类：

## Desktop Note Window

```text
不显示任务栏
不显示 Alt+Tab
特殊 Show Desktop 行为
```

## Manager Window

```text
正常任务栏
正常 Alt+Tab
```

## Dialog / Popup

```text
Owner = Manager / Note
```

明确区分，避免窗口样式互相污染。

---

# 67. Settings 功能

建议：

```text
Notes Folder
启动时运行
托盘行为
默认便签颜色
默认大小
快捷键
自动保存延迟
Show Desktop 保留
任务栏隐藏
Alt+Tab 隐藏
动画
主题
```

---

# 68. 开机启动

推荐使用：

```text
StartupTask
```

或：

```text
注册表 HKCU Run
```

如果使用 MSIX，可用对应 StartupTask。

不要要求管理员权限。

---

# 69. 安装方式

候选：

### MSIX

优点：

- Windows 原生
- 安装/卸载干净
- 自动更新体系更正规

缺点：

- 部分传统桌面行为需要测试打包权限。

### exe Installer

例如：

```text
Inno Setup
WiX
```

适合 Win32/WPF 应用。

开发初期：

```text
dotnet publish
```

即可。

---

# 70. 发布模式

建议：

```text
win-x64
```

如果需要 ARM64：

```text
win-arm64
```

优先：

```text
framework-dependent
```

或：

```text
self-contained
```

根据安装包体积决定。

如果做 portable：

```text
self-contained single-folder
```

体验更直接。

---

# 71. 自动更新

第一版可不做。

后期：

```text
GitHub Releases
自建 update.json
MSIX update
```

均可。

但不要在核心架构中绑定 GitHub。

---

# 72. 测试重点

## Markdown

测试：

```text
Front Matter
Unicode
中文
Emoji
CRLF / LF
空文件
无 Front Matter
损坏 YAML
```

---

## Storage

测试：

```text
Create
Read
Update
Move
Rename
Delete
Trash
Restore
Atomic Write
```

---

## Watcher

测试：

```text
外部保存
连续保存
VS Code atomic save
Obsidian save
文件重命名
目录移动
批量操作
```

---

## Window

至少手工测试：

```text
Windows 10
Windows 11
100%
125%
150%
200% DPI
单屏
双屏
主屏切换
拔显示器
休眠恢复
Explorer 重启
Win + D
任务栏 Show Desktop
Alt + Tab
Virtual Desktop
```

---

# 73. Win+D 是高风险模块

这个功能建议：

> 单独原型验证后再正式集成。

原因是：

Windows Shell 的具体行为在：

- Windows 10
- Windows 11
- 不同更新版本
- 多虚拟桌面
- Explorer 重启

可能存在差异。

所以最早开发阶段就先做：

```text
ShowDesktopPrototype.exe
```

只测试：

```text
创建一个窗口
↓
隐藏任务栏
↓
隐藏 Alt+Tab
↓
Win+D 后保留
↓
应用恢复后正常 Z-order
```

验证成功再继续做完整 UI。

这是整个项目最值得优先验证的技术风险。

---

# 74. 虚拟桌面

Windows Virtual Desktop 是另一个特殊领域。

第一版建议：

```text
便签只属于创建它的当前虚拟桌面
```

不要一开始做：

```text
所有虚拟桌面都显示
```

如果未来需要，可研究 Windows Virtual Desktop COM API。

---

# 75. 安全原则

Markdown 是用户文件。

禁止：

```text
自动执行 Markdown 中脚本
自动执行 HTML script
自动执行 shell
```

Markdown preview：

```text
禁用危险 HTML
```

外链：

```text
明确由默认浏览器打开
```

---

# 76. 第一阶段 MVP

建议只实现：

```text
1. 创建便签
2. Markdown 文件存储
3. 自动保存
4. 多便签窗口
5. 自定义颜色
6. resize
7. 关闭/重新打开
8. 位置恢复
9. 不显示任务栏
10. 托盘
11. Notes Folder 设置
12. 基础搜索
```

---

# 77. 第二阶段

```text
1. Alt+Tab 隐藏
2. Win+D 保留
3. 始终置顶
4. 折叠
5. 标签
6. 文件夹
7. 回收站
8. 外部文件监听
9. 全局快捷键
```

---

# 78. 第三阶段

```text
1. 磁吸
2. 多窗口分组
3. 多显示器优化
4. 鼠标穿透
5. 锁定
6. 图片附件
7. Markdown preview
8. 管理器增强
```

---

# 79. 第四阶段

根据真实用户反馈再考虑：

```text
FTS 搜索
插件
Cloud Sync
Rich Markdown
自动更新
扩展 API
```

不要提前实现。

---

# 80. 推荐开发顺序

实际开发建议：

```text
Step 1
建立 solution / 项目结构

Step 2
Markdown parser + serializer

Step 3
NoteRepository

Step 4
最简单 NoteWindow

Step 5
Auto Save

Step 6
Layout Store

Step 7
WindowManager

Step 8
任务栏/Alt+Tab 行为

Step 9
Show Desktop 原型

Step 10
Tray

Step 11
FileSystemWatcher

Step 12
Search / Manager

Step 13
UI polish

Step 14
Installer
```

---

# 81. 不要先做漂亮 UI

最早优先验证：

```text
Markdown 数据安全
窗口行为
Win+D
多屏 DPI
文件 watcher
```

这几个决定产品能不能稳定运行。

UI 后期改成本很低。

窗口架构后期改成本非常高。

---

# 82. 推荐开发原则

### 原则 1

```text
Markdown 永远是真数据
```

---

### 原则 2

```text
UI state 和 note content 分离
```

---

### 原则 3

```text
Windows-specific code 集中管理
```

---

### 原则 4

```text
没有需求就不增加后台服务
```

---

### 原则 5

```text
优先事件驱动
禁止轮询
```

---

### 原则 6

```text
先保证文件不会丢
再做 UI 动画
```

---

# 83. 最终核心架构

```text
                    ┌─────────────┐
                    │   WPF UI    │
                    └──────┬──────┘
                           │
                  ┌────────▼────────┐
                  │    ViewModel    │
                  └────────┬────────┘
                           │
                ┌──────────▼──────────┐
                │     NoteService     │
                └──────┬───────┬──────┘
                       │       │
               ┌───────▼──┐  ┌─▼─────────────┐
               │Repository│  │ WindowManager │
               └───────┬──┘  └───────┬───────┘
                       │             │
               ┌───────▼──────┐  ┌──▼─────────┐
               │ Markdown I/O │  │ Win32 APIs │
               └───────┬──────┘  └────────────┘
                       │
              ┌────────▼────────┐
              │   *.md Files    │
              └─────────────────┘
```

---

# 84. 推荐最终技术清单

```text
Language
C#

Runtime
.NET

Desktop
WPF

Architecture
MVVM

MVVM Library
CommunityToolkit.Mvvm

Windows Native
Win32
CsWin32 / PInvoke

Markdown
Markdig

YAML
YamlDotNet

Settings
System.Text.Json

Storage
Markdown Files

Search
In-memory search

File Change
FileSystemWatcher

Tray
NotifyIcon

Global Hotkey
RegisterHotKey

Window Management
HWND + Win32

Logging
Microsoft.Extensions.Logging / Serilog

Installer
Inno Setup / MSIX
```

---

# 85. 最终建议

这个项目最核心的价值不应该是：

```text
做一个复杂的 Note Platform
```

而应该是：

```text
做一个真正好用的 Windows 桌面便签
```

因此重点投入：

```text
窗口行为
稳定性
保存可靠性
桌面体验
低资源占用
UI 细节
```

而不是：

```text
数据库
Server
API
多进程
云端架构
复杂同步
```

第一版最理想的状态就是：

```text
一个 exe
+
一个用户指定的 Markdown 文件夹
```

所有便签内容用户完全拥有。

即使未来软件停止维护，用户仍然可以直接打开 `.md` 文件，不存在数据锁定。

这是整个项目最值得坚持的架构原则。


---

# 86. MVVM 设计补充

本项目建议正式采用：

```text
MVVM
+
Service
+
Repository
```

而不是把业务逻辑直接写在 WPF Window 的 code-behind 中。

推荐整体关系：

```text
View
 ↓
ViewModel
 ↓
Service
 ↓
Repository
 ↓
Markdown / JSON / Win32
```

其中：

```text
View
= 界面

ViewModel
= 界面状态 + 用户操作

Model
= 数据结构

Service
= 业务逻辑

Repository
= 数据持久化
```

---

# 87. 一张便签对应什么

每一张便签对应一个：

```text
Note Model
```

例如：

```csharp
public sealed class Note
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public NoteColor Color { get; set; }

    public List<string> Tags { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
```

这个 Model 只描述：

```text
便签本身是什么
```

例如：

```text
标题
正文
颜色
标签
创建时间
修改时间
```

不要在 Note Model 中直接放：

```text
Window
Button
TextBox
HWND
是否获得焦点
UI 动画状态
```

---

# 88. 一张正在显示的便签对应什么

通常：

```text
一张 Note
↓
一个 NoteViewModel
↓
一个 NoteWindow
```

例如：

```text
Docker.md
↓
Note Model
↓
NoteViewModel
↓
NoteWindow.xaml
```

如果有 5 张便签显示：

```text
Note A → NoteViewModel A → NoteWindow A
Note B → NoteViewModel B → NoteWindow B
Note C → NoteViewModel C → NoteWindow C
Note D → NoteViewModel D → NoteWindow D
Note E → NoteViewModel E → NoteWindow E
```

但这里有一个重要原则：

> 所有 Note 都可以加载到内存，但只有真正显示的便签才需要创建 Window。

例如：

```text
100 个 Markdown
↓
100 个 Note Model

当前桌面显示 8 个
↓
8 个 NoteViewModel
↓
8 个 NoteWindow
```

这样可以控制窗口数量和内存占用。

---

# 89. NoteViewModel 职责

推荐：

```csharp
public partial class NoteViewModel : ObservableObject
{
    private readonly INoteService _noteService;

    public Guid Id => Model.Id;

    public Note Model { get; }

    public NoteViewModel(
        Note model,
        INoteService noteService)
    {
        Model = model;
        _noteService = noteService;
    }

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string content = string.Empty;

    [ObservableProperty]
    private bool isCollapsed;

    [ObservableProperty]
    private bool isTopMost;

    [ObservableProperty]
    private bool isLocked;
}
```

ViewModel 负责：

```text
当前 UI 显示什么
用户可以执行什么操作
```

例如：

```text
修改标题
修改正文
切换折叠
切换置顶
锁定
选择颜色
删除
隐藏
```

---

# 90. Command 设计

使用：

```text
CommunityToolkit.Mvvm
```

例如：

```csharp
[RelayCommand]
private void ToggleCollapsed()
{
    IsCollapsed = !IsCollapsed;
}
```

UI：

```xml
<Button
    Command="{Binding ToggleCollapsedCommand}"
    Content="折叠" />
```

这样 View 不需要写：

```csharp
private void Button_Click(...)
```

从而减少 code-behind。

---

# 91. View 的职责

View：

```text
NoteWindow.xaml
ManagerWindow.xaml
SettingsWindow.xaml
SearchWindow.xaml
```

主要负责：

```text
布局
样式
绑定
动画
视觉状态
```

例如：

```xml
<TextBox
    Text="{Binding Title, UpdateSourceTrigger=PropertyChanged}" />

<TextBox
    Text="{Binding Content, UpdateSourceTrigger=PropertyChanged}" />
```

View 不应该直接：

```text
写 Markdown
操作 JSON
搜索文件
删除文件
处理业务规则
```

---

# 92. Code-behind 是否完全禁止

不需要完全禁止。

允许在 View code-behind 中处理：

```text
纯 UI 行为
WPF Window 生命周期
需要直接访问 HWND 的窗口初始化
DragMove
视觉层事件
WM_NCHITTEST 接入
```

不建议在 code-behind 中处理：

```text
保存便签
删除便签
搜索
修改业务数据
文件路径规则
回收站逻辑
```

原则：

> UI 技术细节可以留在 View，业务逻辑进入 ViewModel / Service。

---

# 93. Model 与 UI 状态必须分开

推荐：

```text
Note
```

存：

```text
Title
Content
Color
Tags
```

而窗口位置使用：

```text
NoteLayout
```

例如：

```csharp
public sealed class NoteLayout
{
    public Guid NoteId { get; set; }

    public string? MonitorId { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool IsCollapsed { get; set; }

    public bool IsTopMost { get; set; }

    public bool IsLocked { get; set; }
}
```

原因：

```text
Note
= 用户数据

NoteLayout
= 当前电脑上的 UI 状态
```

两者生命周期不同。

---

# 94. AppSettings 也是 Model

例如：

```csharp
public sealed class AppSettings
{
    public string NotesFolder { get; set; } = string.Empty;

    public bool StartWithWindows { get; set; }

    public string Theme { get; set; } = "System";

    public int AutoSaveDelayMs { get; set; } = 500;

    public bool HideNotesFromTaskbar { get; set; } = true;

    public bool HideNotesFromAltTab { get; set; } = true;

    public bool KeepNotesVisibleOnShowDesktop { get; set; } = true;
}
```

所以：

```text
Model
```

不只是 Note。

本项目主要 Model 可以包括：

```text
Note
NoteLayout
AppSettings
NoteColor
TrashEntry
AttachmentInfo
```

---

# 95. 推荐 ViewModel 列表

建议：

```text
ViewModels/
├── NoteViewModel.cs
├── ManagerViewModel.cs
├── SettingsViewModel.cs
├── SearchViewModel.cs
└── TrashViewModel.cs
```

职责：

## NoteViewModel

单张便签。

## ManagerViewModel

所有便签管理。

## SettingsViewModel

设置。

## SearchViewModel

搜索。

## TrashViewModel

回收站。

---

# 96. ManagerViewModel 设计

管理器窗口可以持有：

```csharp
public partial class ManagerViewModel : ObservableObject
{
    public ObservableCollection<NoteViewModel> Notes { get; }
        = [];
}
```

WPF：

```xml
<ListBox
    ItemsSource="{Binding Notes}" />
```

当：

```text
Notes.Add(...)
Notes.Remove(...)
```

时 UI 自动更新。

---

# 97. Repository 与 Service 的区别

这是本项目中必须明确的一层。

可以简单记：

```text
Repository
= 怎么存

Service
= 要做什么
```

---

# 98. NoteRepository 职责

推荐接口：

```csharp
public interface INoteRepository
{
    Task<IReadOnlyList<Note>> LoadAllAsync();

    Task<Note?> GetAsync(Guid id);

    Task SaveAsync(Note note);

    Task DeleteAsync(Guid id);

    Task MoveAsync(
        Guid id,
        string targetFolder);
}
```

实现：

```text
MarkdownNoteRepository
```

负责：

```text
读取 .md
解析 YAML
序列化 Markdown
原子保存
文件移动
```

Repository 不负责：

```text
显示窗口
决定是否删除
弹确认框
更新任务栏
```

---

# 99. NoteService 职责

例如：

```csharp
public interface INoteService
{
    Task<Note> CreateNoteAsync();

    Task SaveNoteAsync(Note note);

    Task DeleteNoteAsync(Guid noteId);

    Task RestoreNoteAsync(Guid noteId);

    Task MoveNoteAsync(
        Guid noteId,
        string folder);

    Task ChangeColorAsync(
        Guid noteId,
        NoteColor color);
}
```

Service 负责业务规则：

```text
新建便签时生成 Guid
选择默认颜色
生成文件
删除时进入回收站
修改后更新时间
更新内存仓库
触发事件
```

---

# 100. 完整调用链示例：修改便签内容

```text
用户输入
↓
NoteWindow TextBox
↓
Binding
↓
NoteViewModel.Content
↓
AutoSave debounce
↓
NoteService.SaveNoteAsync
↓
MarkdownNoteRepository.SaveAsync
↓
AtomicFileWriter
↓
xxx.md
```

UI 不知道 Markdown 怎么保存。

Repository 也不知道 TextBox 是什么。

---

# 101. 完整调用链示例：修改颜色

```text
用户点击黄色
↓
SetColorCommand
↓
NoteViewModel
↓
NoteService.ChangeColorAsync
↓
更新 Note Model
↓
Repository.SaveAsync
↓
Markdown Front Matter
```

最终：

```yaml
color: yellow
```

View 根据：

```text
NoteViewModel.Color
```

自动切换主题。

---

# 102. 完整调用链示例：删除便签

推荐：

```text
用户点击删除
↓
DeleteCommand
↓
NoteViewModel
↓
NoteService.DeleteNoteAsync
↓
TrashService
↓
Markdown 文件移动到 .pinslip/trash
↓
内存 Repository 移除
↓
事件通知
↓
WindowManager 关闭对应窗口
↓
ManagerViewModel 更新列表
```

不要：

```text
DeleteButton_Click
↓
File.Delete(...)
```

---

# 103. 完整调用链示例：打开便签

```text
ManagerWindow
↓
用户双击 Note
↓
OpenCommand
↓
ManagerViewModel
↓
WindowManager.ShowNote(noteId)
↓
获取 Note
↓
获取 NoteLayout
↓
创建 NoteViewModel
↓
创建 NoteWindow
↓
设置 DataContext
↓
恢复窗口位置
↓
Show()
```

---

# 104. WindowManager 与 MVVM 的关系

WindowManager 不属于普通 Model。

它属于：

```text
Service
```

负责：

```text
窗口创建
窗口销毁
HWND
任务栏行为
Alt+Tab
Win+D
TopMost
窗口坐标
DPI
多屏
```

推荐：

```csharp
public interface IWindowManager
{
    void ShowNote(Guid noteId);

    void HideNote(Guid noteId);

    void CloseNote(Guid noteId);

    void ShowAllNotes();

    void HideAllNotes();

    void SetTopMost(Guid noteId, bool value);
}
```

不要把 Win32 调用塞到：

```text
NoteViewModel
```

否则 ViewModel 会被 Windows API 污染。

---

# 105. 推荐 WindowManager 内部结构

```text
WindowManager
│
├── Dictionary<Guid, NoteWindow>
│
├── LayoutService
│
├── WindowStyleManager
│
├── ShowDesktopManager
│
├── SnapManager
└── MonitorManager
```

例如：

```text
Guid
↓
找到 NoteWindow
↓
获取 HWND
↓
调用 WindowStyleManager
```

---

# 106. ViewModel 不直接持有 WPF Window

推荐避免：

```csharp
public Window Window { get; set; }
```

ViewModel 应尽量保持：

```text
不知道自己由哪个 Window 显示
```

如果需要：

```text
关闭窗口
```

不要：

```csharp
Window.Close();
```

而是调用：

```text
IWindowManager
```

或者通过：

```text
事件 / Messenger
```

请求关闭。

---

# 107. Messenger 是否需要

CommunityToolkit.Mvvm 提供：

```text
WeakReferenceMessenger
```

可以使用，但不要滥用。

适合：

```text
NoteDeletedMessage
SettingsChangedMessage
ThemeChangedMessage
```

但普通一对一调用仍优先使用：

```text
Service
```

避免最后所有逻辑都变成：

```text
Send Message
Receive Message
```

难以追踪。

---

# 108. 推荐事件设计

Core 可以定义：

```text
NoteCreated
NoteChanged
NoteDeleted
NoteMoved
SettingsChanged
```

例如：

```csharp
public sealed record NoteChangedEvent(Guid NoteId);
```

用途：

```text
Repository / Service
↓
事件
↓
ManagerViewModel
↓
刷新列表
```

---

# 109. 自动保存应该放在哪里

不要在 View 中写 Timer。

建议：

```text
NoteViewModel
↓
通知 AutoSaveService
```

AutoSaveService：

```text
按 NoteId debounce
```

例如：

```text
500ms 无新输入
↓
NoteService.Save
```

这样：

```text
TextBox
```

只负责绑定。

---

# 110. 推荐 AutoSaveService

伪结构：

```csharp
public interface IAutoSaveService
{
    void ScheduleSave(Guid noteId);
}
```

内部：

```text
Dictionary<Guid, CancellationTokenSource>
```

每张便签独立 debounce。

这样多个便签同时编辑不会互相影响。

---

# 111. 外部文件变化时 MVVM 如何更新

流程：

```text
FileSystemWatcher
↓
FileWatchService
↓
MarkdownNoteRepository Reload
↓
NoteService 更新 Model
↓
NoteChanged event
↓
对应 NoteViewModel 更新属性
↓
PropertyChanged
↓
WPF 自动刷新
```

不要让：

```text
FileSystemWatcher
```

直接操作 TextBox。

---

# 112. 推荐内存数据层

可以有一个：

```text
NoteStore
```

例如：

```csharp
public sealed class NoteStore
{
    private readonly Dictionary<Guid, Note> _notes = [];

    public IReadOnlyCollection<Note> Notes => _notes.Values;

    public Note? Get(Guid id);

    public void Add(Note note);

    public void Update(Note note);

    public void Remove(Guid id);
}
```

作用：

```text
当前运行时所有 Note 的内存状态
```

它不是数据库。

---

# 113. Repository 与 Store 的关系

推荐：

```text
启动：
Repository
↓
Load Markdown
↓
NoteStore

运行时：
NoteService
↓
修改 NoteStore
↓
Repository 保存

外部文件：
FileWatcher
↓
Repository
↓
更新 NoteStore
```

---

# 114. 推荐核心依赖关系

非常重要：

```text
View
    ↓
ViewModel
    ↓
Service
    ↓
Core Models / Store
    ↓
Repository
```

而：

```text
Repository
```

不能反向依赖：

```text
View
ViewModel
Window
```

---

# 115. 推荐项目引用方向

建议：

```text
StickyNotes.Core
↑
StickyNotes.Infrastructure
↑
StickyNotes.App
```

其中：

## Core

不知道 WPF。

不知道 Win32。

不知道 Markdown 具体实现。

## Infrastructure

实现：

```text
Markdown
JSON
FileSystem
Win32
```

## App

实现：

```text
WPF
View
ViewModel
Application lifecycle
```

---

# 116. 实际项目引用建议

```text
StickyNotes.Core
    无项目依赖

StickyNotes.Infrastructure
    → StickyNotes.Core

StickyNotes.App
    → StickyNotes.Core
    → StickyNotes.Infrastructure

StickyNotes.Tests
    → StickyNotes.Core
    → StickyNotes.Infrastructure
```

---

# 117. 不建议过度 MVVM

本项目虽然使用 MVVM，但不要为了“架构纯洁”增加大量无意义类。

例如一个只有：

```text
Name
Color
```

的小对象没必要：

```text
Model
DTO
Entity
DomainModel
ViewModel
Adapter
Mapper
```

重复五遍。

原则：

> 有明确职责差异才拆。

---

# 118. 推荐简化后的实际结构

第一版可以控制在：

```text
Models
├── Note
├── NoteLayout
└── AppSettings

ViewModels
├── NoteViewModel
├── ManagerViewModel
└── SettingsViewModel

Services
├── NoteService
├── WindowManager
├── LayoutService
├── SearchService
├── AutoSaveService
└── FileWatchService

Storage
├── MarkdownNoteRepository
├── JsonSettingsStore
└── JsonLayoutStore

Windows
├── NativeMethods
├── WindowStyleManager
└── ShowDesktopManager
```

已经足够。

---

# 119. MVVM 与本项目的最终映射

可以直接记住这张关系：

```text
Markdown File
     ↓
MarkdownNoteRepository
     ↓
NoteStore
     ↓
Note Model
     ↓
NoteService
     ↓
NoteViewModel
     ↓
NoteWindow
```

窗口系统是另一条线：

```text
NoteWindow
     ↓
WindowManager
     ↓
WindowStyleManager
     ↓
Win32 / HWND
```

两条线在：

```text
NoteId
```

处关联。

---

# 120. 最终架构示意

```text
                           ┌───────────────────┐
                           │     WPF View      │
                           │    NoteWindow     │
                           └─────────┬─────────┘
                                     │ Binding
                           ┌─────────▼─────────┐
                           │   NoteViewModel   │
                           └─────────┬─────────┘
                                     │
                           ┌─────────▼─────────┐
                           │    NoteService    │
                           └──────┬─────┬──────┘
                                  │     │
                         ┌────────▼─┐ ┌─▼───────────┐
                         │ NoteStore │ │ AutoSaveSvc │
                         └────┬──────┘ └─────┬──────┘
                              │              │
                         ┌────▼──────────────▼───┐
                         │ MarkdownNoteRepository │
                         └───────────┬────────────┘
                                     │
                              ┌──────▼──────┐
                              │   *.md      │
                              └─────────────┘


NoteWindow
    │
    └─────────────→ WindowManager
                        │
             ┌──────────┼──────────┐
             ↓          ↓          ↓
       WindowStyle   ShowDesktop   Snap
             │          │          │
             └──────────┼──────────┘
                        ↓
                    Win32 API
```

---

# 121. MVVM 最重要的几条规则

开发时建议始终遵守：

```text
1. Note Model 不引用 WPF。
2. ViewModel 不直接读写 Markdown。
3. View 不直接操作业务文件。
4. Repository 不知道 Window。
5. WindowManager 不负责 Note 内容。
6. Win32 调用集中封装。
7. ViewModel 通过 Service 执行业务。
8. UI 通过 Binding 和 Command 驱动。
9. 机器 UI 状态使用 NoteLayout，不污染 Note。
10. 所有层尽量使用 NoteId 关联，而不是彼此强引用。
```

这样项目以后功能增加时仍然容易维护。

---

# 122. 对本项目的具体推荐

最终建议正式采用：

```text
C#
.NET
WPF
MVVM
CommunityToolkit.Mvvm

+
Service Layer
+
Repository Pattern
+
NoteStore
```

这里不需要把它做成大型企业架构。

目标是：

```text
结构清楚
依赖方向明确
UI 与业务分离
Win32 与数据分离
但类数量不过度膨胀
```

这与项目“轻量化”的目标并不冲突。

相反，它可以避免以后出现：

```text
NoteWindow.xaml.cs
几千行
```

这种难以维护的情况。
