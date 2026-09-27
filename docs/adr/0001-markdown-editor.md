# ADR 0001：采用 Milkdown + WebView2CompositionControl 作为 Markdown 编辑内核

- 状态：实施中；阶段 A、阶段 B 已完成，阶段 C 图片待开发
- 日期：2026-09-26
- 决策范围：便笺正文编辑、Markdown 互操作、图片与附件、格式工具栏

## 1. 背景

LumiMemo 的正文必须同时满足：

1. 便笺内直接编辑渲染后的内容，不暴露 Markdown 标记；
2. 磁盘上的唯一正文格式仍是 Markdown；
3. 近期加入本地图片、表格、代码块、引用、链接、任务列表等格式；
4. 保留毛玻璃窗口、主题变量、自动保存、外部文件同步和多便笺窗口；
5. 不自动加载远程图片，不允许 Markdown 执行任意 HTML 或脚本。

当前 `MarkdownRichTextBox + MarkdownFlowDocumentConverter` 已证明原生 WPF 自制方案的维护成本过高：列表标记、内嵌复选框、选区、输入法、撤销栈和 Markdown 反向序列化互相耦合。继续增加图片、表格和代码块会形成一套自研文档编辑器，风险与产品目标不匹配。

## 2. 候选方案

| 方案 | 所见即所得 | 图片/表格 | Markdown 编辑成熟度 | WPF/主题 | 结论 |
|---|---:|---:|---:|---:|---|
| WPF `RichTextBox` + 自制转换器 | 是 | 需自制 | 低 | 最自然 | 停止扩展，迁移后删除 |
| Markdig + MdXaml | 否，主要是只读渲染 | 支持渲染 | 解析成熟，编辑缺失 | 原生 WPF | 不能作为编辑内核 |
| AvalonEdit + 预览 | 否，编辑区显示源码 | 可预览 | 源码编辑成熟 | 原生 WPF | 不符合产品交互 |
| WebView2 + Milkdown Crepe | 是 | 原生支持并可扩展 | 高，基于 ProseMirror/remark | 需要桥接 | **采用** |
| 商业 WPF 富文本控件 | 多数偏 RTF/DOCX | 支持 | Markdown 往返能力不一 | 原生 WPF | 许可证和格式模型不合适 |

