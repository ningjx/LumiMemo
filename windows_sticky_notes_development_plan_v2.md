# Windows 轻量桌面便签开发方案 v2

> 技术栈：**C# + .NET + WPF + Win32 P/Invoke**
> 数据方案：**Markdown 文件为唯一真实数据源（Source of Truth）**
> 目标平台：**Windows 10 (1809+) / Windows 11**
> 产品定位：类似 Microsoft Sticky Notes 的轻量桌面便签，但更强调 Markdown、文件可控、本地优先、窗口行为可定制。

---

# 0. 关于本文档

## 0.1 版本说明

本文档是 v1（`windows_sticky_notes_development_plan.md`，4388 行 / 122 小节）的修订版。v1 的内容方向正确，但存在三类问题：前后章节对同一事物给出不同定义、若干关键决策留成"开发时再定"、以及一批在实际实现中必然踩到的坑没有覆盖。

v2 的处理方式：

| 处理 | 说明 |
|---|---|
| **合并** | v1 的 §1–§85 与 §86–§122 有明显的两套内容（后者是追加的 MVVM 补充）。v2 按主题重新组织为 §0–§24，**每个概念只有一处权威定义**，不再出现平行章节 |
| **定死** | v1 中所有"开发时根据…决定"的悬置项，v2 全部给出结论并写明理由；确实需要后续决策的集中在 §0.4 |
| **补全** | 补齐 v1 缺失的数据模型字段、流程规格、边界条件与降级行为 |
| **纠错** | 修正 v1 中会导致实现出错的示例（附件相对路径、Design Token 类型等） |

v1 → v2 的逐节对照见 [附录 A](#附录-a-v1--v2-章节对照)，评审中提出的问题在 v2 中的落点见 [附录 B](#附录-b-评审问题在-v2-中的落点)。

**唯一权威原则**：v2 生效后，v1 作废。两份文档冲突时以 v2 为准，不要在两份之间来回对照。

---

## 0.2 术语表

本文档统一使用以下术语，避免 v1 中"关闭/隐藏/删除/回收站/回收区"混用造成的歧义。

| 术语 | 英文 / 代码标识 | 含义 |
|---|---|---|
| **便签** | Note | 一条笔记数据，对应磁盘上一个 `.md` 文件。**便签是一个数据概念，不等于窗口** |
| **便签窗口** | NoteWindow | 桌面上显示某个便签的那个窗口。一张便签最多有一个便签窗口 |
| **笔记目录** | NotesFolder | 用户在设置里指定的、存放全部便签 Markdown 文件的目录 |
| **关闭便签** | Close | 销毁便签窗口（窗口对象可回池复用），便签数据保留在同一位置。**不等于删除**。见 §17.3 |
| **隐藏便签** | Hide | 保持便签窗口实例存在但不可见（托盘菜单的"隐藏全部便签"用的就是这个）。v1 曾把"关闭"和"隐藏"混为一谈，v2 只用"关闭"表达用户意图，不提供"隐藏"这个用户操作 |
| **删除便签** | Delete | 把 Markdown 文件移到回收站目录 |
| **回收站** | Trash | 笔记目录下的 `.lumimemo/trash/` 目录 |
| **管理器** | ManagerWindow | 用来搜索、浏览、整理的普通应用窗口（见 §15.8） |
| **速记浮窗** | QuickCaptureWindow | 全局快捷键呼出的临时输入浮窗（见 §15.7） |
| **消息窗口** | MessageWindow | 一个 `HWND_MESSAGE` 消息专用窗口，用于接收 `WM_HOTKEY`。**不是隐藏的顶层窗口**——v2 没有宿主窗口，理由见 §13.10 |
| **设备状态** | DeviceState | 窗口坐标、折叠、置顶等"只对当前这台电脑有意义"的状态 |
| **用户数据** | UserData | 便签正文、标签、颜色等"跟着用户走"的内容 |

---

## 0.3 与 PinSlip 的功能差异清单

本项目参考 [homerious/pinslip](https://github.com/homerious/pinslip) 的产品形态，但技术栈与数据布局完全不同（pinslip 是 Electron + Go Service，本项目是单进程 WPF）。为避免"这是漏了还是故意不做"的反复讨论，此处一次性列清。

### 已纳入本方案

阶段列必须与 §22 的路线图一致，两处若不同以 §22 为准。

| 功能 | 阶段 | 出处 |
|---|---|---|
| Markdown 便签 + 自动保存 | 一期 | §11、§16.1 |
| 多便签窗口、自定义颜色、可 resize | 一期 | §13 |
| 折叠成标题条（含 `ExpandedHeight`） | 一期 | §15.2 |
| 任务框点击打勾 | 一期 | §15.6 |
| 标签、文件夹、搜索（含排序与摘要） | 一期 | §5.7、§5.8、§12、§15.8 |
| 回收站（含保留策略与索引重建） | 一期 | §7 |
| 始终置顶、锁定 | 一期 | §13.5、§13.7 |
| 全局快捷键 + **速记浮窗** | 一期 | §15.7、§17.6 |
| 外部文件监听 | 一期 | §10 |
| **内容缩放（A+/A−）** | **已剔除** | §15.5（已作废） |
| 多显示器与 DPI 变化的正确处理 | 一期 | §13.8、§14.5 |
| 图片附件 | 一期 | §6.1–§6.3 |
| 深色模式跟随系统 | **推迟到界面重构那一轮** | §15.3 |
| Markdown 预览 | 二期 | §16.7 |
| 编辑器交互助手（列表延续、Tab 缩进） | 二期 | §16.2 |
| 外部冲突的差异视图 | 二期 | §11.4 |
| 崩溃恢复 | 二期 | §11.6 |
| 附件孤儿清理 | 二期 | §6.4 |
| 鼠标穿透 | 二期（**取决于原型 P2**） | §13.7、§22.2 |
| 笔记目录切换 | 二期 | §8.6 |
| 便签组（"小分队"） | 三期（可选） | §15.11、§22.4 |
| 批量操作、列表内拖拽排序 | 三期 | §22.4 |
| 多语言（i18n） | 三期 | §22.4 |

### 本轮拍板的三项范围裁决

功能开发收尾阶段逐条定下，**推翻本表与下文几处的原始写法**：

| 裁决 | 内容 |
|---|---|
| **内容缩放整条剔除** | 「内容缩放（A+/A−）」不需要，**整个功能删掉**，不是推迟。为什么不是推迟：它已经在 `NoteLayout` / `AppSettings` / layout.json / settings.json 里占了好几个字段，而「每张便签各自记忆」这一条把它绑进了布局持久化——留着一个没有入口的字段，等于给后来的人留一个「文档说要做」的假线索。代码里对应的四处已一并删除，详见 §15.5 |
| **退出失败提示剔除** | 保存一张便签很快，没有必要为它挡一次退出再加一个三选一对话框。退出只做 flush，失败写一条 Error 日志，详见 §11.5 |
| **深色模式与一切样式内容推迟** | 深色模式不是独立功能，它是「界面整体改成透明毛玻璃」的一部分——配色、每色深色变体、`DynamicResource` 都取决于毛玻璃之后纸面的颜色怎么定，现在做等于白干。**所有样式相关的工作（含 §15.3 的深色变体、§15.10 的 `Colors.xaml` 深色套）统推到界面重构那一轮**，详见 §15.3 |

### 明确不做（且在架构上不预留）

| 功能 | 理由 |
|---|---|
| Git 同步 / 云同步 | 用户明确不需要。Markdown 是纯文本，用户可用任何同步工具自行处理（外部同步的注意事项见 §10.5） |
| MCP Server / localhost API | 违反"无后台服务、无 HTTP、无 IPC"的核心原则（§2.3）。未来若要做，应作为**独立的可选进程**，不污染主程序架构 |
| 浏览器插件 | 同上，且需要服务端接口 |
| 便签之间的磁吸/互相吸附对齐 | 需要一套便签间的几何约束求解，而"贴到屏幕边缘"由系统通过 Aero Snap 免费提供（§13.3）。收益不抵复杂度 |
| 插件系统、扩展 API | 无需求 |
| 移动端、AI 功能、协作/分享、便签加密、主题商店、自动更新、全局置顶、任务栏按钮开关 | 本表只列**影响架构选型**的几项；功能层面的其余项见 §22.5，各项理由见该表引用的章节 |
| 便携模式（portable 部署） | **本表不收**——它属于"v2 不做但架构上预留"（`IAppPaths`），见 §8.1、§22.5、§23.2 |

### 有意与 PinSlip 不同

| 项 | PinSlip | 本项目 | 理由 |
|---|---|---|---|
| 是否显示任务栏按钮 | 每张便签都有 | **都显示，且不提供开关** | 便签是用户要反复切换过去的窗口，Alt+Tab 能找到比"任务栏干净"更重要（§13.4） |
| 「显示桌面」时便签的行为 | 保留（Electron 窗口的默认行为） | **未置顶的照样留在屏幕上**（`restoreAfterShowDesktop`，默认开）；置顶的系统根本不动它 | 便签有 owner，"显示桌面"本就不会最小化它，只是被升起的桌面盖住；盖住它的那张桌面在置顶档上，所以便签也得临时上到同一档才压得住——"豁免"仍然只有 `WS_EX_TOPMOST` 一条路。**Win+D 与点任务栏右下角那个按钮是两条不同的前台序列**，详见 §13.6 |
| 笔记目录层级 | `notes/` 子目录 | 便签直接放在笔记目录下 | 用户选择 Obsidian vault 时层级更自然（§5.7） |
| 标题 | 从正文第一行派生 | 同左（v2 采纳） | 与 Obsidian 一致，无需同步两处（§5.4） |
| 快捷捕捉键 | `Ctrl+Shift+N` | **`Ctrl+Shift+N`（与 PinSlip 相同，可改）** | 与 PinSlip 保持一致的肌肉记忆；显示全部的键用 `Ctrl+Alt+N`（§17.6） |

---

## 0.4 待决事项

v2 已把 v1 中的悬置项全部定死。以下是**仅剩的五项**，每项都标注了截止点——到点必须定，否则会阻塞后续工作。**除这五项外，v2 中的所有设计选择均为结论，不再是待决项。**

| # | 事项 | 已有倾向 | 截止点 |
|---|---|---|---|
| D-1 | 是否提供"文件名跟随标题自动重命名" | 提供，但**默认关闭**（§5.6） | 二期开始前。影响 watcher 的 `Renamed` 处理与用户预期 |
| D-2 | 是否额外发布 framework-dependent 精简版（约 5MB） | 主发布 self-contained（约 80MB）；精简版作为可选项（§23.1） | 一期打包前 |
| D-3 | 是否采购代码签名证书 | 不阻塞一期；对外分发前需要（§23.4） | 一期发布后、对外分发前 |
| D-4 | 启动时锁定的具体 .NET LTS 版本号 | 项目启动时最新的稳定 LTS（§2.1） | 写第一行代码之前 |
| D-5 | `maxOpenWindows` 超过上限时怎么处置（提示什么、在哪一步拦） | **文档断链**：§8.2 的字段表写着"见 §13.9"，而 §13.9 通篇没提这个字段 | 一期接上窗口池的上限逻辑之前 |

**D-4 不是设计问题，是时间问题**——启动当天查一下最新的 LTS 版本号即可，列在这里只是提醒不要在实现中途升级目标框架。

**D-5 是这一轮才发现的**：`AppSettings.MaxOpenWindows` 默认 50，字段表把处置方式指向 §13.9，但 §13.9 只讲了「不为每一张便签创建窗口」的实现约束，没有任何一句说超限时该拦在哪一步、该提示什么、是拒绝新开还是先关最旧的。目前实现里**没有任何一处读它**（不阻塞，先按 50 这个值不动）。

---

# 1. 项目目标与范围

## 1.1 产品定位

一个常驻 Windows 桌面的轻量便签程序。用户随手记下东西，它安静地待在桌面上；所有内容都是用户自己目录里的普通 Markdown 文件，任何 Markdown 编辑器都能打开。

## 1.2 核心目标

**数据**

- 每个便签对应笔记目录下的一个 Markdown 文件，文件格式不加密、不加私有结构
- 用户可指定笔记目录，可随时更换
- 不依赖数据库作为主数据源
- 支持外部编辑器（VS Code、Obsidian、Typora）直接修改，程序能感知并同步

**窗口行为**

- 每张便签一个独立桌面窗口，各自记忆位置、尺寸、折叠与缩放状态
- 任务栏与 Alt+Tab 中都能找到打开的便签（见 §13.4 的取舍说明）
- `Win + D`（点任务栏右下角的"显示桌面"按钮同理）时**未置顶的便签照样留在屏幕上**：便签有 owner，系统本就不会把它最小化，只是被升起的桌面盖住；桌面升到置顶档的同时便签也被临时提到那一档，桌面退下去时再撤回（设置项 `restoreAfterShowDesktop` 默认开）；托盘菜单与 `Ctrl+Alt+N` 作为兜底（见 §13.6）
- 普通应用窗口可以覆盖便签，默认不永久置顶
- 提供可选"始终置顶"——z 序上盖住普通窗口，而且"显示桌面"全程不去碰它，因此是**唯一不被打扰**的那一档（见 §13.6）
- 多显示器、显示器增减、DPI 缩放变化下位置与尺寸都保持合理（§13.8）

**工程**

- 单进程，无后台服务、无子进程、无 HTTP；唯一的进程间通信是单实例互斥与通知（§17.2）
- 空闲时 CPU 接近 0，全事件驱动，禁止轮询
- 用户数据与设备状态严格分离

## 1.3 明确不做

见 §0.3 的两张表。此外以下事项也明确不做：

- 不做多用户 / 团队协作
- 不做加密笔记（用户可以自己用 BitLocker / VeraCrypt）
- 不做富文本所见即所得编辑（理由见 §16.1）
- 不做插件系统

## 1.4 支持矩阵

| 维度 | 支持范围 | 备注 |
|---|---|---|
| 操作系统 | Windows 10 1809+ / Windows 11 | 视觉上会有差异，见 §13.2 |
| 架构 | x64 优先，ARM64 视发布需要 | §23 |
| 显示器 | 单屏、多屏、混合 DPI、热插拔 | §13.8 |
| 虚拟桌面 | 跟随系统默认行为（便签属于创建它的那个虚拟桌面），**v2 不做额外处理**（不主动跟随切换、不跨桌面复制） | — |
| 文件系统 | 本地 NTFS 优先 | 网络盘 / 云目录有限制，见 §10.5 |

---

# 2. 技术选型

## 2.1 核心技术栈

```text
语言        C#
运行时      .NET 10 LTS（或项目启动时最新的稳定 LTS）
桌面框架    WPF
本地互操作  Win32 P/Invoke（通过 CsWin32 生成）
架构模式    MVVM + Service + Repository
```

**.NET 版本策略**：以项目正式启动（Step 1）时最新的**稳定 LTS** 为准。WPF 的 Per-Monitor V2 DPI 支持要求 .NET Core 3.0 以上，本项目无此顾虑。不使用 preview / RC 版本作为基础目标框架。

## 2.2 依赖清单

每一项都写明**用途**和**选它的理由**，避免后来者替换时不知道代价。

### CommunityToolkit.Mvvm

用途：`ObservableObject`、`[ObservableProperty]`、`[RelayCommand]`、`WeakReferenceMessenger`。

理由：源码生成器（source generator）实现，无运行时反射开销，对这个"窗口多、绑定多"的项目很重要。

```xml
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
```

### Markdig

用途：Markdown 解析（生成 AST）与渲染、扩展语法（任务列表）。

理由：本项目**只需要解析**——从正文提取标题、重写图片链接、渲染预览。Markdig 的 AST 可以直接遍历，不必自己写正则。

必开配置：

```csharp
var pipeline = new MarkdownPipelineBuilder()
    .UseTaskLists()          // 任务框
    .UseAutoLinks()
    .DisableHtml()           // 安全：禁止正文内嵌原始 HTML
    .Build();
```

**注意**：`DisableHtml()` 是安全要求（§19），不是可选项。

### YamlDotNet

用途：解析与序列化 Markdown 文件开头的 YAML Front Matter。

理由：Front Matter 是用户可手改的，解析器必须容忍格式瑕疵并按 §5.10 降级，而不是抛异常。

**不要**用 YamlDotNet 序列化 `AppSettings` / `NoteLayout`——那是 JSON 的事（§8.4）。

### System.Text.Json

用途：`settings.json`、`layout.json`、`trash-index.json`。

理由：.NET 自带，无额外依赖。命名策略见 §8.4。

### Microsoft.Extensions.Logging

用途：日志抽象。

实现：**自己写一个按天滚动的文件 Provider**（约 150 行），不引 Serilog。

理由：为一个日志库引入一整套 sink 依赖，与"轻量"目标不符。日志量很小（§21.1 只记异常和生命周期事件），自写滚动器足够。

### Microsoft.Extensions.DependencyInjection

用途：服务组装。

理由：只用来在 `App.OnStartup` 里注册服务与解析根对象。**不使用 Generic Host / IHostedService**——那套生命周期会与 WPF 的 `Application` 生命周期打架（§4.3）。

### Microsoft.Windows.CsWin32

用途：自动生成 Win32 P/Invoke 声明。

理由：手写 `[DllImport]` 的签名错误是这个项目最容易出、也最难查的一类 bug（`SetWindowLongPtr` 在 32/64 位下的名字差异、`DwmSetWindowAttribute` 的参数类型等）。CsWin32 从元数据生成，类型安全。

维护方式：项目根放一个 `NativeMethods.txt` 列出需要的 API（见 [附录 D](#附录-d-win32-api-清单)）。

### H.NotifyIcon.Wpf

用途：托盘图标。

理由：WPF 没有原生托盘控件。备选是 `System.Windows.Forms.NotifyIcon`，但那条路要在 csproj 打开 `UseWindowsForms`，把第二套 UI 框架和消息循环拖进来，且难以做出符合 §15 设计风格的菜单。

### 不引入的依赖

- 任何 MVVM 框架（Prism、Caliburn 等）：CommunityToolkit.Mvvm 已足够，见 §18.6
- 任何 ORM
- 任何 UI 控件库（MahApps、HandyControl 等）：本项目 UI 很轻，自己写样式比接管第三方主题的成本更低，也更容易控制"低干扰"的视觉目标（§15.1）

## 2.3 明确不采用的技术

```text
SQLite / EF Core        第一版不引入，见 §12.4
Electron / Node.js      与目标平台和"单进程低占用"冲突
React / Tauri           Web 技术栈，本项目是原生 WPF
Go / Rust 后台服务      不需要多进程
HTTP Server / localhost API / MCP Server
Git Sync / Cloud Sync
独立的原生 Service
Generic Host / IHostedService
```

设计原则：

> **只有实际需求出现时才增加基础设施。**

这条原则在 v1 中已经写对，v2 继续保留，并加一条更具体的判据：**如果一个新功能需要引入一个新进程、一个 HTTP 服务、或一个数据库，那么它的默认答案是不做**，除非能证明用现有的文件 + 事件机制无法实现。

---

# 3. 总体架构

## 3.1 分层与依赖方向

**这是本文档唯一的架构权威图**，其他章节只描述细节，不再重复画分层。

```text
┌─────────────────────────────────────────────────────┐
│  LumiMemo.App          （引用 WPF）                 │
│                                                     │
│  Views/            NoteWindow, ManagerWindow,       │
│                    SettingsWindow,                  │
│                    QuickCaptureWindow, Dialogs/     │
│  ViewModels/       NoteViewModel, ManagerViewModel… │
│  Services/         WindowManager, AutoSaveService,  │
│                    FileWatchService, SearchService… │
│  Windowing/        WindowStyleManager, DwmInterop,  │
│                    DisplayChangeWatcher,            │
│                    MessageWindow                    │
└───────────────────────┬─────────────────────────────┘
                        │ 依赖
┌───────────────────────▼─────────────────────────────┐
│  LumiMemo.Core         （不引用 WPF、不引用 Win32） │
│                                                     │
│  Models/           Note, NoteLayout, AppSettings,   │
│                    TrashEntry, QuickCapture,        │
│                    NoteColor, AttachmentRef         │
│  Stores/           NoteStore                        │
│  Services/         NoteService, LayoutService,      │
│                    SettingsService, TrashService,   │
│                    TitleDeriver                     │
│  Abstractions/     INoteRepository, IFileWatcher,   │
│                    INoteService, ISettingsStore,    │
│                    ILayoutStore, ITrashStore,       │
│                    IAttachmentStore, IAppPaths,     │
│                    IWindowManager, IClock           │
│  Events/           NoteCreated / Changed / Deleted… │
└───────────────────────┬─────────────────────────────┘
                        │ 实现
┌───────────────────────▼─────────────────────────────┐
│  LumiMemo.Infrastructure （不引用 WPF）             │
│                                                     │
│  Storage/          MarkdownNoteRepository,          │
│                    FrontMatterParser / Serializer,  │
│                    LinkRewriter, AtomicFileWriter   │
│  Settings/         JsonSettingsStore,               │
│                    JsonLayoutStore                  │
│  Trash/            FileSystemTrashStore             │
│  Attachments/      FileSystemAttachmentStore,       │
│                    OrphanScanner                    │
│  Watching/         FileSystemWatcherAdapter         │
│  Windows/          NativeMethods(CsWin32),          │
│                    MonitorEnumerator, DpiMath,      │
│                    LayoutMath                       │
│  Logging/          RollingFileLoggerProvider        │
│  Io/               AppPaths, SystemClock, LongPath  │
└───────────────────────┬─────────────────────────────┘
                        │ 读写
                  笔记目录 *.md / *.json / NTFS
```

**三条不可违反的依赖规则**：

1. `Core` 不引用 WPF、不引用 Win32、不引用任何具体存储实现。它只定义模型、内存状态、业务规则和抽象接口。
2. `Infrastructure` **不引用 WPF**。它只做 IO 与 P/Invoke。涉及 `System.Windows.Window` 的代码一律属于 `App`。
3. 依赖方向只能向下。`Infrastructure` 不知道 `App` 存在。

> **为什么要把 WindowManager 放在 App 而不是 Infrastructure**：v1 把 `WindowStyleManager` / `ShowDesktopManager` 放在 Infrastructure，但它们必须操作 `System.Windows.Window` 和 `HwndSource`。放 Infrastructure 会迫使 Infrastructure 引用 WPF，违反规则 2。v2 明确：**`IWindowManager` 的接口在 Core（方法签名只用 Guid 和原始类型），实现和它的 WPF 辅助类都在 App**。

## 3.2 各层职责边界

| 层 | 负责 | 不负责 |
|---|---|---|
| View | 布局、样式、绑定、动画、视觉状态 | 不读写文件、不做业务判断（§18.6） |
| ViewModel | 界面状态 + 用户操作编排 | 不直接读写 Markdown、不持有 Window、不做 IO |
| Core.Services | 业务规则（何时保存、何时删、标题怎么算、ID 怎么生成） | 不碰 UI、不碰 HWND、不做具体 IO |
| Core.Stores | 运行时内存状态（NoteStore） | 不做 IO、不发事件 |
| Core.Abstractions | 接口定义 | 无实现 |
| Infrastructure | 具体 IO、解析、P/Invoke、日志 | 不含业务规则、不含 UI |
| App.Services | 窗口生命周期、进程级编排 | 不直接读写 Markdown（经由 Core.Services） |

## 3.3 三条权威数据流

v1 在 §111、§113、§119 给了三条互相矛盾的数据流。v2 只保留以下三条，**全文除此之外不再出现其他流向描述**。

### 流 1：用户编辑（UI → 磁盘）

```text
用户输入
  ↓
NoteWindow 的 TextBox（Binding, UpdateSourceTrigger=PropertyChanged）
  ↓
NoteViewModel.Content
  ↓
AutoSaveService.ScheduleSave(noteId)          ← 去抖 500ms，按 noteId 独立计时
  ↓
NoteService.SaveNoteAsync(noteId)
  ↓  1. 从 NoteStore 取出当前 Note
  ↓  2. 更新 UpdatedAt
  ↓  3. NoteRepository.SaveAsync(note)
  ↓  4. 记录本次写入的 content hash（§10.3）
  ↓
AtomicFileWriter → 笔记目录/xxx.md
```

### 流 2：外部文件变化（磁盘 → UI）

```text
外部编辑器保存文件
  ↓
FileSystemWatcher（线程池线程）
  ↓
FileWatchService：按路径去抖 300ms
  ↓  比对最近写入 hash 集合 → 是自己写的就丢弃（§10.3）
  ↓
Dispatcher.InvokeAsync → 切到 UI 线程（§3.4）
  ↓
NoteRepository.ReloadAsync(path) → 解析出 Note
  ↓
NoteStore.Update(note)        ← 唯一的写入点
  ↓
NoteChanged 事件（Core.Events）
  ↓
NoteService 的订阅者：应用业务规则（颜色变了？标题变了？）
  ↓
NoteViewModel 更新属性 → PropertyChanged → WPF 绑定刷新
```

### 流 3：用户从管理器打开便签（UI → 新窗口）

```text
ManagerWindow 双击某条便签
  ↓
ManagerViewModel.OpenNoteCommand
  ↓
INoteService.OpenNote(noteId)                ← Core 层：只做业务判断
  ↓  1. NoteStore.TryGet(noteId) → 不存在则返回 null，流程到此为止
  ↓  2. LayoutStore.GetOrCreate(noteId) → layout
  ↓  3. layout.IsOpen = true
  ↓  4. 返回 NoteOpenRequest(note, layout)
  ↓
NoteViewModelFactory.Create(note, layout)    ← App 层：唯一的 ViewModel 构造点
  ↓
IWindowManager.ShowNote(vm, layout)          ← App 层：只有它碰窗口对象
  ↓  1. 已有窗口？→ 激活并返回
  ↓  2. _pool.Rent(vm) / new NoteWindow { DataContext = vm }
  ↓  3. PlaceWindow（DPI 缩放 + 工作区夹取，§13.8）
  ↓  4. ApplyVisualState（折叠 / 置顶 / 锁定）
  ↓  5. window.Show() → DWM 圆角与阴影
  ↓
LayoutStore.MarkDirty() → 去抖后写 layout.json
```

**为什么第 4 步和 `ShowNote` 不在 `OpenNote` 里面**：`INoteService` 在 Core，`NoteViewModelFactory` 与 `WindowManager` 在 App。Core 不能依赖 App（§3.1 规则 1），而让两边互相依赖又会形成构造循环（§14.1）。所以这一步由 **App 层的发起方**（这里是 `OpenNoteCommand`）串起来——它是 App 层的命令，天然同时看得见 Core 的接口和 App 的实现。

**关键约束**（这一条修正了 v1 §103 里"WindowManager 创建 NoteViewModel"的分层错误）：

```text
WindowManager        ✗→ 创建 ViewModel
NoteViewModel        ✗→ 创建 Window
NoteViewModelFactory ←  两者唯一的交汇点，只在 App 层
IWindowManager       ←  只认构造好的 (NoteViewModel, NoteLayout) 二元组
```

**WindowManager 的接口里没有 `Register` 方法**——它自己维护 `Guid → NoteWindow` 的映射（§14.3），外部不需要、也不应该插手。`LayoutService.ApplyToWindow` 这个 API 在 v2 中不存在：窗口的摆位是 `ShowNote` 内部的步骤（§14.2）。

## 3.4 线程模型

这是 v1 完全缺失、但实现时必然遇到的部分。**规则如下，无例外**：

| 规则 | 说明 |
|---|---|
| **T1** | `NoteStore` 的所有写入只能在 UI 线程执行 |
| **T2** | `NoteStore.Snapshot()` 返回缓存的只读数组，不返回活引用、也不每次分配新数组（§9.1） |
| **T3** | `FileSystemWatcher` 的事件在线程池线程上到达，**必须先 `Dispatcher.InvokeAsync` 切到 UI 线程**，之后才能碰 Store / ViewModel / ObservableCollection |
| **T4** | `AutoSaveService` 用 `DispatcherTimer`（而非 `System.Timers.Timer`），从根源上避免跨线程 |
| **T5** | `ObservableCollection`（管理器列表）只允许在 UI 线程修改。任何后台产生的集合变更都要经 Dispatcher 封送 |
| **T6** | 文件 IO（读、写、解析）可以在后台线程执行，但**结果的应用**必须回到 UI 线程 |
| **T7** | `async void` 只允许出现在事件处理器和 Command 中，且内部必须 try/catch（§24.2） |

违背 T3 或 T5 的典型症状是 `NotSupportedException: This type of CollectionView does not support changes to its SourceCollection from a different thread`，或随机的 `InvalidOperationException: Collection was modified`。

**唯一允许的后台写入路径**：`AtomicFileWriter` 在后台线程写文件，完成后用 `Dispatcher.InvokeAsync` 回到 UI 线程更新 NoteStore 与 hash 记录。

---

# 4. 解决方案结构

## 4.1 工程划分与引用方向

v1 在 §5 列了四个工程，却在 §4 的画里多出一个"Application 层"，并且服务实现无处安放。v2 明确为**六个工程：三个产品工程 + 三个测试工程**（测试工程的划分理由见 §21.1）。

```text
LumiMemo.Core                无项目依赖
LumiMemo.Infrastructure      → LumiMemo.Core
LumiMemo.App                 → LumiMemo.Core
                             → LumiMemo.Infrastructure

LumiMemo.Core.Tests          → LumiMemo.Core
LumiMemo.App.Tests           → LumiMemo.Core
                             → LumiMemo.App
LumiMemo.Integration.Tests   → LumiMemo.Core
                             → LumiMemo.Infrastructure
                             → LumiMemo.App
```

**注意 `LumiMemo.App` 不额外引用第三方**：所有依赖都由 App 的 csproj 引入，Core 和 Infrastructure 保持最小依赖集（Core 零第三方依赖，Infrastructure 只有 YamlDotNet、Markdig、CsWin32）。

**`LumiMemo.Core.Tests` 不引用 App**，`LumiMemo.App.Tests` 引用 App。这意味着：

- 所有**纯逻辑**必须在 Core（或 Infrastructure 的纯函数部分）里——这层测试跑得最快、最稳定
- **ViewModel 的逻辑在 `LumiMemo.App.Tests` 里测**，前提是 ViewModel 不直接依赖 WPF 的具体类型（§18.6 的禁令正是为此存在）
- **涉及真实 HWND 的逻辑（窗口、DWM、热键、DPI 消息）无法自动化**，进手工测试清单（§21.4）
- **因此，凡能纯函数化的窗口计算都必须下沉**：屏幕边界裁剪、DPI 换算、折叠高度计算、布局恢复算法——全部放在 Infrastructure 的 `Windows/DpiMath.cs`、`Windows/LayoutMath.cs` 里，接收纯数值参数，**不接收 HWND**。它们是 bug 高发区，必须可单测。这条是 v2 新增的硬性要求。

## 4.2 目录结构

```text
LumiMemo.sln

src/
├── LumiMemo.App/
│   ├── App.xaml
│   ├── App.xaml.cs                 ← 组合根（§4.3）
│   ├── app.manifest                ← 必须声明 PerMonitorV2（§13.8）
│   │
│   ├── Views/
│   │   ├── NoteWindow.xaml
│   │   ├── ManagerWindow.xaml
│   │   ├── SettingsWindow.xaml
│   │   ├── QuickCaptureWindow.xaml
│   │   └── Dialogs/                ← 冲突提示、确认框、新建文件夹等
│   │
│   ├── ViewModels/
│   │   ├── NoteViewModel.cs
│   │   ├── ManagerViewModel.cs
│   │   ├── SettingsViewModel.cs
│   │   ├── TrashViewModel.cs          ← SettingsWindow 的回收站页（§7.4、§18.1）
│   │   ├── QuickCaptureViewModel.cs
│   │   ├── TrayViewModel.cs           ← 托盘菜单，无窗口（§15.9）
│   │   └── ConflictViewModel.cs       ← 冲突对话框（§11.4）
│   │
│   ├── Services/
│   │   ├── WindowManager.cs
│   │   ├── NoteViewModelFactory.cs     ← 唯一的 NoteViewModel 构造点（§14.1）
│   │   ├── NoteWindowPool.cs
│   │   ├── AutoSaveService.cs
│   │   ├── FileWatchService.cs     ← 适配 Core 的 IFileWatcher 到 WPF Dispatcher
│   │   ├── SearchService.cs
│   │   ├── TrayService.cs
│   │   ├── HotkeyService.cs
│   │   ├── ThemeService.cs
│   │   ├── WpfDispatcher.cs            ← 实现 IDispatcher（§21.1）
│   │   └── DialogService.cs            ← 实现 IDialogService
│   │
│   ├── Abstractions/                   ← 只属于 App 的抽象（Core 不需要认识它们）
│   │   ├── IDispatcher.cs              ← 让 ViewModel 脱离 WPF 线程也能测（§21.1）
│   │   └── IDialogService.cs           ← 让 ViewModel 不直接弹 MessageBox（§18.6）
│   │
│   ├── Windowing/
│   │   ├── MessageWindow.cs            ← HWND_MESSAGE 消息专用窗口，接收 WM_HOTKEY（§17.6）
│   │   ├── WindowStyleManager.cs       ← WS_EX_TOOLWINDOW / WS_EX_LAYERED
│   │   ├── DwmInterop.cs               ← 圆角与阴影（§13.1）
│   │   └── DisplayChangeWatcher.cs     ← WM_DISPLAYCHANGE（§14.5）
│   │
│   ├── Controls/
│   │   ├── NoteToolbar.xaml
│   │   ├── TagChip.xaml
│   │   └── MarkdownTextBox.xaml
│   │
│   ├── Themes/
│   │   ├── Colors.Light.xaml
│   │   ├── Colors.Dark.xaml
│   │   ├── Typography.xaml
│   │   ├── Controls.xaml
│   │   └── NotePalette.xaml            ← 7 色 × 明暗（§15.3）
│   │
│   └── Resources/
│       └── Strings.resx                ← 所有 UI 字符串（§24.2）
│
├── LumiMemo.Core/
│   ├── Models/
│   │   ├── Note.cs
│   │   ├── NoteLayout.cs
│   │   ├── AppSettings.cs
│   │   ├── TrashEntry.cs
│   │   ├── QuickCapture.cs
│   │   ├── AttachmentRef.cs
│   │   └── NoteColor.cs              ← enum
│   ├── Stores/
│   │   ├── NoteStore.cs
│   │   └── SearchIndex.cs
│   ├── Services/
│   │   ├── NoteService.cs
│   │   ├── LayoutService.cs
│   │   ├── SettingsService.cs
│   │   ├── TrashService.cs
│   │   └── TitleDeriver.cs           ← 标题派生规则（§5.4），纯函数，重点单测
│   ├── Abstractions/
│   │   ├── INoteRepository.cs
│   │   ├── INoteService.cs             ← Core 的业务入口（§14.2）
│   │   ├── IFileWatcher.cs
│   │   ├── ISettingsStore.cs
│   │   ├── ILayoutStore.cs
│   │   ├── ITrashStore.cs
│   │   ├── IAttachmentStore.cs
│   │   ├── IWindowManager.cs
│   │   ├── IClock.cs
│   │   └── IAppPaths.cs
│   └── Events/
│       └── NoteEvents.cs
│
├── LumiMemo.Infrastructure/
│   ├── Storage/
│   │   ├── MarkdownNoteRepository.cs
│   │   ├── FrontMatterParser.cs
│   │   ├── FrontMatterSerializer.cs
│   │   ├── LinkRewriter.cs           ← 移动便签时重写相对链接（§6.3）
│   │   └── AtomicFileWriter.cs
│   ├── Settings/
│   │   ├── JsonSettingsStore.cs
│   │   └── JsonLayoutStore.cs
│   ├── Trash/
│   │   └── FileSystemTrashStore.cs
│   ├── Attachments/
│   │   ├── FileSystemAttachmentStore.cs
│   │   └── OrphanScanner.cs          ← 孤儿附件扫描（§6.4）
│   ├── Watching/
│   │   └── FileSystemWatcherAdapter.cs
│   ├── Windows/
│   │   ├── NativeMethods.txt         ← CsWin32 输入
│   │   ├── MonitorEnumerator.cs
│   │   ├── DpiMath.cs                ← 纯函数：DPI 换算（§13.8）
│   │   └── LayoutMath.cs             ← 纯函数：工作区夹取、层叠摆放、折叠高度
│   ├── Logging/
│   │   └── RollingFileLoggerProvider.cs
│   └── Io/
│       ├── AppPaths.cs
│       ├── SystemClock.cs
│       └── LongPath.cs               ← \\?\ 前缀处理（§5.10）
│
├── LumiMemo.Core.Tests/                ← 只引用 Core（§21.2）
│   ├── Parsing/        Front Matter 往返、损坏降级、标题派生
│   ├── Identity/       ID 生成、冲突、缺失回写
│   ├── Search/         匹配与排序
│   ├── Paths/          路径安全校验、附件相对路径计算
│   └── Math/           DpiMath、LayoutMath、工作区夹取
│
├── LumiMemo.App.Tests/                 ← 引用 Core + App（§21.1）
│   ├── ViewModels/     NoteViewModel、ManagerViewModel 的逻辑
│   ├── Scheduling/     AutoSaveService 的去抖与 flush
│   └── Messaging/      Messenger 注册与注销
│
└── LumiMemo.Integration.Tests/         ← 真实文件系统（§21.3）
    ├── Storage/        原子写入、编码行尾、链接重写、.lumitmp 清理
    ├── Watching/       FileSystemWatcher 的增删改、防自触发
    ├── Trash/          回收站索引与恢复冲突
    └── Conflicts/      三路比较与冲突判定
```

## 4.3 依赖注入组装

组合根只有一处：`App.xaml.cs` 的 `OnStartup`。

```csharp
// App.xaml.cs（示意）
protected override async void OnStartup(StartupEventArgs e)
{
    var services = new ServiceCollection();

    // Core：无状态的服务与单例 Store
    services.AddSingleton<IClock, SystemClock>();
    services.AddSingleton<IAppPaths, AppPaths>();
    services.AddSingleton<NoteStore>();
    services.AddSingleton<INoteService, NoteService>();
    services.AddSingleton<LayoutService>();
    services.AddSingleton<SettingsService>();
    services.AddSingleton<TrashService>();
    services.AddSingleton<TitleDeriver>();

    // Infrastructure：IO 实现
    services.AddSingleton<INoteRepository, MarkdownNoteRepository>();
    services.AddSingleton<IFileWatcher, FileSystemWatcherAdapter>();
    services.AddSingleton<ISettingsStore, JsonSettingsStore>();
    services.AddSingleton<ILayoutStore, JsonLayoutStore>();
    services.AddSingleton<ITrashStore, FileSystemTrashStore>();
    services.AddSingleton<IAttachmentStore, FileSystemAttachmentStore>();

    // App：窗口相关的服务
    services.AddSingleton<IWindowManager, WindowManager>();
    services.AddSingleton<NoteWindowPool>();
    services.AddSingleton<NoteViewModelFactory>();
    services.AddSingleton<AutoSaveService>();
    services.AddSingleton<FileWatchService>();
    services.AddSingleton<SearchService>();
    services.AddSingleton<TrayService>();
    services.AddSingleton<HotkeyService>();
    services.AddSingleton<IDialogService, DialogService>();
    services.AddSingleton<IDispatcher, WpfDispatcher>();

    // ViewModel：只注册无参构造的那些
    services.AddTransient<ManagerViewModel>();
    services.AddTransient<SettingsViewModel>();
    services.AddTransient<TrashViewModel>();
    services.AddTransient<QuickCaptureViewModel>();

    _provider = services.BuildServiceProvider();
    ...
}
```

**约束**：

- **不使用 Generic Host**。WPF 的 `Application` 已经是进程生命周期的拥有者，再套一层 Host 会导致退出顺序难以控制（托盘图标不消失、日志不 flush 是典型症状）。启动顺序由 §17.1 明确，退出顺序由 §17.4 明确，都在 `App.xaml.cs` 里显式写。
- **`NoteViewModel` 不在 DI 容器里注册**。它需要构造参数 `Note` + `NoteLayout`，由 `NoteViewModelFactory` 手工创建（§14.1）。DI 只注册无参构造的 ViewModel。
- **`ManagerViewModel` / `TrashViewModel` 等在 App 层手工解析**（`_provider.GetRequiredService<T>()`），不要注入 `IServiceProvider` 到 ViewModel 里当服务定位器用。
- **不在 ViewModel 构造函数里做 IO**。构造必须是纯赋值，初始化放在 `InitializeAsync()` 里由工厂或 `Loaded` 事件调用（否则设计时预览和单测都会挂）。
- **`IDispatcher` 必须注册**（§21.1）。ViewModel 需要封送时注入它，不直接引用 `System.Windows.Threading.Dispatcher`——否则 `LumiMemo.App.Tests` 无法在没有 WPF 线程的情况下测 ViewModel。

---

# 5. 数据设计

## 5.1 Markdown 为唯一真实数据源

每个便签对应笔记目录下的一个 Markdown 文件：

```text
笔记目录/
├── Docker 常用命令-20260919-a3f2b1c8.md
├── 项目计划-20260919-7e1d90ab.md
├── 工作/
│   ├── 周报-20260920-4c88f012.md
│   └── 复盘-20260921-b2a71e55.md
├── 生活/
│   └── 购物清单-20260919-99abc034.md
├── attachments/
│   └── 20260919-101530-a3f2b1c8.png
└── .lumimemo/
    ├── trash/
    │   └── 20260919-120000-周报-20260920-4c88f012.md
    └── trash-index.json
```

**与 v1 的差异**：v1 把 `settings.json`、`layout.json`、`logs/` 也放在笔记目录下的 `.pinslip/`。v2 只把**回收站**留在笔记目录内，其余全部移到 `%LOCALAPPDATA%`（§8.1）。理由：

- 回收站必须与笔记同卷，`File.Move` 才是原子的（跨卷会退化成"复制 + 删除"）
- 配置与日志是设备状态，不该跟着笔记目录被同步/备份（§8.1 有完整理由）

**目录名用 `.lumimemo` 而不是 `.pinslip`**：v1 沿用了参考项目的名字，那是别人的产品名，不应出现在我们自己的目录里。

用户可以直接对这些文件做的操作：复制、备份、同步、用 Obsidian 打开、用 VS Code / Typora 编辑。**应用不锁定格式，也不依赖任何私有文件来理解便签内容**（`.lumimemo/trash-index.json` 丢失时可以从目录内容重建，见 §7.2）。

## 5.2 Markdown 文件格式规范

一个便签文件由两部分组成：可选的 YAML Front Matter，然后就是纯粹的 Markdown 正文。

````markdown
---
id: 3f2a91c4-5b8e-4d17-9a62-8c1f4e7b0d33
color: yellow
createdAt: 2026-09-19T10:15:30+08:00
updatedAt: 2026-09-19T10:42:07+08:00
tags:
  - docker
  - linux
---

Docker 常用命令

启动：

```bash
docker compose up -d
```

查看：

- `docker ps`
- `docker logs`
````

**格式规则**：

| 规则 | 说明 |
|---|---|
| 分隔符 | `---` 必须独占一行，位于文件最开头（允许 BOM，见 §5.9）。结束分隔符同样是独占一行的 `---` |
| 换行 | Front Matter 与正文之间保留**一个空行** |
| 正文 | 从结束分隔符之后的第一个字符开始，原样保存，不做任何规范化 |
| 无 Front Matter | 合法。见 §5.5 的 ID 补给流程 |
| 序列化顺序 | 固定为 `id, color, createdAt, updatedAt, tags`，便于 diff |
| 引号 | 字符串值统一用双引号；日期用 ISO 8601 带时区偏移 |
| 空 tags | 省略 `tags` 键，不写空数组 |

> v1 §7 的示例把嵌套的 ` ```bash ` 写在 ` ```markdown ` 里，导致外层围栏提前闭合、文档渲染错乱。v2 的示例统一用**四反引号**做外层围栏。这不是排版洁癖：示例会被人直接复制进代码和测试用例。

## 5.3 Front Matter 字段规格

**v2 只定义以下五个字段。** 这是完整清单，新增字段必须先改本文档。

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | GUID 字符串 | 是（可由程序补写） | 便签的稳定身份。**文件的唯一身份标识，不是文件名** |
| `color` | 枚举字符串 | 否 | `yellow` / `pink` / `blue` / `green` / `purple` / `orange` / `gray`。缺失时用设置里的默认色 |
| `createdAt` | ISO 8601 带偏移 | 否 | 创建时间。缺失时用文件创建时间，再退回文件修改时间 |
| `updatedAt` | ISO 8601 带偏移 | 否 | 最后修改时间。由程序维护 |
| `tags` | 字符串数组 | 否 | 标签。空则不写该键 |

### v2 相对 v1 的三处变更

1. **去掉 `title`**。v1 在 Front Matter 里存 `title`，同时又定义了"Front Matter → H1 → 文件名 → 无标题"的回退链，两套说法无法同时实现。v2 采纳 PinSlip 的做法：**标题完全派生，不存储**（§5.4）。
2. **去掉 `locked`**。v1 §7.1 说 Front Matter"可以保存 locked"，但 `locked` 在 v1 §33/§64 又被描述为窗口交互状态，且 `NoteLayout` 里也有 `Locked`。v2 明确 `locked` 属于**设备状态**，只写 `layout.json`（§8.3）。
3. **不用 `title` 作为规范字段**。若用户的 Obsidian vault 里已有 `title` 字段，解析时**保留并原样写回**（不删除用户数据），但程序不使用它。这是"不破坏用户文件"原则的一部分。

### 未知字段的处理

Front Matter 中出现本表之外的键（用户自定义、Obsidian 插件写入的字段），必须**原样保留并在写回时保持顺序**。

**注意"保持顺序"对容器类型有硬性要求**：.NET 的 `Dictionary<string, object?>` **不保证枚举顺序**，用它会导致每次写回时未知字段的顺序随机变化——在 git 里表现为无意义的 diff 抖动。必须用**保序**的类型：

```csharp
// Note 上的字段类型（§9.2）
public List<KeyValuePair<string, object?>> UnknownFrontMatterKeys { get; set; } = [];
```

用 `List<KeyValuePair<...>>` 而不是 `OrderedDictionary`：前者在 `System.Text.Json` 下序列化行为明确、可变且保序；后者需要自定义转换器。

这条很容易被忽略但影响很大：用户的 Obsidian vault 里往往有 `aliases`、`cssclasses`、或者 Dataview 插件的字段。程序静默丢弃它们等于在破坏用户数据；而顺序抖动会让用户的 git 历史充满噪声。

## 5.4 标题策略

**结论：标题完全派生，不存储。**

派生算法（`TitleDeriver`，纯函数）：

```text
1. 取正文中第一行「非空且有内容」的行
   - 跳过纯空行
   - 跳过 HTML 注释
   - 跳过单独的图片行（`![...](...)` 单独成行时不算标题）
2. 剥离 Markdown 标记：
   - 行首的 1~6 个 `#` 加空格
   - 行首的列表标记 `- ` `* ` `+ ` `1. ` `- [ ] ` `- [x] `
   - 行首的引用标记 `> `
   - 成对的强调标记 `**` `__` `*` `_` `~~`
   - 行内代码的反引号
   - 链接 `[text](url)` → 保留 text
   - 行内 HTML 标签 → 去掉标签只留文本
3. Trim，合并连续空白为单个空格
4. 截断到 60 个字符（按 Unicode 文本元素计数，不要按 char 切，否则 emoji 和中文组合字符会被切坏）
5. 若结果为空 → "无标题"
```

**为什么派生而不存储**：

- 外部编辑器（Obsidian）里用户改的是正文，不是 Front Matter。派生方案下改完立刻生效，不需要同步
- 不需要在"改标题"时决定"改 Front Matter 还是改 H1"
- 不需要处理"两处 title 不一致"的冲突
- 与 Obsidian 的"文件名即标题"心智模型一致

**代价与应对**：标题是每次读正文时算出来的，有微小开销。应对是**在 NoteStore 里缓存派生结果**，只在正文变更时重算（`Note.Title` 是只读派生属性，不是存储字段）。

**UI 上不允许直接编辑标题**。便签窗口标题栏显示的标题是正文第一行的投影，用户想改标题就去改正文第一行——这与 Obsidian、Bear、Notion 的行为一致。

## 5.5 ID 生命周期

`id` 是便签的稳定身份，必须有明确的生命周期规则，否则会出现 v1 完全没有覆盖的三种情况。

### 生成

| 时机 | 行为 |
|---|---|
| 应用内新建便签 | `Guid.NewGuid()`，写入 Front Matter |
| 扫描到没有 `id` 的文件 | 生成新 GUID，**回写文件**（见下） |
| 扫描到 `id` 非法的文件（不是合法 GUID） | 视为缺失，同上 |

### 启动时的补给流程

**顺序很重要**，必须与 §17.1 的启动流程一致：

```text
1. 扫描笔记目录，得到全部 .md 路径
2. 逐个解析（不启动 watcher）
3. 对缺 id / id 非法的文件：
   a. 生成新 GUID
   b. 用 AtomicFileWriter 写回（保留原有 Front Matter 未知字段与顺序）
   c. 记录到"本次启动补写的文件"列表
4. 处理 id 冲突（见下）
5. 建立 NoteStore
6. 【此时才】启动 FileSystemWatcher
```

**为什么 watcher 要放在最后**：第 3 步会修改一批用户文件，如果 watcher 已在运行，会立刻收到一堆 Changed 事件，与 §10.3 的自写抑制逻辑叠加，产生的时序问题很难调试。放在最后最简单也最稳。

**回写失败时**（文件只读、被占用）：不阻塞启动，在 NoteStore 里保留这张便签（用内存中的临时 ID），并在管理器和便签窗口上标记"未同步"，同时写日志。用户下次保存时会重试。

### 冲突处理

同一次扫描中出现两个文件声明同一个 `id`：

```text
1. 两个文件都加载，按文件路径做 OrdinalIgnoreCase 排序
2. 排序第一的文件：保留原 id
3. 其余文件：生成新 GUID，回写文件
4. 记一条 Info 级日志
5. 如果同一个 id 出现在超过两个文件里，全部重新生成，只保留第一个
```

**为什么按路径排序而不是按修改时间**：路径是确定的、可复现的，保证同一台机器上多次运行得到相同结果。按修改时间会在文件被同步工具"触碰"后产生不同的胜者，导致便签身份在不同设备间漂移。

### 便签被移动 / 重命名

`id` 不变。文件的物理路径变了，但便签还是同一个便签。`layout.json` 按 `id` 索引，因此窗口位置、折叠状态、置顶设置全部跟着走。

## 5.6 文件命名规则

**结论：`{标题摘要}-{创建日期}-{短ID}.md`，创建时确定，之后不自动重命名。**

```text
Docker-常用命令-20260919-a3f2b1c8.md
购物清单-20260919-99abc034.md
无标题-20260919-1f0e7d22.md
```

| 组成部分 | 规则 |
|---|---|
| 标题摘要 | 派生标题（§5.4）经过文件名安全化：删除 `< > : " / \ | ? *` 与控制字符；连续空白与 `-` 折叠为单个 `-`；去掉首尾的 `.`、空格、`-`；截断到 40 个字符；结果为空时用 `无标题` |
| 创建日期 | 本地时间的 `yyyyMMdd` |
| 短ID | 新 GUID 去掉连字符后的前 8 位十六进制（小写） |
| 扩展名 | `.md` |

**保留字处理**：Windows 文件名不能是 `CON`、`PRN`、`AUX`、`NUL`、`COM1`~`COM9`、`LPT1`~`LPT9`（不区分大小写，且带扩展名也算）。标题摘要命中这些词时，追加 `_`。

**重名处理**：理论上短 ID 已经保证了唯一性。但如果目标文件已存在（极小概率），追加 `-2`、`-3` 直到可用。

**为什么不自动重命名**：

1. **watcher 稳定性**：每次标题变化都重命名会产生大量 Renamed 事件，与去抖、自写抑制、外部编辑冲突三套逻辑交互，是 bug 温床
2. **同步工具友好**：OneDrive / Dropbox 对"删除 + 新建"和"重命名"的处理不同，频繁重命名会产生同步流量甚至冲突副本
3. **用户预期**：用户在 Obsidian 里引用过这个文件时，重命名会打断链接
4. **一致的行为**：创建后文件名稳定，是更容易理解和预测的规则

**代价**：标题改了但文件名不变，两者会不一致。这是**可接受的**——文件名在这里是"创建时的快照"，真正的身份是 `id`，真正的标题是正文第一行。Obsidian 用户对这种不一致很熟悉（Obsidian 自己的 `aliases` 就是同一个问题的另一种解法）。

> 如果后续发现用户确实需要，可以作为设置项 D-1 加入（默认关闭）。开启后的实现要点：重命名走 `File.Move`，watcher 的 Renamed 事件须按"新旧路径的 id 相同"识别为同一便签而不是"删除 + 新建"。这是为什么它被推迟——需要先有稳定的 watcher 实现。

**新建便签时的默认文件名**：正文为空时标题派生结果是"无标题"，文件名为 `无标题-20260919-1f0e7d22.md`。用户输入第一行内容后，文件**不会**改名（同上）。

**实现说明（本轮已落地）**：上面这张表终于有了执行者——`INoteRepository.CreateAsync`（实现是 `MarkdownNoteRepository.CreateAsync`）。**为什么不落在 `NoteService`**：分配文件名要做三件纯文件系统的事（探一个没被占用的名字、探目标路径是否存在、写第一次盘），而 Core 不碰文件系统（§3.1），`NoteService` 里没有任何一件能做。它因此只剩编排：把仓储交出来的那张收进 `NoteStore`、通知 `SearchIndex.OnNoteAdded`（§3.3 流 3 的业务层那一步）。**布局条目不在这里建**——布局是设备状态，谁开窗谁负责（§8.3），在这里顺手建一条会让「新建了但没开窗」在 `layout.json` 里留下永远用不上的记录。

派生规则本身**没有重写**，`MarkdownNoteRepository` 直接调 `Core/Services/NoteFileNameBuilder`（§19.4 的越界校验就在里面）：标题传 `TitleDeriver.Derive(正文)`，初始正文为空串于是得到 `无标题`；创建日期取 `IClock.Now` 的本地偏移（`SystemClock` 给的就是 `DateTimeOffset.Now`），因此 `{createdAt:yyyyMMdd}` 确实是本地日期；`isTaken` 直接传 `File.Exists`。

**一处与前面「重名处理」不同的兜底**：`targetFolder` 是接口上已有的参数（相对笔记目录根），拼接后要过一遍越界校验。`NoteFileNameBuilder.Build` 里那道 §19.4 校验只改写**文件名主干**，不动**目录**，所以 `..\..\Windows` 这样的输入会拼出 `{根}\..\..\Windows\无标题-xxxxxxxx.md` 并真的写出去。实现因此在拼目录这一步先自己夹一道：目标目录越出笔记目录时**退回到笔记目录根并记一条警告**，不让它走到写盘。本轮没有调用方传 `targetFolder`，留一个会炸的参数等于给后来的人埋雷，所以它实现了而不是抛 `NotSupportedException`。

**笔记目录没了就拒绝，不悄悄重建**（这一条是对 §11.5 那张处置表的落实）：路径未配置、或配置的路径已经不存在时，`CreateAsync` 抛 `InvalidOperationException`，**不**自动 `CreateDirectory`。`AtomicFileWriter` 会自己建目录，若笔记目录是被拔掉的移动盘，它会兴高采烈地造一个空目录出来，而用户的便签全在别处。管理器那一侧把 `IOException` / `UnauthorizedAccessException` / `InvalidOperationException` 接住并提示「笔记目录可在设置里更改」。

**初始落盘形态没有新增序列化逻辑**：`FrontMatterSerializer` 已经把 `id/color/createdAt/updatedAt` 全写出来、空 `tags` 省略键（§5.8），空正文的新便签因此正好落在 §5.9 要的三件上——UTF-8 无 BOM、CRLF、以换行结尾。

## 5.7 文件夹

文件夹直接等于磁盘目录，**不建立任何数据库层面的 folder 表**。

```text
便签所在目录 = 文件所在的目录
移动便签到文件夹 = File.Move（+ 重写相对链接，见 §6.3）
新建文件夹 = Directory.CreateDirectory
```

### 约束

| 项 | 规则 |
|---|---|
| 层级深度 | 不限制，但管理器 UI 在超过 5 层时把路径中间折叠为 `…` |
| 隐藏目录 | 扫描时**跳过所有以 `.` 开头的目录**（`.lumimemo`、用户的 `.obsidian`、`.git` 等） |
| 附件目录 | `attachments/` 是保留名，不作为便签文件夹展示给用户；如果用户已有的目录叫这个名，仍然跳过（§6.1 说明如何配置） |
| 非法字符 | 新建文件夹时校验 Windows 文件名规则，并检查保留字 |
| 重名 | 不覆盖，提示已存在 |

### 删除文件夹

**这是 v1 完全没定义的场景**，v2 明确给出两个选项（与 PinSlip 一致）：

```text
删除文件夹 "工作"（非空）
  ↓
┌ 文件夹里还有 3 张便签，怎么处理？
│
│ ○ 便签移到根目录，只删除文件夹
│ ● 整个文件夹移到回收站（便签一并进回收站）
│
│              [ 取消 ]  [ 确定 ]
```

- 空文件夹：直接 `Directory.Delete`，不经确认（但仍记日志）
- "便签移到根目录"：逐个 `File.Move` 到笔记目录根，然后删空目录
- "整个文件夹进回收站"：把**整个目录**移到 `.lumimemo/trash/` 下（而不是逐个移动文件），`trash-index.json` 记录一条"目录级条目"（§7.1）

## 5.8 标签

标签写在 Front Matter 的 `tags` 数组里（§5.3）。

```yaml
tags:
  - work
  - docker
```

### 规范化规则

规范化在**写入时**执行一次，之后按规范形式存储与比较：

| 规则 | 说明 |
|---|---|
| 去首尾空白 | `" work "` → `"work"` |
| 不允许内部空白 | 含空格的标签替换为 `-`（`"to read"` → `"to-read"`），与 Obsidian 的标签规则一致 |
| 不允许 `#` 前缀 | 用户输入 `#work` 时剥掉 `#` |
| 大小写 | **保留原始大小写，但比较时忽略大小写**。即 `Work` 和 `work` 视为同一个标签，存储时以首次出现的写法为准 |
| 去重 | 同一便签内不允许重复标签（忽略大小写去重） |
| 空标签 | 直接丢弃 |
| 长度上限 | 64 字符，超出则拒绝并提示 |

**实现落在 `Core/Services/TagRules.cs`，且只有这一份**（`Normalize` / `TryAdd` / `Split` + `MaxLength = 64`）。解析 Front Matter 与管理器那个标签编辑框都走它：两处各写一份的话，「编辑完写回、再读回来标签变了」这种缺陷只会在特定输入下出现，极难查。

三条与直觉不同的落地细节：

- **规范化在读的时候也跑一遍**（解析 Front Matter 时），而不是只在写入时。这样 `Note.Tags` 里永远是规范形式，展示/比较/写回三处都不必各自记着再规范化一次——漏掉一处就是同一个标签被当成两个。
- **长度上限不在 `TagRules` 里执行**。§5.8 对它要求的是"拒绝并提示"，那是**输入校验**；读文件时套用它等于把用户已有的长标签直接丢掉，属于破坏数据。`Split` 照样把超长标签交出来，由调用方（编辑框的校验回调）决定怎么拒。
- **空格不在分隔符里**。上面那条"内部空白换成 `-`"决定了 `"to read"` 是一个标签，若 `Split` 按空格拆，用户打「to read」会得到两个标签，与那条规则直接打架。分隔符是 `, ， 、 ; ； \n`——中文标点必须收：中文输入法下打出全角逗号是默认行为，不是用户写错了。

**大小写去重不能交给 `Distinct()`**：它按默认比较器（字节序）挑一个留下，用户写的 `Work` 可能被判成重复而以 `work` 落盘。`TryAdd` 是"先查再加"，保留首次出现的写法。

### 内存索引

`SearchIndex` 维护 `Dictionary<string, HashSet<Guid>>`（键是忽略大小写后的标签），在以下时机重建或增量更新：

- 启动建索引时全量构建
- `NoteCreated` / `NoteChanged` / `NoteDeleted` 事件触发时增量更新
- 外部文件变化导致标签变化时，同样走增量更新

**不要**为标签单独落盘——它是从 Markdown 完全可重建的派生数据。

## 5.9 编码与行尾

这一节 v1 只出现在测试清单里，没有结论。**v2 给出明确规则**，因为这是"与 Obsidian 互通"承诺能否兑现的关键。

| 项 | 规则 |
|---|---|
| **写入编码** | UTF-8。**BOM 的有无跟随原文件** |
| **读取编码** | 按 UTF-8 读取；若存在 BOM 则剥离后解析，但**记住它有 BOM**（`Note.HadBom`），写回时原样加回去 |
| **新增文件** | UTF-8 **无 BOM** |
| **异常编码** | 若文件不是合法 UTF-8（用户在 GBK 编辑器里存过），按 `Encoding.Default`（系统 ANSI）回退读取，并**在管理器中标记该便签为"编码异常"**，提示用户用 UTF-8 重新保存。不要静默按 UTF-8 硬读，那会写出乱码并覆盖原文件 |
| **行尾** | **保持文件原有的行尾风格**。读取时检测（含 `\r\n` 则视为 CRLF，否则 LF），写回时沿用 |
| **末尾换行** | **保持原文件的状态**：原来有结尾换行就写，原来没有就不写 |

**统一原则：编码、行尾、BOM、末尾换行——四者都是"原样保留"，不做任何规范化。**

**为什么**：这四者只要有一项被程序"顺手规范化"，用户的 git diff 就会显示整个文件被改写。用户看到自己几百行的笔记显示为"全文修改"，会立刻认为这个便签程序在破坏文件——而实际上它只是加了个 BOM 或补了个换行。**最小改动原则（§24.1 原则 3）在这里的意义是具体的。**

**"保持 BOM"看起来不如"统一去掉 BOM"干净**，但干净不是目标。用户如果有 20 个带 BOM 的文件（Windows 记事本写的）和 30 个不带 BOM 的（VS Code 写的），程序统一成任一种都会动到其中一批文件。保持原样则一个字节都不动。

**只有新建的便签**才由程序决定初始形态：UTF-8 无 BOM、CRLF、带结尾换行。这是最通用的组合。

**这三项必须随 `Note` 一起携带**（§9.2 的 `LineEnding` / `HadBom`），并且**在每次从磁盘读取时重新探测**——用户可能在程序外把文件的行尾改掉了。

**写入时的自检**：保存前比对"将要写出的字节"与"读入时的原始字节"，如果两者相同则**跳过写入**。这能避免大量无意义的文件写入（用户打开看一眼就关掉的便签不该触发磁盘写），也是 §10.3 防自触发之外的第二重保险。

## 5.10 解析与降级矩阵

文件可能以各种形式损坏。**核心原则：任何情况下都不能让用户的便签从管理器和桌面上消失**，也不能静默覆盖可能有价值的内容。

| 情况 | 处理 | 标记 |
|---|---|---|
| 无 Front Matter | 正文从文件开头算起，生成 id 并回写 | 无 |
| Front Matter 未闭合（只有开头的 `---`） | 视为无 Front Matter，整份文件当正文；**生成 id 时采用"追加 Front Matter"而非"替换"策略** | 无 |
| YAML 语法错误 | 尝试解析，能解析出的字段照用；失败则全部字段用默认值。**原文件先备份为 `xxx.md.bak-<时间戳>`，再写回修复后的版本** | 管理器显示警告图标，日志记 Warning |
| `id` 缺失或非法 | 生成新 id 并回写（§5.5） | 无 |
| `id` 冲突 | 保留排序第一者，其余重新生成（§5.5） | 日志记 Info |
| `color` 值非法 | 用默认色 | 无 |
| `createdAt` / `updatedAt` 无法解析 | 用文件系统时间戳 | 无 |
| `tags` 不是数组（是字符串） | 按单个标签处理 | 日志记 Warning |
| 文件不是合法 UTF-8 | 按 ANSI 回退（§5.9） | 管理器标记"编码异常" |
| 文件为空（0 字节） | 当作一张空便签加载（生成 id 和 Front Matter） | 无 |
| 文件只有 Front Matter | 正文为空，正常加载 | 无 |
| 文件过大（> 1MB） | 正常加载但**不参与全文搜索**，并在管理器中提示 | 管理器标记"文件过大" |
| 路径过长（> 260 字符） | 用 `\\?\` 前缀访问（`LongPath` 助手）。写入时如果目标路径超长，提前报错而不是写入失败 | 提示用户 |
| 路径包含非法字符 | 不加载，记 Warning 日志 | 无法在 UI 中展示（用户需自行处理） |
| 文件被其他进程独占锁定 | 重试 3 次（间隔 100/300/600ms），仍失败则**保留 NoteStore 中的上次内容**，标记"暂不可读" | 便签窗口显示状态条 |

**关于 `.bak` 备份**：只在"因为文件损坏而要写回修改"时才创建，命名 `{原文件名}.{yyyyMMddHHmmss}.bak`，放在同目录。程序**不自动清理**这些文件（用户可能依赖它们找回数据），但在设置里提供"清理旧备份"的入口。

---

# 6. 附件

## 6.1 存放与命名

图片等附件统一放在笔记目录下的 `attachments/`：

```text
笔记目录/
├── attachments/
│   ├── 20260919-101530-a3f2b1c8.png
│   └── 20260919-101712-7e1d90ab.jpg
├── Docker 常用命令-20260919-a3f2b1c8.md
└── 工作/
    └── 周报-20260920-4c88f012.md
```

**命名规则**：`{yyyyMMdd}-{HHmmss}-{8位随机十六进制}.{ext}`

- 时间戳让人能按时间排序
- **随机后缀是必需的**：v1 用 `20260919_001.png` 这种递增序号，在"同一秒粘贴两次"或"从别处拷入同名文件"时会覆盖已有附件，导致便签里的图片被替换成别的图——这是静默的数据损坏
- 写入前仍要检查目标是否存在，存在则重新生成随机后缀

**目录名可配置**：默认 `attachments`。设置里可以改，用于两种情况——用户的 Obsidian vault 已经有一个不同名的附件目录；或者用户想把附件放在笔记目录之外（此时 `attachments/` 与笔记不再同卷，见"注意事项"）。

**启动时检测**：如果笔记目录下已存在同名目录且里面**有非本程序命名的文件**，说明这个目录是用户的（很可能是 Obsidian 的附件目录）。此时**不要**直接往里写，而是提示用户"检测到已存在的 attachments 目录，是否继续使用？"，建议改为 `lumimemo-attachments`。

## 6.2 相对路径规则

Markdown 里用**相对于当前便签文件所在目录的路径**引用附件：

| 便签位置 | 附件路径 |
|---|---|
| 笔记目录根：`Docker 常用命令-20260919-a3f2b1c8.md` | `attachments/20260919-101530-a3f2b1c8.png` |
| 子目录：`工作/周报-20260920-4c88f012.md` | `../attachments/20260919-101712-7e1d90ab.jpg` |
| 二级子目录：`工作/2026/复盘-....md` | `../../attachments/....png` |

> **v1 的错误**：v1 §23 给出的示例是 `![image](../attachments/20260919_001.png)`，但 v1 §6.1 的示例里存在位于**笔记目录根**的便签（`临时.md`）。对根目录便签，`../attachments/` 会指向笔记目录**之外**——如果用户选的是 Obsidian vault 根目录，这个路径直接跳出 vault。v2 要求路径必须按便签所在目录到附件目录的相对深度计算，并把这个计算抽成纯函数 `RelativePath(from, to)` 单测。

**生成时的计算**：

```text
相对深度 = 便签所在目录相对于笔记目录的层数
路径 = "../" × 相对深度 + "attachments/" + 文件名
```

**写入 Markdown 的形式**：

```markdown
![图片](attachments/20260919-101530-a3f2b1c8.png)
```

- 使用标准的 Markdown 图片语法，**不使用** Obsidian 的 `![[...]]` 内嵌语法（那会破坏与其他编辑器的兼容性）
- 始终只写相对路径，不写绝对路径（绝对路径在换电脑或换目录后全部失效）
- 路径分隔符统一用 `/`（Markdown 惯例，Windows 的 `\` 在部分渲染器里是转义字符）

## 6.3 移动便签时重写链接

**这是 v1 完全遗漏、但必然发生的破坏性场景。** v1 §50 说"移动便签到文件夹：`File.Move` 即可"，v1 §23 说附件用相对路径——两者放在一起，便签一移动就裂图（在便签内裂，在 Obsidian 里也裂）。

### 移动便签的完整流程

```text
NoteService.MoveNoteAsync(noteId, targetFolder)
  ↓
1. 校验目标目录存在且在笔记目录之内
2. 计算新旧「相对深度」的差值 delta
3. 如果 delta != 0：
     a. 用 Markdig 解析正文，取出所有图片节点与本地链接节点
     b. 对每个「相对路径」的链接，按 delta 增减前导 "../"
     c. 保留绝对 URL（http/https）、锚点（#xxx）、以及非相对路径不动
     d. 生成新正文
4. File.Move(旧路径, 新路径)
5. 用新正文写回（原子写）
6. 更新 Note 的 FilePath 与 Content
7. NoteStore.Update
8. 触发 NoteMoved 事件
```

### 具体规则

| 链接形态 | 处理 |
|---|---|
| `attachments/x.png`（相对） | 按 delta 加减 `../` |
| `../attachments/x.png`（相对） | 同上 |
| `https://...` / `http://...` | 不动 |
| `#anchor` | 不动 |
| `/absolute/path` | 不动（用户有意写的绝对路径） |
| `C:\...` | 不动 |
| `[[Obsidian 内链]]` | 不动（本次不处理 Obsidian 双链语法，见下） |

### 边界情况

- **delta 为正**（移到更深的目录）：给每个相对附件路径加一级 `../`
- **delta 为负**（移到更浅的目录）：去掉相应级数的 `../`；如果去掉后 `../` 变成负数（指向笔记目录之外），说明原路径本来就指到 vault 之外，此时**保持原样并警告**，不要猜
- **正文里同时有指向其他便签的相对链接**：同样重写（用户可能手动写了 `[参见](../工作/周报.md)`）
- **重写失败**（Markdig 解析异常）：中止移动，提示用户。**不要**先移动文件再重写——顺序反了会出现"文件已移动但链接未修正"的半完成状态

> **关于 Obsidian 双链 `[[文件名]]`**：v2 不处理。理由是双链的解析规则由 Obsidian 的 vault 配置决定（是否使用相对路径、是否包含扩展名），程序无法可靠推断。如果用户的 vault 大量使用双链，移动便签后双链可能失效——这一点在 §10.5 的"外部同步注意事项"里向用户说明。

## 6.4 孤儿附件清理

**问题**：粘贴图片会往 `attachments/` 堆文件；删除便签、或删除正文里的图片引用后，附件留在原地，目录持续膨胀。

### 引用关系不落盘

**不做引用计数表**。理由是引用关系随时可能被外部编辑器改变（用户在 VS Code 里加了一行图片引用），落盘的计数会立刻失真，而失真的计数比没有计数更危险——它会让人以为某个附件是孤儿而误删。

**改为按需全量扫描**：

```text
设置 → 附件管理 → 清理未引用的附件
  ↓
1. 遍历笔记目录下所有 .md（跳过 .lumimemo 和隐藏目录）
2. 用 Markdig 解析，收集所有指向 attachments 目录的引用
3. 列出 attachments/ 下所有文件
4. 差集 = 未被任何便签引用的文件
5. 展示给用户确认（默认全选，可以取消勾选）
6. 用户确认后移到回收站（不是直接删除）
```

**性能**：几千个文件规模下，全量扫描是数百毫秒级的操作，且是用户显式触发的，可以接受。

### 删除便签时附件怎么办

**默认：附件留在原地**，不做任何处理。理由：

- 删除便签进的是回收站，用户可能恢复。如果附件也一起被移走，恢复流程要连带恢复附件，复杂度高
- 一个附件可能被多张便签引用（用户手动复制了引用），跟着某一张便签进回收站会破坏其他便签
- 附件留在原地是可逆的；被连带删除再恢复则容易出问题

**用户想彻底清理时**，走上面的"清理未引用的附件"。这个入口在删除便签后的提示里可以顺带提一句（"有 3 张图片仅被该便签引用，可在设置中清理"）。

### 孤儿附件的展示

在"附件管理"页面同时显示：附件总数、总占用空间、未引用数量、未引用占用空间。给用户一个判断依据。

---

# 7. 回收站

## 7.1 结构

回收站位于**笔记目录内**（必须同卷，否则 `File.Move` 不是原子操作）：

```text
笔记目录/.lumimemo/
├── trash/
│   ├── 20260919-120000-周报-20260920-4c88f012.md        ← 单文件删除
│   ├── 20260919-121500-购物清单-20260919-99abc034.md
│   └── 20260919-123000-工作/                            ← 目录级删除
│       ├── 周报-20260920-4c88f012.md
│       └── 复盘-20260921-b2a71e55.md
└── trash-index.json
```

**删除后的文件名**：`{删除时刻}-{原文件名}`。加时间戳前缀是为了避免"删除同名文件两次"时的冲突。

## 7.2 索引

`trash-index.json`：

```json
{
  "version": 1,
  "entries": [
    {
      "trashName": "20260919-120000-周报-20260920-4c88f012.md",
      "originalRelativePath": "工作/周报-20260920-4c88f012.md",
      "noteId": "4c88f012-9a1e-4d33-8b77-2f0c5e91a4b2",
      "deletedAt": "2026-09-19T12:00:00+08:00",
      "kind": "file",
      "size": 2048
    },
    {
      "trashName": "20260919-123000-工作",
      "originalRelativePath": "工作",
      "noteId": null,
      "deletedAt": "2026-09-19T12:30:00+08:00",
      "kind": "directory",
      "size": 17520
    }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `trashName` | 在 trash 目录下的实际名字 |
| `originalRelativePath` | 相对于笔记目录的原路径，恢复时用 |
| `noteId` | 文件级条目记录便签 id；目录级条目为 null |
| `deletedAt` | 删除时间，用于保留策略 |
| `kind` | `file` 或 `directory` |
| `size` | 字节数，用于统计展示 |

### 索引丢失 / 不一致时的重建

**关键设计**：索引是**可重建的派生数据**，不是真数据。用户手动去 `.lumimemo/trash/` 里把文件拖回来、或者手动删掉 trash 里的文件，索引就会与实际不符。

启动时（以及在管理器中打开回收站时）做一致性检查：

| 情况 | 处理 |
|---|---|
| trash 里有文件但索引里没有 | 从文件名解析时间戳与原名，补一条条目，`originalRelativePath` 用解析出的文件名放在笔记目录根。同时**从文件内部读 `id`** 补充 `noteId` |
| 索引里有条目但 trash 里的文件不存在 | 说明用户手动删掉了或拖回去了。**检查原路径是否已存在同名文件**：存在则静默移除该条目；不存在则保留条目并标记"文件已不在" |
| 索引文件本身损坏 | 完全凭 trash 目录内容重建 |
| 索引文件不存在 | 同上（首次使用正常情况） |

## 7.3 恢复与冲突

```text
恢复便签
  ↓
1. 读条目取 originalRelativePath
2. 检查目标路径是否已被占用
   ├─ 未占用 → File.Move 回去 → 移除条目 → 触发 NoteCreated
   └─ 已占用 → 弹对话框：
        ┌ 原位置已有同名文件
        │
        │ ○ 恢复到原位置并重命名（周报-20260920-4c88f012 (1).md）
        │ ● 恢复到笔记目录根
        │ ○ 取消
        └
```

**恢复目录级条目**：递归 `Directory.Move`，内部的便签文件全部重新进入扫描。如果目标目录名已被占用，同样给出"重命名 / 恢复到根目录 / 取消"三选。

**恢复后的身份**：文件里的 `id` 是原样保留的（回收站只是移动文件，没改内容）。因此恢复后**窗口位置、折叠状态、置顶设置也全部回来**（`layout.json` 按 id 索引，删除时不清除，见 §8.3）。

**如果恢复时发现 id 与现有便签冲突**（用户删除后又新建了一个，恰好复制了同一个 id）：走 §5.5 的冲突处理，重新生成 id。

**实现说明（本轮已落地）**：上面那个三选一**不是** `MessageBox`。`MessageBox` 只有「是 / 否 / 取消」这套系统按钮，凑不出「重命名 / 恢复到笔记目录根 / 取消」三档，也没有地方给选项加说明。改为自绘的 `ChoiceDialog`（单选组 + 确定/取消），经 `IDialogService.ChooseAsync` 暴露。

| 位置 | 内容 |
|---|---|
| `Abstractions/IDialogService.cs` | `Task<int> ChooseAsync(title, message, choices, defaultIndex)`，返回选中项下标 |
| `Views/ChoiceDialog.xaml` + `.xaml.cs` | 单选组对话框。按钮文案固定「确定 / 取消」，选项文字由调用方给 |
| `ViewModels/TrashViewModel.cs` | 判定「原位置被占用」后摆出三档，选「取消」或直接关掉对话框都不动文件 |

**关掉对话框返回 `-1` 而不是抛异常**：关掉与点「取消」对调用方是同一件事，让它们走同一条分支比逼每个调用点各判一次要好（`-1` 与「取消」那一档合流）。

**恢复的落点由 `targetRelativePath = null` 表达「回原位」**，而不是传一份与 `OriginalRelativePath` 相等的字符串。存储层据此拿条目自己的原路径当落点——两者在「目标被占用时要不要重命名」上走的分支不同，用一个显式的 `null` 比用字符串相等去猜要稳。

**「恢复到笔记目录根」只取文件名**（`Path.GetFileName`）。带着原来的目录走的话，这一档跟「回原位」就没有区别了——它存在的意义正是**绕开那个被占用的目录**。

## 7.4 保留策略与清空

### 自动清理

设置项：`trashRetentionDays`，可选 `7` / `30` / `90` / `0`（永不清理），**默认 30**。

执行时机：应用启动后延迟 60 秒（避免与启动流程争 IO），异步执行。

```text
遍历 trash-index.json 的 entries
  ↓
deletedAt + retentionDays < 现在
  ↓
删除对应文件/目录 + 移除条目
```

**清理必须在 UI 上可感知**：启动后如果有清理发生，托盘提示一次（"已清理 5 个项目"），不要静默删用户的东西。

### 手动清空

```text
设置 → 回收站 → 清空回收站
  ↓
第一次确认：确认清空 3 个文件（共 2.1 MB）？  [取消] [清空]
  ↓
第二次确认：此操作不可撤销，确定要永久删除吗？  [取消] [永久删除]
```

**两次确认是刻意的**——清空回收站是应用里唯一不可逆的破坏性操作。第二次确认的按钮文字必须是"永久删除"而不是"确定"，让用户没有看错的余地。

**实现说明（本轮已落地）**：上面示意里的按钮顺序 `[取消] [清空]` 在本轮实现里是**反的**——代码里是「清空 / 取消」「永久删除 / 取消」，即确认键在左、取消键在右，两个对话框的**默认焦点都落在取消上**。

| 约定 | 落地 |
|---|---|
| 按钮顺序 | 确认键在左、取消键在右（Windows 平台惯例，`MessageBox` 也是这个次序） |
| 默认焦点 | 取消。敲回车时不该替用户按下那唯一不可逆的键 |
| 第二次的按钮文字 | 「永久删除 / 取消」，正文「此操作不可撤销，确定要永久删除吗？」 |
| 第一次的正文 | 「回收站里有 N 个项目（共 X）」+ 询问句 |

因为按钮文字要可定制，这两次确认**都走 §7.3 那个自绘的 `ChoiceDialog`**，不是 `MessageBox`——`MessageBox` 的按钮文案由系统提供，换成它会直接落空「必须是"永久删除"」这条要求。

**回收站本来就空时一句都不问**：空回收站上弹两遍「确定要永久删除吗」纯粹是骚扰，直接返回。

### 打开回收站

设置里有"打开回收站目录"按钮，用资源管理器打开 `.lumimemo/trash/`。用户可以自己把文件拖回去（程序会自动识别，见 §7.2）。

## 7.5 删除文件夹

见 §5.7 的"删除文件夹"。目录级条目在回收站里以**一个条目**展示（显示文件夹名、内含便签数、总大小），而不是展开成一堆文件。

---

# 8. 配置与设备状态

## 8.1 存放位置

**结论：`settings.json` 与 `layout.json` 全部放在 `%LOCALAPPDATA%`，不跟随笔记目录。**

```text
%LOCALAPPDATA%\LumiMemo\
├── settings.json          ← 应用设置
├── layout.json            ← 窗口位置、尺寸、折叠、置顶、缩放等设备状态
├── logs\
│   └── app-2026-09-19.log
└── recovery\
    └── （崩溃恢复文件，第一阶段不做，见 §11.6）
```

笔记目录下**只有**：

```text
笔记目录/
├── *.md / 子目录
├── attachments/
└── .lumimemo/trash/ + trash-index.json
```

### 为什么这样分（v1 在这件事上自相矛盾）

| 理由 | 说明 |
|---|---|
| **笔记目录要保持可移植** | 用户可能把笔记目录放在 OneDrive / Dropbox / 移动硬盘 / Obsidian vault 里。往里塞 `settings.json` 会让这些工具把设备状态一起同步到别的机器 |
| **设备状态跨机器是有害的** | 电脑 A 是 2560×1440 双屏，电脑 B 是 1920×1080 单屏。把 A 的窗口坐标同步到 B，结果是窗口跑到屏幕外。这正是 v1 §8 想避免的问题，但 v1 §9/§10 又把配置放回了笔记目录 |
| **回收站必须留在笔记目录** | 唯一的例外，理由是同卷才能原子移动（§7.1） |
| **`%LOCALAPPDATA%` 不需要管理员权限** | 普通用户可写，符合"不要管理员权限"的目标（§17.7） |

### 代价与后续

放弃的是**纯 portable 部署**（整个应用 + 配置 + 笔记放 U 盘里带着走）。v2 不做，但把路径访问全部收敛到 `IAppPaths` 抽象后面：

```csharp
public interface IAppPaths
{
    string SettingsFile { get; }
    string LayoutFile { get; }
    string LogDirectory { get; }
    string RecoveryDirectory { get; }
    string NotesFolder { get; }             // 来自 settings
    string TrashDirectory { get; }          // NotesFolder/.lumimemo/trash
    string TrashIndexFile { get; }
    string AttachmentsDirectory { get; }    // NotesFolder/{attachmentsFolderName}
}
```

未来如需 portable 模式，只需换一个 `PortableAppPaths` 实现，不改任何调用方。

## 8.2 settings.json

```json
{
  "version": 1,
  "notesFolder": "D:\\Notes",
  "attachmentsFolderName": "attachments",
  "theme": "system",
  "defaultColor": "yellow",
  "defaultWidth": 360,
  "defaultHeight": 420,
  "showStatusBar": true,
  "restoreAfterShowDesktop": true,
  "startWithWindows": true,
  "minimizeToTrayOnClose": true,
  "showTrayIcon": true,
  "singleClickTrayAction": "toggleManager",
  "globalQuickCaptureHotkey": "Ctrl+Shift+N",
  "globalShowAllHotkey": "Ctrl+Alt+N",
  "autoSaveDelayMs": 500,
  "searchDebounceMs": 150,
  "trashRetentionDays": 30,
  "enableAnimations": true,
  "maxOpenWindows": 50,
  "logLevel": "Information"
}
```

> **v1 的问题**：v1 §9 的 JSON 和 §94 的 `AppSettings` 类**各缺一半**——JSON 里没有 `notesFolder`（而启动流程第一步就要读它），类里没有 `defaultColor` / `defaultWidth` / `singleClickTrayAction` / `globalNewNoteHotkey` 等。v2 的规则是：**§8.2 的 JSON 就是 §9.3 的 `AppSettings` 类的序列化结果，两者必须逐字段对应**，改其一必须改其二。

### 字段说明（仅列不直观的）

| 字段 | 说明 |
|---|---|
| `notesFolder` | 空字符串表示"尚未选择"，启动时进入首次运行向导（§8.6） |
| `attachmentsFolderName` | 相对的目录名，**不含路径分隔符**（校验见 §19.4） |
| `theme` | `system` / `light` / `dark`。默认跟随系统（§15.3） |
| `showStatusBar` | 便签底部的"已保存 / 字数"状态条是否显示（§15.2） |
| `restoreAfterShowDesktop` | 「显示桌面」（Win+D）期间是否把不置顶的便签临时提到置顶档，让它们不被升起的桌面盖住，**默认 `true`**。关掉则便签被桌面盖住后就不再管，要等用户点回别的窗口才露出来。机制见 §13.6 |
| `minimizeToTrayOnClose` | 关闭**管理器窗口**时的行为：true = 收进托盘，false = 退出应用。**只影响管理器窗口**，便签窗口的关闭语义固定（§17.3） |
| `singleClickTrayAction` | 托盘图标的单击行为：`toggleManager` / `newNote` / `showAllNotes` |
| `globalQuickCaptureHotkey` | 速记浮窗的全局热键，默认 `Ctrl+Shift+N`（§15.7、§17.6） |
| `globalShowAllHotkey` | "显示全部便签"的全局热键，默认 `Ctrl+Alt+N`（§13.6） |
| `searchDebounceMs` | 搜索输入停止多久后执行，默认 150ms（§12.4） |
| `maxOpenWindows` | 同时打开的便签窗口上限，超过时提示用户。见 §13.9 |

**v2 从 v1 删掉的四个字段及原因**（不要因为"v1 有"就加回来）：

| 删掉的字段 | 原因 |
|---|---|
| `keepNotesVisibleOnShowDesktop` | v1 这个开关想靠未公开手段让便签"豁免"Win+D。v2 删掉它时（§13.6 实测之前）的结论是"豁免只有置顶一条路，没有可切的"。**后来 §13.6 实测出第二条路**——「显示桌面」期间把便签临时提到置顶档——这个诉求于是以 `restoreAfterShowDesktop` 这个名字回来了。两者机制完全不同：**不要照 v1 的名字把它加回去**，那个名字描述的是"豁免"，而新字段描述的是"显示桌面期间的临时处理" |
| `hideNotesFromSystemWindowList` | 任务栏与 Alt+Tab 由同一个 `WS_EX_TOOLWINDOW` 控制，且 v2 决定便签**始终显示**，没有可切换的东西（§13.4） |
| `snapDistancePx`、`snapToScreenEdges`、`snapBetweenNotes` | v2 不做便签之间的磁吸；贴屏幕边缘由系统 Aero Snap 免费提供，没有可配置项（§0.3、§13.3） |

## 8.3 layout.json

```json
{
  "version": 1,
  "displays": {
    "\\\\?\\DISPLAY#DEL41A6#5&2b1c3d4e&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}": {
      "friendlyName": "DELL U2720Q",
      "boundsPx": { "x": 1920, "y": 0, "width": 2560, "height": 1440 },
      "dpi": 144
    }
  },
  "notes": {
    "3f2a91c4-5b8e-4d17-9a62-8c1f4e7b0d33": {
      "isOpen": true,
      "displayId": "\\\\?\\DISPLAY#DEL41A6#5&2b1c3d4e&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
      "x": 1260,
      "y": 320,
      "width": 380,
      "height": 460,
      "expandedHeight": 460,
      "isCollapsed": false,
      "isTopMost": false,
      "isLocked": false
    }
  }
}
```

### v2 相对 v1 的关键新增

| 字段 | 为什么必需 |
|---|---|
| **`isOpen`** | v1 最大的数据模型缺口：`NoteLayout` 和 `layout.json` 都没有记录"这张便签退出时是打开的"，但 v1 §45 要求启动时"恢复需要显示的窗口"、v1 §63 要求只给可见便签建窗口。**没有这个字段，会话恢复无法实现**。这是 v2 新增的最重要字段 |
| **`expandedHeight`** | v1 只有 `Height`。折叠时如果改 `Height`，展开就不知道原高度；如果不改 `Height`，窗口真实尺寸与视觉尺寸不一致，会污染工作区夹取与显示器判定（§15.2） |
| **`displayId` + `displays`** | 用稳定的显示器设备路径而不是 `\\.\DISPLAY1`。设备名在热插拔/重启后可能被重新分配（§13.8） |

### 坐标单位

**`x` / `y` / `width` / `height` / `expandedHeight` 全部是物理像素（physical pixels），不是 DIP。**

理由与后果：

- 物理像素是 Win32 API 的原生单位，`SetWindowPos`、`GetWindowRect`、显示器工作区全部用像素，中间不需要换算，减少出错面
- 恢复时要知道**保存当时的 DPI** 才能正确还原视觉大小——所以 `displays` 里记了 `dpi`
- 如果保存后用户改了缩放（100% → 150%），恢复时需要按新 DPI 把宽高缩放（`width * newDpi / oldDpi`），位置坐标则保持像素值然后做边界裁剪
- 这条规则必须在 `LayoutService` 里**只有一处实现**，任何其他地方都不允许直接读写 `NoteLayout` 的坐标

### 文件级规则

| 规则 | 说明 |
|---|---|
| 未在 layout 中出现的便签 | 新建便签时按 §17.2 的算位算法分配位置，`isOpen` 默认为 `true` |
| 便签被删除 | **不清除 layout 条目**（保留 30 天或直到 id 被复用）。理由：用户从回收站恢复后，窗口位置能原样回来（§7.3） |
| 条目过多 | 定期清理：`isOpen == false` 且对应的 .md 已不存在超过 90 天的条目 |
| 显示器已不存在 | 恢复时按 §13.8 的"显示器不存在时"策略重新定位 |
| 文件损坏 | 全部用默认值，把损坏文件重命名为 `layout.json.corrupt-{时间戳}` 并记 Warning |

## 8.4 JSON 命名与版本迁移

### 命名策略

**统一使用 camelCase 键名**，通过显式特性固定，不依赖全局策略：

```csharp
public sealed class AppSettings
{
    [JsonPropertyName("notesFolder")]
    public string NotesFolder { get; set; } = "";

    [JsonPropertyName("autoSaveDelayMs")]
    public int AutoSaveDelayMs { get; set; } = 500;
    ...
}
```

**为什么用显式特性而不是 `JsonNamingPolicy.CamelCase`**：全局策略下改一个 C# 属性名就会静默改变磁盘上的键名，老配置文件读不出来（字段静默回到默认值，用户设置丢失）。显式特性让"改属性名"和"改磁盘格式"变成两件独立的事。

v1 在这里有两套说法：§12 的 `NoteLayout` 用 `Collapsed`/`AlwaysOnTop`，§93 用 `IsCollapsed`/`IsTopMost`，而 §8 的 JSON 示例用的是 `collapsed`/`alwaysOnTop`。v2 统一为一套（§9.3 的模型定义）。

### 序列化格式

```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    WriteIndented = true,              // 用户可能要手改，保持可读
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,   // 全部写出，便于用户看到可用的设置项
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping  // 让中文路径不被转义成 \uXXXX
};
```

**`UnsafeRelaxedJsonEscaping` 是必需的**：默认编码器会把中文路径（`D:\笔记`）转义成 `D:\u7B14\u8BB0`，用户打开配置文件会以为坏了。

### 版本迁移

两个配置文件都带 `version` 字段，当前都是 `1`。

```csharp
// 读取流程
var json = File.ReadAllText(path);
var doc = JsonNode.Parse(json);
int version = doc?["version"]?.GetValue<int>() ?? 1;   // 缺省视为 1

switch (version)
{
    case 1: /* 当前版本 */ break;
    case > 1:
        // 配置文件来自更新的版本：不要尝试解析，直接改名保留并用默认值启动
        // 提示用户"配置文件由更新版本的程序创建"
        break;
}
```

**向后兼容的字段增删规则**：

- **新增字段**：必须给默认值，老配置文件读进来后该字段用默认值（不要抛异常）
- **删除字段**：保持读入但忽略（不要报错），下次写回时自然消失
- **改字段语义**：必须升 `version`，并写迁移代码
- **改字段名**：视为"删旧增新"，同时写迁移代码把老键名的值搬到新键名

## 8.5 布局保存时机

v1 定义了 `SaveLayout(noteId)` 这样的按便签保存接口，但 `layout.json` 是一整个文件，按单便签保存意味着频繁的全文件重写（每次窗口移动都写一次，还要走原子写入的"临时文件 + 替换"）。

**v2 改为：整体保存 + 触发点 + 节流。**

```text
触发保存的时机：
  1. 窗口移动/缩放结束（WM_EXITSIZEMOVE）
  2. 折叠/展开切换完成
  3. 置顶/锁定切换
  4. 便签打开/关闭
  5. 应用退出前（必须，同步执行）

节流规则：
  - 前 4 项触发时，设置一个 1 秒的 DispatcherTimer
  - 定时器到期时执行一次整文件写入
  - 期间多次触发只写一次（自然合并）
  - 退出时绕过节流，立即同步写入
```

**拖动窗口期间绝不写磁盘**。这条要在 §20 的性能目标里体现（拖动时 CPU 使用率不因磁盘 IO 抖动）。

**写入失败的处理**：布局写入失败**不打断用户操作**，只记 Warning 日志。布局是设备状态，丢了顶多是窗口位置回到默认，不影响数据。

## 8.6 笔记目录的首次设置与切换

**这是 v1 完全没有定义的一条完整流程**，而它是用户第一次启动必然遇到的第一件事。

### 首次运行（笔记目录未设置）

```text
启动 → 读 settings.json → NotesFolder 为空或指向不存在的目录
  ↓
弹出「首次运行」向导（一个简单的窗口，不是裸的文件夹选择框）
  ↓
┌───────────────────────────────────────────────┐
│  欢迎使用 LumiMemo                             │
│                                               │
│  选择一个文件夹来存放你的便签。                 │
│  每张便签是一个独立的 .md 文件，                │
│  你可以用任何编辑器打开它们。                   │
│                                               │
│  ○ 使用默认位置                                │
│    C:\Users\Ning\Documents\LumiMemo           │
│                                               │
│  ○ 选择一个已有文件夹                          │
│    如果你已经在用 Obsidian 之类的工具管理笔记，  │
│    可以直接选择同一个文件夹。                   │
│    [ 浏览... ]                                │
│                                               │
│  [ 开始使用 ]                                  │
└───────────────────────────────────────────────┘
```

**默认位置用 `Documents\LumiMemo` 而不是 `%LOCALAPPDATA%`**：便签是**用户文档**，应该出现在用户的文档目录里、被用户看得到、被其他备份工具覆盖到。`%LOCALAPPDATA%` 是给程序和配置用的（§8.1）。这两个位置的语义不同，不能混。

**选择已有文件夹时**要立刻扫描并告诉用户发现了什么：

```text
选择的文件夹里有 37 个 .md 文件
其中 12 个有 LumiMemo 的 Front Matter，25 个没有（会作为新便签加入）

⚠ 这个文件夹不是空的。LumiMemo 只会读写其中的 .md 文件，
   不会删除或修改其他文件。
```

**`.md` 之外的文件一律不碰**。文件夹里可能有 `.pdf`、图片、其他工具的配置文件——程序只认 `.md`，其他文件既不读也不写。

**警告非空目录**：如果文件夹里有大量非 `.md` 文件，明确提示"这个文件夹看起来不是专门用来放便签的，确定要用它吗？"。用户误选了主目录或桌面会导致几百个文件被扫描进来。

### 切换笔记目录

**这是一个破坏性操作，必须走完整流程。**

```text
设置页 → 更改笔记目录
  ↓
1. 检查新目录
   - 是否存在（不存在 → 提示"目录不存在，是否创建？"）
   - 是否可写（写入一个临时文件再删除）
   - 是否是当前目录（是 → 无操作）
   - 是否与当前目录有嵌套关系
       - 新目录在当前目录之内 → 只警告（不阻止）
       - 当前目录在新目录之内 → 只警告（不阻止）
       - **完全相同的路径** → 拒绝
  ↓
2. 关于未保存的内容
   - 强制 flush 所有便签（§11.1）
   - 任何一个保存失败 → **中止切换**，提示先处理保存失败
  ↓
3. 关于当前的 layout.json / 回收站
   - 三选一（必须让用户选，不能默认）：
       ① 保留当前的窗口布局（适用于"同一个笔记库换了盘符/路径"）
       ② 重新生成布局（适用于"换了一套完全不同的笔记"）
       ③ 保留回收站内容
     → 默认全选，但明确列出后果
  ↓
4. 确认对话框（列出所有后果）
  ↓
5. 执行
   - 关闭所有便签窗口
   - 停止 FileSystemWatcher
   - 清空 NoteStore 与 SearchIndex
   - 写入新的 NotesFolder 到 settings.json
   - 用新目录重新执行 §17.1 的第 5–6 步（清理临时文件 + 全量扫描）
   - 重启 FileSystemWatcher
   - 按第 3 步的选择恢复或重建 layout
  ↓
6. 结果反馈
   - 新目录里发现 N 张便签
   - 恢复 M 个窗口
  ↓
7. **旧目录一个字节都不动**
```

**最重要的一条：切换目录不移动、不删除、不修改旧目录里的任何文件。** 这是纯引用变更。用户如果需要把文件搬过去，那是用户自己在资源管理器里做的操作，不是便签程序的职责。想让程序"顺便帮忙搬"的需求听起来合理，但它意味着程序要去移动用户的整个笔记库，风险远大于便利。

**如果不做这一步**，最坏的情况是用户以为"切换目录 = 移动文件"，切完发现新目录是空的、旧目录还在，产生"我的笔记没了"的恐慌。所以第 4 步的确认对话框必须**明确写出"旧目录的文件不会被移动"**。

**目录不存在时**：不要静默创建，先问。用户可能只是路径打错了，静默创建一个空目录会掩盖错误。

---

# 9. 内存数据层

## 9.1 NoteStore

`NoteStore` 是运行时的便签内存状态。**它不是数据库**，也不做 IO。

```csharp
public sealed class NoteStore
{
    private readonly Dictionary<Guid, Note> _notes = new();
    private Note[] _orderedCache = [];

    public Note? TryGet(Guid id) => _notes.GetValueOrDefault(id);

    // 返回快照：调用方可以安全遍历，Store 在别处增删不会影响它
    public IReadOnlyList<Note> Snapshot() => _orderedCache;

    public bool Contains(Guid id) => _notes.ContainsKey(id);

    // 全部写入方法只能在 UI 线程调用（§3.4 规则 T1）
    public void Add(Note note);
    public void Update(Note note);          // 同时使 Title 派生缓存失效
    public void Remove(Guid id);
    public void Clear();
}
```

**实现要点**：

- `Snapshot()` 返回一个被缓存的数组，只在增删改时重建，避免每次搜索都 `ToArray()` 分配
- `Note` 对象本身是**可变引用**。Store 里持有的是唯一实例，ViewModel 拿到的是同一个引用，因此不存在"两份数据"问题（§18.4）
- **不在这里发事件**。Store 只负责状态，事件由 `NoteService` 在完成一次完整操作后统一发出（避免"改了一半就通知"）

## 9.2 模型定义（唯一权威）

> **这是本文档唯一的模型定义处。** v1 在 §11/§12 和 §87/§93 各定义了一遍且互相冲突（属性名不同、字段不同），v2 合并到此处，其他章节只引用不重复。

### Note

```csharp
public sealed class Note
{
    /// <summary>稳定身份，来自 Front Matter 的 id。</summary>
    public required Guid Id { get; init; }

    /// <summary>Markdown 文件的完整路径。移动便签时变化，Id 不变。</summary>
    public required string FilePath { get; set; }

    /// <summary>
    /// 正文（不含 Front Matter）。
    /// 这是<b>用户数据</b>，是唯一权威内容来源。
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>派生属性：不在存储中，改 Content 时失效重算（§5.4）。</summary>
    public string Title => _titleCache ??= TitleDeriver.Derive(Content);
    private string? _titleCache;
    internal void InvalidateTitle() => _titleCache = null;

    public NoteColor Color { get; set; } = NoteColor.Yellow;

    public List<string> Tags { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>文件原有的行尾风格，写回时沿用（§5.9）。</summary>
    public LineEnding LineEnding { get; set; } = LineEnding.CrLf;

    /// <summary>解析时文件是否带 BOM，用于日志与诊断。</summary>
    public bool HadBom { get; set; }

    /// <summary>
    /// Front Matter 中本程序不认识的键，解析时保留、写回时原样输出（§5.3）。
    /// 必须是保序容器——用 Dictionary 会导致写回顺序随机、git diff 抖动。
    /// </summary>
    public List<KeyValuePair<string, object?>> UnknownFrontMatterKeys { get; set; } = [];

    /// <summary>解析过程中发现的异常（编码异常、YAML 损坏等），用于在 UI 上提示（§5.10）。</summary>
    public List<NoteParseIssue> ParseIssues { get; set; } = [];
}
```

**注意与 v1 的差异**：

- v1 的 `Note` 有两个版本：§11 有 `FilePath` 用 `Id { get; init; }`；§87 没有 `FilePath` 用 `Id { get; set; }`。v2 取 §11 的形状（`FilePath` 必需，`Id` 是 `init`），并补齐了 v1 缺失的 `LineEnding`、`HadBom`、`UnknownFrontMatterKeys`、`ParseIssues`
- 新增 `Title` 作为**派生属性**，不再是存储字段

### NoteLayout

```csharp
public sealed class NoteLayout
{
    public required Guid NoteId { get; init; }

    /// <summary>退出时这张便签是否是打开的。启动时据此恢复窗口（§8.3）。</summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>所在显示器的设备路径，跨会话稳定（§13.8）。</summary>
    public string? DisplayId { get; set; }

    // 以下坐标与尺寸全部是「物理像素」，不是 DIP（§8.3）
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 420;

    /// <summary>
    /// 保存这份布局时目标显示器的 DPI（96 / 120 / 144 / 192 …）。
    /// 恢复时按 currentDpi / Dpi 缩放物理尺寸，保证跨 DPI 显示器后外观大小一致（§13.8）。
    /// </summary>
    public uint Dpi { get; set; } = 96;

    /// <summary>展开状态下的高度。折叠时 Height 变成标题条高度，展开时恢复为此值（§15.2）。</summary>
    public double ExpandedHeight { get; set; } = 420;

    public bool IsCollapsed { get; set; }
    public bool IsTopMost { get; set; }
    public bool IsLocked { get; set; }
}
```

**注意与 v1 的差异**：v1 §12 用 `Collapsed` / `AlwaysOnTop` / `Locked`，v1 §93 用 `IsCollapsed` / `IsTopMost` / `IsLocked`。v2 统一为 §93 的写法（`Is` 前缀），并补齐 `IsOpen`、`Dpi`、`ExpandedHeight`、`DisplayId`。

**`Dpi` 为什么是每便签一个而不是每显示器一个**：`layout.json` 的 `displays` 表记录的是"每台显示器**当前**的 DPI"，会随用户改缩放设置而变；而 `NoteLayout.Dpi` 记录的是"这份布局**保存时**的 DPI"，是一个历史快照，两者必须分开。窗口摆位只依赖后者（§13.8）；前者仅用于诊断与显示器识别。**不要用 `displays[displayId].dpi` 去算缩放**。

### 其他模型

```csharp
public enum NoteColor { Yellow, Pink, Blue, Green, Purple, Orange, Gray }

public enum LineEnding { Lf, CrLf }

public enum NoteParseIssueKind
{
    MissingId, DuplicateId, InvalidYaml, InvalidEncoding,
    FileTooLarge, UnreadablePath, TemporarilyLocked
}

public sealed record NoteParseIssue(NoteParseIssueKind Kind, string? Detail);

public sealed class TrashEntry
{
    public required string TrashName { get; init; }
    public required string OriginalRelativePath { get; set; }
    public Guid? NoteId { get; set; }
    public DateTimeOffset DeletedAt { get; set; }
    public TrashEntryKind Kind { get; init; }
    public long Size { get; set; }
}

public enum TrashEntryKind { File, Directory }

/// <summary>正文中对附件的引用，扫描孤儿附件时用（§6.4）。</summary>
public sealed record AttachmentRef(string RelativePath, string FileName);
```

> **v1 遗留的 `NoteMetadata` 类被删除**。v1 在 §4 的架构图和 §5 的目录里都列了它，但全文没有定义。它的职责（Front Matter 的字段集合）已经由 `Note` 本身承担。

## 9.3 AppSettings

```csharp
public sealed class AppSettings
{
    [JsonPropertyName("version")]              public int Version { get; set; } = 1;

    [JsonPropertyName("notesFolder")]          public string NotesFolder { get; set; } = "";
    [JsonPropertyName("attachmentsFolderName")] public string AttachmentsFolderName { get; set; } = "attachments";

    [JsonPropertyName("theme")]                public string Theme { get; set; } = "system";
    [JsonPropertyName("defaultColor")]         public NoteColor DefaultColor { get; set; } = NoteColor.Yellow;
    [JsonPropertyName("defaultWidth")]         public double DefaultWidth { get; set; } = 360;
    [JsonPropertyName("defaultHeight")]        public double DefaultHeight { get; set; } = 420;
    [JsonPropertyName("showStatusBar")]        public bool ShowStatusBar { get; set; } = true;
    [JsonPropertyName("restoreAfterShowDesktop")] public bool RestoreAfterShowDesktop { get; set; } = true;

    [JsonPropertyName("startWithWindows")]     public bool StartWithWindows { get; set; }
    [JsonPropertyName("minimizeToTrayOnClose")] public bool MinimizeToTrayOnClose { get; set; } = true;
    [JsonPropertyName("showTrayIcon")]         public bool ShowTrayIcon { get; set; } = true;
    [JsonPropertyName("singleClickTrayAction")] public string SingleClickTrayAction { get; set; } = "toggleManager";

    [JsonPropertyName("globalQuickCaptureHotkey")] public string GlobalQuickCaptureHotkey { get; set; } = "Ctrl+Shift+N";
    [JsonPropertyName("globalShowAllHotkey")]      public string GlobalShowAllHotkey { get; set; } = "Ctrl+Alt+N";

    [JsonPropertyName("autoSaveDelayMs")]      public int AutoSaveDelayMs { get; set; } = 500;
    [JsonPropertyName("searchDebounceMs")]     public int SearchDebounceMs { get; set; } = 150;

    [JsonPropertyName("trashRetentionDays")]   public int TrashRetentionDays { get; set; } = 30;

    [JsonPropertyName("enableAnimations")]     public bool EnableAnimations { get; set; } = true;
    [JsonPropertyName("maxOpenWindows")]       public int MaxOpenWindows { get; set; } = 50;
    [JsonPropertyName("logLevel")]             public string LogLevel { get; set; } = "Information";
}
```

**这个类与 §8.2 的 JSON 示例必须逐字段一致。** 改一处必须改另一处——这是 v1 出问题的地方，v2 用一个显式的规则钉住。

**`AutoSaveDelayMs` 的合法范围是 300–800**（§11.1）。读入时做钳制，超范围的配置值不生效但也不报错，改写日志。`SearchDebounceMs` 的范围是 100–500。

**这四个边界值住在 `JsonSettingsStore` 的 `public const` 上**（`MinAutoSaveDelayMs = 300`、`MaxAutoSaveDelayMs = 800`、`MinSearchDebounceMs = 100`、`MaxSearchDebounceMs = 500`），而不是各自散在读写两处。设置窗口（§15.9）复用同一批常量——它是唯一会**主动**越界的地方（用户在输入框里手打一个 9999），如果那边自己写一份数字，两处迟早会对不上，而且表现是"界面上接受了、重启后变回去了"这种最难查的一类。

**属性用 `string` 而不是枚举**（`theme`、`singleClickTrayAction`、`logLevel`）：配置文件可能被用户手改或来自降级后的旧版本，用枚举会让一个不认识的字符串导致整份配置反序列化失败，退化成"所有设置都丢了"。用字符串 + 读取时校验并回退到默认值，容错性更好。

## 9.4 SearchIndex

`SearchIndex` 是从 `NoteStore` 派生的辅助索引，用于加速搜索与标签浏览。

```csharp
public sealed class SearchIndex
{
    // 标签（忽略大小写）→ 便签集合
    private readonly Dictionary<string, HashSet<Guid>> _byTag = new(StringComparer.OrdinalIgnoreCase);

    // 便签 id → 正文化（小写、去 Markdown 标记）的缓存，用于排序与摘要
    private readonly Dictionary<Guid, string> _plainText = new();

    public void Rebuild(IReadOnlyList<Note> notes);
    public void OnNoteAdded(Note note);
    public void OnNoteUpdated(Note note);
    public void OnNoteRemoved(Guid id);

    public IReadOnlySet<Guid>? NotesWithTag(string tag);
    public IReadOnlyCollection<string> AllTags { get; }
    public string GetPlainText(Guid id);
}
```

**维护时机**：与 `NoteStore` 的所有变更同步（`NoteService` 在完成一次修改后同时更新两者）。索引是**纯内存的派生数据，不落盘**。

**`_plainText` 的用途**：搜索时不能直接拿 Markdown 原文做匹配——否则搜 "docker" 会命中 ```` ```docker ```` 这种围栏标记，而搜 "标题" 会因为正文里的 `#` 而漏掉。缓存一份去掉 Markdown 标记的纯文本，用于匹配和摘要生成。

---

# 10. 文件监听

外部编辑器改了 Markdown，程序必须能感知。这一章决定"与 Obsidian 互通"能否真正兑现。

## 10.1 监听配置

```csharp
var watcher = new FileSystemWatcher(notesFolder)
{
    Filter = "*.md",
    IncludeSubdirectories = true,

    // 缓冲区大小：默认 8192 字节，批量操作必然溢出，见 §10.4
    InternalBufferSize = 64 * 1024,

    NotifyFilter =
        NotifyFilters.FileName |    // 重命名、创建、删除
        NotifyFilters.LastWrite |   // 内容修改
        NotifyFilters.Size          // 部分编辑器的"原地写"只改大小
};

watcher.Created += OnCreated;
watcher.Changed += OnChanged;
watcher.Deleted += OnDeleted;
watcher.Renamed += OnRenamed;
watcher.Error   += OnError;         // 必须订阅，见 §10.4
watcher.EnableRaisingEvents = true;
```

**必须显式设置 `InternalBufferSize`**。默认 8KB 在"用户把一整个文件夹的笔记复制进来"或"git checkout 切换分支"时会立刻溢出。

**必须订阅 `Error` 事件**。这是 v1 完全遗漏的：溢出后 `FileSystemWatcher` **不会自动恢复**，后续所有变更都会静默丢失——对"Markdown 是唯一数据源"的产品来说，这是系统性数据不一致的来源。

## 10.2 事件去抖

`FileSystemWatcher` 的事件不是严格"一文件一次"。一次保存可能触发：

```text
Created → Changed → Changed → Renamed
```

**按文件路径去抖**，参数：

```text
去抖窗口 = 300ms（可按需调到 200~500ms）
键 = 规范化后的完整路径（ToLowerInvariant，因为 Windows 文件系统不区分大小写）
```

实现：

```csharp
private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();

private void ScheduleProcess(string fullPath)
{
    var key = Normalize(fullPath);

    // 取消该路径上一次的待处理任务
    if (_pending.TryRemove(key, out var old))
    {
        old.Cancel();
        old.Dispose();
    }

    var cts = new CancellationTokenSource();
    _pending[key] = cts;

    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(DebounceMs, cts.Token);
            await ProcessFileAsync(key, cts.Token);
        }
        catch (OperationCanceledException) { /* 被更新的同路径事件取代，正常 */ }
        finally
        {
            _pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cts));
            cts.Dispose();
        }
    }, cts.Token);
}
```

**关键点**：

- 用 `TryRemove(KeyValuePair)` 重载而不是 `TryRemove(key)`——后者会把**别人刚放进去的新 cts** 删掉，导致新事件被吞
- 去抖窗口要**大于**外部编辑器"先写临时文件再重命名"的耗时（VS Code、Obsidian 的原子保存通常是几十毫秒，300ms 足够）

## 10.3 防自触发

程序自己写文件也会触发 watcher，如果不加处理会造成：光标跳动、内容闪烁、无限循环、重复保存。

**v2 用两层防护，两层都要有。**

### 第一层：写入抑制窗口

```csharp
// 记录本程序刚刚写过的路径与时间
private readonly Dictionary<string, DateTimeOffset> _internalWrites = new();

private void MarkInternalWrite(string fullPath)
    => _internalWrites[Normalize(fullPath)] = _clock.Now;

private bool IsInternalWrite(string fullPath)
{
    if (!_internalWrites.TryGetValue(Normalize(fullPath), out var t)) return false;
    return (_clock.Now - t) < SuppressWindow;   // 默认 3 秒
}
```

在 watcher 的入口处检查：命中则**直接丢弃事件**，不做任何后续处理。

### 第二层：内容 hash 比对

**v1 的方案**是"记下刚才写入的 hash，事件到达时比对，相等就忽略"。这个方案单独用会有漏洞：

```text
t=0    用户停止输入，启动 500ms 去抖
t=500  自动保存写文件（记下 hash = H1）
t=520  watcher 事件入队
t=560  用户又输入了一个字，启动新的去抖
t=800  watcher 的去抖窗口到期，读取文件 → 内容已经是新的了 → hash = H2 ≠ H1
       → 误判为"外部修改" → 触发冲突提示对话框
```

**修正**：维护一个**有界的最近写入 hash 集合**，而不是"最后一个 hash"。

```csharp
// 每个路径保留最近 N 条写入 hash（含时间戳），超过 5 秒的自动过期
private readonly Dictionary<string, Queue<(string Hash, DateTimeOffset At)>> _recentHashes = new();
private const int MaxRecentHashesPerPath = 8;
private static readonly TimeSpan HashRetention = TimeSpan.FromSeconds(5);

private bool MatchesRecentWrite(string fullPath, string currentContentHash)
{
    if (!_recentHashes.TryGetValue(Normalize(fullPath), out var queue)) return false;
    var cutoff = _clock.Now - HashRetention;
    return queue.Any(e => e.At >= cutoff && e.Hash == currentContentHash);
}
```

**hash 算法**：用 `System.IO.Hashing.XxHash64`（需引 `System.IO.Hashing` 包）或 `SHA256` truncated。便签内容通常只有几 KB，`SHA256` 的性能完全够用且**不需要额外依赖**，建议直接用 `SHA256.HashData` 并只比较前 8 字节。

**内容 hash 的一致性**：比对时的内容必须与写入时**按同样的方式归一化**（同样的编码、同样的行尾）。建议直接对"将要写入磁盘的 byte 序列"取 hash，读回后对"从磁盘读出的 byte 序列"取 hash——这样连 BOM 和行尾的差异都不会造成误判。

### 判定流程

```text
收到 watcher 事件（已去抖）
  ↓
IsInternalWrite(path)?  ──是──→ 丢弃
  ↓ 否
读文件内容 → 计算 hash
  ↓
MatchesRecentWrite(path, hash)?  ──是──→ 丢弃
  ↓ 否
判定为「真正的外部修改」→ 进入 §11.4 的冲突处理
```

## 10.4 缓冲区溢出与错误恢复

```csharp
private void OnError(object sender, ErrorEventArgs e)
{
    var ex = e.GetException();

    if (ex is InternalBufferOverflowException)
    {
        _logger.LogWarning("文件监听缓冲区溢出，将执行全量重扫");

        // 必须做的三件事，缺一不可：
        // 1. 全量重扫笔记目录
        // 2. 与 NoteStore 做差异比对，补齐遗漏的增删改
        // 3. 重置 watcher（Dispose + 重建），因为溢出后的实例状态不可信
        _ = _rescanCoordinator.RequestFullRescanAsync();
        ResetWatcher();
    }
    else
    {
        _logger.LogError(ex, "文件监听发生异常，重置 watcher");
        ResetWatcher();
    }
}
```

**差异比对的方法**（全量重扫后）：

```text
磁盘上的文件集合 vs NoteStore 里的便签集合
  ├─ 磁盘有、Store 没有       → 新增便签
  ├─ Store 有、磁盘没有       → 删除便签
  ├─ 两边都有但 Content hash 不同 → 按外部修改处理（§11.4）
  └─ 两边都有且相同           → 不动（避免无谓的 UI 刷新）
```

**全量重扫要节流**：连续多次溢出时（比如用户一次拖入 5000 个文件），不要每次都扫。用 1 秒的合并窗口，期间只安排一次重扫。

## 10.5 网络盘与云同步目录

`FileSystemWatcher` 底层是 `ReadDirectoryChangesW`，在以下位置**会漏事件或不触发**：

- SMB / 网络共享
- 部分 NAS 的映射盘
- OneDrive 的某些模式（尤其是"文件按需"下服务端触发的下载）
- 虚拟磁盘 / 部分加密卷

**处理策略**：

1. **启动时检测**：用 `DriveInfo.DriveType` 判断笔记目录所在卷。如果是 `Network`，或路径落在已知的云同步目录下，在设置页和首次启动时给出提示：

   > 检测到笔记目录位于网络位置，此处的文件变更监听可能不可靠。建议使用本地目录，或在更换编辑器后手动点击"重新加载"。

2. **提供手动兜底**：托盘菜单和设置页加一个"重新加载全部便签"命令，执行一次全量重扫（复用 §10.4 的比对逻辑）。这是给网络盘用户的逃生出口。

3. **不在 watcher 正常时启轮询**。v1 的原则"优先事件驱动、禁止轮询"继续有效。轮询只作为**可选的、用户显式开启的**兜底（默认关闭），间隔不低于 30 秒，且只在检测到网络目录时才在设置里出现这个开关。

**OneDrive "文件按需"的额外注意**：笔记文件可能是"仅联机可用"的占位符，读取时会触发下载（可能很慢或失败）。解析时遇到 `FileAttributes.Offline` 或读取超时，按 §5.10 的"文件被锁定"处理，不要无限等待。

## 10.6 事件到 UI 的封送

`FileSystemWatcher` 的事件回调**在线程池线程上执行**。所有后续处理必须先切回 UI 线程（§3.4 规则 T3）：

```csharp
private async Task ProcessFileAsync(string fullPath, CancellationToken ct)
{
    // 以下全部在后台线程：IO 密集，可以在这里做
    if (IsInternalWrite(fullPath)) return;

    var readResult = await _repository.ReadFileAsync(fullPath, ct);
    if (MatchesRecentWrite(fullPath, readResult.ContentHash)) return;

    // 到这里才切 UI 线程：应用变更
    await _dispatcher.InvokeAsync(() =>
    {
        _noteService.ApplyExternalChange(fullPath, readResult);   // 触碰 NoteStore / 发事件
    }, DispatcherPriority.Background);
}
```

**用 `DispatcherPriority.Background` 而不是 `Normal`**：外部批量修改（比如 git 切分支）时会有大量事件涌入，用 `Background` 让用户的输入和 UI 交互优先，避免界面卡顿。用 `Normal` 会让批量事件把输入事件挤在后面，表现为"打字一顿一顿的"。

---

# 11. 保存

## 11.1 自动保存

```text
用户输入
  ↓
500ms 无新输入（可配置 300~800ms）
  ↓
保存
```

**绝不允许每个字符写一次文件。**

### AutoSaveService 结构

```csharp
public sealed class AutoSaveService
{
    // 按便签独立计时：多个便签同时编辑互不影响
    private readonly Dictionary<Guid, DispatcherTimer> _timers = new();
    private readonly INoteService _noteService;
    private readonly IClock _clock;

    public void ScheduleSave(Guid noteId)
    {
        if (_timers.TryGetValue(noteId, out var timer))
            timer.Stop();

        var t = new DispatcherTimer(
            TimeSpan.FromMilliseconds(_settings.AutoSaveDelayMs),
            DispatcherPriority.Background,
            OnTimerTick,
            _dispatcher);

        t.Tag = noteId;
        _timers[noteId] = t;
        t.Start();
    }

    /// <summary>退出前调用：立即保存所有待保存的便签，同步执行。</summary>
    public async Task FlushAllAsync();

    /// <summary>
    /// 关闭单张便签前调用：若有待保存内容则立即同步保存，并停掉该便签的计时器。
    /// 与 FlushAllAsync 的区别是"只针对一张"，且是同步的——窗口 Closing 里不能 await。
    /// </summary>
    public void SaveNow(Guid noteId);

    /// <summary>
    /// 取消该便签尚未触发的计时器，**不保存**。便签被删除 / 移入回收站 /
    /// ViewModel 被释放时调用（§18.3）——这时要保存的是已经被删掉的东西。
    /// </summary>
    public void CancelScheduledSave(Guid noteId);
}
```

**用 `DispatcherTimer` 而不是 `System.Timers.Timer`**：`DispatcherTimer` 的回调天然在 UI 线程上，从根源上避免跨线程问题（§3.4 规则 T4）。`System.Timers.Timer` 需要手动封送，是这个项目里很容易出错的点。

**放在哪里**：放在 App 层的 `AutoSaveService`，由 `NoteViewModel` 的 `Content` setter 调用：

```csharp
partial void OnContentChanged(string value)
{
    _noteService.ApplyLocalEdit(Model);        // 更新 NoteStore 里的内容
    _autoSave.ScheduleSave(Model.Id);          // 只安排，不立即保存
}
```

**`NoteViewModel` 不自己写 Timer**（v1 §109 的判断是对的，v2 保留）。

## 11.2 原子保存

**禁止** `File.WriteAllText(path, content)`——崩溃或断电时文件会变成空文件或写了一半的内容。

### 流程

```text
1. 目标路径 = P
2. 临时路径 = P 同目录下的 {P}.{yyyyMMddHHmmssfff}-{8位随机}.lumitmp
3. 用 FileStream 写临时文件
   - FileMode.CreateNew（保证不会覆盖已存在的临时文件）
   - FileShare.None
   - 写完后 fs.Flush(flushToDisk: true)   ← 必须真正落盘，不是只刷到 OS 缓存
4. 替换目标文件：
   - 目标存在 → File.Replace(临时, 目标, 备份: null, ignoreMetadataErrors: true)
   - 目标不存在 → File.Move(临时, 目标)
   ← 分支是必需的，见下
5. 替换失败时：重试（100ms / 300ms / 600ms，共 3 次）
6. 仍失败 → 删除临时文件，向上抛异常（由 §11.5 处理）
```

### 必须注意的六点

| # | 注意点 | 原因 |
|---|---|---|
| 1 | **`File.Replace` 要求目标已存在** | 新建便签的**第一次保存**目标不存在，会抛 `FileNotFoundException`。必须分支到 `File.Move`。这是 v1 §20 遗漏的 |
| 2 | **临时文件必须与目标同目录** | 跨卷时 `Replace`/`Move` 会退化成"复制 + 删除"，失去原子性。所以临时文件放在笔记目录内，不是 `%TEMP%` |
| 3 | **临时文件名必须带随机成分** | 固定名 `P.tmp` 在两次保存撞车、或崩溃残留下次互相覆盖 |
| 4 | **临时文件后缀不要用 `.md`** | 用 `.lumitmp` 可以保证不被 `Filter = "*.md"` 的 watcher 捕获，也不需要额外的过滤逻辑。同时在扫描笔记目录时跳过 `.lumitmp` 文件 |
| 5 | **启动时清理遗留的 `.lumitmp`** | 进程崩溃会留下临时文件。启动扫描时删除所有 `*.lumitmp`（只删这一种后缀，绝不删用户自己的 `.tmp`） |
| 6 | **`File.Replace` 会改变文件标识** | 创建时间、NTFS 文件 ID 都会变。对 OneDrive / Dropbox 这类同步工具意味着"删一个 + 新建一个"，可能产生同步流量或冲突副本；某些备份工具也会重复备份 |

### 关于第 6 点的取舍

v1 §20 只提到了"杀软/同步软件可能短暂占用文件，应支持有限次数重试"，没有提 `File.Replace` 本身的副作用。v2 的处理：

- **默认使用 `File.Replace`**（原子性优先）
- **替换后恢复逻辑时间戳**：`File.SetLastWriteTimeUtc(target, note.UpdatedAt.UtcDateTime)`，让文件系统时间和便签的 `updatedAt` 一致，减少同步工具把它判定为"新文件"
- **在文档和设置页说明**：如果用户把笔记目录放在同步服务里，会看到较多的同步活动，这是保证文件不损坏的代价
- 不使用"原地覆写 + Flush"的方案。虽然它能保持文件 ID，但崩溃时存在写入一半的窗口，风险不可接受——符合 §24 "先保证文件不会丢" 的原则

## 11.3 输入法（IME）组合期

**这是 v1 完全没有覆盖、但中文用户每天都会遇到的问题。**

场景：用户用拼音输入法打字，从敲下第一个字母到选词上屏，中间可能持续几秒。这段时间里 `TextBox.Text` 会被输入法修改（显示候选、插入临时字符）。如果自动保存在这个窗口触发，会把"拼音中间态"写进文件。

### 处理

```csharp
// 在 MarkdownTextBox 控件里
TextCompositionManager.AddTextInputStartHandler(this, OnCompositionStart);
TextCompositionManager.AddTextInputUpdateHandler(this, OnCompositionUpdate);
TextCompositionManager.AddTextInputHandler(this, OnCompositionEnd);

private void OnCompositionStart(object s, TextCompositionEventArgs e)
    => _vm.IsComposing = true;

private void OnCompositionEnd(object s, TextCompositionEventArgs e)
{
    _vm.IsComposing = false;
    _vm.RequestSaveNow();       // 组合结束后立即安排一次保存（更快的反馈）
}
```

`AutoSaveService.ScheduleSave` 内部：

```csharp
if (_vm.IsComposing) return;    // 组合期不安排保存
```

**组合结束时的额外处理**：组合结束后立即（不等待 500ms 去抖）安排一次保存。因为用户输入完一个词往往就停下来了，此时给出"已保存"的视觉反馈比等 500ms 更符合直觉。

**注意**：`IsComposing` 是 ViewModel 上的 UI 状态，不是 `Note` 的字段。它属于 `NoteViewModel` 的临时状态，不落盘。

## 11.4 外部编辑冲突

场景：便签程序里改了内容，同时 VS Code 也改了同一个文件。

### 判定依据

不是靠"有没有未保存的修改"，而是靠**三路比较**：

```text
基线 = 上次从磁盘读到的内容（或上次写入的内容）
本地 = NoteStore 里的当前内容
磁盘 = 文件当前的字节
```

| 情况 | 判定 | 处理 |
|---|---|---|
| 磁盘 == 本地 | 无变化（多半是自写触发的，应该已经被 §10.3 拦截） | 忽略 |
| 磁盘 == 基线，本地 != 基线 | 用户在外部编辑器里改了，但本地没改 | **静默 reload**，更新 Store 与 UI |
| 磁盘 != 基线，本地 == 基线 | 本地改了（但还没保存），外部没改 | 合并时不可能出现（外部改了磁盘才会走到这里）；忽略 |
| 磁盘 != 基线，本地 != 基线，磁盘 == 本地 | 双方改成了同样的内容 | 静默接受，更新基线 |
| **磁盘 != 基线，本地 != 基线，且磁盘 != 本地** | **真冲突** | 弹提示，见下 |

### 冲突 UI

```text
┌─────────────────────────────────────────────┐
│  这张便签在外部被修改了                       │
│                                             │
│  文件：工作/周报-20260920-4c88f012.md        │
│  修改时间：2026-09-19 14:32                 │
│                                             │
│  你的改动还没有保存，两边的内容不一致。        │
│                                             │
│  [ 重新加载（放弃我的改动） ]                 │
│  [ 覆盖外部版本（保留我的改动） ]             │
│  [ 查看差异 ]                                │
│  [ 稍后再说 ]                                │
└─────────────────────────────────────────────┘
```

| 选项 | 行为 |
|---|---|
| 重新加载 | 用磁盘内容替换本地，丢弃本地改动 |
| 覆盖外部版本 | 把本地内容写入磁盘。**写入前先把磁盘版本另存为 `{文件名}.conflict-{时间戳}.md`**，绝不静默销毁外部修改 |
| 查看差异 | 简单的逐行 diff 视图（第一阶段可先不做，按钮置灰） |
| 稍后再说 | 不处理，便签窗口上挂一个持续的状态条，用户可以随时点开重新处理。**不要自动重复弹窗** |

**第一阶段的最小实现**：只提供"重新加载"和"覆盖外部版本"两项，且覆盖时仍然做 `.conflict-` 备份。差异视图放到第三阶段。

## 11.5 保存失败

保存失败**必须让用户知道**，且**绝不能丢内存里的内容**。

```text
保存失败
  ↓
1. 内存内容保持不变（不因为写失败就把 NoteStore 里的内容回滚）
2. 便签窗口顶部显示一条警示条：无法保存，点击查看详情
3. 状态栏持续显示"未保存"（而不是"已保存"）
4. 写日志（Warning 级，含路径与异常类型，不含正文）
5. 重试策略：如果是"文件被占用"，在 1s / 3s / 10s 后自动重试；其他错误不自动重试
6. 用户手动点"重试"时立即再试一次
```

### 常见错误与针对性处理

| 错误 | 处理 |
|---|---|
| 文件只读 | 提示"文件为只读，是否移除只读属性？"，提供一键移除（**这一条与本节其他几项一样尚未落地，见 §11.5 末的说明**） |
| 目录无权限 | 提示路径与可能的解决方向；建议检查笔记目录是否可以更换 |
| 文件被占用 | 自动重试（1s/3s/10s）。持续失败则提示"文件可能被其他程序占用" |
| 磁盘满 | 提示"磁盘空间不足"，并在状态栏保留"未保存"直到用户处理 |
| 路径过长 | 用 `\\?\` 前缀重试（§5.10）。仍然失败则提示 |
| 文件被外部删除 | 提示"文件已被删除，是否重新创建？"。用户确认后把内容写到同路径（相当于恢复） |
| 笔记目录不存在 | 提示并引导去设置里重新选择目录 |

### 退出时的处理

**退出前必须 flush 所有待保存内容**（§17.4）。**flush 失败也照退，不弹窗、不拦人。**

原设计在这里放了一个「有 N 张便签未能保存 / [重试] [另存为...] [仍然退出]」的三选一对话框。**已剔除**：保存一张便签是一次几毫秒的原子写，为它挡在退出的路上、再让用户读一个三选一的框，交互成本远大于它挡下的那点风险。用户按了退出就是想退出。

失败时只做两件事：

- **写一条 Error 日志**（§20.5 的日志落盘已接上），带上便签 id 与失败原因——这是事后唯一还查得到的东西
- 那张便签的**内存内容不丢**：它的 `Note` 仍在 `NoteStore` 里，`SaveStatus` 仍是 `Failed`。也就是说，同一个会话里若之后又有一次保存成功（用户回去改了别的字，或者自动保存去抖的下一次触发），它会跟着写下去

**本节其余几项仍未定**：上面那张「常见错误与针对性处理」表里的顶部警示条、只读文件的一键移除，以及状态条上那句可点的 `无法保存 · 点击查看`（§15.2 的四档文案之一）——它们是与这个对话框同一批的「保存失败要不要打扰用户」的问题，但**作废的只是退出时那一个对话框**。这几项留待后续单独裁决，现在按"保存失败只记日志、状态条照常显示 `无法保存`"实现。

## 11.6 崩溃恢复

500ms 的去抖窗口意味着崩溃时最多丢失最后 500ms 的输入。

**第一阶段不做**恢复文件机制（与 v1 一致），接受这个窗口。但保留 `%LOCALAPPDATA%\LumiMemo\recovery\` 目录的路径定义（§8.1 的 `IAppPaths` 里已有），便于后续加入。

**如果后续要做**，正确的做法是：

```text
- 每 5 秒（或输入 30 个字符）把"脏便签"的 id + 内容写到 recovery/{noteId}.json
- 正常退出时清空 recovery 目录
- 启动时如果 recovery 目录非空 → 说明上次是崩溃退出的
  → 对每个 recovery 文件，与磁盘内容比较
     - 磁盘更新 → 丢弃 recovery 文件（用户之后又保存过）
     - recovery 更新 → 提示用户"上次未正常退出，有 N 张便签有未保存的内容"
- recovery 文件写入不影响正常的 markdown 保存路径，是纯旁路
```

**注意**：不要在 §11.2 的原子保存路径里做恢复文件的写入（那会让每次保存都写两个文件，拖慢正常的保存）。

**实现说明（本轮已落地）**：§17.5 第二层（后台线程未处理异常）已接上，但那一条里**没有 `TryEmergencySave`**——原设计把它写成「进程即将终止时把未保存内容写进 `recovery\`」，而那正是本节说的「第一阶段不做」。所以上面这条「保留路径定义、不做机制」在实现里仍是完整的现状：`IAppPaths.RecoveryDirectory` 有定义、有替身、有用例，但**整个仓库里没有任何一处写它**。将来要补，实现要点见 §17.5 的实现说明。

---

# 12. 搜索

## 12.1 匹配

第一阶段**不引入 SQLite FTS5**，全部在内存里做（§12.4 说明何时才需要）。

匹配对象是 §9.4 的 `_plainText`（去掉 Markdown 标记的纯文本）、标题、标签：

```csharp
public IReadOnlyList<SearchHit> Search(string query)
{
    query = query.Trim();
    if (query.Length == 0) return [];

    var hits = new List<SearchHit>();

    foreach (var note in _store.Snapshot())
    {
        var title = note.Title;
        var plain = _index.GetPlainText(note.Id);

        // 1. 标题匹配
        int titlePos = title.IndexOf(query, StringComparison.OrdinalIgnoreCase);

        // 2. 标签匹配（精确 + 前缀）
        var matchedTags = note.Tags
            .Where(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 3. 正文匹配
        int bodyPos = plain.IndexOf(query, StringComparison.OrdinalIgnoreCase);

        if (titlePos >= 0 || matchedTags.Count > 0 || bodyPos >= 0)
            hits.Add(new SearchHit(note, titlePos, matchedTags, bodyPos));
    }

    return Rank(hits, query);
}
```

**实现说明（与上面的骨架不同，已按实际约束修正）**：搜索不住在 `SearchIndex` 上，而是一个纯静态类 `Core/Search/NoteSearch.cs`，签名是：

```csharp
NoteSearch.Search(
    IReadOnlyList<Note> notes,
    string? query,
    Func<Guid, string> plainTextOf,
    IReadOnlySet<Guid>? topMostIds,
    DateTimeOffset now)
```

骨架里 `_store.Snapshot()` 与 `_index.GetPlainText(...)` 那两行暗示搜索能自己够到这两个容器，但 `SearchIndex` 只是"从便签派生出来的、不落盘的"辅助索引（§9.4），**它没有便签列表**（在 `NoteStore`）、**也没有置顶状态**（在 `LayoutService`）。为了一次搜索去反向依赖两个上游容器，等于把索引从叶子变成枢纽，此后 `NoteStore` 一改就要复查索引。改成纯函数后，索引、存储、布局三者互不认识，组合交给管理器 ViewModel 做；`SearchIndex` 也不必为了可测性去造假便签列表。

`now` 显式传入同理——真实时钟会让"7 天内 +30"这类用例在临界点上偶发失败。

**空查询的语义**：`Search("")` 返回**空列表**——"没有搜索"不等于"匹配到全部"。管理器的列表在查询词为空时**不走 `Search`**，而是直接取 `NoteStore.Snapshot()` 按 `UpdatedAt` 降序展示（§15.8）。这条区分很重要：如果让空查询返回全部，那些"评分很低但确实匹配"的排序规则会把整个列表重排一遍，用户会看到列表在输入框被清空的瞬间跳动。这个模型是唯一的搜索入口，两个方法必须共用（`Rank` 也要能被列表复用），不能各写一套排序——那样会在两处给出不一致的顺序。
**关于中文**：`IndexOf` 的子串匹配对中文是完全可用的（比分词或 bigram 更精确，不会出现"搜 Docker 命中了 Doc 和 ker"这种假阳性）。PinSlip 用 bigram 是因为它的搜索是增量的、需要跨词边界匹配；本项目是全量内存扫描，直接用子串匹配即可。

**大小写**：用 `OrdinalIgnoreCase`。中文没有大小写问题，英文按字节序忽略大小写即可（不需要 `CultureInfo` 的复杂规则）。

## 12.2 排序

**v1 的搜索没有排序**——结果是 `Dictionary` 的遍历顺序，用户最想要的那条可能排在 200 条结果的第 87 位。v2 加入明确排序。

**评分规则**（分数越高越靠前）：

| 匹配位置 | 基础分 |
|---|---|
| 标题完全等于查询 | 1000 |
| 标题以查询开头 | 800 |
| 标题包含查询 | 600 |
| 标签完全等于查询 | 500 |
| 标签包含查询 | 400 |
| 正文包含查询 | 200 |

**加权修正**：

| 修正项 | 分值 | 理由 |
|---|---|---|
| 匹配位置在正文开头（前 100 字符内） | ×1.5 | 开头通常是摘要性内容，更可能是用户要找的 |
| 匹配在正文中出现的次数 | 每次 +10（上限 +100） | 出现多次说明主题相关 |
| **置顶的便签** | +50 | 用户显式标为重要的，优先展示 |
| `updatedAt` 在 7 天内 | +30 | 近期修改的更可能是当前关心的 |
| `updatedAt` 在 30 天内 | +10 | |
| 正文为空 | −100 | 空便签不太可能是搜索目标 |

**排序稳定性**：评分相同时按 `UpdatedAt` 降序，再相同时按 `Id` 升序。**必须有确定性的最终排序键**，否则同一查询两次得到不同顺序，用户会觉得界面在乱跳。

**两条式子上的裁决（表格没写清楚，实现时定下来的）**：

1. **六条基础分取最高的一条，不相加。** 表一是一张优先级阶梯——"标题完全等于 1000"这个量级明显是要压过一切的信号。若相加，一条"标题包含 + 标签精确 + 正文命中"的便签会拿到 `600 + 500 + 200 = 1300`，反过来压过标题完全等于查询的那条，阶梯当场失效。
2. **×1.5 只乘在"正文包含"那一条上，再参与取最高。** 若乘在总分上，一条"标题命中、正文恰好在开头也出现一次"的便签会平白多拿五成——可它排前面靠的是标题，与正文开头没关系。

两个常数的量级都远大于各类修正项之和（`10 × 10 + 50 + 30 = 180`），所以"取最高"不会让修正项失去意义：同档位之间仍然靠修正项分高下。

**第一阶段实现 6 条基础分 + 置顶/时间修正**（这几条成本极低）。"正文出现次数"和"开头加权"也在第一版做——它们是几行 LINQ 的事。

## 12.3 摘要与高亮

只在正文命中时生成摘要（标题或标签命中不需要）。

```text
1. 取命中位置前后各 40 个字符
2. 前后用 … 表示被截断
3. 把命中的子串用 <Run Foreground="HighlightBrush"> 包起来
4. 如果原文在 UI 上是多行显示，把换行替换为空格（摘要只显示一行）
5. 如果命中位置比 40 更靠近开头，就不加前导 …
```

**实现方式**：ViewModel 暴露 `IReadOnlyList<Inline>` 或一个标记了高亮区间的结构，由 View 用 `TextBlock` + `Run` 渲染。**不要在 XAML 里拼接 HTML 或用 `TextBlock.Text` 加富文本标记**。

**实现说明（已落地）**：那个"标记了高亮区间的结构"是 `Core/Search/SnippetSegment`，即 `readonly record struct SnippetSegment(string Text, bool IsMatch)`；生成它的纯函数在 `Core/Search/SnippetBuilder.cs`。省略号也是一段普通片段（`IsMatch` 为假），View 只需一个循环，不必在首尾单独判断"要不要画省略号"。核心不拼标记字符串有两个理由：拼出来的是给 WPF 看的标记，那就把界面技术漏进了零第三方依赖的 Core（§4.1）；而且用户正文里的尖括号会被当成标记解析。

第 4 条（换行替空格）的实现要点：**先替换再找位置**。替换是一对一的（一个换行换一个空格，长度不变），所以算出来的下标对原文同样成立，不会出现"截到一半发现位置偏了"。若改成"整行拼接后再裁"，两者就会出现偏差。

**第一阶段可以只做摘要不做高亮**（摘要的收益大于高亮），高亮放第二阶段。

## 12.4 何时才需要 SQLite FTS5

**触发条件**（必须同时满足才考虑）：

```text
1. 便签总数 > 5000，且
2. 实测单次搜索耗时 > 100ms（在目标机器上测量，不是估计），且
3. 已经确认瓶颈在搜索而不是在 UI 渲染结果列表
```

**引入后的约束**：

```text
SQLite 只能作为「可删除、可重建的搜索索引」
  ↓
Markdown 仍是唯一真实数据源
  ↓
数据库删除 → 重新扫描 Markdown → 恢复全部索引
```

**绝对禁止**把 SQLite 变成便签内容的唯一主数据源。这条是 v1 §15 的核心判断，v2 原样保留——它保护的是"即使用户删掉这个应用，笔记还在"这个承诺。

**更可能的优化顺序**（在引入 FTS5 之前先试这些）：

1. 把 `_plainText` 的构建改成惰性（只在首次搜索某便签时构建）
2. 搜索结果列表虚拟化（`ListBox` 默认已虚拟化，确认 `VirtualizingPanel.IsVirtualizing` 没被关掉）
3. 搜索去抖（输入停止 150ms 后再搜，而不是每个字符搜一次）
4. 增量搜索（在已有结果集内过滤）——用户加字符时结果集单调缩小，可以在上次结果里搜

这四步几乎一定能解决 5000 条规模下的体验问题，且不需要引入数据库。

---

# 13. 窗口系统

便签程序的"窗口"不是普通窗口：它无边框、要圆角、要阴影、要能拖动缩放、要能置顶、要多个同时存在且各自记忆位置。这一章把每个视觉与行为决策**拍死**，避免实现时反复摇摆。

## 13.1 视觉决策（已拍板）

| 决策项 | v2 结论 | 理由 |
|---|---|---|
| `AllowsTransparency` | **`False`** | 见下 |
| 圆角 | **Win11 交给 DWM**；Win10 接受直角 | 见下 |
| 阴影 | **交给 DWM 系统阴影** | 与上一行同源 |
| 自定义标题栏 | **`WindowChrome`** | 保留系统缩放/拖动/贴边，不需要自己实现 |
| 便签背景 | **必须是不透明颜色** | 见下 |

### 为什么 `AllowsTransparency = False`

`AllowsTransparency = True` 会把窗口变成分层窗口（layered window），WPF 转为软件渲染路径，代价是：

- 渲染性能下降（尤其是多个便签同时重绘时）
- 子像素抗锯齿失效，中文小字会明显发虚
- 窗口出现时间变慢（每帧要经 `UpdateLayeredWindow`）
- 和 DWM 效果（圆角、阴影）配合差，容易出现黑边/锯齿边缘

**关键的一条**：`AllowsTransparency = False` 时，`Background="Transparent"` **是无效的**。WPF 不会把窗口变透明，结果取决于 DWM，通常表现为黑色或不刷新的区域。

> v1 §25 中 `AllowsTransparency="False"` 与 `Background="Transparent"` 同时出现，这一组合必须删除。窗口根元素的背景必须是一个实心颜色（或渐变、图片），它是便签纸的颜色。

### 为什么圆角与阴影都交给 DWM

想要"圆角 + 阴影"，在 WPF 里有两条路：

**路线 A：自己做**（`AllowsTransparency=True` + 自己画圆角矩形 + 自己画 `DropShadowEffect`）
- 拿到完全一致的 Win10/Win11 外观
- 代价是上面列出的全部性能与渲染质量问题，而且要自己处理阴影的边距、裁剪、多屏拼接

**路线 B：交给 DWM**（`AllowsTransparency=False` + `WindowChrome`）
- Win11：系统圆角 + 系统阴影，外观和系统应用完全一致
- Win10：直角 + 系统阴影
- 性能与文字渲染都是最优路径
- 代价是 Win10 上没有圆角

**v2 选择路线 B**。理由是本项目对"低资源占用"和"中文可读性"的要求高于"Win10 上是否有圆角"。在 Win10 上，无边框 + 直角 + 系统阴影仍然是干净的现代外观。

### Win11 圆角的具体设置

```csharp
// Windows 11 build 22000+ 才有这个属性
private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
private const int DWMWCP_DEFAULT = 0;
private const int DWMWCP_DONOTROUND = 1;
private const int DWMWCP_ROUND = 2;
private const int DWMWCP_ROUNDSMALL = 3;

public static void ApplyRoundedCorners(IntPtr hwnd)
{
    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        return;   // Win10 直接跳过，不报错

    int preference = DWMWCP_ROUND;
    int hr = DwmSetWindowAttribute(
        hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

    // 属性不被支持时返回 E_INVALIDARG，静默忽略即可，不是错误
    if (hr != 0)
        _logger.LogDebug("DWM 圆角属性设置失败，HRESULT={Hr}，将使用系统默认", hr);
}
```

**调用时机**：`SourceInitialized` 之后（此时 HWND 已创建）。**不要在构造函数里调用**。

**注意事项**：

- `DwmSetWindowAttribute` 在属性不支持时返回 `E_INVALIDARG`（`0x80070057`），这是**预期情况**，不是异常，不要弹错误
- **不要**同时设置 `WindowChrome.CornerRadius`——Win11 上 DWM 会做圆角，WPF 再裁一次会出现锯齿或双圆角。`WindowChrome.CornerRadius` 在 v2 中保持默认（`0`）或显式设为 `0`
- 折叠状态下便签变得很矮，圆角半径由系统决定，不需要特殊处理

### DWM 阴影的具体设置

`WindowChrome` + `AllowsTransparency=False` 时，窗口保留了 `WS_THICKFRAME`，DWM 通常会自动绘制阴影。**但如果视觉上发现没有阴影**，用下面这个官方支持的技巧强制让 DWM 认为窗口有边框：

```csharp
public static void EnsureSystemShadow(IntPtr hwnd)
{
    // 让 DWM 认为窗口有一圈 1px 的"玻璃边框"，从而为它绘制系统阴影。
    // 底部留 1px 而不是四周都留，是为了避免顶部出现一条高光边。
    var margins = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0,
                                cyTopHeight = 0, cyBottomHeight = 1 };
    DwmExtendFrameIntoClientArea(hwnd, ref margins);
}
```

**这是一步可选优化**，只在实测没有阴影时才加。加了之后要检查：窗口四角是否有 1px 的透明边缘漏出（如果漏出，把 `MARGINS` 全设为 0 并改用 `WS_EX_STATICEDGE` 或直接放弃阴影，不要为了阴影牺牲边缘干净度）。

## 13.2 `WindowChrome` 配置

```xml
<Window
    x:Class="LumiMemo.App.Views.NoteWindow"
    AllowsTransparency="False"
    WindowStyle="None"
    ResizeMode="CanResize"
    Background="{DynamicResource NoteBackgroundBrush}"
    ShowInTaskbar="True"
    UseLayoutRounding="True"
    SnapsToDevicePixels="True"
    TextOptions.TextFormattingMode="Ideal"
    TextOptions.TextRenderingMode="ClearType">

  <WindowChrome.WindowChrome>
    <WindowChrome
        CaptionHeight="0"
        ResizeBorderThickness="6"
        CornerRadius="0"
        GlassFrameThickness="0"
        UseAeroCaptionButtons="False" />
  </WindowChrome.WindowChrome>

  <!-- 内容 -->
</Window>
```

逐项说明：

| 属性 | 值 | 说明 |
|---|---|---|
| `WindowStyle="None"` | 去掉系统标题栏与边框 |
| `ResizeMode="CanResize"` | **必须有**。设成 `NoResize` 会同时失去贴边（Aero Snap）、最大化、以及 DWM 的一些行为 |
| `CaptionHeight="0"` | 不让系统处理任何区域为标题栏。**拖动由我们自己实现**（§13.3） |
| `ResizeBorderThickness="6"` | 距离边缘 6 个 DIP 内是系统缩放热区 |
| `CornerRadius="0"` | 交给 DWM（§13.1） |
| `GlassFrameThickness="0"` | 不要玻璃效果，便签要实心 |
| `UseLayoutRounding="True"` | 让布局坐标落在整数像素上，中文小字更清晰 |
| `ShowInTaskbar` | 见 §13.4 |

**注意 `ResizeMode`**：如果后续要支持"锁定便签"（禁止移动与缩放），**不要**用 `ResizeMode="NoResize"`，而是在 `WM_NCHITTEST` 里对锁定的便签返回 `HTCLIENT`，让系统缩放热区失效。原因是 `NoResize` 会连带关闭 Aero Snap 和 DPI 变更时的一些系统行为，副作用大于收益。

## 13.3 拖动与调整大小

因为 `CaptionHeight="0"`，系统不再处理任何拖动区域，需要自己实现"拖标题栏移动窗口"。

### `WM_NCHITTEST` 处理

```csharp
private const int WM_NCHITTEST  = 0x0084;
private const int HTCLIENT      = 1;
private const int HTCAPTION     = 2;

private IntPtr OnNcHitTest(IntPtr hwnd, int lParam)
{
    // 系统已经把 lParam 转成了屏幕坐标
    int x = unchecked((short)(lParam.ToInt32() & 0xFFFF));
    int y = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));

    // 1. 锁定的便签：整窗不响应鼠标移动/缩放
    if (_vm.IsLocked)
        return (IntPtr)HTCLIENT;

    // 2. 缩放热区：交给系统（由 WindowChrome.ResizeBorderThickness 决定）
    //    这里先让系统算，判断结果是不是 HTCLIENT
    var sys = DefWindowProc(hwnd, WM_NCHITTEST, IntPtr.Zero, lParam);
    if (sys != (IntPtr)HTCLIENT)
        return sys;      // HTLEFT / HTRIGHT / HTTOP ... 直接用系统的

    // 3. 标题栏区域：转成可拖动
    var pt = PointFromScreen(new Point(x, y));   // 转成窗口内 DIP 坐标
    if (_vm.IsInDragArea(pt))
        return (IntPtr)HTCAPTION;

    return (IntPtr)HTCLIENT;
}
```

**为什么标题栏区域用 `HTCAPTION` 而不是自己处理鼠标事件**：返回 `HTCAPTION` 后，拖动、双击最大化、右键系统菜单、Aero Snap（拖到屏幕边缘贴边、拖到顶部最大化）**全部由系统免费提供**。自己用 `MouseLeftButtonDown` + `DragMove()` 会失去贴边和系统菜单。

### `IsInDragArea` 的范围

```text
可拖动区域 = 便签顶部的标题条区域
  - 排除按钮区域（锁定、折叠、更多、关闭）
  - 排除任何可交互控件（进度条、输入框等）
```

**不要**把整个便签背景都设为可拖动。便签主体是编辑器，全窗可拖动会导致点一下就移动窗口、无法选中文字。v1 §69 有类似的判断，v2 明确保留。

**折叠状态**：折叠时只剩标题条，整个标题条都是可拖动区。

## 13.4 任务栏与 Alt+Tab

**这是一个 v1 没理清的地方，需要先理解系统的实际规则。**

| 扩展样式 | 任务栏按钮 | Alt+Tab 条目 |
|---|---|---|
| 无（普通窗口） | 有 | 有 |
| `WS_EX_TOOLWINDOW` | **无** | **无** |
| `WS_EX_APPWINDOW` | **有**（强制） | 有 |
| 有 Owner 的窗口 | 无 | 无（跟随 Owner） |

**关键点：`WS_EX_TOOLWINDOW` 同时控制任务栏和 Alt+Tab，两者不可分开设置。** v1 把它们当成两个独立开关是行不通的。

### v2 的决策

**普通便签窗口：不加任何扩展样式（默认行为）。**

原因：便签是用户主动创建、需要反复切换过去写东西的窗口。Alt+Tab 里能找到它，比"任务栏干净"更重要。多个便签窗口在任务栏/Alt+Tab 里会出现多个条目，这是**符合预期**的——用户能一眼看到所有打开的便签。

PinSlip 的用户指南也提到"任务栏按钮保留"，与本决策一致。

**速记浮窗（§15.7）：`WS_EX_TOOLWINDOW`。**

速记浮窗是"用完即走"的临时入口，不应该污染任务栏和 Alt+Tab。它的完整开关行为是：

```csharp
private const int GWL_EXSTYLE      = -20;
private const int WS_EX_TOOLWINDOW = 0x00000080;

// 加上 TOOLWINDOW
var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
```

**注意**：`SetWindowLongPtr` 改样式后，窗口可能需要 `SetWindowPos` 并带上 `SWP_FRAMECHANGED` 才会生效。而且这个操作在窗口已经显示之后再改，任务栏按钮的移除有时不干净——**建议在 `SourceInitialized` 事件里、窗口显示之前就设好**。

### 用户可选？不做

v2 **不提供**"便签是否显示在任务栏"的开关。理由：这个设置项的价值很低，而它一旦可配，就要处理"用户改了设置后已有窗口如何响应"的问题（重设样式、任务栏按钮残留等），复杂度不成比例。

## 13.5 置顶

```csharp
// WPF 的 TopMost 直接映射到 SetWindowPos(HWND_TOPMOST)
_vm.IsTopMost = true;   // 绑定到 Window.TopMost
```

**置顶的三档语义**（v2 明确）：

| 档位 | 实现 | 说明 |
|---|---|---|
| 普通 | `TopMost = false` | 和其他窗口一样 |
| 置顶 | `TopMost = true` | 在所有**非置顶**窗口之上 |
| 全局置顶 | `TopMost = true` + 更高的 z 序维护 | **v2 不做** |

**"全局置顶"为什么不做**：让便签盖住全屏游戏、任务管理器、UAC 提示是**有害**的，而且系统会不断把你的窗口压下去（全屏独占应用、管理员权限窗口），需要持续抢焦点，代价高、体验差。普通 `TopMost` 已经覆盖了 99% 的需求。

**置顶便签与其他置顶窗口**：多个 `TopMost` 窗口之间的相对顺序由最后激活时间决定，这是系统行为，不做干预。

**置顶状态必须在 §8 的 layout.json 里持久化**（`IsTopMost` 字段已在 §9 定义），重启后恢复。

## 13.6 Win+D 与"显示桌面"

**这是一个 v1 完全没有讨论、但用户一定会遇到的冲突：按 Win+D 时便签会跟着一起消失。**

### 事实澄清：Win+D 没有最小化便签，只是把它盖住了

本节的早期版本先后给过两个**都不成立**的解释：先是"Win+D 把便签隐藏了"（据此断言 `IsIconic` 查不到），后来又改成"Win+D 走 `ShowWindow(SW_MINIMIZE)`，`WindowState` 会变成 `Minimized`"。第二个说法比第一个接近真相，但它描述的是**普通窗口**（比如管理器窗口）的命运，被错误地套到了便签头上。

便签不是普通窗口。它 `ShowInTaskbar="False"`，WPF 因此给它挂了一个 `Hidden Window` 当 **owner**；而"显示桌面"**跳过一切有 owner 的窗口**。同一时刻两类窗口的实测对比：

| 观测 | 管理器窗口（普通窗口） | 便签窗口（有 owner） |
|---|---|---|
| `IsIconic` | `False` → **`True`** | **全程 `False`** |
| `IsWindowVisible` | `True` | **全程 `True`** |
| `GetWindowRect` | 被移到 `-32000,-32000` | **`1887,417` 一动不动** |
| `GetForegroundWindow` | **`Progman`**（正是 `GetShellWindow()` 的返回值） | 同左 |

**便签从头到尾没被最小化过，位置也没动过。** 用户看到的"消失"是它被升到一切之上的桌面窗口（`Progman` / `FolderView`）**盖住**了——点回任意一个别的窗口，桌面就降下去，便签原样露出来。

这条修正也解释了另一个曾经把排查带偏的现象：Win+D 之后用 `EnumWindows` 加「`IsWindowVisible` 且尺寸 > 300×300」这个条件去枚举，**一个便签窗口都找不到**。当时据此推断"它们被隐藏了"，其实是**管理器**被最小化之后 `GetWindowRect` 给的成了 `199x34`，两个数都小于 300。尺寸条件筛掉的是管理器，不是便签。

**于是整件事的性质变了**：既然便签没有被最小化，就没有"还原"可言——旧方案里那条 `ShowWindow(SW_SHOWNOACTIVATE)` 是打在一个从未离开过原位的窗口上的。真正要解决的是 **z 序**问题：怎么让便签重新出现在升起的桌面**之上**。

**本机环境的两个附带观察**（可能会随系统版本变化，记录备查）：`GetShellWindow()` 拿到的 `Progman` 扩展样式是 `0x00200080` = `WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP`；枚举得到 8 个 `WorkerW` 顶层窗口，**没有一个含 `SHELLDLL_DefView`**。后者意味着网上那些"挑不带 `SHELLDLL_DefView` 的 `WorkerW` 挂上去"的教程在这版系统上直接选不出目标。

而另一个常被提及的规避手段——用 `SetParent` 把窗口挂到桌面窗口（`Progman`）上变成子窗口，让"贴住壁纸"成为窗口的固有属性——**本项目实测走不通**，见下一节。

### 实测：`SetParent` 挂桌面层在 WPF 上不可用

本项目一度按这个思路实现过一版：不置顶时 `SetParent(hwnd, GetShellWindow())`，置顶时再摘掉。真机验收的结果是**两头落空**：

| 观测项 | 置顶 | 不置顶（挂过桌面层） |
|---|---|---|
| `PrintWindow` 像素采样 | `#FDF3C4` 80.7% + `#F7E9A0` 13.6%（正常） | **`#000000` 100%（整片纯黑）** |
| `GetWindow(GW_OWNER)` | WPF 自己的 `Hidden Window` | **同样是那个 `Hidden Window`** |

同一张便签切一次置顶就能复现，切回来立刻恢复正常。也就是说：

1. **owner 根本没设上**——期望的 `Progman` 从未出现在 `GW_OWNER` 里，挂载没有产生任何它该产生的效果；
2. **代价却是实打实的**——窗口整片渲染成黑色，便签彻底不可用。

官方文档给了这条路的定性：`SetParent` 的 MSDN 页面对**跨进程**调用明确写着 "Unexpected behavior or errors may occur"，并把跨进程情形列为会触发子窗口所在进程 DPI 感知被**强制重置**的场景。

不过这里要如实记一笔：**实测并没有观察到 DPI 感知被重置**（进程仍是 `PER_MONITOR_AWARE`），所以变黑的确切机制并未定位。这本身就是"不受支持"的典型表现——它确实坏了，但坏在哪一步无法从文档推出来，也就无从修。社区在这个话题上的共识是同一句话：不要建立跨进程的父子/所有者窗口关系。

**结论：不要重新尝试这条路。** `src/LumiMemo.Infrastructure/Windows/NativeMethods.txt` 的 D.8 段里记着这次实测结论，`WindowManager.ApplyTopMost` 的注释里也留了警告。

### 为什么普通 z 序够不到桌面之上

"显示桌面"把桌面窗口抬到了**置顶档**，而普通 z 序那一档够不到置顶档。三条直觉上可行的路实测全部落空：

| 尝试 | 结果 |
|---|---|
| 事后 `SetWindowPos(HWND_TOP, SWP_NOACTIVATE)` | **压不过**——窗口中心处 `WindowFromPoint` 拿到的仍是 `FolderView` |
| 换到"桌面刚成为前台"的那一刻调用 | **只在头两秒有效**，之后又被盖回去 |
| 同上，但允许抢焦点 | 唯一"看起来成功"的一次——`focusStolen=YES`，前台被抢回了便签。用户正站在桌面上，接下来的按键会打进便签里 |

**结论：`WS_EX_TOPMOST` 是唯一压得住"显示桌面"的手段。**

### 方案：显示桌面期间临时置顶

`restoreAfterShowDesktop`（§8.2，默认开）打开时，链路两步，全部走公开受支持的 API：

1. **检测**：`SetWinEventHook(EVENT_SYSTEM_FOREGROUND, …)`。桌面窗口成为前台，就是要开始盖住一切了——这时把可见的、非置顶的便签逐个 `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)` 提到置顶档。
2. **撤销**：桌面离开前台时，把上一步提过的那些便签清回普通档，并各自插回提升前的位置；插完还要盯一小会儿，因为这一刻"显示桌面"自己也在恢复窗口（见下第五条）。

三个 `SWP_NO*` 标志一个都不能省：`SWP_NOMOVE` / `SWP_NOSIZE` 保证几何不变，`SWP_NOACTIVATE` 保证不抢前台——用户这时候正站在桌面上，抢走焦点意味着接下来的按键会打进便签里。

**撤销之所以是两步。** `HWND_NOTOPMOST` 是唯一能清掉 `WS_EX_TOPMOST` 的手段，可它的语义是"排到**所有**非置顶窗口之上"——只用它清标志，便签虽然退出了置顶档，却会浮到一切普通窗口的最上面（实测：本来被浏览器盖着的便签，还原后跑到了浏览器前面）。所以清完之后要紧接着 `SetWindowPos(hwnd, 锚点, …)` 把它插回去。两步之间不返回消息循环，用户看不到中间态。

**锚点取自提升之前，不是"撤销那一刻的前台"。** 提升的时候就把每张便签当时的 **第一个算数的** z 序邻居（从 `GetWindow(GW_HWNDPREV)` 起往上走，隐藏窗口、桌面窗口、置顶窗口一律跳过）记下来，撤销时插回它后面——从哪儿来，回哪儿去。"当前前台"看着更顺手，实际是一张一直在变的牌。前四条讲的是锚点该怎么取，第五条讲的是取到锚点之后为什么还插不住——都实测踩过：

- **「显示桌面」有两条入口，撤销那一步的前台不一样。** 按 Win+D 时前台直接变成桌面窗口（`Progman`）；点任务栏右下角那个按钮时，前台先在**置顶档的** `Shell_TrayWnd`（任务栏）上停 60～110 ms（Win+D 只要 15～30 ms）才落到 `Progman`，而**还原时它压根不去别处，就停在任务栏上**。拿一个置顶窗口当锚点，`SetWindowPos` 会把便签一并提拔进置顶档，而且这次错误的提拔**再也撤不掉**——提升那一步会跳过已置顶的窗口，撤销那一步又只处理自己提升过的那些。清掉临时置顶 **3 ms 后**便签又变回 `WS_EX_TOPMOST`，就是这条路径实测到的。
- **Win+D 的还原是个过程，不是一个瞬间。** 系统先把被掀掉的那批窗口逐个还原（管理器就在其中），之后才把前台还给原来那个窗口；还原途中收到的那几次前台事件，指向的都是"正在被还原的窗口"，与便签该待的位置毫无关系。据此把便签插到管理器后面，还会让它变成一张*名义上是前台、实际被盖住*的窗口（实测复现）。
- **便签上面压着一串看不见的窗口，"紧邻上面"与"盖住它的"经常不是同一个。** 每个有过输入焦点的线程都挂着一个 `IME` 与一个 `MSCTFIME UI`（`visible = False`），实测一张便签上面能连着压五六个；它们上面还夹着 `tooltips_class32`——**那个是置顶档的**。只问一次 `GW_HWNDPREV`，十有八九拿到的是这些幽灵。把便签插到幽灵后面，它会落进一个"看着像在最上面"的位置：排到浏览器之上、却排在这些幽灵之下——用户看到的就是"便签浮在所有可见窗口之上"。而万一锚点正好落在置顶档的 tooltip 上，撤销那一步会判定锚点不可用直接跳过，便签就**永久**留在普通窗口的最上面。锚点的意义是"用户眼里盖住便签的那个窗口"，所以只能落在可见窗口上：从 `GW_HWNDPREV` 起往上走，跳过 `IsWindowVisible` 为假的那些。
- **取锚点的时机必须在"桌面还没升起来"的时候，不能等到撤销时当场问一次。** `WINEVENT_OUTOFCONTEXT` 的事件是投递到注册线程的消息队列上的，等 `OnForegroundChanged` 真正跑起来，"显示桌面"早已把盖住便签的那批普通窗口藏好了（实测那一刻从便签往上问，看到的全是不可见的）。而锚点的筛选规则恰恰**跳过不可见窗口**，于是问到的要么是别的便签窗口，要么是排在便签**下面**的某个窗口（实测问到过文件资源管理器），要么一路走到顶端拿到 `IntPtr.Zero`。**锚点的意义是"用户眼里盖住便签的那个窗口"，那就得趁用户还看得见它的时候采**：前台切到某个窗口、某张便签刚开出来、某张便签被拖动或缩放，都顺手把每张可见便签当时的邻居记一遍，提升时直接用记下的值，取不到才退回当场问。
- **插位是一次性的，而"显示桌面"的恢复是个过程——插好的位置会被顶掉。** 撤销由"桌面不再是前台"触发，可那一刻系统**还在把窗口一张张恢复上来**，每恢复一张就把它抬到顶上，刚刚插好的便签随即被压下去。实测轨迹：还原后 +45 ms 便签已经落在正确的邻居后面，+91 ms 就掉到全屏幕最底下，此后纹丝不动；同一套操作连做六轮，还原后的 z 序下标是 1、1、2、1、1、4——**看着像"时好时坏"，其实是插位与恢复洪流谁先谁后**。所以归位不能只插一次：插完还要**每隔 60 ms 复查一次**，谁的前驱还不是记下的那个邻居就再插一次，一直管到复查窗口跑完（约 1 秒）为止。
- **可"这会儿是对的"不能当成收手的理由——一收手，后面还有的恢复就没人管了。** 复查的判据是**实际 z 序**而不是"插过几次"，但只看一眼当下、对了就停，会漏掉后面还有的恢复——实测点任务栏右下角那个按钮：还原 **+1 ms** 便签就已经落在正确位置，第一拍复查看到的正是"对的"，于是收手；恢复洪流却在 **+150 ms、+200 ms** 才把它推到 z 序第 4、第 7 位，此后无人过问，便签停在最底下。同一份代码按 Win+D 却是好的，因为那一路第一拍看到的恰好是错的、复查继续跑了下去——**"Win+D 正常、任务栏按钮不正常"就是这么来的，差别只在复查第一拍撞上哪一种，与入口本身无关**。所以退出条件只有一个：窗口期跑完。窗口期只比实测涨落时长（约 250 ms）大四倍，成本是十几次指针比较；一直管着也不会跟用户抢位置——用户随后点到前台的窗口总是抬到锚点*之上*，便签仍在锚点之后，前驱不变，复查便什么都不做；用户要是点回便签本身，那次前台切换已经把复查叫停。

前两条合起来，就是"Win+D 一切正常、点任务栏按钮却出问题"的全部来源——**按现象去分辨是哪条入口，反而会被带偏。** 第三条与入口无关，它是「时好时坏」的来源——那串幽灵窗口每次排在哪一段都不一样，同一套操作做两遍可以一遍对一遍错。第四条与入口无关，它决定的是**锚点本身对不对**。第五条与入口、与锚点对不对都无关：**只要插位被恢复洪流顶掉，便签就掉到全屏幕最底下**——它既不挑入口也不挑锚点，纯粹是时机。实测把锚点换成提前采来的正确值、插位仍只做一次，受控实验里 3 轮**全漂**；改用复查收敛之后同一实验 10 轮（Win+D 5 轮 + 任务栏按钮 5 轮）**全稳**——但那是合成实验里的稳，用户在自己桌面上按真实节奏操作时，任务栏按钮那一路**仍然偶发掉底**，直到查明白"复查第一拍看着对了就收手"这一层（见上面最后两条）。这也是它最初被误判成"跟谁启动有关"的原因：同一个实例往往连着复现同一种结果。

**撤销必须分两轮：先全部清标志，再全部归位。** 不能"清一张、归位一张"地穿插着做——给 A 归位时若 B 还挂在置顶档上，而 A 提升前恰好排在 B 下面（两张便签在屏幕上叠着），`SetWindowPos` 会连带把 A 也提拔进置顶档。两轮之间不返回消息循环，用户看不到中间态。这两轮只是把便签**摆个大概**，真正落定靠第五条的复查——恢复洪流还没跑完，此刻插得再准也会被顶掉。

**锚点不可用时就只做前一轮。** 取锚点时那三条筛选已经把这几类窗口挡在门外了，这里是**第二道保险**——记录锚点与使用锚点之间隔着整个"显示桌面"，期间窗口的可见性、层级、甚至存活与否都可能变，不能只靠取的时候干净。四种情况：锚点为空（便签提升前就在 z 序最顶端——按上面的规则，那意味着往上全是隐藏窗口，而 `HWND_NOTOPMOST` 给它的位置正是原位）、锚点已失效（提升与撤销之间隔着整个"显示桌面"，那个窗口可能已经被销毁，句柄还可能被复用）、锚点已不可见（提升时记下的是可见窗口，撤销时它可能已经隐藏，或者句柄已被复用成别的窗口——输入法那类辅助窗口本来就会随时显隐）、锚点自己就在置顶档（插到置顶窗口后面会连带提拔，见上）。这些都在实测里出现过。

顺带记两个把排查带偏的坑：**`HWND_NOTOPMOST` 配 `SWP_NOZORDER` 是死路**——想"只清标志位、不动 z 序"，实测调用返回成功而 `WS_EX_TOPMOST` 纹丝不动，标记位和 z 序是一体的；**把窗口自己当 `hWndInsertAfter` 也是死路**——窗口会落进"已激活却排在别人之下"的非自然状态，之后再也提不上去。

**为什么不传 `WINEVENT_SKIPOWNPROCESS`**：用户点回自家的便签时，前台同样离开了桌面，我们也得把临时置顶撤掉。滤掉本进程的事件会让便签在那种情况下永远留在最上层（实测复现）。代价只是多收几次事件，回调里只有两次指针比较。

**这不属于附录 D.8 禁用的 `SetWindowsHookEx`。** 那一条禁的是往别人的消息流里插钩子（会让整个桌面卡顿、还会被安全软件盯上）；`EVENT_SYSTEM_FOREGROUND` 是系统提供的**通知**，只订阅一个事件，而且用 `WINEVENT_OUTOFCONTEXT` 注册——回调走我们自己的消息队列，不注入任何进程。唯一的硬要求是**必须在有消息泵的线程（也就是 UI 线程）上注册**，否则回调永远不会被派发。

**代价**：临时置顶的那一瞬，便签会从别的置顶窗口（如果有）之下换到它们之上，撤回来时也一样。全程不动的只有用户主动置顶那一档。

### 两档语义

| | z 序 | 显示桌面（Win+D） |
|---|---|---|
| 不置顶（默认） | 会被普通窗口盖住 | 被桌面盖住，**随即被临时提到置顶档**，桌面退下去时再撤回 |
| 置顶 | 盖住所有非置顶窗口 | **系统根本不动它**，全程不干预 |

关掉 `restoreAfterShowDesktop`，不置顶那一档就退化成"被桌面盖住就不再管"，只剩置顶一条路——也就是本节早期版本描述的那个世界。

### 配套的缓解措施

让用户能一键把便签找回来（临时置顶被设置关掉、或某个系统版本上失效时兜底）：

1. **全局热键**：`Ctrl+Alt+N` 恢复所有已打开的便签并激活
2. **托盘菜单**：第一项就是"显示全部便签"
3. **双击托盘图标**：等同"显示全部便签"

这三条都是常规实现，成本很低。**尚未实现**——托盘与全局热键都排在当前这一轮之后。

### 不做的事

**不使用 `WM_WINDOWPOSCHANGING` 取消最小化的 hack。**

这个 hack 的做法是：拦截该消息，检测到窗口位置被移到 `x = -32000`（系统最小化的标志位置）时把消息标记为已处理来取消最小化。它**不属于受支持的用法**，会在以下情况出问题：

- **它和上面那套"临时置顶"是两件完全不同的事，别混起来。** 后者压根不碰最小化：它不撤销系统的任何动作，也不依赖任何未文档化的约定，改的只是自己窗口的 z 序，系统那边记的还原列表从头到尾是自洽的（用户再按一次 Win+D 恢复正常时不会打架）。前者是"撤销系统刚做的动作"，一旦漏掉某种触发路径就会出现状态不一致
- **对便签来说它更是无从下手**：便签根本不会被"显示桌面"最小化（见本节上方），窗口位置永远到不了 `x = -32000`，这条 hack 连触发条件都不存在
- 多显示器排列变化、远程桌面切换、UAC 提权时行为不一致
- 系统更新可能改变 `x == -32000` 这个内部约定——它从未被文档化

**在文档里明确记录这条不做**，避免以后有人"顺手加上"。

**同样不要用 `SetParent` 把便签挂到桌面窗口**——理由与实测数据见本节上方，这条也被记进了附录 D.8。

## 13.7 点击穿透

### 需求来源

用户把便签用来"贴参考信息"（比如一段 API 文档、一个命令清单）时，希望便签只是**显示**，不要挡住下面的窗口。PinSlip 的"锁定"隐约指向这个需求。

### 技术路径与风险

点击穿透需要给窗口加 `WS_EX_LAYERED | WS_EX_TRANSPARENT`：

```csharp
private const int WS_EX_TRANSPARENT = 0x00000020;
private const int WS_EX_LAYERED     = 0x00080000;
```

**风险点**：`WS_EX_LAYERED` 会让窗口走分层窗口路径。对 WPF 来说，`AllowsTransparency=False` 的窗口本来是 DWM 合成的普通窗口，加上 `WS_EX_LAYERED` 之后：

- 渲染可能异常（黑屏、内容不更新、ClearType 失效）
- 与硬件加速的 DirectX 表面交互行为在文档中没有明确保证
- DPI 变更、多显示器移动时的不一致

**这是必须做原型验证才能承诺的功能。** v2 不把它列为一期必做项。

### 原型验证清单（必做）

写一个最小原型（一个无透明 WPF 窗口 + 手动设 `WS_EX_LAYERED | WS_EX_TRANSPARENT`），逐条验证：

```text
[ ] 窗口内容正常渲染，无黑屏、无闪烁
[ ] 文字仍然清晰（ClearType 是否失效）
[ ] 鼠标点击确实穿透到下层窗口
[ ] 键盘焦点行为可接受（穿透的窗口不应抢焦点）
[ ] 从 100% 缩放到 200% 的显示器上拖动，渲染仍正常
[ ] 便签内容滚动时仍正常
[ ] 关闭穿透（移除扩展样式）后窗口完全恢复正常
```

### 降级方案

**如果原型验证失败**（任何一条不通过），点击穿透功能改为：

**用"折叠"替代"穿透"。** 折叠后的便签只占一条标题栏的高度，对下层窗口的遮挡降到最低，同时完全保持可交互性（点标题栏可以展开）。这是一个 100% 受支持的、稳定的替代方案，实际使用体验未必比穿透差。

**明确不做**的中间方案：把便签截图后作为桌面壁纸的一部分来模拟穿透——无法交互、多屏拼接复杂、用户会觉得"便签坏了"。

### 与锁定模式的关系

**锁定 ≠ 穿透。** 两个独立的概念：

| 模式 | 能否拖动 | 能否编辑 | 能否点击穿到下层 |
|---|---|---|---|
| 普通 | 能 | 能 | 否 |
| 锁定 | 否 | 否 | 否 |
| 穿透（若原型通过） | 否 | 否 | **是** |

锁定模式（`IsLocked`）只做"禁止移动 + 编辑器只读"，**不改变窗口的鼠标穿透属性**。穿透是一个独立的高级开关，只有原型验证通过才提供。

## 13.8 DPI 与坐标

### DPI 感知模式

`app.manifest` 中声明 **PerMonitorV2**：

```xml
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
  </windowsSettings>
</application>
```

**不要**用 `System` 模式（跨屏拖动时字会糊）。**不要**用 `PerMonitor`（V1，缺少非客户区的自动缩放和子窗口的 DPI 传递，拖到不同 DPI 的屏上会出现标题栏尺寸不对）。

### 坐标的存储与恢复

**存储的是物理像素 + 保存时的 DPI**（§9 的 `NoteLayout`）：

```csharp
public double X, Y, Width, Height;   // 物理像素
public uint   Dpi;                   // 保存时该显示器 DPI，如 96 / 120 / 144 / 192
```

**为什么不用 DIP 存储**：DIP 的换算依赖当前显示器的 DPI，而"当前"在恢复时还没确定（要先知道窗口在哪个显示器上，才知道 DPI）。存物理像素没有这个循环依赖。

**为什么要一起存 DPI**：如果只存物理像素，用户把 100% 屏的便签拖到 200% 屏后重启，窗口会保持同样的物理像素尺寸——在 200% 屏上看起来只有原来一半大。存下 DPI 就能算出正确的缩放。

### 恢复算法

```text
输入：saved { X, Y, W, H, Dpi }
  ↓
1. 确定目标显示器：
   MonitorFromPoint(saved.X + saved.W/2, saved.Y + saved.H/2, MONITOR_DEFAULTTONEAREST)
  ↓
2. 取目标显示器的当前 DPI（GetDpiForMonitor）
  ↓
3. 计算缩放：
   scale = currentDpi / savedDpi        （savedDpi 为 0 或缺失时视为 96）
  ↓
4. 换算物理尺寸：
   w = saved.W * scale
   h = saved.H * scale
  ↓
5. 用 SetWindowPos 直接设定位与尺寸（物理像素）
  ↓
6. 夹取到工作区（见下）
```

### 工作区夹取

```text
workArea = 目标显示器的 rcWork（不含任务栏的区域）

if (w > workArea.width)  w = workArea.width;
if (h > workArea.height) h = workArea.height;

// 至少保留 80px 的标题条在可视区域内，避免窗口被拖到完全看不见的地方
if (x + w < workArea.left + 80)  x = workArea.left;
if (x > workArea.right - 80)     x = workArea.right - w;
if (y < workArea.top)            y = workArea.top;
if (y > workArea.bottom - 40)    y = workArea.bottom - h;
```

### 显示器不存在时

如果保存时所在的显示器已经拔掉（`MonitorFromPoint` 落在了已断开的显示器上），**不要**直接把坐标照搬：

```text
目标显示器 == 主显示器（因为原显示器不在了）
  ↓
坐标落在主显示器工作区之外
  ↓
按"层叠"策略重新摆放到主显示器：
  第 1 张：(workArea.left + 24,                workArea.top + 24)
  第 2 张：(workArea.left + 24 + 28,           workArea.top + 24 + 28)
  ...
  第 N 张：超过 6 张后回到起点，带 8px 偏移
```

**在日志里记录这次位置修正**，便于用户排查"为什么便签跑到别的地方去了"。

### `WM_DPICHANGED`

窗口从一个 DPI 的显示器拖到另一个时会收到 `WM_DPICHANGED`。WPF 的 PerMonitorV2 支持已经会处理大部分逻辑（按 `lParam` 指向的建议 RECT 调整窗口尺寸）。

**要做的两件事**：

1. **不要拦截这个消息**，让它走默认处理
2. **在 DPI 变更完成后保存一次布局**（因为窗口尺寸被系统改了，`RestoreBounds` 已经变化）。保存时机放在 `WM_DPICHANGED` 处理完之后，用一个短延时（200ms）合并连续的变更

### 缩放变化时保存布局的时机

**只在 `WM_EXITSIZEMOVE` 时保存位置/尺寸**，不要在处理 `WM_MOVE` / `WM_SIZE` 时保存。

原因：拖动过程中这两个消息每帧都会触发，每次都写 layout.json 会造成大量磁盘写入。`WM_EXITSIZEMOVE` 是"用户松手了"的准确信号。

例外情况（也需要保存）：

- 最大化/最小化/还原（`WM_SIZE` 中 `wParam == SIZE_MAXIMIZED` 等）
- DPI 变更完成（200ms 去抖后）
- 折叠/展开（我们自己触发的尺寸变化）
- 程序退出前（§17.4 flush）

## 13.9 窗口生命周期与窗口池

### 只为"可见"便签创建 Window

**核心的资源约束**：便签文件可能有几千个，但用户在屏幕上的便签通常不超过 20 个。**绝不为每一张便签创建窗口。**

```text
NoteStore 里的便签数量  ≠  窗口数量
窗口数量 = 当前 IsOpen == true 的便签数量
```

`IsOpen` 由 §9 的 `NoteLayout` 承载，持久化在 layout.json，重启后恢复。

### 状态转换

| 操作 | Note | NoteLayout | Window | NoteViewModel |
|---|---|---|---|---|
| 新建便签 | 创建 | 创建（`IsOpen=true`） | 创建 | 创建 |
| 关闭便签窗口 | **保留** | `IsOpen=false` | 可选回收 | 释放 |
| 删除便签（进回收站） | 移除 | **保留**（`layout` 条目不清除，§8.3） | 销毁 | 释放 |
| 折叠 | 保留 | `IsCollapsed=true` | 保留（变矮） | 保留 |
| 显示桌面（Win+D） | 保留 | `IsOpen` **不变** | 被桌面盖住期间临时置顶（§13.6） | 保留 |
| 隐藏（托盘"隐藏全部便签"） | 保留 | `IsOpen` 不变 | 保留（`Hide()`，不可见） | 保留 |

**关键：关闭便签窗口 ≠ 删除便签。** 关闭只是"不在桌面上显示"，便签内容仍在文件里、仍在 NoteStore 里、仍能被搜索到。用户右键便签列表可以重新打开。这一点在 v1 中不够明确。

### 窗口池

创建 WPF `Window` 不是零成本的（尤其是首次创建，要加载样式、模板）。为保证稳定在 100ms 内的响应，引入一个轻量窗口池：

```csharp
public sealed class NoteWindowPool
{
    private readonly Stack<NoteWindow> _idle = new();

    // 池的目标大小：稳态下保留这么多空闲窗口。
    // 默认 3，不是 8 —— 每个空闲的 WPF 窗口常驻约 3~8MB，
    // 池越大内存占用越高（见 §20.3）。
    private const int TargetIdle = 3;

    // 硬上限：超过这个数量就真正销毁而不是入池，避免峰值后池无限膨胀
    private const int MaxIdle = 8;

    public NoteWindow Rent(NoteViewModel vm);  // 池空则新建，否则复用
    public void Return(NoteWindow w);          // 重置绑定后入池；超 MaxIdle 则销毁
}
```

**两个常量的分工**：`TargetIdle` 决定"稳态保留几个"，`MaxIdle` 决定"最多能缓存几个"。用户一次性打开 20 张便签后关掉，池不会留下 20 个空闲窗口，只在 `MaxIdle` 之内保留。

**入池前必须做的清理**（否则会出现数据串台，这是一个很容易踩的坑）：

```text
1. DataContext = null        ← 必须先断开，否则旧 VM 会被旧窗口的绑定持有
2. 清除所有绑定（用 BindingOperations.ClearAllBindings 或重建内容）
3. 重置 WindowState = Normal
4. 移除该窗口上挂的所有事件处理器
5. 清空编辑器内的文本与撤销栈
```

**不池化的情况**：折叠状态、穿透状态、锁定状态会影响窗口的样式与行为。**只有处于"普通状态"的窗口才回池**，其他状态直接销毁。

本池化是**可选的优化**。第一版可以先不做池化，直接创建/销毁，实测如果打开便签有肉眼可见的延迟再加。**是否启用、池多大，都以 §20.1 的实测数据为准，不要凭"理论上更快"决定。**

## 13.10 关于"隐藏宿主窗口"

**v2 决定：不要隐藏的顶层宿主窗口。**

v1 §100 附近提出的"创建一个隐藏的宿主窗口作为便签窗口的 Owner"，在 v2 中取消，理由如下。

### 为什么不需要

它原本要解决三件事，每件都有更简单的解法：

| 原本要解决的问题 | v2 的解法 |
|---|---|
| 没有便签时应用不能退出 | `Application.ShutdownMode = OnExplicitShutdown`，退出只由托盘菜单/主流程控制 |
| 托盘图标需要宿主 | `H.NotifyIcon` 自己内部使用消息窗口，不需要我们提供 |
| 便签窗口需要一个 Owner | **不需要 Owner**。便签是无主的独立顶层窗口，这正是我们想要的行为 |

### 取消它顺带解决的一个坑

WPF 的 `Window.Owner` 有一个不直观的约束：**被指定的窗口必须已经显示过**（`Show()` 或 `ShowDialog()`），否则在设置 `Owner` 时会抛 `InvalidOperationException`。一个"创建了但从不显示的隐藏宿主窗口"恰好不满足这个前提，所以 v1 的这条路在 WPF 层面就会失败——必须绕到 Win32 的 `SetWindowLongPtr(GWLP_HWNDPARENT)` 才能实现。

既然不设 Owner 就没有这个问题，**取消宿主窗口是更干净的选择**。

### 如果将来确实需要一个隐藏窗口

不要创建一个"不可见的顶层窗口"，而应该创建**消息专用窗口**：

```csharp
// 父窗口设为 HWND_MESSAGE (-3)：这是系统专门为这种用途提供的机制，
// 它不在 z 序、不在任务栏、不在 Alt+Tab、不接收广播消息
new WindowInteropHelper(w) { Owner = (IntPtr)(-3) };
// 或直接 CreateWindowEx 时用 HWND_MESSAGE 作为 hWndParent
```

**绝不要**用 `Visibility = Hidden` 或 `Opacity = 0` 的普通窗口来代替——它会出现在任务栏、Alt+Tab，以及 Win+D 的响应列表里。

---

# 14. WindowManager

## 14.1 职责边界

**v1 §103 存在明显的分层违规**：`WindowManager` 直接 `new NoteViewModel(...)`，让窗口层承担了 ViewModel 的构造职责，导致 WindowManager 同时依赖数据层、服务层和 ViewModel 层，无法单独测试。

**v2 的职责划分**：

```text
NoteService（Core 层）
  - 便签的业务操作：新建、删除、恢复、应用外部修改
  - 知道「能不能开、该用哪份 layout」：校验存在性、把 layout.IsOpen 置 true，然后交出 (Note, NoteLayout)
  - **不构造 ViewModel、不碰窗口**——那两步属于 App 层（理由见下面的依赖方向）

WindowManager（App 层，只做窗口的物理管理）
  - 只负责：创建 / 显示 / 隐藏 / 销毁 / 摆放 窗口对象
  - 接收「一个已经构造好的 (NoteViewModel, NoteLayout) 二元组」
  - 不知道 Note 是怎么来的，不知道业务规则

NoteViewModelFactory（App 层）
  - 唯一的 NoteViewModel 构造入口
  - 从 DI 容器取依赖（`INoteService`、`AutoSaveService`、`IDispatcher`、`IDialogService` 等）
```

**依赖方向**：

```text
发起方（App 层：命令处理器 / 热键 / 托盘菜单）
  → INoteService.OpenNote(noteId)        （Core：校验 + 置 IsOpen + 交出 (Note, NoteLayout)）
  → NoteViewModelFactory.Create(...)     （App：构造 ViewModel）
  → IWindowManager.ShowNote(vm, layout)  （App：真正开窗）

禁止项：
WindowManager ✗→ NoteService              （窗口层不反向调用业务层）
WindowManager ✗→ NoteViewModelFactory
NoteService   ✗→ NoteViewModelFactory     （Core 不依赖 App，§3.1 规则 1）
NoteService   ✗→ IWindowManager           （同上；Core 只定义这个接口给 App 用，自己不去调）
```

**为什么"开窗"必须由 App 层的发起方串起来，而不是由 `NoteService` 一步做完**：`NoteService` 在 Core，`NoteViewModelFactory` 与 `WindowManager` 在 App。让 `NoteService` 直接调它们，Core 就引用了 App（违反 §3.1 规则 1）；而如果反过来让 `NoteViewModelFactory` 依赖 `INoteService`（它确实需要——`NoteViewModel` 要用 `INoteService`）、同时 `NoteService` 又依赖 `NoteViewModelFactory`，就成了**构造循环**，DI 容器会直接报循环依赖。所以拆成上面这三步：Core 做 Core 能做的判断，App 做 App 能做的构造与开窗。

`WindowManager` **只认 `NoteViewModel` 这个类型，不认它的构造方式**——构造好的实例由调用方传进来。

## 14.2 接口

```csharp
public interface IWindowManager
{
    /// <summary>为指定的 ViewModel 创建并显示一个便签窗口。若该便签已有窗口，则激活并返回。</summary>
    void ShowNote(NoteViewModel viewModel, NoteLayout layout);

    /// <summary>
    /// 关闭便签窗口。**只动窗口，不改数据**——不删除便签，也不写 layout。
    /// IsOpen 的置位与 layout 的落盘由 INoteService.MarkNoteClosed 负责（§17.3、§14.1 的依赖方向）。
    /// </summary>
    void CloseNote(Guid noteId);

    /// <summary>关闭所有便签窗口（不改变各自的 IsOpen 状态，用于会话级隐藏）。</summary>
    void HideAllNotes();

    /// <summary>
    /// 把**已经存在**的便签窗口全部恢复显示（被最小化的还原），并激活第一个。
    /// 它不知道该显示哪些便签——"哪些便签应该打开"是 INoteService.OpenAll 的判断（§17.6）。
    /// </summary>
    void ShowAllNotes();

    /// <summary>把窗口的当前几何信息写回 NoteLayout（物理像素 + DPI）。</summary>
    void CaptureGeometry(Guid noteId, NoteLayout target);

    /// <summary>应用折叠/展开后的尺寸变化。</summary>
    void ApplyCollapsed(Guid noteId, bool collapsed);

    /// <summary>应用置顶变化。</summary>
    void ApplyTopMost(Guid noteId, bool topMost);

    /// <summary>应用锁定变化。</summary>
    void ApplyLocked(Guid noteId, bool locked);

    /// <summary>尝试启用/禁用点击穿透。返回 false 表示当前环境不支持（§13.7 原型失败）。</summary>
    bool TryApplyClickThrough(Guid noteId, bool enabled);

    /// <summary>显示器配置发生变化（WM_DISPLAYCHANGE）时的重排。</summary>
    void OnDisplayConfigurationChanged();

    /// <summary>查询某个便签是否有打开的窗口。</summary>
    bool IsNoteOpen(Guid noteId);
}
```

**与 v1 §104 的差异**：v1 的接口里缺少 `SetLocked`、`RestoreLayout`、`SaveLayout`，而 §54 里又出现了这些方法——两处不一致。v2 合并为上面这一份，方法名与 §14.3 的语义一一对应：

- v1 的 `SetLocked` → `ApplyLocked`（`Apply*` 前缀统一表示"把模型状态反映到窗口"）
- v1 的 `RestoreLayout` → 合并进 `ShowNote`（开窗时必然要按 layout 摆位，拆开没有意义）
- v1 的 `SaveLayout` → `CaptureGeometry`（明确它是"从窗口读回几何"，而不是"写文件"；写文件是 §8 的 LayoutStore 的职责）

**接口里没有"创建便签"这类业务方法**——那是 `INoteService` 的事。

### 两个被反复引用的入口

§3.3 的三条数据流与附录 C 用到了 `NoteViewModelFactory.Create` 和 `INoteService` 的若干方法。这两个是全文引用频率最高的入口，签名以此处为准：

```csharp
// App 层：NoteViewModel 的唯一构造入口（§14.1）。刻意不做成接口——
// 它没有可替换的行为，测试里直接 new 即可（§21.5 的替身表里没有它）。
public sealed class NoteViewModelFactory
{
    public NoteViewModelFactory(INoteService noteService, AutoSaveService autoSave, IDispatcher dispatcher, ...);

    /// <summary>为给定的 Note + NoteLayout 构造一个新的 NoteViewModel。不缓存、不查重。</summary>
    public NoteViewModel Create(Note note, NoteLayout layout);
}

// Core 层：便签的业务入口。所有"该不该开窗、该不该落盘"的判断都在这里（§14.1）。
// 注意分工：真正移动回收站文件的是 TrashService（§7.1），这里只编排顺序与内存状态。
public interface INoteService
{
    // 磁盘 → 内存
    Task LoadAllAsync(CancellationToken ct = default);
    void ApplyExternalChange(string path, NoteReadResult readResult);

    // 编辑 → 内存（不落盘，落盘由 SaveNoteAsync 负责）
    void ApplyLocalEdit(Note note, string content);

    // 内存 → 磁盘
    Task SaveNoteAsync(Guid noteId);
    Task SaveAllAsync(CancellationToken ct = default);

    // 业务操作
    Task<Note> CreateNoteAsync(NoteColor? color = null, string? targetFolder = null);
    Task MoveNoteAsync(Guid noteId, string targetFolder);
    Task DeleteNoteAsync(Guid noteId);
    Task RestoreFromTrashAsync(Guid noteId, string? targetPath);

    // 窗口开关：业务判断在这里（校验存在性、置 IsOpen、标记 layout 脏），
    // 真正的开/关窗口由 App 层发起方接着调用 IWindowManager（§14.1 的依赖方向）
    NoteOpenRequest? OpenNote(Guid noteId);              // null = 便签不存在
    IReadOnlyList<NoteOpenRequest> OpenAll();            // 托盘/热键"显示全部"（§17.6）
    void MarkNoteClosed(Guid noteId);                    // 置 IsOpen=false + layout 脏（§17.3）
}

/// <summary>App 层开窗所需的最小信息：显示哪个便签、用哪份 layout。</summary>
public readonly record struct NoteOpenRequest(Note Note, NoteLayout Layout);
```

`NoteOpenRequest` 存在的意义是把"业务判断"和"开窗"解耦：`INoteService` 交出结果，App 层拿去构造 ViewModel 并开窗，两边谁都不需要认识对方的类型。

**`NoteReadResult`**：解析器（§5.2）的输出，是**尚未绑定 id 与路径**的中间形态——`Content`、`LineEnding`、`HadBom`、`UnknownFrontMatterKeys`、`ParseIssues`，以及从 Front Matter 解析出的 `Id` / `Color` / `Tags` / `CreatedAt` / `UpdatedAt`（缺失或非法时按 §5.10 的降级矩阵填默认值）。把"解析内容"和"绑定身份"分成两步，是为了让解析器能纯函数化、脱离文件系统单测（§21.1）。

**关于接口的取舍**（全篇统一按这条规则，不再逐处说明）：

**规则：只有当"实现落在别的层"或"测试必须替换它"时才定义接口。** 由此分成两组：

| 组 | 有哪些 | 为什么 |
|---|---|---|
| **有接口** | Core 的 `Abstractions/` 全部（`INoteService`、`INoteRepository`、`IFileWatcher`、`ISettingsStore`、`ILayoutStore`、`ITrashStore`、`IAttachmentStore`、`IWindowManager`、`IAppPaths`、`IClock`），以及 `IDispatcher`、`IDialogService` | 要么实现落在 Infrastructure / App，要么是 §21.5 替身表里点名要替换的（`FakeClock`、`ImmediateDispatcher`、`RecordingDialogService`）。替身表里出现的必须有接口——比如 `LumiMemo.App.Tests` 要测 `AutoSaveService` 的调度（§21.1），就必须能注入一个假的 `INoteService` |
| **没有接口**（具体类） | `AutoSaveService`、`NoteViewModelFactory`、`FileWatchService`、`SearchService`、`TitleDeriver`、`LayoutService`、`SettingsService`、`TrashService` | 同层内部使用、没有可替换行为。`NoteViewModelFactory` 只是 `new NoteViewModel(...)` 的包装；`TitleDeriver` 是纯函数；`TrashService` 的测试在 `LumiMemo.Integration.Tests` 里用真实临时目录跑（§21.1、§21.5），不需要替身 |

`WindowManager` 是**具体类 + 实现 `IWindowManager`**：接口是为了让 ViewModel 依赖抽象（§18.6）、并让窗口层可被替换成记录型替身，类本身没有第二个实现。

因此文中提到 App 内部服务时一律用具体类名（`AutoSaveService`），不写 `IAutoSaveService` 这类并不存在的名字。

## 14.3 实现要点

```csharp
public sealed class WindowManager : IWindowManager
{
    private readonly Dictionary<Guid, NoteWindow> _windows = new();
    private readonly NoteWindowPool _pool;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<WindowManager> _logger;
    // 注意：这里没有 INoteService，也没有 NoteViewModelFactory

    public void ShowNote(NoteViewModel viewModel, NoteLayout layout)
    {
        _dispatcher.VerifyAccess();   // 所有窗口操作必须在 UI 线程

        if (_windows.TryGetValue(viewModel.Id, out var existing))
        {
            RestoreIfMinimized(existing);
            existing.Activate();
            return;
        }

        var window = _pool.Rent(viewModel);
        window.DataContext = viewModel;

        PlaceWindow(window, layout);       // §13.8 的算法
        ApplyVisualState(window, layout);  // 折叠/置顶/锁定

        window.Closed += (_, _) => OnWindowClosed(viewModel.Id);

        window.Show();
        DwmInterop.ApplyRoundedCorners(window.Handle);   // §13.1
        DwmInterop.EnsureSystemShadow(window.Handle);    // §13.1（实测需要时才加）

        _windows[viewModel.Id] = window;
    }

    private void OnWindowClosed(Guid noteId)
    {
        if (!_windows.Remove(noteId, out var window)) return;
        _pool.Return(window);              // 池化前按 §13.9 做清理
    }
}
```

**所有公开方法都做 `VerifyAccess()`**。窗口操作必须只在 UI 线程发生，这个断言能在开发期第一时间发现跨线程调用（§3.4 规则 T1/T2）。

## 14.4 几何信息的回写

`WindowManager` **不直接写 layout.json**。它的职责是"从窗口读回几何"，把结果填进调用方给的 `NoteLayout` 对象：

```csharp
public void CaptureGeometry(Guid noteId, NoteLayout target)
{
    _dispatcher.VerifyAccess();
    if (!_windows.TryGetValue(noteId, out var window)) return;

    // RestoreBounds 才是「还原状态」的位置。
    // 如果窗口当前是最大化/最小化，Window.Left/Top 是不可用的，
    // 直接用它们会把最大化时的坐标写进 layout.json，导致下次还原到错误的尺寸。
    var bounds = window.WindowState == WindowState.Normal
        ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
        : window.RestoreBounds;

    // WPF 的 Left/Top 是 DIP，这里统一转成物理像素
    var source = PresentationSource.FromVisual(window);
    var m = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;

    target.X      = bounds.X      * m.M11;
    target.Y      = bounds.Y      * m.M22;
    target.Width  = bounds.Width  * m.M11;
    target.Height = bounds.Height * m.M22;
    target.Dpi    = (uint)Math.Round(96 * m.M11);
}
```

**两个容易出错的点**：

1. **必须用 `RestoreBounds` 处理最大化/最小化状态**。这是最常见的"布局记错了"的原因。
2. **`TransformToDevice` 取自窗口自己的 `PresentationSource`**，而不是 `VisualTreeHelper.GetDpi(this)` 之类——前者反映的是窗口当前所在显示器的实际缩放矩阵，在跨屏拖动后仍然正确。

**回写时机**：`WM_EXITSIZEMOVE`、最大化/还原、DPI 变更去抖后、折叠/展开、程序退出前（§13.8 已列）。

**回写后的持久化**：`NoteService` 收到几何变化 → 标记 layout 脏 → 由 §8 的 `LayoutStore` 按去抖策略写 layout.json（默认 1 秒去抖 + 退出时强制 flush）。**窗口层不碰文件**。

## 14.5 显示器配置变化

订阅 `WM_DISPLAYCHANGE`（通过主窗口之外的隐藏消息窗口，或 `SystemEvents.DisplaySettingsChanged`）：

```text
显示器配置变化
  ↓
1. 对每个已打开的便签窗口：
   - 计算其当前中心点落在哪个显示器
   - 该显示器是否还存在？
     ├─ 不存在 → 移到主显示器（§13.8 的层叠策略）
     └─ 存在但工作区变小（如分辨率降低）→ 按 §13.8 夹取
  ↓
2. 用 SetWindowPos 应用修正
  ↓
3. 标记 layout 脏，触发一次保存
  ↓
4. 不要让这一步触发「外部修改冲突」提示——它是程序自己做的调整
```

**注意**：`SystemEvents.DisplaySettingsChanged` 是从系统事件线程来的**跨线程回调**，必须用 `Dispatcher.InvokeAsync` 封送到 UI 线程（§3.4 规则 T3）。

---

# 15. UI 与视觉

## 15.1 设计基准

| 项 | 值 | 说明 |
|---|---|---|
| 基准字号 | 14 DIP | 便签正文 |
| 字体族 | `Microsoft YaHei UI, Segoe UI` | 中英混排的首选；**不打包字体**，直接用系统字体 |
| 行高 | 1.5 倍字号 | 中文密集阅读的舒适值 |
| 便签最小宽度 | 240 DIP | 再窄标题会被截断到无法辨认 |
| 便签最小高度 | 120 DIP（展开）/ 44 DIP（折叠） | |
| 便签默认尺寸 | 360 × 420 DIP | |
| 圆角 | 交给 DWM，不设数值（§13.1） | |
| 内容内边距 | 12 DIP | |
| 标题条高度 | 36 DIP | |
| 动画时长 | 150ms | 折叠/展开、面板滑入 |

**关于字体**：不要为了"统一外观"打包思源黑体之类的字体。中文字体文件动辄十几 MB，会让安装包体积翻几倍，而且系统自带的雅黑在 Windows 上的渲染是最优的（有 hinting）。设成 `Microsoft YaHei UI, Segoe UI` 让中文走雅黑、英文和符号走 Segoe UI。

## 15.2 便签窗口布局

```text
┌───────────────────────────────────────────────┐  ← 6px 缩放热区（系统）
│ ≡  工作/周报-20260920                    📌 ⚙ ✕│  ← 标题条，36 DIP
├───────────────────────────────────────────────┤
│                                               │
│  正文编辑区                                    │  ← 可滚动
│                                               │
│                                               │
├───────────────────────────────────────────────┤
│ 已保存 · 124 字                                │  ← 状态条，24 DIP（可隐藏）
└───────────────────────────────────────────────┘
```

**标题条**（自左向右）：

| 元素 | 行为 |
|---|---|
| 折叠按钮 | 折叠/展开（`IsCollapsed`） |
| 标题文本 | 显示 `Note.Title`（派生，§5.4）。点击进入重命名（§15.4） |
| 置顶按钮 | 切换 `IsTopMost`，图标有选中态 |
| 菜单按钮 | 下拉菜单：颜色、标签、在资源管理器中显示、导出、删除 |
| 关闭按钮 | 关闭窗口（不删除数据，§13.9） |

**标题条是拖动区**（§13.3），按钮区域排除在外。

**折叠状态**：整个窗口只剩标题条 + 状态条，高度约 44 DIP。折叠时：
- 编辑器仍然存在，只是 `Visibility=Collapsed`（**不要销毁**，否则展开后光标位置和撤销栈都会丢）
- 标题条仍然是拖动区
- 窗口高度缩到 44 DIP，展开时恢复 `ExpandedHeight`（§9 的 `NoteLayout.ExpandedHeight`）

**状态条**：显示保存状态与字数。**这是 v1 缺失的一项**——用户在便签里写字时需要一个"确实保存了"的信号，否则会不放心。状态条可整体隐藏（设置项），默认显示。

保存状态的四种文案：

| 状态 | 文案 | 颜色 |
|---|---|---|
| 无未保存内容 | `已保存` | 次要灰 |
| 有未保存内容 | `正在保存…` 或 `未保存` | 次要灰 |
| 保存失败 | `无法保存 · 点击查看` | 警示色，可点击 |
| 外部冲突 | `外部已修改 · 点击处理` | 警示色，可点击 |

## 15.3 颜色主题

```csharp
public enum NoteColor
{
    Yellow, Pink, Blue, Green, Purple, Orange, Gray
}
```

**每种颜色需要三个色值**，而不是一个：

```text
{Color}Background    便签纸背景
{Color}TitleBar      标题条背景（比背景略深，形成层次）
{Color}Accent        强调色（选中、链接、复选框勾选）
```

**定义方式**：一个 `ResourceDictionary`，按颜色分组：

```xml
<ResourceDictionary xmlns:sys="clr-namespace:System;assembly=System.Runtime">
  <!-- 便签纸：黄 -->
  <Color x:Key="NoteYellowBackgroundColor">#FFFDF3C4</Color>
  <SolidColorBrush x:Key="NoteYellowBackgroundBrush"
                   Color="{StaticResource NoteYellowBackgroundColor}" />
  <SolidColorBrush x:Key="NoteYellowTitleBarBrush" Color="#FFF7E9A0" />
  <SolidColorBrush x:Key="NoteYellowAccentBrush"    Color="#FFB58900" />
  ...
</ResourceDictionary>
```

**关于 v1 §40 的问题**：那里写了

```xml
<sys:Double x:Key="NoteCornerRadius">12</sys:Double>
```

`CornerRadius` 在 WPF 里是一个**结构体**（`System.Windows.CornerRadius`），不是 `double`。用 `sys:Double` 作为它的资源会导致在 XAML 中使用时类型转换失败。v2 中**不需要这个资源**——圆角由 DWM 提供（§13.1），`WindowChrome.CornerRadius` 保持 0。

如果将来确实需要 WPF 侧画圆角矩形（比如便签内部的卡片），正确写法是：

```xml
<CornerRadius x:Key="CardCornerRadius">8</CornerRadius>
<!-- 或 -->
<CornerRadius x:Key="CardCornerRadius">8,8,0,0</CornerRadius>
```

`CornerRadius` 在 XAML 的默认命名空间里（`System.Windows`），不需要 `sys:` 前缀。

**主题切换**：便签颜色是**每张便签独立**的（存在 Front Matter，§5.4），不是全局主题。应用整体的亮/暗色是另一个维度，v2 支持跟随系统（`SystemParameters.HighContrast` 与 Win11 的浅色/深色），在设置里可选"跟随系统 / 始终浅色 / 始终深色"。

**深色模式下的便签颜色**：每种 `NoteColor` 需要一套深色变体（`NoteYellowBackgroundBrushDark` 等）。切换明暗时用 `DynamicResource` 而不是 `StaticResource`，这样运行时能正确刷新。

> **本轮拍板：上面两段推迟到「界面整体改透明毛玻璃」那一轮一起做。**
> 深色模式没法单独做完——它要定的恰恰是「纸面在暗色下是什么颜色」，而毛玻璃之后纸面本身要重新定值。此刻先推一份深色变体，等毛玻璃落地时整套得重来一遍。
> **凡属样式的内容统归那一轮**：本节的两个深色套、§15.10 `Colors.xaml` 的深色套、`theme` 字段的界面入口（§15.9 的推迟表里那一行）。

**实现说明**：`App/Resources/Colors.xaml` 已落地（由 `App.xaml` 合并进来）。**上面给的只有黄色那一组三值，其余六组是自拟的**——按同样的明度关系推：背景很浅（L≈90%）、标题条中浅（L≈80%）、强调色饱和且深（L≈35%），七个颜色的强调色两两分得开。文档示例里那种"先写 `<Color>`、再让 `SolidColorBrush` 用 `StaticResource` 引它"的写法**没有照做**：眼下没有任何消费者要那个 `Color` 本身（唯一用得着的是本节末的深色变体，而那一套还没做），多一层间接只是多一处要同步的地方。深色变体与 `DynamicResource` 一并留给**界面重构那一轮**（见上框）。

**强调色在管理器里已经用起来了**：结果项右侧那个 8×8 的颜色点取的是 `{Color}AccentBrush` 而不是背景色——这么小的点上，七个浅背景色在白底上彼此分不开。转换在 `Views/Converters/NoteColorToBrushConverter.cs`。右键菜单「颜色」子菜单那七个色块引的是同一批笔刷。

**颜色这条链路的状态（截至颜色/标签编辑落地）**：数据面已经全通——管理器改色 → `INoteService.ApplyColorEdit` → 写回 `Note.Color` → `SaveNoteAsync` 写进 Front Matter → 重启还在；管理器列表那一行的颜色点当场就换。**但便签窗口还没跟着换**：`NoteWindow.xaml` 既不绑 `Color` 也不绑 `Tags`（`NoteViewModel.Color` 是一份没有消费者的镜像），所以 `SetColorAsync` 刻意**不发**任何消息——发出去就是一条没有接收方的管线。便签窗口用哪一支笔刷、切换时怎么刷新，等**界面整体改毛玻璃那一轮**一起做：半透明的纸与"每张便签自己的颜色"会互相打架，颜色这套值在毛玻璃下要重新定，不是换个笔刷的事。

## 15.4 标题与重命名

**标题是派生的，不是独立字段**（§5.4）。这带来一个必须明确的交互：**用户如何"改标题"？**

```text
用户点标题 → 变成可编辑输入框
  ↓
用户输入「周报」
  ↓
写回内容的第一行：
   - 内容以 # 开头 → 重写这个 # 行的内容：# 周报
   - 内容不以 # 开头 → 把第一行替换为 # 周报
   - 内容为空 → 直接变成 # 周报
  ↓
标题重新派生 → 显示「周报」
```

**这是 v1 §49 遗留的"改标题写回哪里"未定义的问题**，v2 在此拍死：**永远写回正文第一行，不引入额外的 Front Matter 标题字段**。

理由：如果 Front Matter 里也存一个 `title`，就会出现"标题字段"和"第一行 H1"两个都可能存在、可能不一致的状态，而"标题派生"的前提正是"只有一处真相"。保持单一来源，比允许用户用两种方式改名更重要。

**删除标题**：用户把标题清空 → 删除正文第一行的 `# ` 前缀（而不是删掉整行内容）。

**标题的派生规则**（§5.4 已定义，这里只重复交互侧的约定）：

```text
1. 去掉 Front Matter
2. 找第一个非空行
3. 若该行形如 # / ## / ### ... 开头 → 取 # 之后的文本
4. 否则取该行的前 60 个字符
5. 若内容全空 → 「无标题便签」
6. 若第一个非空行是代码块围栏 ``` 或表格分隔行 → 跳过，继续找
```

## 15.5 内容缩放

> **本节整条作废，功能已从代码里删除。**
>
> 内容缩放（`Ctrl+=` / `Ctrl+-` / `Ctrl+0` / `Ctrl+滚轮`、状态条上那个可点的百分比）**不需要**——裁定为**删除**，不是推迟。理由见 §0.3 的裁决表：它不是一个只差界面入口的功能，而是已经在四处占了字段（`NoteLayout.ContentScale`、`AppSettings.DefaultContentScale`、layout.json 的 `contentScale`、settings.json 的 `defaultContentScale`），而「每张便签各自记忆」又把这一条绑进了布局持久化。「留着字段、以后再做」的真实代价，是给后来的人留一条「文档说要做」的假线索，外加一份永远不会有人写的配置键。
>
> **已删除的位置**（改这块时别再按本节加回来）：`NoteLayout.ContentScale`、`LayoutFileModel` 的 `contentScale` 与其双向映射、`JsonLayoutStore.DefaultContentScale`、`AppSettings.DefaultContentScale`、`StartupSequence` 里那一次赋值、`NoteViewModel.ContentScale`、`SettingsViewModel` 的 `ContentScale` 与两个上下限常量、`SettingsWindow.xaml` 的「内容缩放」那一行，以及四处测试里的相应断言。
>
> **没有连带删的**：`NoteLayout.ExpandedHeight`（折叠用，与缩放无关）、`showStatusBar`（状态条本身还在，只是不再有缩放百分比这一项）。§16.3 快捷键表里的 `Ctrl+0` / `Ctrl+=` / `Ctrl+-` / `Ctrl+滚轮` 四条一并删掉，**没有转派给别的功能**：便签里的 `Ctrl+滚轮` 回落到 `TextBox` 自己的默认行为（不缩放），不做滚轮缩放。
>
> 以下保留原设计原文，**仅供追溯，任何一条都不再是实现依据**。

### 原设计（已作废）

便签内容可以整体放大缩小，用于"贴参考信息时字太小看不清"的场景。

**规格**（对齐 PinSlip 的用户指南）：

| 项 | 值 |
|---|---|
| 步进 | 10% |
| 范围 | 50% ~ 200% |
| 每张便签 | **各自记忆**，互不影响 |
| 持久化 | `NoteLayout.ContentScale`（§9），**不进 Markdown** |
| 复位 | `Ctrl+0` 回到 100% |
| 入口 | 状态条的缩放百分比可点击 → 弹出 50%–200% 的滑杆；`Ctrl+=` / `Ctrl+-` / `Ctrl+滚轮` |

**实现**：**不要**逐个控件的改字号。用整体缩放：

```xml
<!-- 便签内容根元素 -->
<Grid x:Name="ContentRoot"
      LayoutTransform="{Binding ContentScaleTransform}">
```

`ContentScaleTransform` 是一个 `ScaleTransform`（在 ViewModel 里持有，`ScaleX = ScaleY = ContentScale`）。

**用 `LayoutTransform` 而不是 `RenderTransform`**：`RenderTransform` 只改变绘制，不改变布局尺寸，缩放后内容会溢出或留空。`LayoutTransform` 会真正参与布局计算，滚动条范围也随之正确。

**代价**：`LayoutTransform` 在缩放变化时会触发完整布局重算，对很长的便签有一次性开销。因为缩放是低频操作（用户偶尔调一次），这个代价可以接受。

**与系统 DPI 缩放的关系**：两者是**相乘**的。100% DPI + 150% 内容缩放 = 最终 150%；200% DPI + 150% 内容缩放 = 最终 300%。这是用户期望的行为，不需要特殊处理。

**注意**：缩放是"内容"缩放，**标题条和状态条不跟着缩放**——否则 200% 时标题条会占掉半个窗口。`LayoutTransform` 只挂在内层的内容容器上。

## 15.6 可点击任务复选框

Markdown 的任务列表 `- [ ] 待办` / `- [x] 已完成` 在便签里应该可以直接点击勾选。

**这是 v1 遗漏的一项核心体验**，PinSlip 支持，用户对便签的期待里这是自然的一部分。

### 渲染

Markdig 开启 `UseTaskLists()` 扩展，把 `- [x] 文本` 解析成带任务列表标记的节点，渲染成一个 `CheckBox` + 文本 + 一个不可见的锚点（记录它在**源文本中的字符偏移**）。

### 点击行为

```text
用户点击复选框
  ↓
1. 从 Tag 里取出该任务的源文本偏移（int offset）
2. 在 Content 的对应位置定位 "[ ]" 或 "[x]"
3. 只替换这 3 个字符：' ' ↔ 'x'
4. 立即触发保存（不等 500ms 去抖）——勾选是有明确意图的操作
```

**关键点**：**只改那 3 个字符**。

不要用"重新生成整段 Markdown"的方式——那会丢失用户原文里的其他格式（缩进、行尾空格、非常规列表符号）。直接做一次 `StringBuilder` 的定点替换，改动最小，也就不会触发不必要的 diff。

### 源文本偏移的可靠性

偏移必须在**每次解析时重新计算**，不能跨解析复用（用户编辑后所有偏移都会失效）。

```csharp
// 解析时记录每个任务项在源文本中的位置
public sealed record TaskItemAnchor(int MarkerOffset);   // "[ ]" 中 "[" 的位置
```

点击时用当下的解析结果，而不是缓存的旧偏移。

### 为什么不用"重新序列化 AST"

把 Markdig 的 AST 重新序列化成 Markdown 是**不保真**的（会丢失原始空格、缩进风格、非标准写法）。对"用户的 .md 文件必须保持原样"这个核心承诺来说，这是不可接受的。**只做定点字符替换。**

**文本里的行内代码与代码块中的 `- [ ]` 不渲染成复选框**，这是 Markdig 的正确行为，与 GFM 一致。

### 点击复选框不移动光标

勾选后**不要**把焦点抢到编辑器并跳到那个位置。用户的意图是勾一下，不是去编辑它。焦点保持在原来的地方。

## 15.7 速记浮窗

**PinSlip 的 `Ctrl+Shift+N` 速记浮窗是 v1 完全遗漏的一项核心功能**，v2 纳入一期范围。

### 目的

解决"突然想到一件事，但不想打断当前工作去找便签、开便签、定位便签"的问题。

### 交互

```text
按下全局热键 Ctrl+Shift+N
  ↓
在任何应用上方弹出一个小浮窗（居中偏上）
  ↓
光标已经在输入框里，直接打字
  ↓
Esc          → 丢弃，关闭
Ctrl+Enter   → 保存为一张新便签，关闭
点击外部      → 视同 Ctrl+Enter（保存）
```

**为什么"点击外部 = 保存"而不是"= 丢弃"**：用户速记时往往是"写两句、切回去做别的事"，如果点击外部就丢弃会让人措手不及。保存是更安全的方向（内容进了便签，之后可以再整理或删除）。

### 技术规格

| 项 | 值 |
|---|---|
| 窗口样式 | `WS_EX_TOOLWINDOW`（不进任务栏/Alt+Tab，§13.4） |
| 置顶 | `TopMost = true` |
| 尺寸 | 520 × 180 DIP |
| 位置 | 主显示器工作区水平居中，顶部 1/5 处 |
| 内容 | 一个多行纯文本框 + 底部一行提示（`Ctrl+Enter 保存 · Esc 取消`） |
| 初始焦点 | 必须拿到键盘焦点（见下） |
| 保存结果 | 新建一张便签，内容为浮窗里的文本，颜色取"上次新建便签用的颜色"，`IsOpen = true`，位置在空闲区域 |

### 拿焦点的坑

**从后台热键弹出的窗口不会自动获得键盘焦点**，这是 Windows 的前台锁定（foreground lock）机制。

```csharp
// 1. 先 Show，窗口必须已经可见
_window.Show();

// 2. 尝试直接激活
if (!_window.Activate())
{
    // 3. 失败时用 AttachThreadInput 把本线程的输入队列连到当前前台窗口的线程，
    //    借用它的前台权限，激活后再断开
    var fg = GetForegroundWindow();
    uint fgThread = GetWindowThreadProcessId(fg, out _);
    uint myThread = GetCurrentThreadId();

    if (fgThread != myThread)
    {
        AttachThreadInput(myThread, fgThread, true);
        _window.Activate();
        SetForegroundWindow(_window.Handle);
        AttachThreadInput(myThread, fgThread, false);
    }
}

// 4. 把焦点放进文本框
_editor.Focus();
Keyboard.Focus(_editor);
```

**这是必须验证的一步**：热键弹出但焦点没进去（用户开始打字却发现打到了下面的应用里）是这类功能最常见的失败模式，而且不会报错，只会让人莫名其妙。**必须在不同前台应用（浏览器、VS Code、资源管理器、全屏视频）下各测一遍。**

### 浮窗不落盘为常规窗口

浮窗关闭后**销毁**，不进窗口池（它的行为特性与其他便签窗口差异太大，见 §13.9）。

如果浮窗已经打开，再次按热键 → 激活已有的浮窗，不新建。

### 不做的部分

**不做"浮窗内容先存草稿、关闭后还在"**。速记的价值是"零决策"——要么保存成便签，要么丢弃。引入草稿状态会让它变成一个需要管理的东西。

## 15.8 便签列表与搜索面板

一个独立的管理窗口（从托盘菜单打开），用于查找和打开便签。

```text
┌─────────────────────────────────────────────────┐
│ 🔍 搜索便签...                      [ + 新建 ]  │
├─────────────────────────────────────────────────┤
│ ⟨全部⟩  ☐ 置顶   ☐ 有标签   ☐ 最近 7 天           │  ← 过滤条
├─────────────────────────────────────────────────┤
│ 周报-20260920  ⟨周报⟩                     黄 ●  │
│ 本周完成：窗口系统原型…            · 2 天前     │
├─────────────────────────────────────────────────┤
│ Docker 常用命令                          蓝 ●  │
│ docker compose up -d --build…       · 1 周前    │
└─────────────────────────────────────────────────┘
```

`⟨全部⟩` 是尖括号不是复选框，因为它**不是第四个条件**——见下面过滤条那一段。

**规格**：

| 项 | 说明 |
|---|---|
| 搜索 | 增量，输入停止 150ms 后执行（§12.4） |
| 排序 | 按 §12.2 的评分规则；无查询词时按 `UpdatedAt` 降序 |
| 结果项 | 标题（加粗）、标签（小徽章）、摘要（单行，命中词高亮）、颜色点、相对时间 |
| 双击 | 打开或激活该便签窗口 |
| 右键 | 置顶 / 颜色 / 标签 / 在资源管理器中显示 / 移入回收站 |
| 虚拟化 | `ListBox` + `VirtualizingStackPanel`，必须确认 `IsVirtualizing` 没被关掉 |
| 结果上限 | 单次渲染最多 200 条，超出提示"还有 N 条结果，请细化搜索词" |

**200 条上限的理由**：搜到一个常见词（比如"的"）可能命中上千条。全部渲染会让面板卡住，而用户也不会去看第 500 条。先给 200 条，让用户细化。

**过滤条是"与"关系**：勾选了"置顶"和"最近 7 天"就是两个条件同时满足。

**实现说明（本轮已落地）**：搜索框、150ms 去抖、副标题按有无查询词切换、200 条上限与溢出提示都已接上。落点如下。

| 位置 | 内容 |
|---|---|
| `ViewModels/ManagerViewModel.cs` | `SearchQuery`（去抖入口）、`SearchDebounceMilliseconds`（由启动序列从 `AppSettings.SearchDebounceMs` 灌入）、`MaxRenderedResults = 200`、`HasOverflow` / `OverflowHint`、`EmptyHint` / `CountText` 按查询词与过滤条切换；`FilterTopMost` / `FilterTagged` / `FilterRecent` + `IsFilterAll` / `ClearFilters` / `PassesFilter`；`ToggleTopMost` / `TopMostMenuHeader` / `NotifyContextMenuOpening`；`RevealInExplorerAsync`；`SetColorAsync` / `EditTagsAsync` + `ValidateTagsInput` / `PersistEditAsync`（后两者是私有的） |
| `Views/SnippetPresenter.cs` | 把 `IReadOnlyList<SnippetSegment>` 画成一行文字的自绘 `TextBlock` |
| `Views/Converters/NoteColorToBrushConverter.cs` | 颜色名 → `Note{颜色}AccentBrush` 笔刷；取不到时退到一支冻结的灰色（无 WPF 应用的进程里 `Application.Current` 是 `null`） |
| `Views/Converters/NoteColorMatchesConverter.cs` | 「当前颜色是不是参数指定的那一个」，只用于颜色子菜单那七项的 `IsChecked`（`Mode=OneWay`）。反向抛异常 |
| `Services/DialogService.PromptAsync` + `Views/PromptDialog.xaml(.xaml.cs)` | 单行输入对话框（文档没有给样子，自拟）。带一个调用方传进来的校验回调，不过时不关窗 |
| `Core/Services/TagRules.cs` | §5.8 规则的唯一实现（`Normalize` / `TryAdd` / `Split`）。解析器与管理器的标签编辑框都走它 |
| `Resources/Colors.xaml` | §15.3 调色板。**只有黄色三值是文档给的，其余六组是自拟的**（背景很浅 L≈90%、标题条中浅 L≈80%、强调色饱和且深 L≈35%）。文档 §15.3 示例还给背景另写了 `<Color>` 资源，这里**没有照做**——眼下没有消费者 |
| `Messages/NoteTopMostChangedMessage.cs` | 「某一张便签的置顶被别处改了」。与 `NotesChangedMessage`（"有哪些便签变了"、接收方整表重读）刻意分开：合并之后管理器每切一次置顶都要整表重扫，而重扫会把用户正在看的那一行从选中状态里抖出去 |
| `ViewModels/NoteListItem.cs` | 副标题的片段由 `Query` 决定：`SnippetBuilder.Build` 摘得出就用摘要，摘不出退回「修改时间 · 字数」；`Tags` / `Color` 是给结果项那两个装饰用的直通 |

**两条路径不能合并。** 查询词为空时直接取 `NoteStore.Snapshot()` 按 `UpdatedAt` 降序（`NoteSearch.OrderForList`），有查询词时才走 `NoteSearch.Search` 的评分排序。若让空查询也走评分，那些"分数很低但确实匹配"的规则会把整个列表重排一遍，用户会在清空输入框的瞬间看到列表乱跳。

**`SnippetPresenter` 为什么要派生一个类。** 摘要是一行连着排的文字，用 `ItemsControl` 会把每段变成独立的排版单元（段间多出间距、行尾省略号失效）。而 `TextBlock.Inlines` **不是依赖属性**，绑不上——只剩"派生一个类、在属性变更时自己重建 `Inlines`"这一条路。只有命中段设前景色，其余段一律不设、靠继承拿外层 `Foreground`，于是调用方在 XAML 上写一个 `Foreground` 就统一改掉了非命中部分的颜色。

**副标题的"修改时间"用绝对时刻（`9/18 11:19`）而不是上面示意图里的「2 天前」。** 这是上一轮已定的裁决（相对时间要跟着"现在"变，列表刷新的时机就成了一件要额外定义的事），此处与示意图不一致，以裁决为准。字号那一截取的是 `Note.Content.Length`，**含 Markdown 标记**——用户看到的数与文件里的字节数一致。

**这一节里没有"排序字段"这一项**——上面表格里的「排序」是一条规定（按 §12.2 评分；无查询词按 `UpdatedAt` 降序），不是让用户选的字段，所以没有排序下拉框。

**过滤条（全部 / 置顶 / 有标签 / 最近 7 天）**。「全部」**不是第四个条件、也不参与那个"与"运算**——它就是"一个条件都不勾"。做成一个真的复选框会立刻出现"全部 + 置顶"该是什么意思这种答不上来的问题。因此落地成：三个条件各是一个 `ToggleButton`（点一下生效、再点一下取消），「全部」是一个 `Button`，按 `ClearFiltersCommand`，并在 `IsFilterAll` 为真时由 `DataTrigger` 亮起，表示"现在没有在筛"。

**过滤排在搜索之前**（`ManagerViewModel.BuildItems`）。被条件筛掉的便签压根不该参与 §12.2 的评分与排序，也不该进 `_matches`——状态栏的「筛选出 N 条」与「还有 N 条结果」读的都是 `_matches`，若过滤排在搜索后面，用户会看到"找到 2 条"而列表里只有一行。置顶那一份 id 集合（`TopMostIds()`）在两个阶段都要用，所以只建一次。

**过滤条与 §12.2 的「七天内 +30」共用同一个窗口常量**（`NoteSearch.RecentWindow`）。两处各写一个 `TimeSpan.FromDays(7)` 的话，把窗口改成 3 天时只会改到一处，症状是"搜出来的结果比筛出来的多"——两者都叫"最近"却没有同一套口径。`ClearFilters` 里逐次给三个属性赋值会把列表重建三遍（点一下肉眼可见地卡三下），用 `_clearingFilters` 把中间几次 `OnFilterChanged` 挡掉；**不能**图省事直接写后备字段，那连 `PropertyChanged` 都没有，界面上三个按钮不会弹回来，而工具包的 MVVMTK0034 也正是为了拦这一手。

**结果项的颜色点与标签徽章**。颜色点取的是调色板里的**强调色**而不是背景色——这个点只有 8×8，七个浅色背景在白底上彼此分不开。颜色名到笔刷的映射在 `Views/Converters/NoteColorToBrushConverter.cs` 里（`Note{颜色}AccentBrush` 去 `Application.Current.TryFindResource`），而不是在 `NoteListItem` 上放一个 `Brush` 属性：笔刷是界面概念，调色板只能有一处真相源。标签徽章是一个横向 `StackPanel` 的 `ItemsControl`，空标签时不占宽度。`NoteListItem.Tags` **直接交出 `Note.Tags` 本身**，不复制、不排序——标签的数量与顺序都是用户自己定的（§5.8 按 Front Matter 原样保留）。

**虚拟化显式钉住**（`IsVirtualizing="True"` / `VirtualizationMode="Recycling"` / `ScrollViewer.CanContentScroll="True"`）。前两个本来就是默认值，写出来是为了将来有人给 `ListBox` 套一层外层 `ScrollViewer` 时能立刻看出这里被改过。真正要命的是第三个：`CanContentScroll` 为 `False` 时 `ListBox` 按像素滚动，于是必须先把所有项都测量一遍，虚拟化就名存实亡了；它与 `HorizontalScrollBarVisibility="Disabled"` 是一对，外层 `ScrollViewer` 拿到无限宽度时同样会失去虚拟化。这个列表可能有上千条（搜索命中多时），关掉虚拟化的代价是肉眼可见的卡顿。

**`[ + 新建 ]` 放在工具条最前面**，走 `NewNoteCommand`——它与托盘菜单、将来的 `Ctrl+N` 是同一条路（顺序仍是 §3.3 流 3：业务层建 → 布局层算位 → 工厂造 ViewModel → 开窗），入口可以多，路径只能有一条。

**`[ + 新建 ]` 与「显示全部 / 隐藏全部」不属一类动作**：它是这个窗口里唯一"造出新东西"的动作，所以排在那几个"对已有的东西做事"之前。

**工具条末尾有一个齿轮按钮**（打开设置窗口，§15.9 的补充）。它用齿轮而不是"设置"两个字，是因为它跟左边三个不属一类：那三个是"对便签做事"，它是"对程序做事"。这一段点击处理**放在代码后置而不是 ViewModel 命令里**——它不含任何状态判断（"已开着就唤到前面"这条规则在 `SettingsWindowLauncher` 里），跟列表项双击是同一档。真有一个"什么时候不该开"的条件时再挪进 ViewModel。

**右键菜单五项齐了**：置顶 / 颜色 / 标签 / 在资源管理器中显示 / 移入回收站。分隔线以上是「改这张便签自己」，以下是「跟外头打交道」——五种动作里只有前三种会改便签的内容。

**颜色与标签本轮只做到数据链路**：写回内存 + 写回 Front Matter，验收面是管理器那一行的颜色点与徽章。便签窗口怎么跟着换色留到毛玻璃那一轮（理由见本节末「改颜色不发任何消息给便签窗口」那一段）。

菜单挂在 `ListBox` 上而不是每一行上：每行一个的话，虚拟化列表滚一遍就造出一堆一模一样的实例。代价是 `ContextMenu` 是独立的 `Popup`、不在 `ListBox` 的视觉树里，靠继承拿不到 ViewModel，`DataContext` 得显式绑到 `PlacementTarget`。（试过改挂到 `ItemContainerStyle` 上，为的是免掉下面那一拦——行不通：`ItemContainerStyle` 里一个不带 `BasedOn` 的隐式 `Style` 会把 `ListBoxItem` 的默认样式整个替换掉，连 `Template` 一起，列表项当场就不正常了。真要挂得补 `BasedOn="{StaticResource {x:Type ListBoxItem}}"`。）

**共享的 `ContextMenu` 有一个绑定陈旧的坑，靠 `NotifyContextMenuOpening()` 补掉。** 菜单是同一个实例，`DataContext` 绑在 `PlacementTarget.DataContext` 上——每次打开，`PlacementTarget` 都是同一个 `ListBox`、求值结果没变，于是这条绑定不会重新求值，挂在它下面的**所有**绑定也就不去重读。症状：先用便签窗口标题条上的置顶按钮把某张便签置顶，再在管理器里右键它，菜单上还写着「置顶」。所以 `ManagerWindow.OnListContextMenuOpening` 在选中该行之后**显式喊一声**。光靠 `OnSelectedNoteChanged` 不够——右键落在**已经选中**的那一行时选中项没变，一声通知都不会发。

**这一声要喊两句，不是一句**（`TopMostMenuHeader` + `SelectedNote`）。颜色子菜单那七项绑的是 `SelectedNote.Color`，与 `TopMostMenuHeader` 不是同一条属性路径——陈旧的病根在 `DataContext` 那一条绑定上，只有把**下面每一条**依赖它的属性都通知一遍才治得住。少喊第二句的具体症状：右键一张蓝色的便签、在子菜单里点了「蓝」（当前就是蓝），那一项的勾会被 `MenuItem` 自己拨掉——源没变、绑定不会去纠正它，于是七个项里一个亮的都没有，用户以为颜色没了。

**菜单标题写成"点下去会发生什么"**（`TopMostMenuHeader`：已置顶时是「取消置顶」），而不是永远写着「置顶」——后者在已经置顶的便签上分不出这是"再置顶一次"（无动作）还是"取消置顶"。

**「置顶」写完必须发一条消息**（`NoteTopMostChangedMessage`）。置顶的真实状态在 `NoteLayout.IsTopMost` 上，而开着的便签窗口另存一份镜像（`NoteViewModel.IsTopMost`，标题条那个拨动按钮绑的就是它）。镜像这一侧的改动会顺着 `WindowManager.OnViewModelPropertyChanged` 往下走（真的把 HWND 设成 topmost、把布局标记为脏），但反过来——**从布局层改**——没有任何东西会通知窗口：那个字段的写入不发任何通知。于是管理器里点「置顶」，窗口既不置顶、按钮也还显示着未置顶，用户再点一下那个按钮反而把它取消了。这条消息**只在这个方向发**：便签窗口自己切换置顶时，管理器那边没有需要立刻改的东西（列表顺序按修改时间排，与置顶无关——置顶只在搜索结果里加 50 分，那是下一次搜索的事），反方向也发一条就得先有一个"谁先动的手"的判据才能避免两边互相触发。

**「在资源管理器中显示」与「打开笔记目录」是两件事**，所以 `IShellLauncher` 上是两个方法：前者要**选中文件**（`explorer.exe /select,<文件>`），后者只打开目录（`explorer.exe <目录>`）。`/select,<路径>` **整个是同一个参数**——写成两个参数时 explorer 会把后一个当成要打开的目录；与 `OpenFolder` 一样交给 `ProcessStartInfo.ArgumentList`，路径里的空格与中文由运行时加引号，不自己拼 `$"/select,\"{filePath}\""`（§19.3）。失败时弹一句提示而不是静默返回（与 `SettingsViewModel.OpenNotesFolderAsync` 同一手法）：这里的失败只有一个实际来由（文件已经不在了，而列表还是上一轮的快照），但用户刚点了一下按钮，什么都不发生的话他只会以为程序卡住了。

**右键必须先把落在的那一行选中**（`ManagerWindow.OnListContextMenuOpening`）。WPF 的 `ListBox` **不会**因为右键而改变选中项，而菜单项作用在 `SelectedNote` 上——少了这一步，用户在一个没选中的行上点「移入回收站」，删掉的是他上一次选的那张，而且列表一刷新他连"刚才删的是谁"都无从对照。右键落在空白处或滚动条上时菜单整个不弹（`ContainerFromElement` 给不出容器），同理：那一个「移入回收站」会对着一个与鼠标位置无关的选中行执行。

**双击开便签必须同时判「左键」与「落在某一行上」**（`ManagerWindow.OnListMouseDoubleClick`）。两条都不是多余的：

- **判左右键**：`Control.MouseDoubleClick` 挂在 `Mouse.MouseDown` 上、只看 `ClickCount == 2`，**左右键都会触发**。少了这一条，用户在列表上右键连点两下（想调出菜单，或者只是手快）就会凭空开出一张便签窗口，连带管理器失焦——症状看着像"右键点一下窗口就没反应了"，很难联想到是双击。
- **判落在哪一行**：这里早先只有「`SelectedNote` 非空」一个条件，理由是"双击空白处时它天然是 `null`"——**这个前提是错的**。`ListBox` 点空白处**不会**清空 `SelectedItem`，于是左键双击空白处照样会打开上一次选中的那张。判据改用 `ContainerFromElement`：空白处给不出容器，判断才有意义。

**删除的三步顺序不能动**（`ManagerViewModel.DeleteNoteAsync`）：**补一次落盘 → 关窗 → 才搬文件**。便签窗口关闭时自己会存一次（§17.3），但那一次**靠不住**：`NoteWindow.OnClosing` 的做法是「取消这次关闭、把保存排进消息队列、再关一次」，于是 `CloseNote` 返回时那次保存还排在队列里没跑。此时若已经把文件搬进回收站，等它跑起来便签已不在 `NoteStore` 里，`SaveNoteAsync` 会静默返回（那是它刻意为之的行为，见 §11.3）——**用户最后半秒敲的字既没进文件也没进回收站，而他恢复出来的是一份旧内容**，界面上看不出任何异常。所以调用方在搬文件之前主动补一次保存：那一刻便签还在 `NoteStore` 里，写得进去。窗口没开着时不必补——编辑只可能来自一个开着的窗口，而它在关掉的时候已经存过了。

**不弹确认对话框。** 进回收站是可逆的（§7.2 起能恢复），与「清空回收站」（§7.4，那才是 `IDialogService.ConfirmAsync` 的用武之地）不是一回事。

**一处已知的粗糙，记在这里免得将来被当成 bug 查**：删一张**开着**的便签会走 `CloseNote`，于是 `OnNoteWindowClosed` 把 `layout.IsOpen` 置成 `false`——那个回写的语义是「用户关掉了这张便签」（§17.3），而用户其实是删了它。layout 条目本身按 §8.3 保留，位置、折叠、置顶因此都还在，只是从回收站恢复回来时它不会自己弹出来。本轮按"不弹"处理：用户刚恢复一张便签，先看到它在列表里，比它自己蹦到桌面上更合情理。

**一处反直觉的结论，将来改 `SearchIndex.ToPlainText` 前先看这里**：标题那一行<strong>本身就在纯文本里</strong>（`GetPlainText` 拿的是整篇正文），所以「命中标题」必然同时是一次正文命中，摘要总是摘得出来、不会退回日期。想让它退回日期，得先让纯文本不含标题行——而那会连带影响 §12.2 的"出现次数加分"（标题命中的那一次就没了）。

**颜色子菜单的七项写成七个静态节点**。颜色是编译期就定下来的（`NoteColor` 枚举，没有第二个来源会往这个菜单里加颜色），所以每一项写死一个 `CommandParameter="{x:Static models:NoteColor.Xxx}"` 就够；用 `ItemsSource` 去绑定枚举反而要为"当前选中是哪一项"再绕一圈。勾选态由一个只读转换器给出（`NoteColorMatchesConverter`：拿当前颜色与 `ConverterParameter` 那个字符串比），**且必须 `Mode=OneWay`**——`MenuItem.IsChecked` 的默认绑定模式是 `TwoWay`，而它在默认模板上真的会被用户点击改掉，反向那条路会写到只读的 `NoteListItem.Color` 上。那条路本来就不该存在：改颜色是"点了哪一项"这个信息，走 `CommandParameter` 传到 ViewModel，而不是靠 WPF 替我们把勾拨过去；所以反向干脆不实现，写错的那天立刻炸掉。转换器里枚举名拼错**不抛异常**，只是那一项不亮——XAML 里那一行就在眼前，看得见。

**色块放进 `Header` 而不是 `Icon`**。`MenuItem` 的默认模板里勾与 `Icon` 抢同一列，`IsCheckable="True"` 时两者会叠在一起。`Header` 放一个 `StackPanel`（`Ellipse` + `TextBlock`）从构造上避开这个冲突——`MenuItem.Header` 放对象内容是合法且常用的做法。

**子菜单那七个色块引的是 `{StaticResource Note{颜色}AccentBrush}`，所以 `ManagerWindow.xaml` 自己又合并了一次 `Resources/Colors.xaml`**（`App.xaml` 里已经有一份，两处指的是同一个文件，内容不会分叉）。理由不是洁癖：`StaticResource` 在**解析期**求值，而那发生在 `InitializeComponent()` 里——此时 `Application.Current` 可能还不存在（无头测试就是这种情况），窗口自己没合并的话整扇窗都建不起来。改用 `DynamicResource` 能绕开，但那样键名写错只会表现为"色块没颜色"，而 `ManagerWindowTests` 恰恰是拿来抓这类笔误的（它验的就是"XAML 能不能加载"）。补一句：这次是**测试先红了才发现的**（`XamlParseException`：无法找到名为 `NoteYellowAccentBrush` 的资源），不是预防性设计。

**`SetColorAsync` 的签名只有颜色、便签取 `SelectedNote`**。子菜单七项各有各的 `CommandParameter`，而便签是另一个必需参数——命令的两个参数写法要求 XAML 能造出一个元组，而 XAML 造不出来。之所以能安全地退回读选中项：菜单只可能在**某一行上**弹出来，而 `OnListContextMenuOpening` 在弹菜单之前一定先把那行选上了。

**点了当前那一支、或者打开标签对话框却没改，都直接返回。** 白白往下走一趟的话 `ApplyColorEdit` / `ApplyTagsEdit` 会刷新 `UpdatedAt`，于是列表按修改时间重排——用户只是点了一下"确认还是这个颜色"，却看到这一行跳到别处去了。

**标签对话框是自拟的**（文档没有给样子）：`IDialogService.PromptAsync(title, message, initialValue, validate)` + `Views/PromptDialog.xaml`，界面是"一行说明 + 一个输入框 + 取消/确定"。几个要点：

| 决定 | 理由 |
|---|---|
| 输入框里是"逗号分隔的一大串"，不是一列可增删的 chip | 标签数量少，用户改起来是"换掉整批"而不是"逐个调"，一次打完比来回点便宜；chip 那样还得多一个自绘控件与一套增删逻辑 |
| 校验回调由调用方传进去 | 服务层不该认识某一个具体业务规则（这里是 §5.8 的 64 字上限） |
| 校验不过时对话框**不关闭**，错误写在输入框下面 | 关掉再弹一个错误框的话，用户刚打的那一串就没了，得从头再打一遍 |
| 返回类型可空 | "取消"与"输入了空串"是两件事——留空对标签编辑是有意义的（**清空全部标签**）。所以调用方判的是 `input is null`，不是 `input.Length == 0` |
| 校验与写入必须是**同一次拆分** | 两处各拆一遍的话，将来给 `TagRules.Separators` 加一个分隔符，就会出现"校验说有问题的那个标签，写入时其实已经被拆没了" |

**「关掉就落盘」这一步不走 `AutoSaveService`**（`ManagerViewModel.PersistEditAsync`）：去抖是为"连续按键"准备的（§11.1），而这里是一次点完就结束的动作——用户改完颜色随即关掉程序，那几百毫秒就成了纯粹的丢数据窗口。`SaveNoteAsync` 内部还有一道"内容哈希没变就不写盘"的自检（§5.9），所以也不会白白多写文件。失败时 `catch (IOException or UnauthorizedAccessException)` → 弹一句提示，**但不回滚内存**（与 §11.5 一致）：用户改的东西还在，下一次改动或退出时的整批保存会再写一遍；回滚更糟——用户看着颜色自己弹回去，却不知道是为什么。

**改颜色不发任何消息给便签窗口**，这是本轮刻意的留白。`NoteWindow.xaml` 既不绑 `Color` 也不绑 `Tags`（`NoteViewModel.Color` 眼下是一份没有消费者的镜像），发出去就是一条没有接收方的管线。等毛玻璃那一轮把颜色落到窗口背景与标题条时再接上——那时它才有接收方。

## 15.9 托盘图标与菜单

用 `H.NotifyIcon.Wpf`。

```text
双击图标        → 显示全部便签
右键菜单
  ├─ 新建便签            (Ctrl+N)
  ├─ 速记                (Ctrl+Shift+N)
  ├─ 显示全部便签        (Ctrl+Alt+N)
  ├─ 收起全部便签
  ├─ ────────────
  ├─ 便签列表...          (Ctrl+L)
  ├─ 回收站（N）...
  ├─ 重新加载全部便签
  ├─ ────────────
  ├─ 设置...
  ├─ 打开笔记文件夹
  ├─ 关于
  └─ 退出
```

**"回收站（N）"** 里的 N 是当前回收站里的条目数，让用户知道里面有没有东西。点击后打开 `SettingsWindow` 并切到"回收站"页（§7.4、§18.1 的 ViewModel 表）——**回收站没有独立窗口**。

**"重新加载全部便签"** 是网络盘的兜底（§10.5）。

**退出前必须 flush**（§17.4）——如果还有未保存的便签，退出流程会被拦下来。

**实现说明（本轮已落地）**：拆成 `TrayViewModel`（菜单点了做什么，可测）+ `TrayService`（`TaskbarIcon` 与图标管线，不可测），分界线是"要不要真实消息循环"。

实际条目与上面那张图差两条：

| 差异 | 理由 |
|---|---|
| **"速记"不出现** | 速记浮窗（§15.7）还没做。摆一个点不动的菜单项比不摆更糟——与 §15.8 里那三档视图切换按钮同一个理由 |
| **Ctrl+N / Ctrl+Shift+N / Ctrl+Alt+N / Ctrl+L 这些加速键不标** | 全局热键整体推迟（§17.6）。菜单上标一个按不出效果的键名是同一类谎话 |

其余四处实现决定：

- **菜单的位置自己摆，不信 shell 给的锚点**：`TaskbarIcon` 用 `PlacementMode.AbsolutePoint` 摆菜单，坐标取自 shell 塞进托盘消息 `wParam` 里的那个「锚点」（`NOTIFYICON_VERSION_4` 的规矩），而**那个锚点不等于光标位置**——本机（Windows 11 26200、单屏 3840×1600、125%）实测菜单会跑到图标右边一段距离。改成在 `TrayContextMenuOpen` 里把 `Placement` 换成 `MousePoint` 并**清零两个偏移量**（`MousePoint` 会把偏移量*加*在光标位置上），右键点图标时光标就在图标上，菜单于是贴着图标出现，屏幕缩放也由 WPF 自己换算。**库的算术没问题**：喂进去的点 ÷ 缩放、WPF 再 × 缩放，精确落回原处（实测 (3600,1400) → (3600,1400)），错的只是喂进去的那个锚点。**「为什么以前是对的」查不出来**：`TrayService` 建出来之后没再动过，库一直锁在 2.4.1，库的缩放因子在启动的哪个时机取都是 1.25（三种时机各量过一遍），剩下能变的只有 shell 那边——最可能是图标在通知区域里的落点（贴任务栏 vs 收进溢出菜单）或 Windows 更新，两者都不在我们手里。换掉 `MousePoint` 之后这个变量就不再影响结果。
- **图标是画出来的，不是打包的 `.ico`**

- **图标是画出来的，不是打包的 `.ico`**：`GeneratedIconSource`，黄底（§15.3 的标题栏色）+ 黑体 `L`。托盘只有 16×16，一个带底色的字母在深色与浅色任务栏上都认得出，而多一个二进制资源就多一份维护成本。**字体必须显式指定**——它的默认值是图标字体（Segoe Fluent Icons），写字母会得到豆腐块。
- **菜单在代码里建，不走 XAML**：`TaskbarIcon` 不在任何窗口的可视树上，声明成 `App.xaml` 的资源就得靠资源查找去够到 DI 造出来的 ViewModel。命令是现成的对象，直接赋给 `MenuItem.Command` 就够了。
- **"回收站（N）"在菜单弹出那一刻刷新**（挂 `TrayContextMenuOpen`），也在启动跑完之后刷一次。不在每次删除/恢复之后逐个通知：那要让回收站那边反向依赖托盘，而用户看不到菜单的时候那个数字没人看。数不出来（索引损坏、目录被拔）时**归零并咽掉异常**——菜单弹不出来比数字不准严重得多。

关掉管理器窗口 → 收进托盘这条策略在 `TrayService` 里（订阅 `ManagerWindow.Closing`），详见 §17.3。

§8.2 的三个托盘设置（`singleClickTrayAction` / `minimizeToTrayOnClose` / `showTrayIcon`）已接上，由 `StartupSequence.Apply` 灌入。其中 `showTrayIcon` 只在 `Start()` 之前改才有效——图标建起来之后再关掉它，用户就没有任何入口了；设置窗口本轮也没暴露这一项。

### 设置窗口（§15.9 的补充，本轮已落地）

`SettingsWindow` + `SettingsViewModel`，"回收站"是它的两个页签之一。托盘菜单「设置...」与管理器工具条上的齿轮按钮是它的两个入口。

**本轮只暴露"改了立刻生效"的设置项**，够用来验完这条链：默认宽高、显示状态条、显示桌面后恢复、自动保存延迟、搜索去抖、回收站保留期、笔记文件夹（**只读显示 + 「打开」按钮**）。（原表里还有一项「内容缩放」，已随 §15.5 整条剔除。）

**明确推迟的设置项**，以及各自的卡点：

| 推迟项 | 卡点 |
|---|---|
| 主题（亮/深） | 属样式，统归**界面重构那一轮**（§15.3 的框）：深色套取决于毛玻璃之后纸面怎么定色，现在做要重来 |
| 开机自启动 | 属 §17.2 的启动编排，与单实例同一批 |
| 托盘相关（单击行为、关闭时最小化） | 属阶段 9 的 `TrayService` |
| 显示托盘图标 | 设置项本身已接上（§15.9 补充），但它**只在 `Start()` 之前改才有效**——图标建起来之后再关掉，用户就没有任何入口了。要真正可切换得先做"改完重建图标"，本轮不做 |
| 全局速记热键 | 速记浮窗本身推迟了，配了也没有消费方 |
| 日志级别 | 已接通（§20.5）：改 `settings.json` 里的 `logLevel` 真的生效。界面上仍不放它——这是排查问题时手改的旋钮，画在设置页里等于多一个随手把日志关掉的开关 |
| 最大窗口数 | 没有任何消费方 |
| 默认颜色 | §15.3 只有黄色一套色值落地，其余 6 色还是占位 |
| 附件文件夹名 | §6.1 的附件功能没做 |
| **更改笔记目录** | §8.6 是一整条 7 步流程（扫描迁移、冲突、回滚），不是"赋个值就生效" |

**保存时不能 `new` 一份新的 `AppSettings`。** 页面上没画出来的字段（主题、热键、日志级别、开机自启、笔记目录）必须从载入时拿到的那一份上原样带过去。否则用户改一次默认宽度，就会把主题和热键一起静默重置——而且没有任何提示。

**保存前先自己钳一遍，再把钳制结果写回界面。** 用户填了 1000 就该当场看到它变成 800，而不是下次启动时被悄悄改掉。边界值复用 `JsonSettingsStore` 的常量（§8.4），不另写一份。

**`SettingsWindow` 不是单例，也不能是单例**：WPF 的 `Window` 一旦 `Close()` 过就不能再 `Show()`，第二次会抛 `InvalidOperationException`。于是注册成 `AddTransient`，由单例 `SettingsWindowLauncher` 记住"当前开着的那个"，同时只开一个。它的 `Show()` 走的是"没有就新建、开着就唤到前面、关掉之后忘掉它"这三段——**三段都要真的弹出一个窗口才验得到**，所以它在手工清单里（与 `WindowManager`、`TrayService` 同一档）。

**抽了 `ISettingsApplier` 让"保存完立刻生效"这一步可测。** 生产实现是 `StartupSequence`（§17.1），启动时走一次、保存时再走一次，**是同一段代码**——两处各写一遍的话，"启动时对、保存后不对"这类偏差要等到用户重启才发现。直接依赖具体的 `StartupSequence` 则会让 `SettingsViewModel` 在无头测试里根本构造不出来（它的十二个依赖里包括窗口）。接口只有一个方法、一个实现，不改变"谁是设置与存储的串联者"这个答案，只是让这条链能被钉住。

## 15.10 资源字典组织

```text
Resources/
  Colors.xaml          便签颜色（亮色一套；深色套留到界面重构那一轮，见 §15.3）
  Typography.xaml      字号、字体族、行高
  Metrics.xaml         边距、尺寸、动画时长
  Controls/
    NoteWindow.xaml
    SearchPanel.xaml
    QuickCapture.xaml
  Icons.xaml           图标（用 Path 几何，不打包图片）
```

**图标用 `Path` 几何数据，不打包 PNG/SVG**。理由：矢量图标在任意 DPI 下都清晰，且体积几乎为零；打包图片则需要为每个 DPI 准备多套位图。

**在 `App.xaml` 中合并**，注意顺序（后面的可以覆盖前面的）：

```xml
<ResourceDictionary.MergedDictionaries>
  <ResourceDictionary Source="Resources/Metrics.xaml" />
  <ResourceDictionary Source="Resources/Typography.xaml" />
  <ResourceDictionary Source="Resources/Colors.xaml" />
  <ResourceDictionary Source="Resources/Icons.xaml" />
</ResourceDictionary.MergedDictionaries>
```

## 15.11 便签组

**v2 明确：不在一期路线图内，列为后续可做。**

PinSlip 的"便签组"允许把多张便签归成一组，整组一起移动、统一宽度、级联折叠。功能上确实有价值，但它引入的东西远超表面：

- 需要新的持久化概念（组的定义、组与便签的从属关系）——放 layout.json 还是 Front Matter？两边都有明显缺点
- 需要"组手柄"这个新的窗口类型，以及它与成员窗口的联动
- 组移动时要处理每个成员的位置更新、跨屏、DPI 变化
- 便签被单独拖动时要不要脱离组？组的边界如何定义？
- 删除便签、便签进回收站时组的成员关系如何处理

这些都不是小问题，而且**不解决它们也不影响"能用的便签程序"**。一期先把单张便签的体验做扎实（速记、缩放、勾选、搜索、回收站），组留到二期。

**在路线图（§22）中显式标注"暂不做"，避免实现期被临时加进来。**

---

# 16. 编辑器

## 16.1 编辑与预览

便签的核心是一个纯文本编辑器。**v2 采用"编辑为主、预览为辅"**：

| 模式 | 触发 | 说明 |
|---|---|---|
| 编辑模式（默认） | — | 显示原始 Markdown 文本，所见即所得程度**仅为任务复选框**（§15.6） |
| 预览模式 | `Ctrl+E` 或标题条按钮 | 用 Markdig 渲染成只读的流文档 |

**为什么不做"实时所见即所得"**：真正的所见即所得编辑器（隐藏 Markdown 标记、就地渲染标题与列表）需要自己实现一套富文本排版引擎，工作量与风险都极高，而便签的场景其实**不需要**——用户粘一段文字、记几行待办，看到 `#` 和 `-` 反而是可预期的、不会出错的。

**编辑模式下唯一的渲染增强是任务复选框**，因为那是一个"必须点击才有意义"的元素，纯文本形式无法操作。

切到预览模式时**不改变内容，只改变呈现**。切回编辑模式时光标位置尽量恢复到切换前的行号。

## 16.2 编辑器控件

用 `TextBox`（`AcceptsReturn=True`、`AcceptsTab=True`、`TextWrapping=Wrap`、`VerticalScrollBarVisibility=Auto`）。

**用 `TextBox` 而不是 `RichTextBox`**：`RichTextBox` 自带完整的富文本模型（FlowDocument），对"纯文本编辑"是巨大的额外复杂度——撤销栈、剪贴板、文本范围 API 都更难用，而且默认会引入格式粘贴的问题。`TextBox` 就是纯文本，正好。

**`AcceptsTab=True` 的取舍**：便签里 Tab 键用于缩进列表是有意义的。但这也意味着用户无法用 Tab 在控件间导航。因为便签窗口里除编辑器外只有一个标题条的按钮（可以用鼠标点，也有快捷键），影响很小。**保留 `AcceptsTab=True`**。

### 需要自己实现的编辑助手

WPF 的 `TextBox` 不提供这些：

```text
1. 按 Enter 时自动延续列表标记
   "  - 项目" 后按 Enter → 自动插入 "  - "
   "  1. 项目" 后按 Enter → 自动插入 "  2. "（序号递增）
   空列表项上按 Enter → 删除该标记（结束列表）

2. 按 Tab 时缩进/反缩进当前行或多行选中
   单行：在行首插入或移除两个空格
   多行：整块缩进或反缩进

3. Ctrl+Shift+V：纯文本粘贴（去格式）
```

这三条都在 `PreviewKeyDown` 里实现，**必须显式设置 `e.Handled = true`** 才能阻止 `TextBox` 的默认处理。

## 16.3 快捷键

| 快捷键 | 行为 |
|---|---|
| `Ctrl+S` | 立即保存（不等自动保存去抖） |
| `Ctrl+E` | 切换编辑/预览 |
| `Ctrl+N` | 新建便签 |
| `Ctrl+Shift+N` | 速记浮窗（全局热键，§15.7） |
| `Ctrl+L` | 便签列表面板 |
| `Ctrl+Alt+N` | 显示全部便签（全局热键） |
| `Ctrl+B` / `Ctrl+I` | 包裹选中文本为 `**` / `*` |
| `Ctrl+K` | 插入链接 |
| `Ctrl+Shift+V` | 纯文本粘贴 |
| `Esc` | 编辑模式下关闭预览；浮窗里是取消 |
| `F2` | 重命名（进入标题编辑） |

**全局热键 vs 窗口内快捷键**：`Ctrl+Shift+N` 和 `Ctrl+Alt+N` 是**注册到系统的全局热键**（`RegisterHotKey`），在任何应用里按都生效。其他是窗口内快捷键（`InputBindings`）。

**全局热键的冲突处理**：`RegisterHotKey` 失败（返回 false）说明热键被别的程序占了。**不能让程序启动失败**——记一条 Warning 日志，在设置页对应项上标注"该快捷键已被其他程序占用，请更换"，并在第一次冲突时用托盘气泡提示一次。

## 16.4 粘贴与拖放

### 粘贴图片

```text
剪贴板里有位图数据
  ↓
1. 把位图保存为 PNG 到 attachments/（§6 的命名规则）
2. 判断当前便签的文件位置，生成正确的相对路径（§6.2）
3. 在光标位置插入 ![图片](相对路径)
4. 保存（不等去抖）
```

**关键：相对路径必须相对于当前便签文件计算**。v1 §23 写死 `../attachments/...`，对位于**笔记根目录**的便签是错的（根目录的便签应该用 `attachments/...`，多一层 `../`）。路径生成必须走 §6.2 的统一函数，不能有硬编码。

### 粘贴文件

从资源管理器复制文件后粘贴：

```text
单个文件且是图片 → 按图片处理（复制到 attachments/）
其他情况        → 插入一个指向该文件的 Markdown 链接
```

**不做"自动把任意文件复制进 attachments"**——用户粘一个 500MB 的安装包进来，把笔记目录撑爆，不是好行为。只有图片自动收进 `attachments/`。

### 拖放

`AllowDrop=True`，`DragOver` 时给出视觉反馈（编辑器边框高亮），`Drop` 时按上面同样的规则处理。

**拖入的是文件夹时**：只插入文件夹路径的链接，不递归复制。

## 16.5 撤销与重做

**直接使用 `TextBox` 自带的多级撤销栈**，不自己实现。

需要注意的一点：**自动保存不应该清空撤销栈**。`TextBox` 的撤销栈在 `Text` 被程序赋值时会被重置。因此：

```csharp
// 错误：外部更新时直接赋值，会清空撤销栈、并且光标跳到开头
_editor.Text = newContent;

// 正确：只在内容确实不同的时候，且用带 caret 保持的方式更新
if (_editor.Text != newContent)
{
    var caret = _editor.CaretIndex;
    var scroll = _editor.VerticalOffset;
    _editor.Text = newContent;
    _editor.CaretIndex = Math.Min(caret, newContent.Length);
    _editor.ScrollToVerticalOffset(scroll);
}
```

**更重要的一条**：**程序自己触发的更新不该进入撤销栈**。要做到这点，在程序赋值前 `_editor.IsUndoEnabled = false`，赋值后再置回 `true`（赋值后清空一次撤销栈是符合预期的——因为这是"重新加载"语义，不是"编辑"语义）。

**只有在以下场景才重设 `Text`**（这些都属于"重新加载"，清空撤销栈是合理的）：

- 外部文件修改后用户选择"重新加载"（§11.4）
- 用户切换了便签文件
- 回收站恢复

**在正常编辑流程中永远不要重设 `Text`**——用户打字 → ViewModel.Content 变 → 如果这时再去设 `_editor.Text` 就会打断输入。绑定的正确方向是 View 到 ViewModel（`UpdateSourceTrigger=PropertyChanged`），ViewModel 到 View 的变化由绑定系统处理，不需要手动赋值。

## 16.6 输入法

见 §11.3。要点重复一次：**组合期不自动保存**，组合结束后立即安排一次保存。

## 16.7 预览模式的渲染

```csharp
private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
    .UseTaskLists()
    .UseAutoLinks()
    .UseEmphasisExtras()
    .DisableHtml()          // ★ 关键：禁止原始 HTML 直通
    .Build();
```

**`DisableHtml()` 是安全必需的**（§19.2）。便签内容可能来自任意来源（网页复制、别人发的文件），如果 Markdown 里的 `<script>` 或 `<img src="...">` 被原样交给渲染器，会带来两类问题：

- WPF 的 HTML 渲染路径（如果经过 `WebBrowser` 之类）会执行脚本
- `<img>` 可以指向任意 URL，造成浏览便签时对外发起请求（隐私泄漏：可以探测用户是否看了某个便签）

用 `DisableHtml()` 后，HTML 标签被当作纯文本转义显示，这正是便签场景想要的行为。

**渲染目标的造型**：Markdig 的 `WpfRenderer` 不在主库里（在 `Markdig.Wpf` 这个独立包中），且其对样式定制的支持有限。v2 的做法是**自己写一个 `Markdown → FlowDocument` 的转换器**，只支持便签需要的子集：

```text
支持：标题 H1–H3、段落、粗体/斜体、行内代码、代码块、有序/无序列表、
      任务列表、引用块、水平线、链接、图片、表格
不支持：脚注、定义列表、数学公式、图表、原始 HTML
```

理由：需要控制的样式细节（行高、间距、深色模式配色）在现成渲染器里难以覆盖，而便签需要的 Markdown 子集很小，自己写反而更可控。

---

# 17. 应用生命周期

## 17.1 启动序列

顺序很重要，每一步都依赖前一步的结果：

```text
1. App 构造
   - 加载 app.manifest（PerMonitorV2 DPI）
   - ShutdownMode = OnExplicitShutdown

2. 单实例检查（§17.2）
   - 已有实例 → 通知它并退出，流程结束

3. 初始化基础设施
   - 日志系统（§20.5）
   - 路径解析 IAppPaths（§8.1）
   - 首次运行时创建目录结构

4. 读取配置
   - settings.json（不存在或损坏 → 用默认值，并备份损坏的文件）
   - 若笔记目录未设置 → 进入首次运行向导

5. 清理遗留的临时文件
   - 扫描笔记目录，删除所有 *.lumitmp（§11.2）

6. 全量扫描笔记目录
   - 解析所有 .md 的 Front Matter 与内容
   - 构建 NoteStore 与 SearchIndex
   - 收集解析失败的便签（不中断启动，记为 ParseIssue）

7. 读取 layout.json
   - 与 NoteStore 做对账：
     - layout 里有、笔记里没有 → 丢弃这条 layout
     - 笔记里有、layout 里没有 → 生成一条默认 layout（IsOpen=false）

8. 恢复托盘图标
9. 恢复全局热键（§17.6）
10. 启动 FileSystemWatcher（§10）
11. 打开所有 IsOpen=true 的便签窗口（§13）
12. 若启用了"开机启动"，确认注册项与当前状态一致
```

**第 6 步的要点**：

- **扫描必须在后台线程**，UI 线程不能阻塞。启动时先显示托盘图标（让用户知道程序起来了），扫描完成后逐步打开便签窗口。
- **单张便签解析失败不中断整个扫描**。记录到该便签的 `ParseIssues`（§9），在便签窗口里显示一条提示条，内容按"原始文本"呈现（不丢弃用户数据）。
- **扫描进度可选显示**：当笔记数超过 500 时，在托盘图标上显示一个短暂的提示（"正在载入 1200 张便签…"）。

**第 11 步的要点**：打开窗口要**分批**。一次打开 20 个窗口会让界面卡顿几百毫秒。分批策略：第一帧打开前 3 个，之后每帧（`DispatcherPriority.Background`）再开 2 个。

**实现说明（本轮已落地）**：跑的顺序与上面那张表有四处出入，都是核对真实约束后改的。

1. **第 2 步（单实例）挪到"建完容器之后、启动序列之前"**，不由 `StartupSequence` 做。第二个实例要做的只有「通知第一个 + 退出」，让它把设置读一遍、把笔记目录扫一遍再扔掉，纯属拿用户的磁盘开玩笑。判完立刻 `Shutdown()`。
2. **第 4 步的"首次运行向导"是一句话的文件夹选择框**（`IFolderPicker.PickFolder`），不是向导。用户取消时**不退程序**，退回 `我的文档\LumiMemo` 并建出来、记一条 Information——直接退出会留下一个「双击了但什么都没发生」的观感，而用户其实只是点错了按钮。
3. **第 8 步（恢复托盘图标）排在"打开管理器"之前**。关闭策略是「有图标才收得进去」（§17.3），反过来的话，用户在启动那一瞬间关掉管理器就真的退出了。
4. **第 5 步不在启动序列里**：`*.lumitmp` 的清理在 `MarkdownNoteRepository` 扫描时顺手做掉，不必单独走一趟目录。第 9 步（全局热键）、第 10 步（`FileSystemWatcher`）、第 12 步（开机自启动）整体推迟，见 §17.6 与 §10 的偏离说明。
5. **第 4 步的笔记目录是「唯一来源」，而且两条路都留下日志**（`StartupSequence.EnsureNotesFolderAsync`）。`AppPaths.SetNotesFolder` 全仓库只在这里被调用，`MarkdownNoteRepository` 每次扫描都现读 `IAppPaths.NotesFolder`，别处没有任何地方能改它。这不是为了好看：用户曾遇到「`settings.json` 里记的是一个目录、程序却在用另一个」的现场，而当时 `logs\` 是空的（provider 没挂，见 §20.5），只能靠文件时间戳反推。现在两条路各写一行——直接采用设置里那个目录时写一行，重新选择并**写回磁盘之后**再写一行（顺序不能反，先写日志后写文件就成了假话）。

**第 11 步已落地**（`ManagerViewModel.RestoreOpenNotesAsync`，由 `StartupSequence.RunAsync` 在托盘之后调用）。原先「第 11 步本轮不做」的裁决是「首启不自动弹便签」，现在改成**无感启动**（用户裁决）：启动后桌面上只该多出上次开着的那些便签，管理器窗口自己不该冒出来。

1. **判据只有一个：`OpenAll()`。** 「上次开着」完全由 `layout.json` 里的 `isOpen` 决定，与 §17.6 那条「显示全部便签」读的是同一份结果。因此**不需要判断「这是不是第一次启动」**——首启没有布局档，`OpenAll()` 自然返回空，恢复这一步就什么都不做。两件事共用一条判据，就不会出现「首启弹了」或「重开不恢复」这种一半对一半错的状态。
2. **分批的尺寸**：第一批 3 个（已开出来的张数 < 3 时就是全部），之后每批 2 个。批次之间调 `IDispatcher.YieldAsync()`，**最后一批开完不再让帧**（那时已经无事可做，白让一帧只是让调用方多等一轮消息泵）。于是「开 7 张」对应的让帧点是 3 与 5——`ManagerViewModelTests` 里那条用例钉的就是这两个数字，而不是最后开出来几张（一口气开完的实现一样是 7 张）。
3. **`DispatcherPriority.Background` 是承重的，不是装饰。** 新增的 `IDispatcher.YieldAsync` 与 `InvokeAsync` 的唯一区别就是排在哪一档：`Background` 排在 `Render` **之后**，所以重绘先跑。若图省事复用 `InvokeAsync`（`Normal`，比 `Render` **高**），下一批会抢在重绘前面执行，界面照样卡——松了等于没松。
4. **一张都没恢复出来时，管理器才显示。** 这是首启的样子（没有 `layout.json`，没有便签可开），此时若也保持安静，用户双击图标后桌面上什么都不出现，与「程序坏了」没有区别——与 §17.6 那条零窗口兜底是同一条道理。非首启时管理器的入口有两个：托盘菜单的「便签列表…」与 `ShowAll()` 的零窗口兜底，两者都会 `Show()` 它（`ManagerWindowPresenter.BringToFront` 里那句 `Show()` 就是为此存在的）。
5. **恢复用的是与 `ShowAll` 同一个开窗函数**，所以恢复出来的便签窗口**不抢焦点**（`WindowManager.ShowNote` 走 `window.Show()`，不调 `Activate()`）。无感启动要的就是这个：便签出现在桌面上，但用户手上正在做的事不被打断。若某天有人给 `ShowNote` 补一句 `Activate()`，首启那几张便签会把前台抢走。

**新增一步"打开管理器窗口"**（§15.8 补充里的偏离 4）**自无感启动起改为条件执行**：原方案让主界面自己出来，理由是「首启时用户没有任何地方可点」；现在只在**一张便签都没恢复出来**时才这样做，非首启则由桌面上的便签本身充当那个「能点的东西」。托盘菜单仍然是主界面的常规入口。原方案里那句「关掉最后一张便签就 `Shutdown()`」的临时 hack 早已彻底删掉。

**上一轮的教训记在这里**：「重开后桌面已打开的便签不自动显示」这个缺陷之所以拖了几轮才定位，是因为启动那段日志是**完全空白**的——没有任何一行能回答「该恢复几张、恢复了几张」。`StartupSequence` 现在恢复完必写一行 `恢复上次打开的便签：{RestoredCount} 张。`，它的作用就是让下一次同类问题不必再靠文件时间戳反推。

**单实例信号到达时走的是「显示全部便签」那条路**（`ManagerViewModel.ShowAll`），不是另写的"把窗口带到前台"。§17.2 已说明理由，实现上它还多一步：信号在监听线程上触发，必须先经 `IDispatcher.InvokeAsync` 封送到 UI 线程（§3.4 规则 T5）。

## 17.2 单实例

**必须做单实例**。多个实例同时跑会有两个 `FileSystemWatcher`、两份 layout.json 的写入者、两个托盘图标——这是数据损坏的直接来源。

```csharp
// 用命名 Mutex，名字带上用户 SID（多用户登录同一台机器时互不干扰）
var mutexName = $@"Local\LumiMemo.SingleInstance.{UserSid}";
_mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);

if (!createdNew)
{
    // 已有实例：通知它把自己带到前台，然后退出
    SingleInstanceChannel.SignalExistingInstance();
    Shutdown();
    return;
}
```

**"通知已有实例"的实现**：用一个命名管道（`NamedPipeServerStream` / `NamedPipeClientStream`），已有实例在后台线程等待连接，收到消息就 `Dispatcher.InvokeAsync` 切到 UI 线程，然后走**与 `Ctrl+Alt+N` 完全相同**的"显示全部便签"路径（§17.6）。不要另写一套"把窗口带到前台"的逻辑，否则两条路径的行为迟早会不一致。

**不要用 `PostMessage(HWND_BROADCAST)` 广播自定义消息**——这会打扰系统里所有窗口，且消息可能被 UIPI（UAC 完整性级别隔离）拦掉。

**必须处理 `AbandonedMutexException`**：上一个实例崩溃退出时 mutex 会被放弃，`WaitOne` 会抛这个异常。此时应当**继续启动**（视作没有其他实例），而不是崩溃。

**实现说明（本轮已落地）**：`SingleInstanceGuard` 与上面的示例有两处实质差别。

1. **认的是"命名对象在不在"，不是"锁归谁"**，因此**一行 `AbandonedMutexException` 的处理都不需要**。上面那段示例里那个坑其实是个伪问题：命名对象只要还有句柄开着就存在，进程一死（正常退出或崩溃）句柄全部关闭、对象随之销毁，下一个实例拿到的 `createdNew` 就是 `true`。**崩溃后的自愈是天然的。** 反过来，照示例那样走 `WaitOne` 会引入一个真实的坑——**互斥体的归属属于线程，不是对象**，用 `WaitOne` 拿到的锁必须由同一个线程 `ReleaseMutex`，而在异步代码里"哪条线程执行到 `Dispose`"根本不由我们说了算。于是这里用 `initiallyOwned: false`，只把句柄握住。
2. **命名管道的协议是"握一次手"，不是"连上就算数"**。最初的实现只等 `WaitForConnectionAsync`，连上就发事件，结果测试里大概每三次红一次：客户端连上之后**立刻关掉句柄**时，服务端挂着的等待会以 `IOException: 管道正在被关闭` 收场——那一次连接本该算数，却变成了一次异常。改成**客户端写一个字节 → 服务端读到才发事件 → 服务端回写同一个字节 → 客户端读到回音才返回 `true`** 之后，客户端会一直握着句柄直到服务端确认，那个窗口就不存在了。信号方还带重试：监听方是"收一个连接、扔掉那个管道实例、再建一个新的"，换实例的那几毫秒里连接会撞上 `ERROR_PIPE_BUSY`——那正是"用户连点两下程序图标"最容易踩到的时机。

**"通知已有实例"这一步有预算**：第二个实例等回音最多等 2 秒，超时就自己退出。这个延迟直接加在第二个实例的启动上，必须短——用户点两下图标，第二下得几乎立刻消失。通知失败**不提示用户**：第二个实例的窗口一闪而过，弹一个"已经有一个在跑了"只会让人以为自己按错了。

## 17.3 关闭窗口 vs 退出程序

**这是用户最容易被搞混的地方，必须在 UI 上明确区分。**

| 操作 | 结果 |
|---|---|
| 点便签的 ✕ | 关闭这张便签的窗口。**便签仍然存在**，`IsOpen=false`，可被搜索到、可从列表重新打开 |
| 托盘菜单 → 退出 | 退出程序。**所有打开的便签会在下次启动时自动恢复**（因为 `IsOpen` 还是 true） |
| 设置里的"退出时关闭全部便签" | 若开启，退出时把所有便签的 `IsOpen` 置 false |

**默认的"退出"语义 = "只是关掉程序，下次打开还是这样"**，不管便签有多少个。这是唯一符合直觉的默认值。

**第一次点便签 ✕ 时**给一个一次性的提示（托盘气泡）：

> 便签已收起，内容仍在。可在托盘菜单的「便签列表」里重新打开。

只在第一次提示，之后不再打扰。

**没有"主窗口"来承接关闭**：因为不需要主窗口（§13.10），"关闭最后一个便签窗口"不能导致程序退出。`ShutdownMode = OnExplicitShutdown` 正是为此。

**实现说明（本轮已落地）**：

- **关掉管理器窗口 → 收进托盘**（`minimizeToTrayOnClose` 默认为真），实现在 `TrayService` 里，订阅 `ManagerWindow.Closing`。放那里的理由是"收进托盘"这件事的两端就是托盘图标与管理器窗口——**没有图标就没有地方收，收进去就再也叫不出来**，所以那两个属性必须挨在一起。图标没建起来时（`showTrayIcon = false`）关闭照常发生，也就是真退出。
- **必须是 `Hide()` 而不是 `Close()`**：WPF 的窗口一旦 `Close()` 过就不能再 `Show()`，"托盘菜单 → 便签列表"会直接抛 `InvalidOperationException`。这也让 `IManagerWindowPresenter.BringToFront` 里那句 `Show()` 是安全的重入。
- **启动时管理器窗口默认不出现**（无感启动，详见 §17.1）。这一轮之后"管理器是程序的落脚点"这句话的含义变了：它是**退出策略**上的落脚点（便签窗口全关光不退出、托盘图标一直在），不再是**启动**上必须亮出来的窗口。于是「托盘菜单 → 退出」那一行表格里"所有打开的便签会在下次启动时自动恢复"第一次真的兑现了——启动只把便签开回来，管理器留给托盘菜单的「便签列表…」。唯一的例外是**一张便签都没恢复出来**（首启，或上次退出时全部关着），那时管理器自己出来，否则桌面上将没有任何能点的东西。上面那条 `Hide()` 的必要性因此更强：`BringToFront` 现在要负责把一个从未 `Show()` 过的窗口显示出来。
- **它依赖"`Application.Shutdown()` 会忽略 `Closing` 里的 `e.Cancel`"** 这行为（WPF 内部走的是 `Window.InternalClose(shutdown: true, ignoreCancel: true)`）。不成立的话，托盘菜单的「退出」会被这条策略挡下来——**这一条进 §22 的手工清单**，因为它只在真实进程里才验得到。
- **"第一次点便签 ✕ 给一次性气泡"本轮推迟**：它需要一个 `AppSettings` 里不存在的"已提示过"字段，属于设置层的活。没有它也不会出错，只是用户少了那句解释。
- **退出时关掉的窗口不算"用户关了这张便签"**：`Application.Shutdown()` 会把每个便签窗口都走一遍 `Closed` 事件，而 `WindowManager` 在那个事件里做的事是 `MarkNoteClosed`（`IsOpen = false`）。因此 `WpfApplicationLifetime` 在调 `Shutdown()` **之前**先调一次 `IWindowManager.BeginShutdown()`，让窗口层知道接下来这批关闭不是用户操作。**这一步不能挪到 `App.OnExit` 里补**——关窗发生在 `OnExit` 之前（§17.4），那时候窗口已经全关完了，标志设上也没有窗口可拦。少了它，每一次正常退出都会把全部便签记成已关闭，上面表格里"下次启动自动恢复"那一行就成了空话（本轮冒烟时实测到了：退出后 `layout.json` 里三张便签的 `isOpen` 全被清成 `false`）。

## 17.4 退出序列

```text
用户触发退出
  ↓
1. 停止接受新的编辑（窗口仍然可见，让用户看到发生了什么）
2. 取消所有自动保存的待处理计时器
3. FlushAllAsync()：立即保存所有有未保存内容的便签
  ↓
4a. 全部成功
    → 从每个打开窗口的当前状态回写几何信息（§14.4）
    → 写 layout.json（同步写，不走去抖）
    → 注销全局热键
    → 释放 FileSystemWatcher、Mutex
    → Shutdown()
  ↓
4b. 有失败
    → 弹对话框（§11.5 的"有 N 张便签未能保存"）
    → 用户选「重试」→ 回到 3
    → 用户选「另存为」→ 让用户选一个位置导出未保存内容，成功后继续退出
    → 用户选「仍然退出」→ 继续退出，并写一条 Error 日志记录丢失了哪些便签
```

**关键约束**：

- **退出序列全程同步等待**，不接受"后台慢慢保存、程序先退"。进程结束时未落盘的写入会丢。
- **退出时写 layout.json 不走去抖**，直接写。因为已经要退出了，没有"下一次写"来合并。
- **注销全局热键**（`UnregisterHotKey`）虽然进程退出后系统会自动清理，但显式注销可以避免"程序没退干净时热键被占住"。

**"仍然退出"的对话框不接受回车作为默认按钮**。默认按钮是"重试"。用户需要明确地点击"仍然退出"才承担数据丢失。

**实现说明（本轮已落地）**：上面那张表里 **4b 那一支（有便签没保存上时拦下退出）还没做**——它就落在"3"这一步的失败路径上，需要那个三选一对话框，属于 §11.5 的活。本轮落地的是 1→3 与 4a，加两步释放：

```text
用户触发退出（托盘菜单 → 退出 / 系统注销）
  ↓
App.OnStartup 里没有再挂任何 Closed 处理器
  ↓
Application.Shutdown() → 关掉全部窗口 → 触发 App.OnExit
  ↓
OnExit（UI 线程，可以阻塞）：
  1. StartupSequence.ShutdownAndWait()
       - AutoSaveService.Dispose()       停掉待处理计时器
       - NoteService.SaveAllAsync()      flush 未落盘的便签
       - LayoutService.FlushNowAsync()   写 layout.json，绕过节流
  2. SingleInstanceGuard.Dispose()        放掉互斥体
  3. TrayService.Dispose()                收掉托盘图标
  4. ServiceProvider.Dispose()            兜底
```

三处值得记的：

- **整体扔到线程池上跑**（`Task.Run(...).GetAwaiter().GetResult()`）。这些 `async` 方法的续体默认要回 UI 线程，而调用方正**阻塞**着 UI 线程等它完成——不脱离 UI 线程就是必然的死锁。名字带 `...AndWait` 是因为它真的阻塞调用线程，这在 WPF 里通常是禁忌，唯一能这么写的地方就是 `OnExit`。
- **释放顺序有意义**：锁放得比 flush 晚。放早了，另一个实例就能在这一次还没写完 layout 时启动，两份 `layout.json` 于是重叠——那正是单实例要防的事。
- **托盘图标必须显式收掉**，不能只靠容器兜底。留着它，用户点了"退出"之后还会看到一个点得动、但点了没反应的图标，直到鼠标划过才消失。
- **`OnExit` 是唯一还留着 `Application` 的地方**：业务代码一律经 `IApplicationLifetime` 发起退出（托盘菜单、将来的热键），不直接调 `Shutdown()`——那样测试里能断言"点了退出"，而不必真的把测试进程关掉。而收尾本身只有一条路径，就是上面这一段。

## 17.5 未处理异常

三层兜底：

```csharp
// 1. UI 线程异常
DispatcherUnhandledException += (s, e) =>
{
    _logger.LogError(e.Exception, "UI 线程未处理异常");
    // 大多数 UI 异常（绑定错误、命令执行失败）不应该让程序死掉
    e.Handled = true;
    _errorReporter.ReportRecoverable(e.Exception);
};

// 2. 后台线程异常
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    _logger.LogError(e.ExceptionObject as Exception, "后台线程未处理异常，进程将终止");
    // 这一层无法阻止退出，只能尽量把现场记下来
    FlushLogs();
    TryEmergencySave();
};

// 3. Task 里被吞掉的异常
TaskScheduler.UnobservedTaskException += (s, e) =>
{
    _logger.LogError(e.Exception, "未观察的 Task 异常");
    e.SetObserved();     // 阻止进程终止
};
```

**`DispatcherUnhandledException` 设 `Handled = true` 要谨慎**。设了之后程序继续跑，但如果异常来自绑定或渲染这种会被反复触发的地方，会变成"每秒弹一次错误"。

**做法**：设 `Handled = true`，但加一个**同一异常的节流**——同一个异常类型在 10 秒内只报告一次，超过 5 次就不再弹 UI，只写日志。

**`TryEmergencySave`**：在第二层（进程即将终止）里，尝试把所有有未保存内容的便签写到 `%LOCALAPPDATA%\LumiMemo\recovery\`（§11.6 的机制）。这一层里做任何事都有风险（可能已经在崩溃中），所以：

- 只做最简单的文件写入，不复用 §11.2 的原子保存流程（那里面可能抛异常）
- 整体用 `try { } catch { }` 包住，绝不让它再抛
- 有时间上限（比如 2 秒），超时放弃

**实现说明（本轮已落地）**：三层都挂上了，落点是 `App.xaml.cs` 的 `AttachUnhandledExceptionHandlers()`，调用点紧跟在 `BuildServiceProvider` 之后、`ClaimSingleInstance` **之前**——比上面那张挂载表早一步。理由是它下面那几行（取服务、开监听）本身就可能抛，而它们是「打不开程序」这类问题里最该死得有记录的一段。第二个实例走的是「通知完就退」，给它挂一整套处理器没有意义，所以没有更晚挂。

三处与上面代码块的**实质出入**：

1. **第二层没有 `TryEmergencySave`**。§11.6 明写恢复文件机制「第一阶段不做」，而 `TryEmergencySave` 就是往 `%LOCALAPPDATA%\LumiMemo\recovery\` 写那一份。第二层因此只剩「记日志 + 把通道里还没落盘的行冲出去」。`recovery\` 目录仍按 §11.6 的原样留着，但**没有任何写入者**。
2. **`FlushLogs()` 是一个同步的 `bool FileLoggerProvider.Flush(TimeSpan)`，不是 `async Task`**。第二层跑在一条**正在死的线程**上：`await` 的续体在那条线程上未必还有机会被调度，而 `GetAwaiter().GetResult()` 在捕获了 UI `SynchronizationContext` 的地方会直接死锁。实现改成同步轮询一个计数器——`Log<TState>` 入队前先 `Interlocked.Increment`，消费循环每写完一批按条数减回去，`Flush` 等到它归零或者到上限。计数为 0 的含义是「已经不再堵在通道里」，**不是**「已经成功落盘」：`WriteBatch` 自己吞异常，冲不出去的话日志本身也没有别的办法。轮询间隔 20ms——消费循环至少每 `flushInterval` 醒一次，不会白等满一个上限。上限 2 秒。
3. **三个处理器里都只捕获已取好的局部变量**，绝不从容器里现取服务。`App.OnExit` 会 `_provider.Dispose()`，而 `AppDomain.CurrentDomain.UnhandledException` 完全可能在它之后才响；那一刻再去 `GetRequiredService` 就是拿一个已经释放的容器去要东西。同理，「提示框自己出问题」和「上一个框还开着」两种情形在 `ErrorReporter` 里各有一道闸（前者 catch 后只记一笔，后者用 `_reporting` 挡掉嵌套），它自己绝不能再抛——抛出去就是转着圈回到同一个处理器。

**节流的两条规则的确切含义**（`App/Services/ErrorReporter.cs`）：「同一个异常类型在 10 秒内只报告一次」按 `exception.GetType()` 记账，被压掉的那几次**不刷新**那 10 秒的起点——刷新的话，一个每 9 秒抛一次的坏绑定就永远报不出来，而它恰恰最该报。「超过 5 次就不再弹 UI」的**「一轮」边界文档没定**，实现取**静默 60 秒即翻篇**，而不是「整个会话一共 5 次」。后者更像那两句话的字面意思，但代价是几小时之后才出现的另一桩真事故会被早高峰期的计数永久盖掉，而弹窗的全部意义正是「有异常发生了」。无论报不报**都会写日志**：节流省掉的是打扰，不是证据。

**一处对 §19.5 的刻意破例**：三个处理器与 `ErrorReporter` 都**把异常对象本身**交给 `LogError`（§19.5 的规则是「只记 `ex.GetType().Name`」）。§20.5 已经写明「Provider 不替调用方过滤」，所以过滤责任在调用点，而这里是异常**已经逃到进程边界上**的时刻——堆栈是判断「到底哪一行炸了」唯一的线索，不记就等于什么都没留下；日志落在本机 `%LOCALAPPDATA%`，不外发。证据丢了不可逆，多写几行日志随时可以改回来。

## 17.6 全局热键

```csharp
RegisterHotKey(hwnd, HOTKEY_QUICK_CAPTURE, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_N);
RegisterHotKey(hwnd, HOTKEY_SHOW_ALL,      MOD_CONTROL | MOD_ALT   | MOD_NOREPEAT, VK_N);
```

**必须加 `MOD_NOREPEAT`**（`0x4000`）。不加的话用户按住不放会连续触发几十次，弹出几十个速记浮窗。

**接收 `WM_HOTKEY` 需要一个窗口句柄**。因为 v2 取消了隐藏宿主窗口（§13.10），这里需要一个**消息专用窗口**（`HWND_MESSAGE` 作为父窗口的不可见窗口）来接收 `WM_HOTKEY`：

```csharp
// 消息专用窗口：不在 z 序、不在任务栏、不在 Alt+Tab、不接收广播消息
var hwndSourceParams = new HwndSourceParameters("LumiMemo.MessageWindow")
{
    ParentWindow = new IntPtr(-3),     // HWND_MESSAGE
    WindowStyle = 0,
    Width = 0, Height = 0
};
_hotkeyWindow = new HwndSource(hwndSourceParams);
_hotkeyWindow.AddHook(HotkeyWndProc);
```

**这正好印证了 §13.10 的判断**：需要窗口的时候应该建消息专用窗口，而不是建一个隐藏的顶层窗口。

**两个热键对应什么动作**（与托盘菜单是同一套入口，不各写一份）：

```text
HOTKEY_QUICK_CAPTURE → QuickCaptureWindow（§15.7）
HOTKEY_SHOW_ALL      → "显示全部便签"，路径见下
```

**"显示全部便签"（热键 `Ctrl+Alt+N`、双击托盘图标、托盘菜单项、单实例第二次启动，四者共用）**：

```text
INoteService.OpenAll()                    [Core] 取所有 IsOpen=true 的便签，返回 NoteOpenRequest 列表
  ↓ 逐个
NoteViewModelFactory.Create(note, layout) [App]
  ↓
IWindowManager.ShowNote(vm, layout)       [App]
  ↓ 全部走完后
IWindowManager.ShowAllNotes()             [App] 把已存在的窗口统一恢复显示并激活第一个
```

**注意最后一步不是第一步**：`OpenAll()` 决定"应该有哪些窗口"，`ShowAllNotes()` 只负责"把已经有的窗口亮出来"（§14.2）。两者职责不能混。

**实现说明（本轮已落地）**：上面那三步只覆盖了"有便签可显示"的情形，于是补了一条兜底——**`OpenAll()` 返回空时，把管理器窗口带到前台**（`ManagerViewModel.ShowAll` 的最后一段）。

不补的话，用户把每张便签都点过 ✕ 之后程序就一个界面都没有了，而此时这条路有两个入口是用户换不掉的：**双击托盘图标**（§15.9 固定走它）与**再启动一个实例**（§17.2 的原话是"通知它把自己带到前台"）。那两下于是毫无反应，在用户眼里与"程序坏了"没有区别。

兜底写在 `ShowAll` 里而不是各个入口上，理由与本节开头那条一样：**入口可以多，路径只能有一条**。判据取 `OpenAll()` 的结果而不是去问窗口层有几个窗口——`OpenAll()` 为空就是"没有该显示的"，这与"窗口存在 ⟺ `IsOpen` 为真"是同一条不变式的两面。

**`RegisterHotKey` 失败的处理**：见 §16.3。不阻塞启动，记日志 + 设置页标注 + 首次托盘气泡提示。

## 17.7 开机自启

通过注册表实现，**不用任务计划**：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
  LumiMemo = "C:\...\LumiMemo.exe" --startup
```

**用 `HKCU` 而不是 `HKLM`**：`HKLM` 需要管理员权限，便签程序不应该要求提权。

**`--startup` 参数的意义**：开机启动时**不打开任何窗口**，只驻留托盘。用户开机后看到的应该是一个干净的桌面，而不是十几张便签铺满屏幕。

**另外，如果任务栏上还残留着上次的布局**，`IsOpen` 为 true 的便签在开机启动时**也不自动打开**——需要用户从托盘点一下。这是 `--startup` 与正常启动的**唯一区别**：

```text
正常启动：恢复所有 IsOpen=true 的便签
--startup 启动：不恢复窗口，只驻留托盘（IsOpen 状态保持不变）
```

**首次设置开机启动时要提示**这个行为，否则用户会以为"我的便签丢了"。

**写入注册表失败**（权限、组策略限制）时，在设置页显示错误并回滚 UI 上的勾选状态，不要让开关显示为开启而实际没生效。

---

# 18. MVVM 落地规范

## 18.1 ViewModel 清单

| ViewModel | 对应 View | 职责 |
|---|---|---|
| `NoteViewModel` | `NoteWindow` | 单张便签：内容、标题、颜色、标签、保存状态、折叠/置顶/锁定状态、缩放 |
| `ManagerViewModel` | `ManagerWindow` | 便签列表与搜索（§15.8） |
| `QuickCaptureViewModel` | `QuickCaptureWindow` | 速记浮窗（§15.7） |
| `SettingsViewModel` | `SettingsWindow` | 设置。**回收站是它的一个页签**，不单独开窗口（§7.4） |
| `TrashViewModel` | （`SettingsWindow` 的"回收站"页） | 回收站条目、恢复、清空 |
| `TrayViewModel` | （托盘图标，无窗口，§15.9） | 托盘菜单的可用状态与命令 |
| `ConflictViewModel` | `ConflictDialog`（`Views/Dialogs/`） | 外部修改冲突处理（§11.4） |

**每个 ViewModel 只暴露它对应的 View 需要的东西**。`NoteViewModel` 不应该知道"便签列表"的存在，也不应该知道回收站。

### `NoteViewModel` 的状态归属

这是 v1 里容易含混的地方，v2 明确三类状态的归属：

| 状态 | 归属 | 是否落盘 | 落在哪 |
|---|---|---|---|
| `Content`、`Tags`、`Color` | `Note`（数据模型） | 是 | Markdown 文件 |
| `IsCollapsed`、`IsTopMost`、`IsLocked`、`X/Y/W/H` | `NoteLayout`（机器状态） | 是 | layout.json |
| `SaveStatus`、`IsComposing`、`IsDirty`、`CaretIndex` | `NoteViewModel`（纯 UI 临时状态） | **否** | 内存 |

**第三类绝不落盘。** 特别是 `CaretIndex`——有人会想"恢复光标位置多贴心"，但这会让每次光标移动都产生一次状态变化，需要额外的持久化去抖，收益远小于成本。

## 18.2 ViewModel 之间的通信

**用 `WeakReferenceMessenger`（CommunityToolkit.Mvvm 自带），不用 C# 事件。**

理由：C# 事件是强引用，`NoteService` 持有 `NoteViewModel` 的事件处理器会让 ViewModel 无法回收——**便签窗口关了，ViewModel 还活着**，打开关闭几百次就是内存泄漏。

```csharp
// 发送
WeakReferenceMessenger.Default.Send(new NoteDeletedMessage(noteId));

// 接收
WeakReferenceMessenger.Default.Register<NoteDeletedMessage>(this, (r, m) =>
{
    if (m.NoteId == Id)
        _windowManager.CloseNote(Id);
});
```

**预定义的消息类型**：

| 消息 | 发送方 | 接收方 |
|---|---|---|
| `NoteCreatedMessage` | NoteService | ManagerViewModel、TrayViewModel |
| `NoteDeletedMessage` | NoteService | NoteViewModel、ManagerViewModel |
| `NoteUpdatedMessage` | NoteService | ManagerViewModel |
| `NoteExternalChangedMessage` | FileWatcher | NoteViewModel |
| `ThemeChangedMessage` | SettingsViewModel | 所有便签窗口 |
| `SettingsChangedMessage` | SettingsViewModel | 各处 |

**实现说明（现状）**：上表里「发送方」一栏写的 `NoteService`，三条**都做不到**——`NoteService` 在 `LumiMemo.Core`，而 Core 零第三方依赖（§4.1），它调不到 `WeakReferenceMessenger`。消息只能从 App 层发。于是本轮落地的是一条 App 层的 `NotesChangedMessage`（无载荷，`LumiMemo.App/Messages/`），语义是「便签集合变了，持有列表的界面重读一遍」：

| 发送点 | 接收方 | 为什么需要 |
|---|---|---|
| `TrashViewModel.RestoreAsync` | `ManagerViewModel` | 管理器绑的 `Notes` 是 `Refresh()` 从 `NoteStore` 拷出来的**快照**，不是 `NoteStore` 本身。`INotifyCollectionChanged` 只负责「我改了这份快照，界面跟着变」，**不管**「别人改了 `NoteStore`，我要不要重算快照」——而 `NoteStore`/`SearchIndex` 都不是可观察的。少了这一声，用户从回收站恢复一张便签后管理器毫无察觉，得手动刷新才看得见 |

两条**有意没做**：

1. **`NoteUpdatedMessage` 不接**。照表做的话，用户在便签里每敲一个字都会让管理器把整张列表重排一遍——而管理器的列表只关心**有哪些便签**，不关心某一张的正文。
2. **`ManagerViewModel` 自己的增删不发消息**。新建（`NewNoteAsync`）、删除（`DeleteNoteAsync`）、重扫（`ReloadAllAsync`）各自直接调 `Refresh()`：自己改的自己知道，绕消息一圈只是多一层间接。发消息是留给「别人改了、我无从知道」那一种情形的。

**注意 `WeakReferenceMessenger` 的一个陷阱**：注册时用的是 `this`，如果 `this` 被别处长期持有（比如被 register 到 messenger 之外的容器），弱引用就不起作用了。**ViewModel 只应该被它的 View 通过 `DataContext` 持有**。

## 18.3 ViewModel 的生命周期

**必须实现 `IDisposable`**，且**必须在窗口关闭时调用**：

```csharp
// NoteWindow.xaml.cs
protected override void OnClosed(EventArgs e)
{
    base.OnClosed(e);
    (DataContext as IDisposable)?.Dispose();   // ★ 不能漏
    DataContext = null;
}

// NoteViewModel
public void Dispose()
{
    WeakReferenceMessenger.Default.UnregisterAll(this);
    _autoSave.CancelScheduledSave(Id);
    _contentSubscription?.Dispose();
    _isDisposed = true;
}
```

**三个必须清理的东西**：

1. **Messenger 注册**（`UnregisterAll`）
2. **自动保存计时器**（否则窗口关了还会触发一次保存，而且会碰已释放的对象）
3. **对 `Note` 的订阅**（如果用了 `INotifyPropertyChanged` 的 `PropertyChanged`）

**`Dispose` 要幂等**：加 `_isDisposed` 标志，重复调用直接返回。`OnClosed` 在某些路径下（比如异常）可能走到两次。

**`NoteViewModel` 不持有 `NoteWindow` 的引用**。需要操作窗口时走 `IWindowManager`，通过 `Id` 查找。这样 ViewModel 对窗口的依赖是"接口 + 标识符"，不是"强引用到具体的窗口对象"，也让 §13.9 的窗口池复用成为可能。

## 18.4 绑定规范

| 规范 | 说明 |
|---|---|
| 所有绑定必须显式指定 `Mode` | 不要依赖默认值（`TextBox.Text` 默认是 `LostFocus`，这个默认值经常造成 bug） |
| 编辑器内容用 `UpdateSourceTrigger=PropertyChanged` | 配合 §11.1 的 500ms 去抖；**不要**用 `LostFocus`，否则切窗口时内容不更新 |
| 只读展示用 `Mode=OneWay` | |
| 命令用 `Command="{Binding XxxCommand}"` | 不写 `Click` 事件处理器 |
| **绝对禁止在 View 的代码里写业务逻辑** | 只允许：绑定、纯视觉的交互（如动画）、以及需要 HWND 的 Win32 互操作 |

**唯一的例外是 Win32 互操作**。`NoteWindow.xaml.cs` 里会有 `WM_NCHITTEST`、`WM_DPICHANGED`、`WM_EXITSIZEMOVE` 的处理，这些**必须**在窗口的代码里（消息钩子挂在 HWND 上）。这些处理器只做一件事：**把消息翻译成对 ViewModel 或 `IWindowManager` 的调用**，不含任何决策逻辑。

## 18.5 命令规范

用 `CommunityToolkit.Mvvm` 的源生成器：

```csharp
[RelayCommand]
private async Task SaveAsync() { ... }     // 生成 SaveCommand

[RelayCommand(CanExecute = nameof(CanDelete))]
private void Delete() { ... }              // 生成 DeleteCommand + CanDelete 的联动
```

**异步命令必须用 `[RelayCommand]` 生成的 `AsyncRelayCommand`**，它会自动处理"命令执行期间禁用"（防止用户连点两次触发两次保存）。

**`CanExecute` 的变化要显式通知**：源生成器不会自动追踪 `CanDelete` 依赖的属性。需要在相关属性变化时调用 `DeleteCommand.NotifyCanExecuteChanged()`。**这是源生成器最常见的坑**——按钮明明该亮起来却是灰的。

**约定**：所有 `CanExecute` 依赖的属性都在 `OnPropertyChanged` 里集中触发一次通知：

```csharp
protected override void OnPropertyChanged(PropertyChangedEventArgs e)
{
    base.OnPropertyChanged(e);
    SaveCommand.NotifyCanExecuteChanged();
    DeleteCommand.NotifyCanExecuteChanged();
}
```

## 18.6 明确禁止的做法

```text
✗ ViewModel 里出现 System.Windows.Window / MessageBox / Control 类型
✗ ViewModel 里直接操作 File / Directory（必须走 Repository / Service）
✗ WindowManager 里 new NoteViewModel（§14.1）
✗ Note 数据模型里出现 UI 概念（Color 用 NoteColor 枚举，不是 Brush）
✗ View 的 code-behind 里写 if/else 业务分支
✗ 静态可变状态（static 字段）作为跨 ViewModel 的通信手段
✗ 用 C# 事件在 ViewModel 之间通信（用 Messenger，§18.2）
✗ 在构造函数里做 IO
```

**关于 `MessageBox`**：ViewModel 里需要弹对话框时，注入一个 `IDialogService`：

```csharp
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message,
                            string confirmText = "确定", string cancelText = "取消");
    Task<string?> ShowSaveFileDialogAsync(string suggestedName);
    void ShowError(string title, string message);
}
```

这样 ViewModel 可以脱离 WPF 单独单元测试（§21）。

**关于 `NoteColor`**：数据模型里的颜色是枚举（`NoteColor.Yellow`），View 通过 `IValueConverter` 或 `DataTrigger` 转成 `Brush`。**绝不在数据模型里存 `Brush`**——那会把 `System.Windows.Media` 依赖带进数据层。（v1 §11 的 `Note` 定义里颜色类型需要按此修正。）

---

# 19. 安全与隐私

## 19.1 数据边界

**所有数据都在本地用户目录内，程序不发起任何网络请求。**

这是一条**架构级约束**，不只是"当前没做联网"：

```text
✗ 不上传遥测、崩溃报告、使用统计
✗ 不检查更新（用户手动下载新版本）
✗ 不加载任何远程资源
✗ 不在 Markdown 渲染中解析远程内容
✗ 不集成任何云盘 SDK
```

**用户自己把笔记目录放在 OneDrive 里是允许的**——但那是同步客户端在做网络传输，程序本身仍然只读写本地路径。这两件事必须区分清楚，不要因为"用户可能放在云盘里"就引入网络代码。

## 19.2 Markdown 渲染安全

便签内容可能来自任意来源：网页复制、聊天工具粘贴、别人发来的文件。**内容是不可信的输入。**

### 防线一：`DisableHtml()`

```csharp
new MarkdownPipelineBuilder().DisableHtml().Build();
```

原始 HTML 被当作纯文本转义显示。这挡住了：`<script>`、`<iframe>`、`<img src="...">`（远程请求）、`<style>`（可能覆盖应用样式）。

**不需要"允许一部分安全 HTML"的白名单**。便签的场景里，用户写 `<b>粗体</b>` 时想要的是粗体，而 Markdown 的 `**粗体**` 已经能做到。允许 HTML 直通带来的收益接近零，风险是实打实的。

### 防线二：不用 `WebBrowser` / `WebView2` 渲染

v2 的预览用自写的 `Markdown → FlowDocument` 转换器（§16.7），**不引入任何浏览器引擎**。

`WebBrowser`（IE 内核）有历史安全问题且渲染老旧；`WebView2` 需要额外分发运行时（约 100MB+），与"轻量"的产品目标冲突，而且引入了一个完整的浏览器攻击面。

用 `FlowDocument` 渲染的好处是：**渲染目标是一个纯数据结构的排版树，不是执行环境**。即使 Markdown 里含有恶意内容，最坏情况只是显示得奇怪，不存在执行路径。

### 防线三：图片加载的限制

```text
允许：相对路径指向笔记目录内的本地文件
允许：绝对路径指向本地文件（但给出安全提示，见下）
禁止：http:// 和 https:// 开头的 URL（显示为纯文本链接，不自动加载）
禁止：file:// 之外的其他协议（ftp:、data: 等）
```

**为什么禁止远程图片自动加载**：加载一张远程图片就等同于向那个服务器发送了一个"这个用户打开了这张便签"的信号，而图片的 URL 里可能带有便签 ID（跟踪像素）。即使程序自身不做任何上传，自动加载远程图片也会造成隐私泄漏。

**远程图片的处理**：显示一个占位符（"远程图片未加载 · 点击在浏览器中打开"），点击后用系统默认浏览器打开。**用户明确点击才发生网络请求**，且请求由浏览器发起而非本程序。

## 19.3 链接与文件打开的安全边界

便签里可能有各种链接，点击时的行为必须收窄。

| 链接类型 | 行为 |
|---|---|
| `https://` / `http://` | 用 `Process.Start` + `UseShellExecute = true` 交给默认浏览器 |
| `mailto:` | 交给默认邮件客户端。**必须先确认协议在允许列表内** |
| 相对路径（指向笔记目录内） | 在资源管理器中选中该文件 |
| 绝对路径（本地文件） | **弹确认框**，显示完整路径，用户确认后才打开 |
| 其他协议（`file:`、`ms-*:`、自定义协议） | **拒绝并提示**，不在允许列表内 |

**为什么绝对路径要确认**：便签可以来自别人（分享的 .md 文件），里面的链接可以指向 `%APPDATA%\...\config` 之类的位置。让用户看到完整路径再决定，是一个成本极低但有效的防护。

**协议白名单的实现要点**：不要用 `Process.Start(link)` 直接丢给 Shell——那会让任意注册过的协议被触发。先 `Uri.TryCreate` 解析，检查 `Scheme` 在白名单内，再启动。

**"在资源管理器中显示"** 用 `explorer.exe /select,"完整路径"`，**不要**用 `Process.Start(目录路径)`（会直接打开文件夹并可能触发自动运行相关行为）。路径中的引号与 `,` 要正确转义，否则可以被注入额外的 explorer 参数。

## 19.4 路径安全

便签的文件名、标签、附件名都可能影响生成的路径，必须防路径穿越。

```csharp
// 由标题派生文件名时（§5.6），必须做这两步
1. 移除路径分隔符：\ / : * ? " < > |  以及控制字符
2. 规范化后验证：Path.GetFullPath(result).StartsWith(
       Path.GetFullPath(notesFolder) + Path.DirectorySeparatorChar,
       StringComparison.OrdinalIgnoreCase)
   → 不在笔记目录内则拒绝，用兜底名（untitled-{8位id}.md）
```

**第 2 步不能省**。只做第 1 步（移除非法字符）挡不住 `..` 这样的输入——`../../../autoexec.md` 不含任何非法字符，但会让文件写到笔记目录之外。

**同样的检查要应用于**：

- 附件文件名
- 回收站的恢复目标路径
- 从 Markdown 里解析出的相对图片路径（防止 `![](../../../../Windows/System32/x.dll)` 被打开）

**`\\?\` 长路径前缀**（§5.10）：只在**已经通过上述校验**的路径上加前缀，不要在校验之前加（`\\?\` 会绕过 Win32 的路径规范化，让 `..` 校验失效）。

## 19.5 日志中的敏感信息

**日志绝不写便签正文。**

便签内容对用户来说是私密的——可能包含密码、地址、私人笔记。日志文件可能被用户发给别人排查问题，正文出现在里面是不可接受的。

| 可以记 | 不可以记 |
|---|---|
| 便签 ID（GUID） | 便签正文 |
| 文件路径 | 剪贴板内容 |
| 内容长度（字符数） | 标签的完整列表 |
| 内容 hash 的前 8 位 | 完整 hash |
| 异常类型与堆栈 | 异常 Message 中可能内嵌的正文片段 |

**最后一条容易漏**：`YamlDotNet` 解析失败时抛出的异常 Message **会包含出错的那一行原文**。写日志前必须过滤——只记异常类型和行号，Message 用白名单字段（`YamlException.Start.Line`）而不是整个 Message。

**同样注意**：`System.Text.Json` 反序列化失败的 Message 也可能回显输入的片段（配置内容，风险较低但仍应过滤）。

**日志 Provider 不替调用方过滤**（实现说明）：`FileLoggerProvider`（§20.5）会把传给它的异常按 `ToString()` 原样写出——包括 Message 与堆栈。过滤的责任全在调用点，理由有两条：Provider 拿不到「这一行里哪一段是用户内容」这个信息，而按关键词猜（比如见到「便签」就截断）只会把真正有用的那半行也吃掉。

因此仓库里的约定是**给日志传 `ex.GetType().Name` 而不是异常对象**，`JsonLayoutStore`、`MarkdownNoteRepository`、`FileSystemTrashStore` 都照这条写。两处例外，都是「异常里不可能有用户内容」的类型：

- `FrontMatterParser` 根本不进日志，它把结果挂成 `NoteParseIssue`；那里的 `YamlException` 连 `Detail` 都只放 `ex.GetType().Name` 与 `ex.Start.Line`，绝不碰 Message（该文件 369 行那条注释就是为此写的）。
- `SingleInstanceGuard` 命名管道重试那一处传的是异常对象本身——管道 IO 的异常不会有便签正文。

排查时真要完整堆栈，就在调用点自己先取出来，别指望 Provider 帮你把关。

## 19.6 明确不做

```text
✗ 便签内容加密（用户需要的是"文件可以直接打开"，加密与这个目标冲突）
✗ 便签密码锁（同上，且加密后 Obsidian 就读不了）
✗ 权限提权（程序全程以当前用户权限运行，不请求管理员）
✗ 注册系统级右键菜单/文件关联（避免污染用户的系统）
✗ 后台服务/驱动
```

**关于"密码锁"**：如果用户有真正的保密需求，正确的做法是把笔记目录放在 BitLocker 加密的卷上、或用 VeraCrypt 之类的工具，而不是让便签程序自己做加密。理由：自己实现的加密几乎一定不如专用工具可靠，而且一旦加密就没法用其他编辑器打开，违背了"Markdown 是唯一数据源"的核心承诺。

---

# 20. 性能与资源

## 20.1 目标指标

| 指标 | 目标 | 不可接受 |
|---|---|---|
| 冷启动到托盘图标可见 | ≤ 500ms | > 1.5s |
| 冷启动到便签全部恢复（20 张、2000 条笔记） | ≤ 2s | > 5s |
| 空闲内存（10 张便签打开） | ≤ 120MB | > 250MB |
| 空闲内存（仅托盘） | ≤ 40MB | > 80MB |
| 空闲 CPU | 0%（无定时器空转） | > 0.5% |
| 按键到字符显示 | ≤ 16ms | > 50ms |
| 打开一张新便签窗口 | ≤ 100ms | > 300ms |
| 搜索 2000 条笔记 | ≤ 50ms | > 200ms |
| 自动保存单次写入 | ≤ 10ms | > 50ms |

**这些指标必须实测**，不能靠"感觉很快"，也不能靠"估算"。测量方法见 §20.6。

## 20.2 启动性能

**主要成本是扫描笔记目录**。2000 条笔记、每条平均 2KB，总计约 4MB——**读取本身很快，慢的是解析**。

优化顺序：

```text
1. 扫描与解析全部在后台线程（§3.4）
   - UI 线程只负责建托盘图标和窗口

2. 并行解析
   - Parallel.ForEach 或 PLINQ，并行度 = Environment.ProcessorCount（上限 8）
   - 注意：解析是纯 CPU + IO，没有共享可变状态，天然可并行

3. 惰性构建搜索索引
   - 首次扫描时只提取「标题」与「标签」（便宜，用于列表显示）
   - _plainText 等到第一次搜索时再构建（§12.4）
   - 这能把首次搜索推迟到用户真正需要的时候

4. 分批打开窗口（§17.1 第 11 步）

5. 不要在启动时做「孤儿附件扫描」
   - 那是 O(附件数 × 便签数) 的操作，只在用户手动触发时执行（§6.4）
```

**关于并行度的上限 8**：再往上收益递减（磁盘成为瓶颈），而且会挤占用户其他程序的 CPU。8 是个合理的上限。

**首屏原则**：**托盘图标必须在 500ms 内出现**。用户双击图标没反应会以为程序没启动，然后去点第二次——这时第一个实例才刚起来，又触发单实例逻辑。先让图标出现，再慢慢加载。

## 20.3 内存

| 来源 | 说明 |
|---|---|
| .NET 运行时基线 | WPF 应用约 25–35MB，无法避免 |
| 每个打开的便签窗口 | 约 3–8MB（视内容量与视觉树复杂度） |
| 每张已加载便签的数据 | 内容字符串 + 标题缓存，约 2–4KB |
| 搜索索引 `_plainText` | 与正文等量，2000 条约 4MB |
| 窗口池（`TargetIdle = 3`） | 约 10–25MB ← **这是最大的一项** |

**窗口池的取舍**：池化能显著提升打开窗口的速度，但它常驻的每个空闲窗口都是 3–8MB。内存目标是 120MB（10 张便签），池越大越逼近上限。

**v2 的处理**（`TargetIdle` 的定义见 §13.9）：

```text
默认 TargetIdle = 3，MaxIdle = 8
若实测打开便签超过 100ms  → 提高 TargetIdle
若实测内存超过 120MB 目标 → 降低 TargetIdle，直至关闭池化
```

**池化的收益必须用测量来证明**，不要因为"理论上更快"就默认开启。第一版甚至可以完全不做池化，先测出真实的窗口创建耗时再决定。

**内存泄漏的高风险点**（每次打开/关闭便签都会累积）：

```text
✗ WeakReferenceMessenger 的注册没注销      → §18.3
✗ 窗口的 Closed 事件处理器没移除
✗ 静态字典缓存了 Guid → Window/ViewModel 的映射
✗ DataContext 没断开就回池                  → §13.9
✗ FileSystemWatcher 的抑制字典无界增长      → 见下
```

**`_recentHashes` 和 `_internalWrites` 必须有界**（§10.3）：

```text
_internalWrites：条目超过 SuppressWindow（3 秒）即失效，每次访问时清理过期项
_recentHashes：每个路径最多 8 条（§10.3），但若被删除的便签文件永久留下条目，
               同样需要在访问时按时间清理
_pending（去抖字典）：任务完成后必须 TryRemove，失败会导致无界增长
```

## 20.4 UI 响应

**基本原则：UI 线程上不做任何 IO。**

| 操作 | 位置 |
|---|---|
| 文件读写 | 后台线程 |
| Markdown 解析（预览） | 后台线程，完成后切回 UI 线程构建 FlowDocument |
| 搜索 | 后台线程（超过 2000 条时） |
| 附件复制 | 后台线程 |
| 日志写入 | 后台线程（见 §20.5） |

**唯一允许在 UI 线程做的 IO**：退出时的同步保存（§17.4）。因为此时必须等待完成，且程序即将结束。

**长内容便签的编辑**：一个 50KB 的便签在 `TextBox` 里打字会有明显延迟。缓解方式：

```text
1. 编辑器内部按视口虚拟化（TextBox 自带，不用管）
2. 自动保存的 hash 计算在后台线程做（内容先快照，再交给后台算）
3. 预览模式的 Markdown 解析一定在后台，且加 300ms 去抖
```

## 20.5 磁盘写入

**写入次数比写入量更重要**（对 SSD 寿命和电池续航而言）。汇总所有写入路径：

| 触发 | 频率 | 节流 |
|---|---|---|
| 便签内容保存 | 用户输入停止 500ms | ✅ §11.1 |
| layout.json | 窗口移动结束 | ✅ 1 秒去抖 + 仅 `WM_EXITSIZEMOVE` |
| settings.json | 用户改设置 | ✅ 立即写（低频） |
| 日志 | 每条日志 | ✅ **必须异步 + 批量** |
| 回收站索引 | 删除/恢复/清空 | ✅ 立即写（低频） |

**日志的写入策略**（这是最容易出事的一项）：

```text
✗ 每条日志都 File.AppendAllText  → 高频日志会导致大量小写入
✓ 用一个后台消费的 Channel<LogEntry> + 批量 flush（默认 250ms 或积攒 20 条）

日志文件大小上限：单文件 5MB，滚动保留最近 5 个文件
超过总量（25MB）时删除最旧的
```

**为什么日志不用 Serilog**（v1 的判断，v2 保留）：Serilog 的 `File` sink 功能完备，但为了一个本地的滚动日志引入一个额外依赖并不划算，自己写一个带 `Channel` + 滚动的 Provider 约 150 行。**注意 v1 只说了"不用 Serilog"，没有说明自己实现时的具体策略**，这里补上。

**实现说明**：`LumiMemo.Infrastructure/Logging/FileLoggerProvider.cs`。

| 项 | 落地的做法 |
|---|---|
| 队列 | `Channel.CreateBounded<string>`，容量 1000，满了丢最旧的 |
| 批量触发 | 攒满 20 条**或**从这一批的头一条算起过了 250ms，**谁先到算谁** |
| 槽位 | `lumimemo-1.log` … `lumimemo-5.log` 五个固定名，写满 5MB 换下一个 |
| 总量 | 由「5 个槽 × 5MB」这个结构本身保证，没有另写总量检查 |
| 线程 | 消费者整个跑在 `Task.Run` 出来的池线程上（§20.4） |
| 失败 | 写盘与建目录的异常全部吞掉——这是全仓库唯一合理地吞掉所有异常的地方 |
| 释放 | `Dispose` 把通道里剩下的批次写完再走，最多等 2 秒 |
| 级别 | 来自 `settings.json` 的 `logLevel`，由 `StartupSequence.Apply` 推给运行期属性；认不出来的文本退回 `Information` 并记一条警告 |

三处要留意的取舍：

1. **两个触发条件是"谁先到算谁"，不是"先等 250ms 再看积了几条"**。少了「攒满就写」，一次突发（比如启动时那十几行）会白等一个间隔；少了「到点就写」，一条孤零零的日志要等到下一批才落盘——而"刚写完的那条正好是排查要用的"是常态。
2. **滚动按大小而不是按日期**（文件名里不带日期）。代价是文件名不再告诉你哪份更新，所以每条日志都带完整时间戳，用户按文件修改时间找最新的那个。
3. **当前在写哪个槽记在内存里，不按文件时间戳推**。文件时间戳的精度是系统计时器那一档（十几毫秒），一个槽写满得很快时两个槽的时间戳会撞在一起，"谁最新"与"谁最旧"双双退化成"第一个找到的"，程序于是在两个槽之间来回覆盖、另外几个槽形同虚设。时间戳只在进程刚起来、还不知道该接着谁写时用一次。
4. **级别是运行期可改的，不是构造参数一锤定音**。`logLevel` 归 `StartupSequence.Apply` 管，而 `Apply` 在启动时与设置窗口保存后各走一次——"改了立刻生效"这条对日志级别同样成立。代价是那个字段不能是 `readonly`，读它的又是任意线程，于是用 `volatile` 而不是加锁：级别读歪一次最多让某一行多记或少记，不值得为它引一把锁。级别文本认不出来时只退回默认并记一条警告，绝不抛异常——§8.4 已经定下这类字段一律用字符串 + 读取时校验回退，用枚举会让一个错字把整份设置拖成解析失败。

## 20.6 诊断与测量

**必须能测量，否则上面的所有指标都是空话。**

```csharp
// 内置的性能计时器，只在 Debug 或开启诊断时记录
public sealed class PerfScope : IDisposable
{
    // using var _ = _perf.Measure("Startup.ScanNotes");
}
```

**关键埋点**：

```text
Startup.ScanNotes          笔记扫描总耗时 + 条数
Startup.RestoreLayout      布局恢复
Window.Open                单窗口创建耗时
Window.PoolRent            池取用耗时
Search.Execute             搜索耗时 + 条数
Save.AtomicWrite           单次保存耗时
Markdown.Parse             解析耗时 + 内容长度
```

**输出位置**：`%LOCALAPPDATA%\LumiMemo\logs\perf.log`，**只在诊断开关打开时写入**（默认关闭，避免常驻开销）。

**暴露方式**：设置页的"关于"里有一个"诊断"入口，可以看到当前的内存占用、便签数、窗口数，以及一个"导出诊断报告"按钮。报告内容：

```text
版本、.NET 版本、Windows 版本与 build
机器信息（CPU 核数、内存总量——不包含任何硬件序列号等个人标识）
便签总数、标签总数、附件总数
当前打开的窗口数、窗口池占用数
最近 200 条 Warning/Error 日志
各类操作的耗时统计（P50 / P95）
```

**导出报告不含便签正文、不含完整文件路径、不含用户名**。路径要脱敏为相对笔记目录的形式。

**这是 v1 完全没有的一项**（D8）。没有诊断出口会导致用户报障时只能描述"就是很慢"，排查成本极高。

## 20.7 资源释放检查

```text
✓ FileSystemWatcher：程序退出时 Dispose（§17.4）
✓ 全局热键：UnregisterHotKey（§17.4）
✓ Mutex：退出时 ReleaseMutex（否则下次启动会被误判为"已有实例"）
✓ 命名管道：关闭并 Dispose
✓ 日志 Channel：退出前 Flush 并 Complete
✓ 所有窗口：关闭（`Application.Shutdown` 会自动处理，但要确保 DataContext 被 Dispose）
✓ 编辑器里的图片 BitmapImage：如果用 BitmapCacheOption.OnLoad 加载则不会有文件句柄问题；
  若用默认的 OnDemand，图片文件会一直被占用（表现为"无法删除附件"）
```

**最后一条是实际会踩的坑**：WPF 加载图片默认是延迟加载（持有文件句柄），导致附件文件无法被删除或移动。**所有加载附件图片的地方都必须用**：

```csharp
var bmp = new BitmapImage();
bmp.BeginInit();
bmp.CacheOption = BitmapCacheOption.OnLoad;   // ★ 立即加载进内存并释放文件句柄
bmp.UriSource = new Uri(path);
bmp.EndInit();
bmp.Freeze();                                  // 冻结后可跨线程使用，且不再需要 Dispatcher
```

---

# 21. 测试策略

## 21.1 测试工程的引用范围

**v1 §117 的测试工程设计有问题**（A9）：它既说"测试工程引用核心库"，又在测试清单里列了需要 UI 的项（窗口行为、剪贴板），两者矛盾——引用核心库的测试工程无法测试 UI 行为。

**v2 的划分**：

| 测试工程 | 引用 | 测试内容 |
|---|---|---|
| `LumiMemo.Core.Tests` | Core | Markdown 解析/序列化、Front Matter、标题派生、附件路径计算、搜索排序、布局恢复算法、路径安全校验 |
| `LumiMemo.App.Tests` | Core + App | ViewModel 逻辑、AutoSaveService 的调度、去抖逻辑、Messenger 通信、命令的 CanExecute |
| `LumiMemo.Integration.Tests` | 全部 | 真实文件系统上的读写、watcher 事件、原子保存、回收站、冲突处理 |
| 手工测试清单 | — | 所有 Windows 特有行为（§21.4） |

**`LumiMemo.App.Tests` 能测试 ViewModel 的前提是 ViewModel 不依赖 WPF 的具体类型**——这正是 §18.6 那些禁令要保证的。`IDialogService`、`IDispatcher` 这些抽象就是为了让 ViewModel 可测而存在的。

**`IDispatcher` 抽象**：

```csharp
public interface IDispatcher
{
    void VerifyAccess();
    bool CheckAccess();
    void Invoke(Action action);
    Task InvokeAsync(Action action);
}
```

生产实现包装 WPF 的 `Dispatcher`，测试实现直接同步执行。**这样 ViewModel 的异步逻辑可以在单元测试里确定性地验证**，不需要真实的 `Dispatcher`。

## 21.2 单元测试清单（Core）

```text
[标题派生]
  - 空内容 → 「无标题便签」
  - 只有 Front Matter → 「无标题便签」
  - "# 标题" → 「标题」
  - "## 标题" → 「标题」（层级不影响）
  - "  内容" （前置空行） → 「内容」
  - 第一行是 ``` 代码围栏 → 跳到围栏之后
  - 第一行是表格分隔行 "|---|" → 跳过
  - 超长第一行 → 截断到 60 字符
  - 内容只有一行 "#" → 「无标题便签」

[Front Matter]
  - 无 Front Matter → 用默认值，不报错
  - 完整 Front Matter → 所有字段正确解析
  - 未知字段 → 进入 UnknownFrontMatterKeys，序列化后仍然存在
  - id 缺失 → 启动流程补一个
  - id 重复（两份文件）→ 按 §5.5 的规则保留排序第一者，另一份重新生成 id
  - id 非法（不是 GUID）→ 重新生成
  - YAML 语法错误 → 整份便签降级为纯文本，记录 ParseIssue，不丢失内容
  - Front Matter 分隔符不完整（只有开头 --- 没有结尾）→ 视为普通文本

[编码与行尾]（§5.9：编码、行尾、BOM、末尾换行四者全部原样保留）
  - UTF-8 无 BOM + CRLF → 保存后仍然是无 BOM + CRLF
  - UTF-8 有 BOM → 保存后保留 BOM
  - LF 行尾 → 保存后仍然是 LF
  - 无换行结尾 → 保存后仍然无换行结尾（不擅自补）
  - 新建文件 → UTF-8 无 BOM + CRLF + 带结尾换行
  - 内容未变时保存 → 不产生任何磁盘写入（字节级相同则跳过）

[附件路径]
  - 便签在根目录 → "attachments/xxx.png"
  - 便签在 "工作/" → "../attachments/xxx.png"
  - 便签在 "工作/2026/" → "../../attachments/xxx.png"
  - 便签从根目录移动到 "工作/" → 链接被正确重写
  - 附件文件名冲突 → 生成不重复的名字

[搜索排序]
  - 标题精确匹配排在标题包含之前
  - 标题匹配排在标签匹配之前
  - 标签匹配排在正文匹配之前
  - 置顶便签有加权
  - 同分时按 UpdatedAt 降序，再按 Id 升序（确定性）
  - 空查询 → `Search()` 返回空列表；管理器改为显示**全部便签**并按 UpdatedAt 降序（§12.1、§15.8）
  - 中文字符串子串匹配正确

[布局恢复算法]
  - 保存 DPI == 当前 DPI → 尺寸不变
  - 保存 96 / 当前 192 → 尺寸翻倍
  - 窗口超出工作区 → 被夹取
  - 显示器不存在 → 移到主显示器，按层叠排列
  - 尺寸大于工作区 → 被夹取到工作区大小

[路径安全]
  - 标题 "../evil" → 文件名中的 .. 被清除
  - 标题 "..\\..\\x" → 被拒绝，使用兜底名
  - 相对路径 "../../../x" → 被判定为越界

[任务列表勾选]
  - "- [ ] a" → 点击后变成 "- [x] a"
  - 只改 3 个字符，其余原样
  - 缩进的列表项位置计算正确
  - 同一行有多个 "[ ]" 时只改正确的那一个
```

## 21.3 集成测试（真实文件系统）

用 `IDisposable` 的临时目录 fixture，每个测试一个独立的目录树。

```text
[原子保存]
  - 新建文件（目标不存在）→ 走 Move 分支，成功
  - 覆盖已有文件 → 走 Replace 分支，成功
  - 保存过程中目标被别的进程锁住 → 重试，最终成功或抛出明确异常
  - 保存后没有残留 .lumitmp 文件
  - 崩溃现场（手工放一个 .lumitmp）→ 启动时被清理

[FileSystemWatcher]
  - 外部创建 .md → 便签出现在 Store
  - 外部修改 .md → 内容更新
  - 外部删除 .md → 便签从 Store 移除
  - 外部重命名 → 正确处理（不产生重复便签）
  - 程序自己保存 → 不触发外部修改处理（防自触发）
  - 快速连续写入 10 次 → 最终状态正确，无重复处理

[回收站]
  - 删除 → 文件移到 `.lumimemo/trash/`，索引更新
  - 恢复 → 文件回到原位置，索引更新
  - 恢复时原位置已有同名文件 → 冲突处理（重命名而非覆盖）
  - 索引丢失 → 能从 `.lumimemo/trash/` 重建
  - 超过保留期 → 被清理
  
[并发]
  - 一个便签同时被 UI 和 watcher 修改 → 走冲突流程，不丢数据
```

**watcher 测试要注意时序**：`FileSystemWatcher` 是异步的，测试必须等待（轮询 + 超时），不能用固定 `Thread.Sleep`。写一个辅助方法：

```csharp
await WaitUntilAsync(() => store.Contains(id), timeout: TimeSpan.FromSeconds(5));
```

## 21.4 手工测试清单（Windows 特有）

**这些无法自动化，必须在真实机器上逐条过。** 每一条都是"不测就会出问题"的：

```text
[DPI]
  - 100% 屏与 200% 屏之间拖动便签 → 内容清晰，尺寸合理
  - 在 200% 屏上关闭程序，在 100% 屏环境下重启 → 位置合理
  - 运行中改系统缩放（需要注销或重启） → 行为可接受

[多显示器]
  - 便签在副屏，拔掉副屏 → 便签移到主屏
  - 副屏改为竖屏 → 便签被夹取到工作区内
  - 任务栏移到屏幕顶部 → 便签恢复时不与任务栏重叠

[输入法]
  - 微软拼音输入过程中程序自动保存 → 不写入拼音中间态
  - 输入过程中切换便签 → 内容正确
  - 日文/韩文输入法 → 同样正确

[窗口行为]
  - 拖到屏幕边缘 → Aero Snap 生效
  - 拖到屏幕顶部 → 最大化
  - 双击标题条 → 最大化/还原
  - 右键标题条 → 系统菜单出现
  - Win+D → 未置顶的便签**仍显示在桌面之上**（临时置顶），且**焦点不被抢走**（前台仍是桌面）
  - 再按一次 Win+D → 便签的临时置顶被撤掉，且不会反过来盖住刚被还原的窗口
  - Win+D → 置顶的便签全程纹丝不动、不受任何干预
  - 把 settings.json 里的 restoreAfterShowDesktop 改成 false 重启 → Win+D 后未置顶的便签被桌面盖住，点回别的窗口才露出来
  - 任务栏缩略图预览正常

[剪贴板]
  - 从浏览器复制富文本 → 粘贴为纯文本（或按设置）
  - 截图工具复制图片 → 粘贴为附件
  - 从资源管理器复制文件 → 粘贴为链接
  - 其他程序占用剪贴板（如 Office 正在复制大文档） → 不崩溃，提示重试

[文件系统]
  - 笔记目录设在 OneDrive → 正常读写，无冲突副本
  - 笔记目录设在网络盘 → 有提示，手动重载可用
  - 笔记文件被设为只读 → 保存时给出正确提示
  - 磁盘满 → 给出正确提示（可用小容量 VHD 模拟）

[热键]
  - Ctrl+Shift+N 在浏览器全屏、VS Code、记事本、资源管理器下都能弹出并获得焦点
  - 按住 Ctrl+Shift+N 不放 → 只弹一个（MOD_NOREPEAT）
  - 热键被其他程序占用 → 有提示，程序正常启动

[单实例]
  - 双击两次图标 → 只有一个实例，第二次的调用把便签带到前台
  - 上一个实例被强杀（任务管理器结束进程） → 再启动正常
  - 第一个实例刚启动、「正在载入」那一刻就再点一次图标 → 信号不丢（这正是握手协议要修的那个窗口）

[托盘与退出]（阶段 9）
  - 启动后托盘图标出现，悬停显示 "LumiMemo"，图标清晰（16×16 下那个字母认得出）
  - 右键菜单弹出前先刷新「回收站（N）」：删一张便签后再弹 → 数字加了 1
  - 单击图标（默认 toggleManager）→ 管理器窗口出现；再点一次 → 无异常
  - 把 settings.json 的 singleClickTrayAction 改成 newNote 重启 → 单击是新建一张便签
  - 双击图标 → 显示全部便签（且这一步不受 singleClickTrayAction 影响）
  - 「收起全部便签」→ 便签窗口全部消失，**但管理器还在**（程序没退）
  - 关掉管理器窗口 → **程序不退出**，收进托盘；从菜单「便签列表...」能再叫出来
  - **托盘菜单「退出」→ 真的退出**（这一条在验 §17.3 里那句"`Shutdown()` 会忽略 `Closing` 的 `Cancel`"；不成立的话会被"收进托盘"挡下来）
  - 有未保存内容时点退出 → flush 完成后再消失，重开内容还在
  - 退出之后托盘图标**立刻**消失，不留一个点得动却没反应的幽灵图标
  - 「回收站...」→ 设置窗口落在回收站那一页；窗口已开在常规页时也切过去
  - 「设置...」→ 设置窗口落在常规页
  - 「打开笔记文件夹」→ 资源管理器打开笔记目录（把目录改名后再点 → 有错误提示，不是静默无反应）
  - 「重新加载全部便签」→ 在别的编辑器里改过的内容出现在列表里
  - 「关于」→ 版本号形如 0.1.0（三段）
  - showTrayIcon 改成 false 重启 → **没有托盘图标**，此时关掉管理器窗口 = 真退出
```

## 21.5 测试替身

| 抽象 | 生产实现 | 测试替身 |
|---|---|---|
| `IClock` | `SystemClock` | `FakeClock`（可手动推进时间） |
| `INoteService` | `NoteService` | `FakeNoteService`（记录调用、可返回预置的 `NoteOpenRequest`） |
| `IWindowManager` | `WindowManager` | `RecordingWindowManager`（记录开/关窗调用，不创建真实窗口） |
| `IDispatcher` | `WpfDispatcher` | `ImmediateDispatcher`（同步执行） |
| `IDialogService` | `DialogService` | `RecordingDialogService`（记录调用） |
| `IShellLauncher` | `ExplorerShellLauncher` | `RecordingShellLauncher`（记录请求，不真的弹出资源管理器） |
| `IFolderPicker` | `FolderPickerDialog` | `RecordingFolderPicker`（**还没写**，见下） |
| `IApplicationLifetime` | `WpfApplicationLifetime` | `RecordingApplicationLifetime`（只记次数，不关掉测试进程） |
| `IManagerWindowPresenter` | `ManagerWindowPresenter` | `RecordingManagerWindowPresenter` |
| `IFileSystem` | **不采用**（见下） | — |
| `ILogger<T>` | `FileLogger` | `NullLogger` 或 `RecordingLogger`（把日志原文留在内存里，断言「某条日志确实发生了」） |

**`IClock` 是必须的**。去抖、抑制窗口、保留期判断全部依赖时间。用真实时间的测试会既慢又不稳定（flaky）。`FakeClock` 让"500ms 后自动保存"这样的测试变成确定性的。

**阶段 9 的两处已知缺口**（都是刻意留下的，不是漏掉的）：

- **`StartupSequence` 没有自动化测试**，`RecordingFolderPicker` 因此也还没写。它要造出真的窗口（`ManagerWindow`）并在 `RunAsync` 里 `Show()`，而那几个 `await` 的续体必须在同一个有消息泵的 STA 线程上跑完——**测试进程里没有一个能同时满足这两条的脚手架**，硬凑出来的东西比它要验的逻辑还长。§17.1 的验收因此就是 §21.4 里的手工首启清单，那条链本来就只能在真实进程上走。**这不是"以后补"**：如果哪天真的要为它写测试，正确的做法是先把窗口依赖变成可注入的接缝，而不是去搭那个脚手架。
- **`TrayViewModel` 里"打开设置窗口"那两个用例跑在 STA 线程上**（要造出真的 `SettingsWindow` 才能读它落在哪一页）。这不是妥协——`SettingsTab` 的值就是 XAML 里 `TabControl` 的顺序，这个约定只有拿真窗口才验得到，而它正是最容易静默错位的地方。


**关于 `InMemoryFileSystem`**：**不建议做**完整的文件系统抽象。理由是文件系统语义（锁定、权限、编码、原子性）恰恰是集成测试要验证的东西，抽象掉它就测不出真实问题了。策略是：

```text
单元测试        → 纯逻辑，不碰文件系统
集成测试        → 真实的临时目录（§21.3）
不要做的       → 一个模拟的文件系统抽象（成本高、覆盖度反而低）
```

---

# 22. 分期路线

## 22.1 一期：可用的便签程序

**目标：一个自己每天愿意用的便签程序。**

```text
[核心闭环]
  - 新建 / 编辑 / 自动保存 / 关闭 / 删除
  - Markdown 文件读写，Front Matter，标题派生
  - 文件监听与外部修改的重新加载
  - 托盘图标与菜单

[窗口]
  - 无边框 + WindowChrome + DWM 圆角/阴影
  - 拖动、缩放、贴边、最大化
  - 折叠 / 展开（含 ExpandedHeight）
  - 置顶
  - 位置与尺寸持久化（layout.json + 物理像素 + DPI）
  - 多显示器与 DPI 变化的处理

[数据]
  - 笔记目录选择（含首次运行向导）
  - 附件：粘贴图片 → attachments/，相对路径
  - 回收站：删除 / 恢复 / 保留策略 / 手动清空

[体验]
  - 速记浮窗（Ctrl+Shift+N）     ← C1，一期必须
  - 可点击任务复选框             ← 一期
  - 便签列表 + 搜索（含排序与摘要）← C3
  - 状态条（保存状态 + 字数）

[工程]
  - 单实例、日志、诊断入口
  - 退出时的 flush（失败只记 Error 日志，不弹窗，§11.5）
  - 单元测试覆盖 §21.2 的全部清单
```

## 22.2 必须在一期开工前完成的原型验证

**这三项如果不先验证，可能会推翻设计。必须在写正式代码之前用独立的小原型跑通。**

| # | 原型 | 验证内容 | 失败时的应对 |
|---|---|---|---|
| P1 | 无透明 WPF 窗口 + DWM | Win11 圆角与阴影正常；Win10 上无阴影是否可接受 | Win10 接受直角；若连阴影都没有则用 `DwmExtendFrameIntoClientArea`（§13.1） |
| P2 | 点击穿透 | §13.7 的 7 条清单 | 降级为"折叠替代穿透"（§13.7） |
| P3 | 速记浮窗抢焦点 | §15.7 的四类前台应用下都能拿到键盘焦点 | 换用 `SetForegroundWindow` 的其他变体，或退化为"托盘菜单触发" |

**P1 和 P3 的失败概率较高，一定要先做。P2 失败就是直接不做。**

## 22.3 二期：打磨

```text
  - 预览模式（自写 Markdown → FlowDocument，§16.7）
  - 编辑助手（列表自动延续、Tab 缩进、Ctrl+Shift+V）
  - 搜索命中高亮
  - 外部冲突的差异视图
  - 崩溃恢复（recovery 目录，§11.6）
  - 标签的管理界面（批量重命名、删除）
  - 附件管理界面（孤儿附件清理的手动入口）
  - 鼠标穿透（若 P2 通过）
  - 窗口池优化（若实测需要）
  - 笔记目录切换的完整迁移流程
```

## 22.4 三期：可选

```text
  - 便签组（"小分队"）          ← C2，规格需要先细化（见下）
  - 批量操作（多选、批量打标签、批量删除）
  - 列表内拖拽排序（需要一个新的排序字段，先想清楚放哪）
  - 导入/导出（从 PinSlip、从纯文本批量导入）
  - 多语言（i18n）              ← C9
  - SQLite FTS5 搜索索引        ← 仅当 §12.4 的三个条件同时满足
```

**关于"便签组"开工前必须先回答的问题**（这些不解决就不要开始）：

```text
1. 组的定义存在哪里？layout.json（机器状态）还是 Front Matter（跟着文件走）？
   - 放 layout.json：换机器/换笔记目录后组就没了
   - 放 Front Matter：每张便签都要写一个 group 字段，用户手写笔记时这个字段很突兀
2. 便签被单独拖动时，是否脱离组？什么操作算"脱离"？
3. 组内便签被删除/移入回收站时，组如何变化？组内只剩一张时，组还存在吗？
4. 组跨越不同显示器、不同 DPI 时如何统一宽度？
5. 组的成员是否必须都在笔记目录的同一文件夹下？
```

**关于"多语言"的边界**：三期只做**界面文案的本地化**，不做便签内容的多语言（用户写什么就是什么，程序不翻译、不检测语言）。这决定了现在必须做、且必须从第一天做起的一件事——**UI 文案不硬编码**。规则见 §24.2：所有面向用户的字符串进 `.resx` 资源文件，代码里只引用资源键。现在遵守这条，三期加语言包就是"加一个 `.resx` + 一个语言选项"；现在不遵守，三期就要回头全文扫字符串，成本高一个数量级。这是三期清单里唯一一条**需要一期就开始付费**的项目。

## 22.5 明确不做

```text
✗ 云同步 / 账号体系
✗ 协作 / 分享
✗ AI 功能（摘要、自动标签、问答）
✗ 移动端
✗ 便签加密 / 密码锁（§19.6）
✗ 插件系统
✗ 主题商店 / 皮肤
✗ 自动更新（用户手动下载）
✗ 全局置顶（盖住全屏应用，§13.5）
✗ 便签显示在任务栏的开关（§13.4）
✗ Win+D 的"豁免"开关 —— 豁免只有 `WS_EX_TOPMOST` 一条路，已由置顶表达。可配置的是**显示桌面期间的临时置顶**（`restoreAfterShowDesktop`，§8.2、§13.6），那是另一回事，不要把它当豁免开关来理解
```

**以上是"不做、且架构上不为它留口子"的清单**——列在这里的意思是：将来若要做，是新增功能，不是"把预留的开关打开"。写代码时不要为它们设计抽象层。

**与之相对的另一类：v2 不做、但架构上预留**（只有一项）：

```text
△ 便携模式（portable）—— §8.1 已用 IAppPaths 预留，将来加一个 PortableAppPaths 实现即可；
                          但路径解析本身写在一期，不额外造抽象（§23.2）
```

区分标准是**代价**：不做便携模式但留着 `IAppPaths` 的成本接近零（反正路径本来就要有个来源），而给它做一套完整的相对路径 + 迁移机制则不是。

## 22.6 每个阶段的验收标准

**阶段完成的定义是"清单过完"，不是"代码写完"。**

```text
一期验收：
  [ ] §21.4 的手工测试清单全部通过
  [ ] §21.2 的单元测试全部通过，行覆盖率 ≥ 70%（Core 层 ≥ 85%）
  [ ] §20.1 的性能指标实测达标（在目标机器上，不是开发机）
  [ ] P1/P2/P3 原型结论已记录
  [ ] 连续使用一周，没有数据丢失

数据安全验收（一期必须，独立于功能）：
  [ ] 强制结束进程（任务管理器）后重启，最多丢失最后 500ms 的输入
  [ ] 断电模拟（虚拟机强制关机）后，所有 .md 文件可正常打开，无空文件
  [ ] 笔记目录内不存在任何残留的 .lumitmp 文件
  [ ] 便签在程序外被删除后，程序不会重新创建它

与其他工具的互通验收：
  [ ] 用 Obsidian 打开笔记目录，能正常识别 Front Matter 与附件
  [ ] 在 Obsidian 里改一张便签，便签程序能正确同步
  [ ] 用 git 管理笔记目录，便签程序写入后 diff 是最小改动（不产生整文件重写）
```

---

# 23. 打包与发布

## 23.1 发布方式

**采用框架依赖（Framework-Dependent）+ 自包含（Self-Contained）双轨，默认推荐自包含。**

| 方式 | 体积 | 要求 |
|---|---|---|
| 框架依赖 | 约 5MB | 用户需先装 .NET 10 桌面运行时 |
| 自包含 | 约 70–90MB | 无需任何前置依赖 |

**默认发布自包含版本**。桌面便签是"下载下来双击就能用"的工具型软件，让用户先装运行时是不合理的门槛。70MB 对现在的网络条件不是问题。

**目标平台**：`win-x64` 必需，`win-arm64` 可选（如果用户有 ARM 设备）。

**不开 `PublishTrimmed`**：WPF 对裁剪的支持有限，XAML 的反射加载、ResourceDictionary、`IValueConverter` 的类型解析都可能被裁掉，产生运行时才暴露的崩溃。**裁剪带来的体积收益（约 30%）不值得这个风险。**

**不开 `PublishSingleFile`**：单文件模式会把原生库解压到临时目录，导致启动变慢，且与某些杀软的启发式扫描冲突。用普通的多文件目录 + 一个安装包更稳。

**开 `PublishReadyToRun`**：能显著改善冷启动（提前编译去掉 JIT 开销）。这与 §20.1 的"500ms 内出现托盘图标"目标直接相关，**建议开启**。

```xml
<PropertyGroup>
  <PublishReadyToRun>true</PublishReadyToRun>
  <PublishSingleFile>false</PublishSingleFile>
  <PublishTrimmed>false</PublishTrimmed>
  <SelfContained>true</SelfContained>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <TieredPGO>true</TieredPGO>
</PropertyGroup>
```

## 23.2 安装与卸载

**用一个简单的安装包**（Inno Setup 或 MSIX，建议 Inno Setup——它更轻、更可控、无签名要求即可安装）。

```text
安装位置：%LOCALAPPDATA%\Programs\LumiMemo\     ← 用户级安装，不需要管理员
开始菜单：创建快捷方式
桌面：默认不创建（用户可选）
开机启动：默认关闭，安装完成页询问
```

**使用 `%LOCALAPPDATA%\Programs\` 而不是 `Program Files`**：后者需要管理员权限，而便签程序没有任何需要提权的功能。用户级安装也让"卸载"和"绿色使用"都更简单。

**卸载的默认行为**：

```text
删除：程序文件、开始菜单快捷方式、注册表 Run 项
保留：配置（%LOCALAPPDATA%\LumiMemo\）、日志
不动：用户的笔记目录        ← 绝对不动
```

**卸载时明确询问**是否一并删除配置与日志（默认不删）。**笔记目录永远不删、不提示删**——那是用户的文件，程序只是它的一个查看器。

**"便携模式"（不在 v2 范围内，见 §22 分期路线）**：把程序放在 U 盘、用相对路径定位笔记目录。**v2 明确不做**——§8.1 已把 `settings.json` / `layout.json` 固定放 `%LOCALAPPDATA%`，与便携模式的假设直接冲突；同时便携模式会引入"配置与笔记目录都跟随程序目录"这一整套新的路径解析与迁移逻辑，收益不足以支撑一期成本。所以下面这段只作为**未来方向**记录，一期不要实现它。

未来实现时，用一个 `portable.flag` 空文件放在程序目录来激活，激活后：

```text
settings.json、layout.json、日志 → 放在程序目录的 data/ 子目录
笔记目录 → 放在程序目录的 notes/ 子目录
不写注册表 Run 项（开机启动不可用）
```

**但有一条限制必须说明**：便携模式在 U 盘上运行时，`FileSystemWatcher` 对可移动介质的行为与本地磁盘不同（可能漏事件），且 U 盘可能被直接拔出。**便携模式下把自动保存延迟设为允许范围的下限 300ms**（§11.1 规定 300–800ms），并提示用户随时可用托盘菜单的“重新加载全部便签”兜底，降低拔出时丢失内容的风险。

## 23.3 版本与更新

**不做自动更新。** 在"关于"页显示当前版本，并提供一个"检查新版本"的链接（用系统浏览器打开发布页）。

**理由**：自动更新需要一个服务器、一套签名与完整性校验、以及处理"更新时程序正在运行"的复杂逻辑。对个人工具项目，手动下载的成本远低于引入这套基础设施。

**版本号规范**：`{主}.{次}.{修订}`，程序集版本与显示版本一致。

```text
主：数据结构或配置格式不兼容变化（需要迁移）
次：新功能
修订：修复
```

**配置格式版本**：`settings.json` 与 `layout.json` 都有 `version` 字段（§8.4）。**升级时如果发现 `version` 高于当前程序支持的版本**（用户降级了程序），**不要尝试解析**——备份现有文件，用默认值启动，并在启动时提示。

**降级保护很重要**：如果不做，新版写入的数据会被旧版静默破坏。

## 23.4 代码签名

**建议签名，但不是一期的阻塞项。**

未签名的程序会被 SmartScreen 拦下来（"Windows 已保护你的电脑"），用户需要点"更多信息 → 仍要运行"。对个人使用的工具可以接受；如果要分发给别人，签名几乎是必需的。

**注意**：代码签名证书（EV 或 OV）有年费，且从 2023 年起 OV 证书也需要硬件令牌或云 HSM。**这是成本决策，不是技术决策**，在路线图中作为可选项列出即可。

---

# 24. 开发原则

## 24.1 十条原则

**1. Markdown 文件是唯一真实数据源。**
任何内存中的结构、任何索引、任何配置，只要和 Markdown 冲突，Markdown 优先。删除程序目录里的所有其他文件，用户的笔记必须完好。

**2. 绝不静默丢失用户的数据。**
保存失败、冲突、解析失败、退出时有未保存内容——每一种情况都必须让用户知道，且必须提供"把内容救出来"的路径。宁可多弹一次对话框，不可少写一个字节。

**3. 对用户文件的最小侵入。**
只改必须改的字符（§15.6 的复选框、§15.4 的标题），不动行尾、不动 BOM、不重排 Front Matter、不格式化代码块。用户用 git 管理笔记目录时，diff 必须是最小的。

**4. 便签是用户的文件，程序只是查看器。**
不加密、不私有格式、不锁定、不注册文件关联。程序被删除后，笔记依然完整可用。

**5. 只在可见的便签上花资源。**
2000 张便签不创建 2000 个窗口，不为不可见的便签构建搜索索引，不为没打开的功能启动定时器。

**6. UI 线程上不做 IO。**
没有例外（唯一的例外是退出时的同步 flush，§20.4）。

**7. 每个平台特性都要有降级路径。**
`DwmSetWindowAttribute` 失败 → 直角。热键注册失败 → 继续启动并提示。点击穿透不稳定 → 用折叠替代。**任何 Win32 调用都可能失败，失败不能让程序崩溃。**

**8. 先测量，再优化。**
窗口池、FTS5、并行度——每一项优化都必须有测量数据支撑（§20.6）。不做"按理说会更快"的优化。

**9. 显式优于隐式。**
绑定写 `Mode`，命令写 `CanExecute`，去抖写数值，超时写时长。默认值会随版本变化，写死了才是契约。

**10. 宁可少一个功能，不可多一个坑。**
路线图里"不做"的清单（§22.5）和"要做"的清单同样重要。有疑问的功能先不做，想清楚再做。

## 24.2 代码组织规范

```text
[命名]
  - 接口：I{Name}
  - 异步方法：{Verb}Async
  - 私有字段：_camelCase
  - 常量：PascalCase（不用全大写）
  - 异步方法的 CancellationToken 参数名：ct

[文件]
  - 一个文件一个公开类型
  - 文件用 sealed class（除非明确需要继承）
  - 可空引用类型开启（<Nullable>enable</Nullable>），警告视为错误
  - 隐式 using 开启，但 WPF 相关的 using 显式写出

[组织]
  - 类型按功能放，不按"类型种类"放（不要有个全局的 Helpers 文件夹）
  - 一个类型超过 400 行就考虑拆分
  - 超过 50 行的方法必须拆

[异步]
  - 库代码一律 ConfigureAwait(false)
  - 事件处理器用 async void（唯一允许的地方），且必须整体 try/catch
  - 不用 .Result / .Wait()（会死锁）
  - 不用 Thread.Sleep（用 await Task.Delay）
```

**界面文案一律走资源文件，不写字面量。**

v2 不做多语言（§22.4 列为三期），但**字符串从第一天就放进 `Resources/Strings.resx`**，代码里一律 `Strings.SomeKey`，XAML 里一律 `{x:Static ...}` 或经 ViewModel 暴露。

理由：把字符串抽出来是零成本的（写的时候顺手），事后补是极高成本的（要在整个代码库里搜出每一处中文字面量、区分"用户可见文案"和"日志/异常消息"、再逐条替换）。而这一条的收益不只是多语言——文案集中之后，改措辞只需要动一个文件，不用满仓库找。

**例外**：日志消息、异常消息、`Debug.Assert` 的说明**不进 resx**。这些是给开发者看的，不是给用户看的，不需要翻译，而且进 resx 反而让日志检索变麻烦。

## 24.3 明确禁止

```text
✗ 全局可变静态状态（静态字段缓存便签数据）
✗ 在 ViewModel 里引用 WPF 的 Window / MessageBox / Control
✗ 在数据模型里引用 System.Windows.Media（Brush、Color 之外的都算）
✗ 在 View 的 code-behind 里写业务判断
✗ 在构造函数里做 IO
✗ 吞异常（catch 后什么都不做）——至少要记日志
✗ 字符串拼接路径（用 Path.Combine）
✗ 硬编码分隔符 '/' 或 '\\'
✗ 硬编码相对路径层级（如写死 "../attachments"）
✗ 直接 File.WriteAllText 保存便签（必须走 §11.2 的原子保存）
✗ 用 FileSystemWatcher 的默认缓冲区大小（必须显式设 64KB）
✗ 在任何 Win32 调用后不检查返回值
✗ 为"将来可能需要"提前抽象（YAGNI）
```

---

# 附录 A：v1 → v2 章节对照

v1 共 122 节，v2 重组为 24 章 + 4 个附录。本节用于迁移：在 v1 里看到一个概念，能立刻知道 v2 把它放在了哪里。

| v1 章节（主题） | v2 位置 | 说明 |
|---|---|---|
| §1–§6 目标、范围、技术选型 | §1、§2 | 内容基本保留，删去"不采用的技术"与 §2 的重复 |
| §7 Front Matter 格式 | §5.3–§5.5 | 合并 Front Matter + ID 生命周期 |
| §7.1 字段清单 | §5.3 | v1 与 §11 不一致，v2 只保留一处 |
| §8 目录结构、`.pinslip/` | §4.2、§8.1 | **`.pinslip/layout.json` 的做法废弃**，改用 `%LOCALAPPDATA%` |
| §9 settings.json 字段 | §8.2、§9.3 | v1 两处不一致，v2 以 §9.3 的 `AppSettings` 为准 |
| §10 配置位置的讨论 | §8.1 | v2 直接拍板，不再讨论 |
| §11 `Note` 模型 | §9.2 | v2 唯一权威定义；字段名与 v1 §87 对齐 |
| §12 `NoteLayout` 模型 | §9.2 | 新增 `IsOpen`、`ExpandedHeight` |
| §13–§14 存储策略 | §5.1、§5.9 | |
| §15 SQLite/FTS5 的取舍 | §12.4 | v2 给出明确的引入触发条件 |
| §16–§17 Markdown 解析 | §5.2、§5.10、§16.7 | 新增降级矩阵与渲染安全 |
| §18–§22 文件读写、原子写 | §11.2 | 补充 `File.Replace` 首次保存失败、临时文件命名、残留清理 |
| §23 附件与相对路径 | §6 | **v1 的 `../attachments/x.png` 示例对根目录便签是错的**，v2 §6.2 给出通用规则 |
| §25 窗口透明与外观 | §13.1 | **v1 的 `AllowsTransparency=False` + `Background="Transparent"` 组合无效**，v2 弃用 |
| §26、§27 任务栏与 Alt+Tab | §13.4 | v1 当成两个独立开关，实际都由 `WS_EX_TOOLWINDOW` 控制 |
| §28–§32 拖动、缩放、贴边 | §13.2、§13.3 | 改用 `WindowChrome` + `HTCAPTION` |
| §33、§64 锁定 | §13.7 | 锁定与穿透分离 |
| §40 Design Token | §15.3 | **v1 的 `sys:Double` 存 `CornerRadius` 类型错误**，v2 不再需要该资源 |
| §49 标题策略 | §5.4、§15.4 | v2 明确"改标题写回正文第一行" |
| §54、§104 `IWindowManager` | §14.2 | v1 两处定义不同，v2 合并为一份 |
| §64–§72 交互细节 | §15.2、§16.2–§16.5 | |
| §83 "最终核心架构" | §3.1、§3.3 | |
| §87 内存模型 | §9.2 | 与 §11 的冲突以本节为准 |
| §93 状态字段命名 | §9.2 | v1 与 §12 命名不同，v2 统一为 `IsCollapsed` / `IsTopMost` / `IsLocked` |
| §94 `AppSettings` | §9.3 | v1 与 §9 字段不匹配，v2 以此为准 |
| §100 宿主窗口 | §13.10 | **v2 取消**，理由见该节 |
| §103 `WindowManager` 实现 | §14.1、§14.3 | **v1 在窗口层构造 ViewModel，v2 拆出 Factory** |
| §106 ViewModel 生命周期 | §18.3 | |
| §109 自动保存 | §11.1 | |
| §111、§113、§119 数据流 | §3.3 | v1 三处描述不同，v2 归为三条权威数据流 |
| §112、§118、§120 服务清单 | §3.2、§4.1 | |
| §114–§116 项目引用 | §4.1、§4.3 | **v1 的引用方向与 §114 描述相反**，v2 明确单向 |
| §117 测试工程 | §21 | **v1 的引用范围与测试内容矛盾**，v2 拆成三个测试工程 |
| §120 "最终架构示意" | §3.1 | |
| §121–§122 其他 | 分散 | |

**v2 新增的章节（v1 没有对应内容）**：

```text
§0.3   与 PinSlip 的功能差异清单
§3.4   线程模型（T1–T7 规则）
§5.6   文件命名规则
§6.4   孤儿附件清理
§7.2–§7.4  回收站索引与保留策略
§10.4  缓冲区溢出与错误恢复
§10.5  网络盘与云同步目录
§11.3  输入法组合期
§11.4  外部编辑冲突的三路比较
§11.6  崩溃恢复
§12.2  搜索排序
§12.3  摘要与高亮
§13.6  Win+D 的处理与取舍
§13.8  DPI 与坐标的完整算法
§14.4  几何回写（含 RestoreBounds）
§15.2  状态条
§15.4  重命名的写回规则
§15.5  内容缩放（**已作废**，见该节开头；章节保留只为让引用不悬空）
§15.6  可点击任务复选框
§15.7  速记浮窗
§17.5  三层未处理异常
§17.6  全局热键与消息专用窗口
§18.2  ViewModel 通信与消息清单
§19    安全与隐私
§20.6  诊断与埋点
§21.4  手工测试清单
§22.2  三个必做原型
§22.6  验收标准
附录 C  数据流图集
附录 D  Win32 API 清单
```

---

# 附录 B：评审问题在 v2 中的落点

## A 类：内部矛盾（19 项）

| # | 问题 | v2 落点 |
|---|---|---|
| A1 | `layout.json` 位置自相矛盾 | §8.1（统一定在 `%LOCALAPPDATA%`） |
| A2 | 打开/关闭状态无模型承载 | §9.2（`NoteLayout.IsOpen`） |
| A3 | `Note`/`NoteLayout` 字段名不一致 | §9.2（唯一权威定义） |
| A4 | `IWindowManager` 两处定义不同 | §14.2（合并接口） |
| A5 | 文件变更事件流走向不一致 | §10.6（单一流程 + §3.3 流 2） |
| A6 | §114 依赖方向与 §115/§116 相反 | §4.1（单向依赖表） |
| A7 | 服务实现层无归属项目 | §4.1（App 层承载实现） |
| A8 | ViewModel/View 清单三处不一致 | §18.1（完整清单） |
| A9 | 测试工程引用范围与内容矛盾 | §21.1（三个测试工程） |
| A10 | 标题策略歧义，写回位置未定义 | §5.4、§15.4（写回正文第一行） |
| A11 | Front Matter 存什么表述不一致 | §5.3（字段表 + v1 变更说明） |
| A12 | 任务栏与 Alt+Tab 当成两个开关 | §13.4（说明同一机制，给出决策） |
| A13 | `notes/` 子目录缺失、附件路径算错 | §5.7、§6.2（分层规则） |
| A14 | Design Token 类型错误 | §15.3（弃用该资源） |
| A15 | `settings.json` 字段与 `AppSettings` 对不上 | §8.2、§9.3（以 §9.3 为准） |
| A16 | 模型类清单四处不一致 | §9.2、§9.3（唯一清单） |
| A17 | `TrashService` 凭空出现 | §7（回收站完整定义） |
| A18 | `NoteViewModel` 注入与关闭方式矛盾 | §18.1、§18.3（构造注入 + Dispose） |
| A19 | 两处"最终架构"链路不同 | §3.1 |

## B 类：技术可行性与坑（20 项）

| # | 问题 | v2 落点 |
|---|---|---|
| B1 | Win10 上圆角+阴影不能同时成立 | §13.1（选 DWM，Win10 接受直角） |
| B2 | 应优先用 `WindowChrome` | §13.2、§13.3 |
| B3 | WPF `Window.Owner` 挂隐藏宿主会抛异常 | §13.10（取消宿主窗口 + 说明原因） |
| B4 | `FileSystemWatcher` 两个必备项 | §10.1（`InternalBufferSize` + `Error`）、§10.4 |
| B5 | 防自触发需多个历史 hash | §10.3（有界 hash 集合，含竞态示例） |
| B6 | `File.Replace` 首次保存会失败 | §11.2（六点清单第 1 条） |
| B7 | 移动便签导致附件路径断裂 | §6.3（移动时重写链接） |
| B8 | Front Matter `id` 冲突与缺失未定义 | §5.5（生成、补给、冲突处理） |
| B9 | 未指定 DPI 模式与坐标单位 | §13.8（PerMonitorV2 + 物理像素 + DPI） |
| B10 | Show Desktop 方案有失败模式 | §13.6（只有 `WS_EX_TOPMOST` 能压住升起的桌面；不置顶的靠临时上到这一档，另有托盘与热键兜底） |
| B11 | watcher 事件在非 UI 线程 | §10.6（封送到 UI 线程，含优先级选择） |
| B12 | `WS_EX_TOOLWINDOW` 生效时机 | §13.4（在 `SourceInitialized` 中设置） |
| B13 | 托盘图标需指定具体实现 | §15.9（`H.NotifyIcon.Wpf`） |
| B14 | 鼠标穿透与 `WS_EX_LAYERED` 的风险 | §13.7（原型验证 + 降级方案） |
| B15 | 用 WebView2 的额外依赖与安全 | §16.7（改用 `FlowDocument`，不引入浏览器） |
| B16 | 外部链接与文件打开的安全边界 | §19.3（协议白名单 + 绝对路径确认） |
| B17 | 编码与行尾未定 | §5.9（保留编码、保留行尾、保留 BOM 状态） |
| ~~B18~~ | ~~内容缩放缺失~~ | **已剔除**，不需要该功能（§0.3 裁决表、§15.5） |
| B19 | 折叠后恢复原高度缺数据 | §9.2（`NoteLayout.ExpandedHeight`） |
| B20 | 大量窗口创建的策略 | §17.1（分批打开）、§13.9（窗口池）、§15.8（结果上限） |

## C 类：功能疏漏（14 项）

| # | 问题 | v2 落点 |
|---|---|---|
| C1 | 速记浮窗完全缺失 | §15.7（含抢焦点处理）、§22.1 一期 |
| C2 | 便签组规格远远不够 | §15.11、§22.4（暂不做 + 开工前待答问题） |
| C3 | 搜索缺排序、高亮、摘要 | §12.2、§12.3、§15.8 |
| C4 | 回收站缺生命周期管理 | §7.2–§7.4（索引、保留策略、重建） |
| C5 | 附件缺孤儿清理 | §6.4（引用不落盘 + 手动触发） |
| C6 | 编辑器交互细节缺失 | §16.2（列表延续、Tab 缩进）、§16.3（快捷键表） |
| C7 | 会话恢复内容不足 | §8.3（`IsOpen` + 几何 + 折叠 + 缩放） |
| C8 | 笔记目录切换流程缺失 | §8.6 |
| C9 | 多语言完全未提 | §22.4（三期）、§24.2（不硬编码文案的原则） |
| C10 | 深色模式与配色的关系未定义 | §15.3（每色三色值 + 深色变体 + DynamicResource）。**推迟到界面重构那一轮**，与全部样式内容同批 |
| C11 | 全屏应用场景未考虑 | §13.5（不做全局置顶，含理由） |
| C12 | 缺"复制全部""打开所在文件夹" | §15.2（菜单）、§15.9（托盘菜单） |
| C13 | 拖拽排序、批量操作 | §22.4（三期） |
| C14 | 便签在任务栏可见的可选模式 | §13.4（明确不提供开关，含理由） |

## D 类：架构与工程化（10 项）

| # | 问题 | v2 落点 |
|---|---|---|
| D1 | `NoteViewModel` 与 `Note` 双数据源 | §9.2、§18.1（状态三类归属表） |
| D2 | `WindowManager` 创建 ViewModel 破坏分层 | §14.1（拆出 Factory）、§14.2 |
| D3 | `NoteStore` 并发与不可变性未规定 | §9.1（单线程写入 + 快照读） |
| D4 | 布局保存时机与节流未规定 | §14.4、§8.5、§13.8 |
| D5 | DI 容器未选型 | §4.3（`Microsoft.Extensions.DependencyInjection`，不用 Generic Host） |
| D6 | 日志方案未定 | §2.2（依赖选型）、§20.5（自写滚动 Provider + `Channel` 批量写入） |
| D7 | 启动性能与扫描策略 | §17.1、§20.2（后台扫描 + 并行 + 惰性索引） |
| D8 | 无诊断信息出口 | §20.6（埋点 + 诊断报告 + 脱敏） |
| D9 | 单实例 IPC 与"减少 IPC"的原则冲突 | §17.2（命名管道，说明为何这不违反原则） |
| D10 | 测试清单不够可执行 | §21.2–§21.4（可勾选的清单） |

## E 类：文档编辑问题（6 项）

| # | 问题 | 处理 |
|---|---|---|
| E1 | §7 的 Markdown 示例里嵌套代码围栏 | 本文件示例统一用 ```text 包裹，避免嵌套 |
| E2 | §23 的附件相对路径对根目录便签是错的 | §6.2 给出通用规则，示例覆盖三种层级 |
| E3 | §40 的 `CornerRadius` 类型错误 | §15.3 说明并给出正确写法 |
| E4 | 章节编号与内容组织混乱 | 本文件重组为 §0–§24 + 附录 A–D |
| E5 | 若干"待定"没有收口 | §0.4 集中列出剩余待决事项；其余全部拍板 |
| E6 | 术语不统一 | §0.2 给出术语表，全文按此表用词 |

---

# 附录 C：关键数据流图集

## C.1 用户编辑 → 磁盘

```text
用户在编辑器打字
   ↓
TextBox.TextChanged
   ↓
NoteViewModel.Content 更新（去抖后）
   ↓
NoteService.ApplyLocalEdit(note, content)          [UI 线程]
   ├─ note.Content = content
   ├─ note.InvalidateTitle()
   ├─ NoteStore.Update(note)
   └─ SearchIndex.MarkDirty(note.Id)                [惰性重建]
   ↓
AutoSaveService.ScheduleSave(note.Id)              [DispatcherTimer 500ms]
   ↓  （无新输入，计时器到期）
NoteService.SaveNoteAsync(note.Id)                 [UI 线程发起]
   ├─ 序列化 Front Matter + 正文                     [后台线程]
   ├─ 计算内容 hash → 记入 _recentHashes            [后台线程]
   ├─ 标记 _internalWrites[path]                    [写入前，先于实际写]
   ├─ AtomicWriteAsync(path, bytes)                 [后台线程]
   │    ├─ 写 {path}.{ts}-{rand}.lumitmp（CreateNew, Flush(true)）
   │    └─ 目标存在 ? File.Replace : File.Move
   ├─ File.SetLastWriteTimeUtc(path, note.UpdatedAt)
   └─ 上报结果 → NoteViewModel.SaveStatus = Saved    [UI 线程]
   ↓
（若有失败）→ §11.5 的失败处理流程
```

## C.2 外部文件变化 → UI

```text
外部编辑器保存文件
   ↓
FileSystemWatcher 事件                              [线程池线程]
   ↓
ScheduleProcess(path) — 300ms 去抖（按路径）         [线程池线程]
   ↓
IsInternalWrite(path)?  ──是──→ 丢弃
   ↓ 否
读文件 → 计算 hash                                   [线程池线程]
   ↓
MatchesRecentWrite(path, hash)?  ──是──→ 丢弃
   ↓ 否
Dispatcher.InvokeAsync(Background)                   [切到 UI 线程]
   ↓
NoteService.ApplyExternalChange(path, readResult)    [UI 线程]
   ├─ Store 中找不到该 path
   │    → 新便签（Created）→ 加入 Store；若在 layout 中 IsOpen=true 则开窗
   ├─ Store 中找不到且 Store 认为存在 → 删除处理（Deleted）
   └─ 找到 → 三路比较（§11.4）
        ├─ 磁盘 == 本地               → 忽略
        ├─ 磁盘 == 基线，本地 != 基线  → 静默 reload → NoteUpdatedMessage
        └─ 三方都不同                  → 冲突 → ConflictViewModel → 弹窗
   ↓
（若内容变化）Markdown 解析 → 更新 Note → 发 NoteUpdatedMessage
```

## C.3 打开便签窗口

```text
用户双击列表项 / 托盘菜单 / 启动恢复
   ↓
1. INoteService.OpenNote(noteId)                     [Core / UI 线程]
   ├─ note = NoteStore.TryGet(noteId) → 不存在则返回 null
   ├─ layout = LayoutStore.GetOrCreate(noteId)
   │    └─ 不存在 → 生成默认 layout（在空闲区域摆位，§17.2）
   ├─ layout.IsOpen = true
   └─ 返回 NoteOpenRequest(note, layout)
   ↓
2. NoteViewModelFactory.Create(note, layout)          [App，唯一的构造点]
   ↓
3. IWindowManager.ShowNote(vm, layout)               [App，唯一碰窗口的地方]
   ├─ 已有窗口？→ 激活并返回
   ├─ _pool.Rent(vm) → NoteWindow
   ├─ PlaceWindow（§13.8 的 DPI 缩放 + 工作区夹取）
   ├─ ApplyVisualState（折叠/置顶/锁定）
   ├─ window.Show()
   └─ DwmInterop.ApplyRoundedCorners / EnsureSystemShadow
   ↓
LayoutStore 标记脏 → 1 秒去抖后写 layout.json
```

**这三步不能合并**：第 1 步在 Core，第 2、3 步在 App。Core 不允许依赖 App，两边互相依赖又会形成构造循环，所以由 App 层的发起方（命令 / 热键 / 启动恢复逻辑）把它们串起来（§14.1）。

## C.4 关闭与退出

```text
[关闭单个便签]
用户点 ✕
   ↓
NoteWindow.Closing
   ├─ AutoSaveService.SaveNow(noteId)（同步保存未保存内容，§11.1）
   ├─ IWindowManager.CaptureGeometry（回写几何）
   └─ INoteService.MarkNoteClosed(noteId)（置 IsOpen=false + 标记 layout 脏，§17.3）
   ↓
NoteWindow.Closed
   ├─ (DataContext as IDisposable)?.Dispose()
   ├─ DataContext = null
   └─ _pool.Return(window)（按 §13.9 清理）
   ↓
LayoutStore 标脏 → 去抖后写

[退出程序]
托盘菜单 → 退出
   ↓
1. 取消所有待处理的自动保存计时器
2. AutoSaveService.FlushAllAsync()
3. 有失败 → §11.5 的对话框（重试 / 另存为 / 仍然退出）
4. 对每个打开窗口 CaptureGeometry
5. LayoutStore.Flush()（同步写，不去抖）
6. UnregisterHotKey
7. FileSystemWatcher.Dispose()
8. Logger.Flush() + Channel.Complete()
9. Mutex.ReleaseMutex()
10. Application.Shutdown()
```

## C.5 冲突处理

```text
三路比较判定为真冲突（§11.4）
   ↓
ConflictViewModel 弹窗
   ↓
├─ [重新加载]
│    ├─ Store 中的 Note.Content = 磁盘内容
│    ├─ 基线 = 磁盘内容
│    ├─ 发 NoteUpdatedMessage → NoteViewModel 重设编辑器（IsUndoEnabled=false 保护）
│    └─ 清空本地未保存标记
│
├─ [覆盖外部版本]
│    ├─ 先把磁盘版本另存为 {name}.conflict-{时间戳}.md    ← 绝不静默销毁
│    ├─ 把本地内容原子写入原路径（§11.2）
│    ├─ 基线 = 本地内容
│    └─ 清空未保存标记
│
└─ [稍后再说]
     ├─ 便签窗口顶部挂一条持续的状态条
     └─ 不自动重复弹窗
```

---

# 附录 D：Win32 API 清单

所有 P/Invoke 统一通过 **CsWin32**（`Microsoft.Windows.CsWin32` 源生成器）产生，不手写 `[DllImport]`。CsWin32 的好处是自动处理 `SetLastError`、字符串封送、`SafeHandle`，以及 32/64 位的 `LongPtr` 差异（手写 `SetWindowLongPtr` 在 32 位下不存在这个函数，是个经典陷阱）。

## D.1 窗口外观与 DWM

| API | 用途 | 章节 |
|---|---|---|
| `DwmSetWindowAttribute` | 设置 `DWMWA_WINDOW_CORNER_PREFERENCE` 圆角（Win11） | §13.1 |
| `DwmExtendFrameIntoClientArea` | 强制 DWM 绘制系统阴影（可选） | §13.1 |
| `GetWindowLongPtr` / `SetWindowLongPtr` | 读写扩展样式（`WS_EX_TOOLWINDOW`、`WS_EX_LAYERED`、`WS_EX_TRANSPARENT`） | §13.4、§13.7 |
| `WINDOW_EX_STYLE` | 扩展样式的常量枚举；用其中的 `WS_EX_TOPMOST` 判断某个窗口在不在置顶档 | §13.6 |
| `SetWindowPos` | 设置位置尺寸、z 序、`SWP_FRAMECHANGED` | §13.8、§13.6、§13.4 |
| `GetWindow` / `IsWindow` | 取 z 序里紧挨着上面的那个窗口（`GW_HWNDPREV`，临时置顶前记下它，撤销时插回去）；校验锚点句柄是否还有效 | §13.6 |
| `IsWindowVisible` | 取锚点时跳过隐藏窗口——便签上面常压着一串不可见的 `IME` / `MSCTFIME UI`，拿它们当锚点会让便签落到"看着像在最上面"的位置；做锚点校验时也要一起查 | §13.6 |
| `GetWindowRect` | 读取窗口物理像素矩形 | §14.4 |
| `SetForegroundWindow` / `GetForegroundWindow` | 激活窗口（速记浮窗） | §15.7 |
| `AttachThreadInput` | 绕过前台锁定，为浮窗抢焦点 | §15.7 |
| `GetWindowThreadProcessId` / `GetCurrentThreadId` | 配合 `AttachThreadInput`；另用于判断某个窗口是否属于本进程 | §15.7、§13.6 |

## D.2 消息处理

| 消息 | 用途 | 章节 |
|---|---|---|
| `WM_NCHITTEST` | 拖动区、锁定态、缩放热区 | §13.3 |
| `WM_EXITSIZEMOVE` | 布局保存时机 | §13.8 |
| `WM_DPICHANGED` | DPI 变更后的布局保存 | §13.8 |
| `WM_DISPLAYCHANGE` | 显示器配置变化 | §14.5 |
| `WM_HOTKEY` | 全局热键回调 | §17.6 |
| `WM_WINDOWPOSCHANGING` | **明确不处理**（§13.6 说明原因） | §13.6 |
| `DefWindowProc` | 在 `WM_NCHITTEST` 中先取系统判定结果 | §13.3 |

## D.3 显示器与 DPI

| API | 用途 | 章节 |
|---|---|---|
| `MonitorFromPoint` | 判断点落在哪个显示器 | §13.8 |
| `GetMonitorInfo` | 取工作区矩形 `rcWork` | §13.8 |
| `EnumDisplayMonitors` | 枚举所有显示器 | §13.8、§14.5 |
| `GetDpiForMonitor` | 取指定显示器的 DPI | §13.8 |
| `GetDpiForWindow` | 取窗口的 DPI | §13.8 |
| `EnumDisplayDevices` | 从 `\\.\DISPLAY1` 取**设备接口路径**，作为 `displays` 表的键 | §8.3、§13.8 |

> **`EnumDisplayDevices` 是实现阶段补进来的，原表漏了它。** §8.3 要求 `displays` 表的键是
> `\\?\DISPLAY#GSM7754#5&3513048&0&UID4354#{e6f07b5f-...}` 这种跨会话稳定的设备接口路径，
> §13.8 也明说 `\\.\DISPLAY1` 不该当身份用。但除它之外的五条 API 里没有一条产出得了这个路径——
> `GetMonitorInfo` 只给得出 `\\.\DISPLAY1`（连 `MONITORINFOEXW` 变体也只是多给这个 GDI 设备名）。
> 只有对 `\\.\DISPLAYn` 调 `EnumDisplayDevices` 并传
> `EDD_GET_DEVICE_INTERFACE_NAME`，才能从 GDI 设备名映射到 SetupAPI 那边的设备接口路径。
> 相应地 `MONITORINFOEXW`、`DISPLAY_DEVICEW` 两个结构体与 `EDD_GET_DEVICE_INTERFACE_NAME`、
> `MONITORINFOF_PRIMARY` 两个常量也要一并声明。

## D.4 热键与输入

| API | 用途 | 章节 |
|---|---|---|
| `RegisterHotKey` / `UnregisterHotKey` | 全局热键（`MOD_NOREPEAT`） | §17.6 |
| `HwndSource`（非 P/Invoke） | 消息专用窗口，接收 `WM_HOTKEY` | §17.6 |
| `SetWindowLongPtr(GWLP_HWNDPARENT)` | （备选）设置 Owner，仅在需要时 | §13.10 |

## D.5 「显示桌面」与前台事件

| API / 常量 | 用途 | 章节 |
|---|---|---|
| `SetWinEventHook` / `UnhookWinEvent` | 订阅前台变化，识别「显示桌面」的发生与结束 | §13.6 |
| `EVENT_SYSTEM_FOREGROUND` | 关心的那一个事件：前台窗口换了 | §13.6 |
| `WINEVENT_OUTOFCONTEXT` | 回调走本进程的消息队列，不注入任何进程 | §13.6 |
| `GetShellWindow` | 取桌面窗口（`Progman`）。按 Win+D 时前台就是它；点任务栏按钮时前台先经过 `Shell_TrayWnd`，之后才轮到它 | §13.6 |

> **这一节整个是实测的产物。** §13.6 的早期版本以为「显示桌面」会把便签最小化，据此选了
> `Window.StateChanged` + `ShowWindow(SW_SHOWNOACTIVATE)` 这条路，`ShowWindow` 一度列在 D.1 里。
> 实测推翻了那个前提：便签有 owner，系统根本不最小化它，只是被升起的桌面**盖住**。于是需要的
> 不再是"还原"而是"临时上到置顶档"，判据也从"窗口被最小化了"换成"前台变成了桌面窗口"——
> 后者只有 `SetWinEventHook` 收得到。
>
> **它不属于 D.8 禁用的 `SetWindowsHookEx`。** 那一条禁的是往别人的消息流里插钩子（会让整个桌面
> 卡顿、还会被安全软件盯上）；`EVENT_SYSTEM_FOREGROUND` 是系统提供的**通知**，配合
> `WINEVENT_OUTOFCONTEXT` 只订阅事件本身，不注入任何进程。**唯一的硬要求是必须在有消息泵的线程
> （也就是 UI 线程）上注册**，否则回调永远不会被派发。
>
> **「显示桌面」有两条入口，前台序列不一样。** 按 Win+D 时前台直接变成桌面窗口；点任务栏右下角
> 那个按钮时，前台先在**置顶档的** `Shell_TrayWnd` 上停 60～110 ms（Win+D 只要 15～30 ms），
> 之后才落到桌面窗口。本节的判定只问"前台是不是桌面窗口"，所以经过任务栏的那一次不会被误判成
> "显示桌面开始了"——它走撤销分支，而彼时提升记录还是空的，撤销等于空转。至于"撤销时往哪儿插"，
> 那是 §13.6 的事：锚点取自**提升之前**记下的 z 序邻居，与当前前台无关。

## D.6 进程与单实例

| API / 类型 | 用途 | 章节 |
|---|---|---|
| `Mutex`（`Local\` 命名） | 单实例 | §17.2 |
| `NamedPipeServerStream` / `NamedPipeClientStream` | 单实例间通信 | §17.2 |
| `Process`（`ProcessStartInfo` + `ShellExecute`） | 打开链接、在资源管理器中显示 | §19.3 |
| `Microsoft.Win32.Registry`（`HKCU\...\Run`） | 开机自启 | §17.7 |

## D.7 文件系统

| API / 类型 | 用途 | 章节 |
|---|---|---|
| `File.Replace` / `File.Move` | 原子替换 | §11.2 |
| `FileStream.Flush(flushToDisk: true)` | 确保落盘 | §11.2 |
| `FileSystemWatcher` | 文件变更监听 | §10.1 |
| `FileAttributes.Offline` | 检测云同步占位符 | §10.5 |
| `DriveInfo.DriveType` | 检测网络盘 | §10.5 |
| `\\?\` 前缀 | 长路径 | §5.10、§19.4 |

## D.8 明确不使用的 API

| API | 为什么不用 | 章节 |
|---|---|---|
| `SetParent`（挂到 `Progman`） | **实测**：owner 根本没设上，而窗口整片渲染成纯黑（`PrintWindow` 采样 `#000000` 100%）；跨进程调用本身也不受官方支持 | §13.6 |
| `WS_EX_LAYERED`（无原型验证时） | 与 WPF 渲染的交互有已知风险 | §13.7 |
| `WM_WINDOWPOSCHANGING` 取消最小化 | 未公开的内部约定，可能随系统更新失效 | §13.6 |
| `UpdateLayeredWindow` | 走了 `AllowsTransparency=True` 的老路 | §13.1 |
| `RegisterShellHookWindow` | 用于监听窗口创建/销毁，本项目无此需求 | — |
| 全局钩子（`SetWindowsHookEx`） | 需要注入其他进程，杀软敏感、可能触发 UAC/AV 告警。**别和 D.5 的 `SetWinEventHook` 混为一谈**：后者是订阅系统的事件通知，配合 `WINEVENT_OUTOFCONTEXT` 回调走本进程消息队列，不注入任何进程 | — |
| `SHChangeNotifyRegister` | 监听 shell 变更，`FileSystemWatcher` 已足够 | — |
