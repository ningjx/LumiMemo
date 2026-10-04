# Phase 4：旧内核退役（设计稿草案）

> 状态：**已评审通过；M1 实施完成**（2026-10-04）——四套测试全绿、全量构建 0 警告，待用户抽查主程序后提交。
> 上游：`custom-renderer-framework.md`（换内核动机）、`phase2-editing-layer-design.md` §0.3、
> `phase3-beyond-native-design.md` §0.1（「旧内核以其交互契约为对等基线，Phase 4 退役」）。

---

## 0. 范围

**目标**：把旧内核（RichEditBox 版富文本编辑器）整条从代码库移除，主程序只保留 LumiText 新内核；
清掉随退役作废的命名/注释/文档说法；补上一条早已过时、一直红着的存储测试。

**现状盘点（2026-10-04，全仓按类型名扫描 + 构建引用图）**

1. 主程序**早已切换**：`MainWindow` 只 new `LumiEditor`，经 `LumiEditorDocument` 薄适配器接
   `IRichTextDocument`（MainWindow.xaml.cs 私有嵌套类，四成员纯转发）。旧内核**没有任何实例化点**。
2. `MainWindow.xaml` 里只有 `<Grid x:Name="EditorHost" />`，编辑器由代码挂上去——**没有**旧内核的 XAML 残留。
3. 应用里**没有**内核开关 / 对照模式 / 灰度 flag（当初的切换是直接切，没有留存开关）。
4. WPF 前端（`LumiMemo.App` 及其测试）已在 `c3599f6` 删除——**最初设想的「删 WPF」这一步已经完成**，
   本次只剩 WinUI 侧的旧内核。

**不做**：一切新功能；M5 文字流动动画（Phase 3 遗留，另行安排）；界面毛玻璃 / 深色模式轮次；
UIA 无障碍（维持「留接口不实现」）。

---

## 1. 删除对象与证据

| 文件 | 体量 | 谁引用它 | 处置 |
|---|---|---|---|
| `LumiMemo.WinUI/Controls/RichEditorHost.cs` | 39 KB | 无生产引用（仅旧内核内部、其它文件的**注释**） | **删** |
| `LumiMemo.WinUI/Controls/ImageAdorner.cs` | 20 KB | 仅 `RichEditorHost` | **删** |
| `LumiMemo.WinUI/Controls/AdornerGeometry.cs` | 11 KB | 仅 `ImageAdorner` + 其单测（几何契约已由 `LumiText.Core.ImageResizeGeometry` 取代，Phase 3 M4） | **删** |
| `LumiMemo.WinUI/Controls/RtfPict.cs` | 8.8 KB | 仅旧内核 + 其单测 | **删**（见 D1） |
| `LumiMemo.WinUI/Controls/CursorShapes.cs` | 0.9 KB | 仅旧内核（`LumiText.WinUI` 已有一份同实现，Phase 3 M4 移植） | **删** |
| `LumiMemo.WinUI.Tests/Controls/AdornerGeometryTests.cs` | 22 条 | 测被删代码 | **删** |
| `LumiMemo.WinUI.Tests/Controls/RtfPictTests.cs` | 16 条 | 测被删代码 | **删** |

**保留**（与新内核同命，不动）：
`IRichTextDocument`（VM 缝，四个成员的窄接口）、`LumiEditorDocument` 适配器、`FakeRichDocument`（VM 测试替身）、
`SnippetTextBlock` / `ScrollBarReveal` / `NoteColorPalette` / `NoteColorBrushConverter`（列表页、设置页、回收站页在用）、
`LumiText.Demo`（开发对照台，Phase 1/2 的探针与并排对照都靠它）。

**随旧内核消失的能力**：RTF 剪贴板里**内嵌图片**（`\pict`）的解析/缩放/提取（`RtfPict`）。
不进遗留清单——新内核的剪贴板通路本来就不消费它（`RtfProjection` 对图片落 `[图片]` 占位，Phase 2 设计 §7.1 的既定范围），
将来若真要做「RTF 粘贴带图」，从 git 历史取回这份实现即可。

---

## 2. 决策点（默认值已给出，评审时如有异议直接说）

| # | 问题 | 默认 |
|---|---|---|
| D1 | `RtfPict` 随删还是留着备用 | **随删**（无人引用；需要时取 git 历史） |
| D2 | Demo 的 `CompareWindow`（左 RichEditBox / 右新内核并排） | **保留**，只把注释里「现产品同款 RichEditBox」的说法改掉（现产品已不用 RichEditBox） |
| D3 | `Note.RichTextContent`（字段名的字面意思已是历史：现在装 v2 JSON 字节）与 `IRichTextDocument` 的命名 | **不改名**，只改注释。字段名进 `.lumi` 存储契约，改名等于升格式版本，收益不抵成本 |
| D4 | 注释里「移植自旧内核 `X.cs`」这类带文件路径的历史引用 | **改写**为不带路径的表述（保留「契约来源」语义，去掉会指空的引用） |