选择 Milkdown 的原因：它以 Markdown 为输入输出格式，基于 ProseMirror 的结构化编辑模型，Crepe 已包含列表、待办、图片、代码块、链接工具和格式工具栏等能力。项目采用 MIT 许可证，仍在持续维护。参考：[Milkdown](https://github.com/Milkdown/milkdown)、[Crepe API](https://milkdown.dev/docs/api/crepe)。

Markdig 已经以 `1.3.2` 存在于 Infrastructure 层，继续用于非 UI 场景；MdXaml 只负责 Markdown → `FlowDocument`，没有编辑模型，因此不引入。参考：[Markdig](https://github.com/xoofx/markdig)、[MdXaml](https://github.com/whistyun/MdXaml)。

## 3. 依赖与工程结构

### .NET

- `Microsoft.Web.WebView2`：固定到实施时的稳定版，评估时为 `1.0.4191.47`；版本放进 `src/Directory.Packages.props`。
- 使用 `WebView2CompositionControl`，避免普通 `HwndHost` WebView2 的 WPF airspace 问题，使底部工具栏、焦点装饰和其他 WPF 元素可以正常叠放。官方说明：[WebView2 in WPF](https://learn.microsoft.com/en-us/microsoft-edge/webview2/platforms/wpf)。
- 继续使用现有 `Markdig 1.3.2`，不让 App 层直接承担 Markdown 语法分析。

### Web 编辑器

- `@milkdown/crepe`：固定到 `7.22.2`，使用 lockfile；评估时该包为 MIT。
- TypeScript + Vite 构建为静态 `index.html`、JS 和 CSS，产物随应用离线分发。
- 不从 CDN、npm 网站或其他远程地址加载运行时资源。

本机最小离线构建已经验证通过：Vite 转换 1093 个模块，发布资源约 `3.71MB / 179` 个文件，主 JS 约 `1.49MB`。`node_modules` 约 `91.4MB`，只属于开发与 CI，不进入安装包。默认 Crepe 构建会带出 KaTeX 字体和多种代码语言 chunk；阶段 A 先保证功能稳定，再按实际启用功能改用更细的 Milkdown 组合或调整动态资源复制，不能为了几 MB 重新自制编辑内核。

建议目录：

```text
editor/
├── package.json
├── package-lock.json
├── vite.config.ts
└── src/
    ├── editor.ts
    ├── protocol.ts
    ├── lumi-theme.css
    ├── underline-mark.ts
    └── image-node-view.ts

src/LumiMemo.App/Editor/
├── MarkdownEditorHost.xaml
├── MarkdownEditorHost.xaml.cs
├── EditorProtocol.cs
├── WebViewEnvironmentService.cs
└── EditorAssetManifest.cs
```

前端构建产物复制到应用输出目录的 `EditorAssets/`。正式 CI 与发布构建执行 `npm ci && npm run build`；普通 .NET 构建在产物缺失时给出明确错误，不从网络临时下载脚本。

## 4. 运行结构

```text
NoteViewModel.Content（Markdown）
        ⇅ 版本化 JSON 消息
MarkdownEditorHost（WPF）
        ⇅ chrome.webview.postMessage / PostWebMessageAsJson
Milkdown Crepe（WebView2CompositionControl）
        ⇅
ProseMirror 文档模型
```

### 共享 WebView2 环境

- 全进程只创建一个 `CoreWebView2Environment`，使用同一个 `%LOCALAPPDATA%/LumiMemo/WebView2` 用户数据目录。
- 每个可见便笺拥有一个编辑器控件；关闭便笺时立即释放控件和事件订阅。
- 首个编辑器延迟初始化，托盘和普通 WPF 窗口不等待浏览器进程启动。
- 多个控件共享用户数据目录与浏览器进程。官方说明共享 UDF 可以减少额外进程和资源开销：[WebView2 用户数据目录](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder)、[进程模型](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model)。

### 消息协议

只使用有类型、带协议版本和文档修订号的 JSON 消息：

```text
Web → WPF
ready
contentChanged { revision, markdown, composing }
selectionChanged { marks, blockType }
saveImage { requestId, mimeType, bytesBase64, alt }
openLink { href }
editorError { code }

WPF → Web
loadDocument { revision, markdown, imageMap, readOnly }
executeCommand { command }
imageSaved { requestId, markdownUrl, displayUrl }
setTheme { tokens }
setReadOnly { value }
focusEditor
```

- 不使用 `AddHostObjectToScript`，避免给页面暴露通用 .NET 对象。
- 不把 Markdown 拼进 `ExecuteScript` 字符串；统一使用 JSON 序列化和 `PostWebMessageAsJson`。
- 收到消息时检查固定来源、协议版本、消息大小、字段范围和当前文档修订号。
- 编辑器每次事务更新内存状态；消息可做 30–50ms 合并，但现有 500ms 自动保存仍由 `AutoSaveService` 负责。
- 输入法组合状态由浏览器的 composition 事件传给 `NoteViewModel.IsComposing`，组合结束后立即安排保存。

## 5. Markdown 能力与往返规则

第一批正式支持：

- 段落、软换行；
- H1–H3；
- 粗体、斜体、删除线、受控 `<u>` 下划线扩展、行内代码；
- 项目列表、编号列表、任务列表；
- 引用、水平线；
- 链接；
- 围栏代码块；
- GFM 表格；
- 本地图片及 alt/title。

Markdown 是可移植的持久化格式，保证**语义往返**，不承诺字节级往返。Milkdown 保存时允许规范化等价写法，例如列表符号、空格和表格对齐。Front Matter 继续由现有仓储层维护，不进入编辑器。

载入前由 Markdig 做兼容性检查：

- 支持的节点进入编辑器；
- 原始 HTML 除受控的 `<u>` 外不执行；
- 未支持的扩展语法不得静默丢失。首版遇到此类内容时进入受保护状态并提示用户，后续可增加“原样块”节点；
- 外部修改只有在用户继续编辑后才按编辑器格式规范化，不因单纯打开便笺重写文件。

## 6. 图片与附件

图片不能由 WebView2 直接写磁盘，必须经过宿主：

```text
粘贴/拖入图片
→ Milkdown 读取浏览器 File/Blob
→ 发送受大小限制的图片请求
→ WPF 校验 MIME、扩展名、字节数和像素尺寸
→ IAttachmentStore 原子写入 attachments/
→ 宿主计算相对于当前便笺文件的 Markdown 路径
→ 编辑器插入 image node
→ Content 更新并立即保存
```

实施前先补齐 `IAttachmentStore` 的生产实现。首版消息体允许 Base64，单文件限制 20MB；若实测复制大图造成明显峰值，再改为宿主拦截的本地上传请求，避免 Base64 额外内存。

显示图片时：

- Markdig 在宿主侧收集图片引用并解析安全路径；
- 只把确认位于附件目录内的本地文件映射到独立虚拟资源域；
- 编辑器收到 `Markdown URL → 安全显示 URL` 映射，磁盘 Markdown 始终保留相对路径；
- `http/https/data/file` 图片不自动请求，显示占位符；
- 图片加载失败不改写正文。

## 7. 主题与 WPF 工具栏

- WPF 仍拥有窗口、标题栏、底部工具栏和状态栏。
- 点击 WPF 工具栏按钮时发送 `executeCommand`，Milkdown 执行命令并恢复编辑选区。
- `selectionChanged` 返回当前粗体、列表、标题等状态，供 WPF 按钮显示选中态。
- `LumiTheme.xaml` 的语义 token 转为 CSS 变量：正文色、次要色、强调色、选区、代码背景、引用线、圆角和字号。
- WebView 背景透明，实际玻璃与便签底色仍由 WPF 窗口绘制。
- 主题切换只发送 token，不重建编辑器，不清空撤销栈。

## 8. 安全边界

- 编辑器只导航到固定的本地虚拟源；所有其他顶层导航、frame 导航和新窗口请求都取消。
- CSP 默认 `default-src 'none'`，仅允许打包的脚本、样式以及独立附件虚拟域的图片。
- 禁用默认脚本对话框、状态栏、密码自动填充和不需要的浏览器功能；发行版关闭 DevTools。
- 所有链接点击交给 WPF 按现有协议白名单处理。
- 所有 Web 消息验证来源和结构，宿主只提供窄命令，不提供文件系统通用代理。
- WebView2 使用普通用户权限运行。实施遵循微软的[安全指南](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)。

## 9. 发布与降级

- 使用 Evergreen WebView2 Runtime。Windows 11 包含该运行时，多数 Windows 10 设备也已安装；安装程序仍必须检测缺失情况并运行官方 bootstrapper。参考：[WebView2 分发](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)。
- 当前开发机已安装 WebView2 Runtime `153.0.4234.48`，Node `24.14.0`，可以直接进行验证。
- Runtime 缺失或编辑器进程崩溃时，保留最近一次 `NoteViewModel.Content`；显示可恢复错误并允许重试。迁移期可临时保留源码编辑降级入口，完成稳定验证后不保留两套长期编辑引擎。
- 监听 `ProcessFailed`；重建控件时从内存 Markdown 恢复，不从可能尚未保存的磁盘版本覆盖。

## 10. 实施门槛

先做隔离验证，不直接替换生产编辑器。验证必须覆盖：

1. 1、5、20 张同时可见便笺的启动时间、工作集、空闲 CPU；
2. 中文输入法连续组字、撤销/重做、跨段选区与粘贴；
3. WPF 工具栏点击后选区不丢失；
4. 100%、150%、200% DPI 与跨屏移动；
5. 毛玻璃背景、圆角裁剪、窗口置顶和 Win+D 恢复；
6. 本地图片粘贴、拖放、移动便笺后的相对路径；
7. 远程图片、危险链接、原始 HTML 与畸形消息被阻止；
8. WebView2 进程崩溃后不丢失尚未自动保存的正文。

建议通过标准：

- 首个编辑器可输入时间不超过 1 秒，后续窗口不超过 300ms；
- 20 张空闲便笺总空闲 CPU 低于 1%；
- 10 张可见便笺时总工作集不超过 300MB；
- 连续输入和滚动没有肉眼可见卡顿；
- 上述功能测试无数据丢失。

若资源门槛失败，先尝试共享环境、延迟初始化、关闭窗口立即释放、降低 CompositionControl 刷新负担；仍不满足时再重新评估产品允许的同时可见便笺数量。不能退回继续扩展自制 `FlowDocument` 编辑器。

## 11. 分阶段迁移

### 阶段 A：验证外壳

加入 WebView2 包和独立前端工程，实现透明背景、共享环境、消息协议、加载/导出 Markdown，并完成性能记录。

### 阶段 B：基础功能对等

接通正文、自动保存、输入法、撤销重做、现有六个工具栏按钮和外部文件重新载入。达到对等后删除 `MarkdownRichTextBox` 与 `MarkdownFlowDocumentConverter`。

### 阶段 C：图片

实现 `IAttachmentStore`、粘贴/拖放、图片节点、路径验证和附件虚拟域。图片写入成功后再修改 Markdown，失败时正文不出现坏引用。

### 阶段 D：更多格式

加入标题、引用、代码块、链接、水平线、表格及相应工具栏/菜单；补齐 Markdown 兼容性矩阵。

### 阶段 E：发布

加入 Runtime 检测、安装器 bootstrapper、第三方许可证、前端供应链锁定和端到端回归测试。

## 12. 明确放弃的旧约束

本决策替代旧计划中的以下内容：

- “编辑区显示原始 Markdown，预览模式单独渲染”；
- “不用 WebView2”；
- “继续自写 Markdown → FlowDocument 编辑/渲染器”；
- “任意 Markdown 写法必须保持字节级最小 diff”。

仍然保留：Markdown 文件是唯一正文数据源、程序离线运行、远程图片默认不加载、附件路径受限、外部编辑器可读写以及自动保存。

## 13. 实施记录

2026-09-27 已完成阶段 A、B：

- 加入固定版本的 WebView2、Milkdown Crepe、Vite 工程和 lockfile；编辑器资源离线随应用分发；
- 使用共享 `CoreWebView2Environment` 与 `WebView2CompositionControl`；本地虚拟源、CSP、导航拦截和版本化 JSON 消息已启用；
- `NoteViewModel.Content` 双向同步、自动保存、外部重载、中文输入法组合状态已接通；
- 粗体、斜体、下划线、删除线、项目列表和可点击待办列表已接到 WPF 工具栏；
- 下划线以受控 `<u>…</u>` 扩展往返，不在便笺正文中显示源码；
- 原 `MarkdownRichTextBox`、`MarkdownFlowDocumentConverter` 及对应测试已删除；
- 本机前端构建、WPF 构建与 685 项 .NET 测试通过，应用恢复一张便笺后未记录新的 UI 异常。

阶段 C 将在当前桥接协议上增加附件落盘、图片粘贴/拖放与附件虚拟域。