---

## 3. 实施（M1，本轮一段做完）

1. **删除** §1 表里的 5 个源文件 + 2 个测试文件；
2. **注释/文档清理**：`IRichTextDocument`（改「旧内核产 RTF」的说法）、`Note.cs`（同上）、
   `LumiEditor` / `WinClipboard` / `ImageResizeGeometry` / `MoveImageCommands` / `LumiText.WinUI.CursorShapes`
   的「移植自…」注释、Demo `CompareWindow` 注释、Phase 3 设计稿 §0.1 该行标注「已退役」；
3. **修过时测试**（独立于退役，但必须一起清掉，否则验收口径说不清）：
   `LumiNoteStorageTests` 里 `文件名与id不符_以id为准且不改名`、`同id重复_取路径序第一个`
   写的是 **v1 文件**却期望加载成功——而加载端自 `0687fde`（Phase 2 M7）起明确
   「v1（RTF 权威）不迁移、直接跳过」。是**测试没跟上决策**（存储行为是有意为之），
   修测试：`BuildNoteJson` 的版本与字段名对齐 v2（`Document` 取代 `Rtf`），
   `id为空_跳过` 也一并用当前版本，免得它「因为版本错而被跳过」碰巧通过；
4. **验证**：四个测试工程全绿 + 全量构建 0 警告 + 主程序用户抽查
   （新建/编辑/标题/背景/复选框/插图/保存/重开/列表/回收站）。

**预期数量变化**：`LumiMemo.WinUI.Tests` 77 → 39 条；其余三套不变
（Core 266、LumiMemo.Core 187、Integration 133）。

---

## 4. 风险与验证

| 风险 | 应对 |
|---|---|
| 误删仍被反射/XAML 引用的成员 | 已按类型名全仓扫描（含 .xaml）无生产引用；删除后**全量构建**兜底（编译不过就找回来） |
| 删掉的东西别人（将来的需求）还要用 | 只删「新内核已有对等能力」的部分；`RtfPict` 这类唯一能力已在 §1 末写明 |
| 视觉/交互回归 | 退役本身不改任何渲染与交互代码路径；用户抽查一遍主程序即可 |

**验收**：全量构建 0 警告 + 四套测试全绿 + 用户主程序抽查通过 → 旧内核退役完成，
Phase 3 的 M5/M7 回到待办（另有 Phase 4 的「切换收尾」——本稿完成后，Phase 4 只剩 UI 毛玻璃那一轮）。

> 「UI 毛玻璃那一轮」= 2026-09-19 定下的视觉总收口（深色模式 + 配色在玻璃下重定 + chrome 外观统一），
> **玻璃本身早已落地**（`DesktopAcrylicController`，见 `custom-renderer-framework.md` 的硬约束）。
> 该轮的剩余范围、图标与动效方案已成稿：**`ui-theme-round-design.md`**。

---

## 5. 实施结果（M1，2026-10-04）

- **删除**：`RichEditorHost.cs` / `ImageAdorner.cs` / `AdornerGeometry.cs` / `RtfPict.cs` /
  （应用版）`CursorShapes.cs` + `AdornerGeometryTests.cs`（22 条）/ `RtfPictTests.cs`（16 条）。
- **说法清理**（去指空引用，不改语义）：`IRichTextDocument`、`Note.cs` 的「(RTF)」、
  `ImageResizeGeometry`、`MoveImageCommands`、`LumiEditor`、`WinClipboard`、
  `LumiText.WinUI.CursorShapes`、Demo `CompareWindow.xaml(.cs)`；
  Phase 3 设计稿 §0.1 该行标注「已于 Phase 4 退役」。
  Phase 1/2 的 RESULTS 与 phase2 设计稿属历史记录，不回改。
- **过时测试修复**（§3.3）：`LumiNoteStorageTests.BuildNoteJson` 对齐 v2（`Document` 取代 `Rtf`），
  凡期望「正常加载」的用例改用当前版本——「文件名与 id 不符」「同 id 重复」两条不再被
  v1 跳过误伤，「空 id」也一并用当前版本，免得它「因为版本错而被跳过」碰巧通过。
- **验证**：`dotnet build src/LumiMemo.sln` **0 警告 0 错误**；
  四套测试 **LumiText.Core 266 / LumiMemo.WinUI 39（77−38）/ LumiMemo.Core 187 /
  Integration 133** 全绿（Integration 此前 2 条红，见上）。
