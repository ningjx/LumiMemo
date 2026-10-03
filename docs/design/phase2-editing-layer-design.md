# Phase 2 详细设计：编辑层（光标/选区/命令/撤销/IME/剪贴板/自动保存）

- 状态：**M0 已完成（2026-10-03），M1 待开工**——
  O1–O4 已拍板；CsWin32 TSF 覆盖实测通过（RESULTS-Phase2 §1，R-TSF-1 解除）；
  浮动锚定字符级化已落地（f9f9937）；§15 实施纪律已沉淀（M0 探针教训）。
  详见 [float-anchor-charlevel-patch.md](float-anchor-charlevel-patch.md)
- 前置：
  [custom-renderer-framework.md](custom-renderer-framework.md)（框架稿，已确认）、
  [phase0-spike-design.md](phase0-spike-design.md)（S1/S2 已验收，S3 延后至本期 M0）、
  [phase1-readonly-renderer-design.md](phase1-readonly-renderer-design.md)（已验收，总判定 Go；
  §6.3 块级锚定已被字符级锚定取代，见 v4 修订注记）
- 范围红线：本文档只做方案，不修改任何现有代码；开工时按里程碑逐段实施
- 原则：Phase 1 的设计红线原样生效——Core 零 UI 依赖、宿主对上层只暴露窄接口、
  渲染只走 Composition surface 保毛玻璃、新文档模型为权威格式

---

## 0. Phase 2 目标与验收口径

框架稿原文：「光标/选区、键入/删除、IME、撤销重做、剪贴板、自动保存接通。
达到与现有 RichEditBox **功能对等**（粗斜删下划、分点、伪待办、内嵌图片）。」

### 0.1 功能对等基线（现产品 RichEditBox 真实能力，均有代码佐证）

下表即 Phase 2 的「及格线」——新内核必须覆盖；未列入的能力 Phase 2 **不做**。

| 能力 | 现产品实现 | 出处 |
|---|---|---|
| 粗体/斜体/下划线/删除线 | `ITextCharacterFormat` 切换，工具栏 + `Ctrl+B/I/U` | `RichEditorHost.cs:203–228`，`MainWindow.xaml.cs:250–256` |
| 分点（bullet） | `ITextParagraphFormat.ListType = MarkerType.Bullet/None` | `RichEditorHost.cs:365–380` |
| 伪待办（☐/☑） | 行首写字符 + 整行 `ForegroundColor`；Enter 续行/退出；点行首切换 | `RichEditorHost.cs:417–456`、`459–489`、`492–521`、`523–549` |
| 内嵌图片 | 粘贴/拖放时 `ITextRange.InsertImage`，长边 280px；`ImageAdorner` 缩放改写 `\picwgoal/\pichgoal` | `RichEditorHost.cs:922–982`、`616–701`、`826–885` |
| 撤销/重做 | RichEditBox 原生 + `BeginUndoGroup/EndUndoGroup` 合并 | `RichEditorHost.cs` 5 处 |
| IME | 原生 `TextCompositionStarted/Ended`；组字期抑制 `UserEdited` 连带抑制自动保存；组字期不拦截 Enter | `RichEditorHost.cs:95–96`、`270–309`、`311–338` |
| 剪贴板 | 自定义仅一处：剪贴板**只含 Bitmap 不含 Text/Rtf** 时拦截走插图；其余走原生 | `RichEditorHost.cs:922–945` |
| 拖放插图 | `StorageItems` 与 `Bitmap` 两路，落点插入 | `RichEditorHost.cs:826–918` |
| 自动保存 | `UserEdited` → `AutoSaveService` 500ms 去抖（300–800 可配）；关窗 `SaveNowAsync`、退出 `FlushAllAsync` | `NoteViewModel.cs:177/241–252`、`AutoSaveService.cs:27/33–46`、`AppSettings.cs:90–95` |
| 字号/颜色工具栏 | 无（现产品未暴露，只有待办勾选整行写颜色） | — |
| 超链接 | 无 | — |

### 0.2 做

1. **文档模型增量结构**：让「整块替换」的不可变模型支持编辑期的差异更新与增量重排。
2. **光标与选区**：状态机 + 字符级命中测试 + 键盘/鼠标操作 + 渲染通路。
3. **命令系统与撤销/重做**：键入/删除/样式/块结构命令；撤销栈替代 RichEditBox 原生 Undo。
4. **IME / TSF**：`ITextStoreACP2` 最小闭环（S3 spike 纳入 M0），组字期行为对齐现产品。
5. **剪贴板互通**：读 RTF/位图/纯文本；写 RTF/纯文本/位图；现产品「纯 Bitmap 拦截插图」语义平移。
6. **内嵌图片**：行内 `ICanvasTextInlineObject`，与现产品 `InsertImage` 对齐。
7. **`.lumi v2` 存储层落地** + **自动保存接通**：`Note.RichTextContent` 语义演化，VM 层改动最小化。

### 0.3 不做（明确排除，防范围蔓延）

- H1–H3 标题、真复选框待办（矢量框 + 勾选动画）、行/段落背景、浮动环绕、排版动画——**Phase 3**；
  Phase 2 的待办保持「行首 ☐/☑ 字符」的伪待办形态以维持功能对等。
- 浮动图片的锚定编辑（拖动改锚点）——Phase 3；Phase 2 图片沿用 Phase 1 已定的锚定语义只读。
- UIA 无障碍——沿用已确认决策 4：留接口不实现。
- 灰度切换与 RichEditBox 退役——Phase 4。
- 拖放插图——现产品有，但属增强交互；Phase 2 只保「粘贴插图」这一条对等路径，拖放排入 Phase 3。
- 图片缩放（`ImageAdorner` 等价物）——Phase 3；Phase 2 图片尺寸沿用模型给定值。

---

## 1. 现状盘点

### 1.1 现产品 `IRichTextDocument` 窄接口（VM 层对接契约）

定义于 `LumiMemo.WinUI/Controls/IRichTextDocument.cs:8`，**只有 4 个成员**：

```csharp
public interface IRichTextDocument
{
    string PlainText { get; }
    byte[] SaveRtf();
    Task LoadAsync(byte[] rtf);
    event EventHandler? UserEdited;
}
```

`NoteViewModel` 与编辑区的全部交互（`NoteViewModel.cs:152–252`）：

- `AttachDocument(document)` 订阅 `UserEdited`；
- `ApplyUserEdit(plainText)` 把纯文本写进 `Note.Content`，触发 `AutoSaveService.ScheduleSave(Id, PersistAsync)`（500ms 去抖）；
- `PersistAsync()` 调 `_document.SaveRtf()` 拿到 `byte[]` 塞进 `Note.RichTextContent`（Core 层 `Note` 的权威内容字段，`byte[]` 类型）；
- `TryPersistOnCloseAsync()` 先 `CancelScheduledSave`，等 `_saving` 完成后 `PersistAsync()`。

**关键观察**：VM 只跟「纯文本投影 + 权威字节 + 用户改过事件」三样东西打交道。
权威字节的具体格式（RTF 还是新模型 JSON）VM 不解析、不感知——`Note.RichTextContent` 是不透明 `byte[]`。
**这是 Phase 2 换内核时 VM 零改动的支点**（见 §8.2）。

### 1.2 LumiText 现状公开 API（Phase 1 已交付，编辑层要踩的肩膀）

**Core 层文档模型**（不可变 record，零 UI 依赖）：

- `Document(Blocks, Images?)`，`SchemaVersion = 1`，`GetFloats()` 从 `ImageBlock` 派生浮动输入；
- `Block` 多态：`ParagraphBlock(Runs, Style?, SpaceAfter)` / `HeadingBlock(Runs, Level, SpaceAfter)` /
  `TodoBlock(Runs, Checked, SpaceAfter, LeftIndent=26f)` / `DividerBlock` / `ImageBlock(ImageId, Width, Height, Float?)`；
- `TextRun(Text, InlineStyle?)`；`InlineStyle(Bold, Italic, Strikethrough, Underline, Color?, FontSizeRatio?)`；
- `Color32(A, R, G, B)`，JSON 契约 `#AARRGGBB`；
- 注释明示：编辑期增量修改走「整块替换」，**差异结构留待 Phase 2**（`Document.cs:7`）。

**Core 层排版**：

- `FlowLayoutEngine(ITextMeasurer)`；`Layout(Document, contentWidth) → LayoutResult`；
- `LayoutResult : IDisposable`：`Lines / Floats / TotalHeight / Blocks?`、
  `FloatAt(x, y)`、`HitTest(x, y) → HitTestResult`；
- `HitTestResult(Found, BlockIndex, CharIndex)`——**只到行首字符粒度**（`LayoutResult.cs:5` 注释明示
  「Phase 2 引入字符级」），这是本期 §4.2 的改造入口；
- `PlacedLine(BlockIndex, CharStart, CharCount, X, Y, Width, Height, Baseline, Batch, LineOffsetY, Kind, IsBlockStart)`，
  `Kind ∈ {Text, TodoText, Divider, ImagePlaceholder}`——为编辑层分派留了位。

**WinUI 层渲染**：

- `FlowDocumentRenderer(ITextMeasurer)`：`UpdateLayout(...) → LayoutResult`、
  `Render(session, layout, viewport, textColor, debugOverlay)`；
- `VirtualizedTextSurface`：`RenderViewport(session, viewport, dpi)`、`SetMetrics`、`OnScroll`、`Invalidate`；
- `DocumentImageStore`（图片位图字典）。

**WinUI 层宿主**：

- `LumiDocumentView : Grid`（组合 `ScrollViewer + VirtualizedTextSurface + FlowDocumentRenderer + DocumentImageStore`）；
- 公开 API：`SetDocument(Document)`、`InvalidateSurface()`、`ChangeView(...)`、`LayoutStatsChanged`、`DebugOverlay`；
- 注释明示「不响应任何编辑输入；指针只用于滚动」（`LumiDocumentView.cs:15–18`）——
  **本期不改动这个只读宿主**，编辑宿主 `LumiEditor` 另建（§2、§9）。

### 1.3 已查证的外部 API 事实（详见 §11 查证记录）

- Win2D `CanvasTextLayout`：`HitTest(...)`（6 重载，支持 `isTrailingHit`）、
  `GetCaretPosition(int, bool)`、`GetCharacterRegions(int, int) → CanvasTextLayoutRegion[]`
  （`CharacterIndex / CharacterCount / LayoutBounds`）——光标/选区的命中与几何全部就绪；
- TSF `ITextStoreACP2`（Windows 8+）：`AdviseSink / RequestLock / GetText / SetText /
  InsertTextAtSelection / GetSelection / SetSelection / GetTextExt / GetScreenExt / GetACPFromPoint` 等，
  `ITextStoreACPSink.OnLockGranted / OnTextChange / OnSelectionChange / OnLayoutChange`；
  候选窗定位走 `GetTextExt`（屏幕坐标 RECT）；
- TSF 初始化链：`CoCreateInstance(CLSID_TF_ThreadMgr) → ITfThreadMgr.Activate →
  CreateDocumentMgr → ITfDocumentMgr.CreateContext(ITextStoreACP2) → Push`；
- WinUI 3 剪贴板：`Windows.ApplicationModel.DataTransfer.Clipboard`（WinRT，桌面可用），
  `DataPackage.SetText / SetBitmap / SetData(formatId, ...)`；RTF 用字符串格式 ID `"Rich Text Format"`；
- WinUI 3 自定义光标：`ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam/Hand/Arrow)`；
- WinUI 3 键盘：`UIElement.KeyDown/PreviewKeyDown` + `InputKeyboardSource.GetKeyStateForCurrentThread`
  （`CoreWindow.GetKeyState` 在 WinUI 3 返回 null，已弃用）。

---

## 2. 工程结构（Phase 2 完成后）

```text
src/LumiText/
├── LumiText.Core/
│   ├── Documents/
│   │   ├── Document.cs                    ← 沿用；增加编辑期差异结构（§3）
│   │   ├── Editing/                       ← 新：编辑层差异结构与操作
│   │   │   ├── TextPosition.cs            ← 新：文档内绝对位置（BlockIndex + CharIndex）
│   │   │   ├── TextRange.cs               ← 新：选区（Anchor + Active）
│   │   │   ├── IEditCommand.cs            ← 新：命令抽象（Do/Undo 成对）
│   │   │   └── Commands/                  ← 新：InsertText/DeleteRange/ApplyStyle/SplitBlock/MergeBlock/...
│   │   └── Blocks/                        ← 沿用；TodoBlock 增「伪待办」字符前缀投影逻辑（§3.4）
│   ├── Layout/
│   │   ├── FlowLayoutEngine.cs            ← 沿用；增「增量重排」入口（§3.3）
│   │   ├── HitTestResult.cs               ← 改造：到字符级（§4.2）
│   │   └── CaretGeometry.cs               ← 新：光标几何（位置/高度/倾角）
│   └── Editing/                           ← 新：Core 侧编辑内核（零 UI 依赖，可无头测试）
│       ├── EditorCore.cs                  ← 新：状态机 + 命令总线 + 撤销栈
│       ├── SelectionState.cs              ← 新：选区状态
│       ├── ClipboardData.cs               ← 新：剪贴板数据抽象（RTF/纯文本/位图三元组）
│       └── UndoStack.cs                   ← 新
├── LumiText.WinUI/
│   ├── Editing/
│   │   ├── TsfTextStore.cs                ← 新：ITextStoreACP2 实现（§6）
│   │   ├── TsfManager.cs                  ← 新：ThreadMgr/DocumentMgr 生命周期
│   │   ├── WinClipboard.cs                ← 新：Clipboard 互通适配（§7）
│   │   └── CaretSprite.cs                 ← 新：光标 Composition visual
│   ├── Rendering/
│   │   ├── SelectionRenderer.cs           ← 新：选区高亮层
│   │   └── CompositionTextSurface.cs      ← 沿用；增 SelectionRegion 通道
│   └── Controls/
│       ├── LumiDocumentView.cs            ← 沿用（只读，不动）
│       └── LumiEditor.cs                  ← 新：编辑宿主控件（§9）
└── LumiText.Core.Tests/
    ├── Editing/                           ← 新：命令/撤销/选区/伪待办的单测（无头）
    └── Layout/HitTestCharLevelTests.cs    ← 新
```

设计红线沿用：Core 零 UI 依赖（编辑内核可无头测试）；`LumiText.WinUI` 才碰
TSF/剪贴板/Composition；主程序只经窄接口对接。

---

## 3. 文档模型增量（编辑期的差异结构）

### 3.1 为什么需要差异结构

Phase 1 定稿的 `Document` 是**不可变 record 全家桶**，注释明示「编辑期增量修改走整块替换，
差异结构留待 Phase 2」。如果每次键入都重建整棵 `Document` + 全量重排，
3000 字符文档在 M3 实测 7.86ms 的整篇重排会落到每次按键上——不可接受。

### 3.2 编辑操作与文档演化

**核心决定：编辑期保持「不可变 Document」不变，但在其上加一层「会话态」**：

```text
EditorCore（会话，可变）
├── CurrentDocument: Document        ← 当前权威快照（不可变）
├── Selection: TextRange              ← 当前选区
├── UndoStack                         ← 命令历史
└── ApplyCommand(IEditCommand)
      → Document 演化为新 Document（record 的 with 表达式，结构共享）
      → 受影响的 BlockIndex 区间 → 增量重排请求
      → UndoStack.Push(command.Inverse)
```

`Document` 是 record，`with { Blocks = newBlocks }` 只替换 Blocks 列表，
未受影响的块 record 引用不变（结构共享，无深拷贝）。这与 Phase 1 的「整块替换」
语义一脉相承——只是每次命令触发一次替换，而不是整篇重建。

### 3.3 增量重排

`FlowLayoutEngine.Layout` 保持「整篇排版」签名不变（这是 Phase 1 冻结的资产）。
编辑期的增量重排走**调度层**而不是引擎层：

- 每次 `ApplyCommand` 后记录 `dirtyRange: (firstAffectedBlockIndex, lastAffectedBlockIndex)`；
- 重排策略（**首版简化**）：
  - 纯文本键入/删除（单块内）：只重排该块及其后续（因为块高变化会推挤后续块 Y）；
  - 块结构变更（Enter 分块、Backspace 合块、样式切换）：整篇重排；
  - 浮动锚点变更：整篇重排（沿用 Phase 1 的两遍锚定排版）。
- 视口失效：渲染层只 `Invalidate` dirty 区间对应的 Y 范围，
  `VirtualizedTextSurface` 区域重绘（API 已支持 `updateRect`）。

**性能预算**：单块重排成本 ≈ 0.29ms/400 字（M3 实测），单块内键入的增量重排
远在 8ms 帧预算内。整篇重排（块结构变更）沿用 M3 数据 22.45ms/1 万字——
Enter 分块这种低频操作可接受。

### 3.4 伪待办的模型表达（功能对等的权宜）

现产品待办 = 行首 `☐ `/`☑ ` 字符 + 整行颜色。Phase 2 **保持这个形态**（功能对等），
但模型层做一层投影：

- `TodoBlock.Checked` 仍是模型的权威布尔；
- 序列化到 v2 JSON 时写 `{"type":"todo","checked":bool,...}`（Phase 1 已定 schema）；
- **编辑期的键入行为**：用户在 TodoBlock 行首敲 `☐ ` 两字符时，编辑器识别并转换为
  `TodoBlock`（类似 Markdown 自动转换）；反向地，`TodoBlock` 的纯文本投影
  （`PlainText`，供 `Note.Content` / 搜索 / 字数）**带 `☐ `/`☑ ` 前缀**，
  与现产品 `Note.Content` 的既有形态兼容，VM 层无感。

这是一个**有意的过渡形态**：Phase 3 引入真复选框时，模型不变，只改渲染与交互。

---

## 4. 光标与选区

### 4.1 状态机

```text
SelectionState
├── Collapsed(TextPosition caret)              ← 光标态
├── Range(TextRange selection)                  ← 选区态
└── Composition(TextRange composingRange)       ← IME 组字态（§6 叠加）
```

迁移：

- 键盘字符输入：Collapsed → 替换选区（若 Range）→ 插入 → Collapsed；
- `Shift+方向键`：Collapsed → Range（锚定原 caret，active 随方向移动）；
- 鼠标按下：Collapsed 落点；拖动 → Range；松开 → 定格；
- 双击：选中词；三击：选中段；
- IME 组字开始：→ Composition；组字提交/取消：→ Collapsed。

### 4.2 字符级命中测试（`HitTest` 改造）

现状：`LayoutResult.HitTest(x, y)` 只到行首字符粒度（`HitTestResult(Found, BlockIndex, CharIndex)`，
`CharIndex` 实际是行首）。Phase 2 改造为**字符级**：

1. 沿用现有逻辑定位到行（`PlacedLine`）；
2. 取出该行的 `Batch`（`CanvasTextLayout`，经 `NativeLayout`）；
3. 以「点坐标 − 行盒原点 + `LineOffsetY`」换算到布局内坐标；
4. 调 `CanvasTextLayout.HitTest(x, y, out CanvasTextLayoutRegion region, out bool isTrailingHit)`：
   - `region.CharacterIndex` 给字符偏移；
   - `isTrailingHit` 决定光标落在该字符**前**还是**后**；
5. 换算回「块内字符偏移」：`CharStart + region.CharacterIndex + (isTrailingHit ? region.CharacterCount : 0)`；
6. `HitTestResult` 增 `CaretPosition: TextPosition`（BlockIndex + 块内 CharIndex）。

### 4.3 光标几何

`CaretGeometry` 由 `TextPosition` 正查：

1. 定位行盒（同 §4.2 步骤 1）；
2. `CanvasTextLayout.GetCaretPosition(blockCharIndex, isTrailingHit=false) → Vector2`；
3. 光标矩形 = `(行盒原点 + caretPos.X − LineOffsetY, 行盒顶, caretWidth≈1.5px, 行高)`；
4. 斜体段落光标倾角：取行内最大 `FontStyle`，斜体时光标 shear ≈ 12°（与 Word/RichEdit 观感对齐）。

### 4.4 选区几何

选区跨多行/多块：对每一块内子区间调 `GetCharacterRegions(start, count)` 拿到
`CanvasTextLayoutRegion[]`（每行一个矩形，自动处理跨行拆分与双向文本），
取 `LayoutBounds` 平移到文档坐标，得一组矩形——选区高亮 = 这些矩形的并集填充。

### 4.5 渲染通路

- **光标**：独立 `SpriteVisual`（`CaretSprite`），挂在 `VirtualizedTextSurface` 的宿主 visual 上，
  合成器线程做 500ms 闪烁（`CompositionScopedBatch` + `Opacity` 关键帧动画，不占 UI 线程）；
  颜色跟随前景墨色（`LumiDocumentView` 现状 `InkColor = 0xFF282030`，平移过来）；
  IME 组字期光标加粗到 2px（与系统行为对齐）。
- **选区高亮**：`SelectionRenderer` 在 `RenderViewport` 回调里先画选区矩形
  （半透明主题色，建议 `0x40` Alpha 的墨色）再画文本——选区在文字下层，不遮挡字形。
- **滚动跟随**：光标/选区变化时若 caret 不在视口内，`LumiEditor` 调 `ChangeView` 滚动到可见
  （与 RichEditBox 的 `ScrollIntoView` 行为对齐）。

---

## 5. 命令系统与撤销/重做

### 5.1 命令抽象

```csharp
public interface IEditCommand
{
    TextRange Apply(EditorState state);        // 返回命令执行后的新选区
    IEditCommand Inverse(EditorState state);   // 生成逆命令（撤销用）
    string Kind { get; }                       // 用于命令合并（见 5.2）
}
```

首版命令清单（与功能对等基线对齐）：

| 命令 | 触发 | 说明 |
|---|---|---|
| `InsertTextCommand(text)` | 键入、IME 提交、粘贴 | 替换选区后插入；可合并（见 5.2） |
| `DeleteRangeCommand(range)` | Backspace/Delete/剪切 | 记录被删内容供逆命令 |
| `ApplyInlineStyleCommand(style, range)` | 粗/斜/下划/删线工具栏 + 快捷键 | 切换 run 样式，记录旧样式 |
| `SplitBlockCommand(position)` | Enter | 段落一分为二；TodoBlock 行首 Enter 退前缀（§3.4 投影） |
| `MergeBlockCommand(blockIndex)` | 行首 Backspace | 与上一块合并 |
| `InsertImageCommand(imageId, size)` | 粘贴位图 | 行内图片（§8.3 `SetInlineObject`） |
| `ToggleBulletCommand(range)` | 分点工具栏 | 段落级 bullet 标记（模型增 `ParagraphBlock.IsBullet`） |
| `ToggleTodoCommand(range)` | 待办工具栏 | 段落 ↔ TodoBlock 互转 |

### 5.2 撤销栈

替代 RichEditBox 原生 Undo（新内核没有它，必须自建）：

- **结构**：`UndoStack` 双栈（undo / redo），容量上限 1000 条（RichEditBox 默认 100，便签场景放宽）；
- **命令合并**（对应现产品 `BeginUndoGroup/EndUndoGroup`）：
  - 连续 `InsertTextCommand` 且 `Kind` 相同、光标连续、无命令间选区跳变 → 合并为一条；
  - IME 一次组字提交 = 一条（无论候选窗里选了几次）；
  - `ToggleTodoSymbol` 等多步操作显式 `BeginGroup/EndGroup`（沿用现产品语义）；
- ** undo 后文档一致性**：每条命令执行后记录 `Document` 快照的弱引用做完整性校验
  （调试构建下断言；发布构建不存快照，靠逆命令保证）。

### 5.3 命令总线与事件

`EditorCore` 暴露给宿主：

- `event EventHandler? DocumentChanged`——文档内容变化（触发自动保存）；
- `event EventHandler? SelectionChanged`——选区变化（触发光标重绘、IME 候选窗跟随）；
- `event EventHandler? CompositionStarted/Ended`——IME 组字期边界（宿主据此抑制自动保存，
  对齐现产品 `RichEditorHost.OnTextChanged` 在 `_composing` 时 return 的语义，§0.1）。

---

## 6. IME / TSF

### 6.1 前置验证（已完成，2026-10-03）

**CsWin32 覆盖实测**（原 M0 第一个任务，独立完成，不依赖探针）：
`spikes/S3.TsfProbe/` 验证 `Microsoft.Windows.SDK.Win32Metadata` 投影
`msctf.h` / `textstor.h` 的 TSF 接口——**全部覆盖**，`ITextStoreACP2` 为
`[ComImport]` 经典 COM 互操作风格（CLR 自动生成 CCW，无需 ComWrappers）。
详见 `src/LumiText/LumiText.Demo/RESULTS-Phase2.md` §1。**R-TSF-1 已解除**。

**参考实现**：WPF 的 `System.Windows.Documents.TextStore`（.NET Framework 源码公开，
`TextStore.cs`）就是一个完整的 `ITextStoreACP` C# 实现，M4 产品化时结构可直接借鉴。

### 6.2 产品化接入（M4，与 `LumiEditor` 同期）

**决策（2026-10-03 调整）**：不设独立 TSF 探针里程碑。理由：
- 探针的两大验证点（候选窗定位、组字期事件）与 `LumiEditor` 的光标/选区/渲染
  深度耦合——探针里简化掉的「光标几何」「组字装饰」在产品化时仍要重写，
  探针等于白写一遍；
- CsWin32 覆盖实测已单独完成（最大的雷已排）；
- 剩余风险（WinUI 3 线程模型与 TSF STA 的兼容性）在 M4 实施时自然暴露，
  届时若不通再启动最小探针定位，比现在预防性写探针更省。

M4 验收口径（与现产品对齐）：中文 IME 组字、候选窗跟随光标、
组字期不触发自动保存（`EditorCore` 组字期不发 `DocumentChanged`）。
若 M4 实施卡壳，再回退启动最小探针（单文件、internal、能跑就行——见 §15 实施纪律）。

### 6.3 组字期行为（与现产品对齐）

| 事件 | 行为 |
|---|---|
| `ITextStoreACPSink.OnTextChange`（组字中） | 更新 `CompositionRange` 的显示文本（下划线/虚线装饰），**不**触发 `DocumentChanged`，**不**进撤销栈 |
| 组字提交 | 一条 `InsertTextCommand`，触发 `DocumentChanged`，进撤销栈（作为一条命令） |
| 组字取消 | 撤销 `CompositionRange` 显示，文档无变化 |
| 候选窗定位 | IME 调 `GetTextExt(acpStart, acpEnd, out RECT)`——返回**屏幕物理像素**坐标；从 caret 的 DIP 坐标经 `XamlRoot.RasterizationScale` 换算 |

### 6.3 ACP ↔ TextPosition 映射

TSF 用「应用字符位置」（ACP，文档级 int 偏移）寻址；`EditorCore` 用
`TextPosition(BlockIndex, CharIndex)`。维护一个**扁平化文本缓存**：

- 每次文档变化后重建 `string _flatText`（全文按块序拼接，块间 `\n`）+ 
  `int[] _blockStartAcp`（每块在 flatText 中的起始 ACP）；
- ACP → TextPosition：二分 `_blockStartAcp` 定位块，减基值得块内偏移；
- TextPosition → ACP：`_blockStartAcp[blockIndex] + charIndex`；
- 重建成本：3000 字符文档 ≈ 一次字符串拼接，微秒级，可接受。

**注意**：flatText 只是 TSF 寻址的视图，**不是权威文本**——权威是 `Document` 的块结构。
凡是 TSF 经 `GetText` 读到的都是 flatText，经 `SetText/InsertTextAtSelection` 写回的
都翻译成块结构命令。

### 6.4 组字装饰

`CompositionRange` 在渲染层画下划线（`CanvasDrawingSession.DrawLine`，虚线样式），
与系统 IME 观感对齐；不进 `PlacedLine` 体系，是渲染层的临时叠加。

---

## 7. 剪贴板互通

### 7.1 写入（复制/剪切）

`WinClipboard` 适配 `Windows.ApplicationModel.DataTransfer.Clipboard`：

```text
DataPackage
├── SetText(plainText)                          ← 必有
├── SetData("Rich Text Format", rtfBytes)       ← 有样式时
└── SetBitmap(RandomAccessStreamReference)      ← 选区含图片时
```

RTF 生成：新模型 → RTF 投影（**只用于剪贴板互通，不是权威格式**，框架稿红线 4）。
RTF 生成器走最小集：`\b \i \ul \strike` + 颜色表 + 图片 `\pict`，
足以与记事本/Word/浏览器互通。

### 7.2 读取（粘贴）

查询顺序（与现产品 `OnPaste` 语义对齐）：

1. `DataPackageView.Contains("Rich Text Format")` → 解析 RTF → 新模型命令序列；
2. `Contains(StandardDataFormats.Bitmap)` **且不含文本** → 插图命令（现产品的拦截分支）；
3. `Contains(StandardDataFormats.Text)` → 纯文本 `InsertTextCommand`。

RTF 解析：最小子集（同 7.1 的生成集），不支持的 tag 静默跳过；
外部 RTF（Word/浏览器）能读出粗斜删下划与图片即可，复杂结构降级为纯文本。

### 7.3 打包/解包差异

`Clipboard` 是 WinRT API，桌面应用（packaged 与 unpackaged）均可直接用，
无需 HWND 关联。WinUI 3 下需 `DispatcherQueue`（UI 线程调用）——`WinClipboard`
只在 UI 线程被调，自然满足。

---

## 8. 存储层落地与自动保存接通

### 8.1 `.lumi v2` 文件级变化

按 Phase 1 §3.3 已冻结契约：

- `Version: 1 → 2`；新增 `Document`（v2 正文 JSON）；`Rtf` 字段退役；
- 读取端：v1 文件**不做迁移**（2026-10-02 已拍板），直接跳过并记日志；
- 写入端：不再写 `Rtf`；`Text` 由 v2 投影生成（`PlainText`）；
- 容错矩阵（坏文件跳过、高版本跳过、id 为准）原样沿用 `LumiNoteStorage`。

### 8.2 `Note.RichTextContent` 语义演化（VM 零改动的关键）

`Note.RichTextContent` 是 Core 层的 `byte[]`（权威内容），注释写「RTF 正文」。
新内核下这个字段装的是 **v2 JSON 的 UTF-8 字节**——**类型不变、VM 不解析**，
只改注释。这是一条**刻意的兼容路径**：

- `IRichTextDocument.SaveRtf()` 改名 `SaveContent()`（语义：序列化权威格式），
  返回值从「RTF 字节」变为「v2 JSON 字节」；
- `LoadAsync(byte[])` 参数语义同步变为「权威格式字节」；
- `RichEditorHost`（旧内核）继续返回 RTF 字节，直到 Phase 4 退役；
- `LumiEditor`（新内核）返回 v2 JSON 字节；
- **VM 与 `Note` 全程无感**——这是框架稿红线 2「VM 不感知换内核」的兑现。

**接口改名是破坏性变更**：`IRichTextDocument` 在 `LumiMemo.WinUI` 工程内，
改名涉及 `RichEditorHost` / `FakeRichDocument` / `NoteViewModel` 三处调用点，
编译期全部抓到，风险可控。若决定保留 `SaveRtf` 名字（Phase 4 再改），
Phase 2 就用名字妥协换零 churn——**开放决策 O1，见 §13**。

### 8.3 自动保存触发链

```text
EditorCore.DocumentChanged
  → LumiEditor 转发为 IRichTextDocument.UserEdited
  → NoteViewModel.OnDocumentUserEdited（沿用现产品）
  → ApplyUserEdit(PlainText)          ← PlainText 由新模型投影（§3.4）
  → AutoSaveService.ScheduleSave(500ms 去抖)
  → PersistAsync → _document.SaveContent() → Note.RichTextContent
  → LumiNoteStorage.SaveAsync（写 .lumi v2）
```

**IME 组字期抑制**：`EditorCore` 在组字期不触发 `DocumentChanged`（§6.2），
自动保存自然不排程——与现产品 `OnTextChanged` 在 `_composing` 时 return 的语义对齐。

---

## 9. 宿主控件 `LumiEditor`

```csharp
public sealed class LumiEditor : Grid
{
    public void SetDocument(Document document);
    public Document GetDocument();                    // 当前权威快照
    public event EventHandler? UserEdited;            // = EditorCore.DocumentChanged（组字期已抑制）
    public string PlainText { get; }                  // 纯文本投影（§3.4）
    public byte[] SaveContent();                      // v2 JSON 字节
    public Task LoadAsync(byte[] content);
    public bool DebugOverlay { get; set; }
    public event Action<double>? LayoutStatsChanged;
}
```

- 组合：`ScrollViewer + VirtualizedTextSurface + FlowDocumentRenderer + DocumentImageStore`
  （沿用 `LumiDocumentView` 的骨架）+ `CaretSprite + SelectionRenderer + EditorCore +
  TsfManager + WinClipboard`；
- 输入路由：`KeyDown/PreviewKeyDown`（字符、方向、快捷键）、
  `PointerPressed/Moved/Released`（点选/拖拽）、`DoubleTapped/Tapped`（词/段选）、
  `ProtectedCursor` 悬停切换（IBeam 正文 / Hand 待办框，Phase 3 才到）；
- 背景全透明画刷（命中测试铁律，沿用 S2/Phase 1 结论）；
- **`LumiDocumentView` 保持只读不动**——它仍是 Phase 1 验收的回归参照；
  `LumiEditor` 与其并列，不继承（组合优于继承，且避免只读宿主被编辑能力污染）。

---

## 10. 测试与验收

### 10.1 Core 单测（无头，假字体沿用 Phase 1 设施）

- **命令与撤销**：每条 `IEditCommand` 的 Apply/Inverse 往返；命令合并规则；
  撤销栈容量；undo/redo 双栈语义；
- **选区状态机**：§4.1 的全部迁移路径（含 IME 组字叠加）；
- **字符级命中**：假字体下 `HitTest` 到字符偏移的正确性（含 `isTrailingHit` 两侧）；
- **ACP 映射**：`_blockStartAcp` 二分定位、块边界、空块、单块文档；
- **伪待办投影**：`TodoBlock.PlainText` 带前缀；行首敲 `☐ ` 自动转块；
- **RTF 投影**：新模型 → RTF 最小集的往返（粗斜删下划 + 图片 + 颜色）。

### 10.2 WinUI 侧验收（Demo 窗口，沿用 Phase 1 截图管线）

- **S3 探针**（M0）：中文 IME 组字、候选窗跟随光标、组字期不触发自动保存（三截图）；
- **编辑冒烟**：键入/删除/撤销/重做/选区/粗斜删下划/分点/伪待办/插图/粘贴，
  每个动作前后截图对照；
- **自动保存**：Demo 里挂一个假 `AutoSaveService`，断言组字期 0 次调度、
  提交后 1 次调度；
- **视觉回归**：与 `LumiDocumentView` 并排渲染同一文档，除光标/选区外像素一致。

### 10.3 Go / No-Go

- **Go**：Core 测试全绿 + S3 探针三条件满足 + 编辑冒烟全过 + 视觉回归一致
  → 编辑层冻结，进 Phase 3（超越原生）设计。
- **No-Go**：TSF 最小闭环跑不通（CsWin32 不覆盖且手写 vtable 也遇结构性障碍）
  → 回退方案 B（ITextHost），图片环绕需求按框架稿标记为放弃；
  其余单点失败（如 RTF 投影不全）按条件 Go 条款书面记录。

---

## 11. 查证记录（本方案依赖的外部事实，均有来源）

| # | 事实 | 来源 |
|---|---|---|
| V-C1 | `CanvasTextLayout.HitTest` 6 重载（含 `out CanvasTextLayoutRegion, out bool isTrailingHit`）；命中用 layout bounds 不含 draw bounds | Win2D 官方文档 `CanvasTextLayout.HitTest` 方法页（microsoft.github.io/Win2D/WinUI3） |
| V-C2 | `CanvasTextLayout.GetCaretPosition(int characterIndex, bool isTrailingHit)` 返回 `Vector2`；另有带 `out CanvasTextLayoutRegion` 重载 | 同上 `GetCaretPosition` 方法页 |
| V-C3 | `CanvasTextLayout.GetCharacterRegions(int, int) → CanvasTextLayoutRegion[]`；region 含 `CharacterIndex/CharacterCount/LayoutBounds` | 同上 `GetCharacterRegions` 方法页 + `CanvasTextLayoutRegion` 结构页 |
| V-T1 | `ITextStoreACP2` 接口方法表（Windows 8+）：`AdviseSink/RequestLock/GetText/SetText/GetSelection/SetSelection/InsertTextAtSelection/GetTextExt/GetScreenExt/GetACPFromPoint/GetEndACP/GetActiveView` 等 | Microsoft Learn `ITextStoreACP2 (textstor.h)` |
| V-T2 | `RequestLock` 经 `ITextStoreACPSink.OnLockGranted` 授权；锁仅在 `OnLockGranted` 调用期间有效；`TS_LF_READ/READWRITE/SYNC` | Microsoft Learn「Document Locks」 |
| V-T3 | TSF 初始化链：`CoCreateInstance(CLSID_TF_ThreadMgr) → ITfThreadMgr.Activate → CreateDocumentMgr → ITfDocumentMgr.CreateContext(ITextStoreACP2) → Push`；`CreateContext` 的 `punk` 接受 `ITextStoreACP` 或 `ITfContextOwnerCompositionSink` | Microsoft Learn `ITfThreadMgr` / `ITfDocumentMgr::CreateContext` |
| V-T4 | `GetTextExt(acpStart, acpEnd, out RECT)` 返回**屏幕坐标**下指定字符范围的包围盒——IME 候选窗定位的依据；调用方须持只读锁 | Microsoft Learn `ITextStoreACP::GetTextExt` |
| V-T5 | `GetScreenExt(vcView, out RECT)` 返回整个文档显示面的屏幕包围盒（候选窗不越过它的依据）；文档最小化时返回零矩形 | Microsoft Learn `ITextStoreACP::GetScreenExt` |
| V-T6 | TSF 示例存在于 `microsoft/Windows-classic-samples`（`Samples/Win7Samples/winui/input/tsf/`）；Windows 8 起另有 `Samples/IME`（SampleIME） | GitHub `microsoft/Windows-classic-samples` |
| V-T7 | WPF 自带 `System.Windows.Documents.TextStore`（.NET Framework 源码公开）就是一个完整的 C# `ITextStoreACP` 实现，可借鉴结构 | dotnetframework.org `TextStore.cs` |
| V-T8 | CsWin32 经 `Microsoft.Windows.SDK.Win32Metadata` 投影 Win32 API；`msctf.h`/`textstor.h` 的 TSF 接口是否在 metadata 覆盖范围内**未在公开资料中确认**——M0 第一天实测 | microsoft/CsWin32 README + Microsoft Learn「Call Win32 APIs from a C# Windows app」 |
| V-CL1 | `Windows.ApplicationModel.DataTransfer.Clipboard` 在 WinUI 3（桌面）可用；`DataPackage.SetText/SetBitmap/SetData(formatId, ...)`；`Clipboard.GetContent() → DataPackageView.Contains/GetTextAsync/GetBitmapAsync` | Microsoft Learn「Copy and paste in WinUI and UWP Apps」 |
| V-CL2 | RTF 的剪贴板格式 ID 是字符串 `"Rich Text Format"`（经 `RegisterClipboardFormat` 注册的标准名） | 业界惯例 + Xojo/TX Text Control 文档交叉印证 |
| V-CU1 | WinUI 3 自定义光标：`UIElement.ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.X)`；`InputSystemCursorShape` 含 `Arrow/IBeam/Hand/Wait/Cross/Size...` 等；`CoreWindow.PointerCursor` 已弃用 | Microsoft Learn `InputSystemCursor.Create` / `InputSystemCursorShape`；platform.uno `ProtectedCursor` 文档 |
| V-KB1 | WinUI 3 键盘：`CoreWindow.GetKeyState` 返回 null（弃用），替代为 `Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey)` | Microsoft Learn `CoreWindow.GetKeyState`（备注明确）+ `InputKeyboardSource` |

未验证、M0 第一天需要以最小代码确认的点：

- U-TSF1：CsWin32 对 `msctf.h`/`textstor.h` 的投影覆盖（V-T8）；
- U-TSF2：WinUI 3 窗口（packaged）激活 TSF 的 COM 单元模型要求（STA/MTA）——
  WPF `TextStore` 是 STA，WinUI 3 默认线程模型下的行为需实测；
- U-C1：Win2D `HitTest` 在跨 Band 行盒（一行被浮动切成多段）下的 region 归属——
  Phase 1 的按批分组绘制让一行可能对应多个 `CanvasTextLayout`，命中分派逻辑需实测。

---

## 12. 风险登记

| # | 风险 | 概率 | 应对 |
|---|---|---|---|
| R-TSF-1 | CsWin32 不覆盖 TSF 接口，需手写 COM vtable | ~~中~~ **已解除（2026-10-03）** | `spikes/S3.TsfProbe` 实测全部覆盖；`ITextStoreACP2` 为 `[ComImport]` 经典 COM 互操作，CLR 自动生成 CCW |
| R-TSF-2 | WinUI 3 线程模型与 TSF STA 假设冲突，候选窗不跟随或组字丢字符 | 中 | M4 产品化时自然暴露（§6.2 决策：不设独立探针）；若不通再启动最小探针定位（§15 纪律） |
| R-TSF-3 | ACP 扁平化缓存与块结构不同步（编辑后忘了重建 `_blockStartAcp`） | 中 | 缓存重建收进 `ApplyCommand` 的不变量；Core 单测覆盖 §10.1 |
| R-E1 | 增量重排边界算错（某次编辑后后续块 Y 没更新） | 中 | 首版保守策略：除单块键入外一律整篇重排（§3.3）；视觉回归兜底 |
| R-E2 | 撤销栈命令合并把「用户认为的两步」并成一步 | 低 | 合并规则严格（同 Kind + 光标连续 + 无选区跳变）；Enter/粘贴/IME 提交强制断合并 |
| R-E3 | 内嵌图片的 `ICanvasTextInlineObject` 实现比预期复杂（度量回调 + Draw 回调 + 设备资源生命周期） | 中 | Phase 1 §8.3 已查证 API 存在；M 序列里给独立里程碑，失败则内嵌图片降级为「浮动图片锚定到当前字符」（O4，2026-10-03 拍板）并书面记录 |
| R-P1 | 增量重排后 3000 字符键入帧长超 8ms | 低 | §3.3 预算 0.29ms/400 字；实测超标则退整篇重排 + 异步排版（先画旧帧后排新版） |
| R-S1 | `IRichTextDocument` 改名（`SaveRtf → SaveContent`）波及 `RichEditorHost` / `FakeRichDocument` / 测试 | 低 | 编译期全抓；或按 O1 决策保留名字 |

---

## 13. 开放决策（2026-10-03 已全部拍板）

| # | 问题 | 结论 |
|---|---|---|
| O1 | `IRichTextDocument.SaveRtf` 是否改名 `SaveContent` | ✅ **改名**（语义诚实，编译期抓全；让「权威格式不再是 RTF」在类型层面显形） |
| O2 | 伪待办的纯文本投影是否带 `☐ `/`☑ ` 前缀 | ✅ **带**（与现产品 `Note.Content` 形态一致，VM 无感） |
| O3 | 撤销栈容量 | ✅ **1000**（便签文档小，内存代价可忽略，撤销深度体验更好） |
| O4 | 内嵌图片失败的降级形态 | ✅ **降级为浮动图片锚定当前字符**（保功能对等底线；锚定语义随 2026-10-03 字符级补丁同步升级） |

---

## 14. 里程碑（建议顺序，每段独立可验收）

| 里程碑 | 内容 | 验收 |
|---|---|---|
| **M0** | **设计评审 + 前置验证**（已完成，2026-10-03）：O1–O4 拍板 + CsWin32 覆盖实测 + 浮动锚定字符级化 | 本设计稿 + RESULTS-Phase2 §1 + 锚定补丁（f9f9937） |
| **M1** | 文档模型增量结构（§3）+ 命令系统 + 撤销栈（§5）+ Core 单测 | `LumiText.Core.Tests` 编辑系列全绿 |
| **M2** | 字符级命中测试（§4.2）+ 光标几何（§4.3）+ 选区几何（§4.4） | 假字体单测 + Demo 可视化命中调试层 |
| **M3** | `LumiEditor` 骨架（§9）+ 键入/删除/选区/光标渲染（§4.5） | Demo 里可打字、可拉选区、光标闪烁 |
| **M4** | TSF 产品化接入（§6.2/6.3/6.4，含 `TsfTextStore` + `TsfManager` 落地 `LumiText.WinUI/Editing/`） | Demo 里中文 IME 全流程：组字、候选窗跟随、组字期不触发自动保存 |
| **M5** | 剪贴板互通（§7）+ RTF 投影 | 与记事本/Word 互粘对照 |
| **M6** | 内嵌图片（§0.2.6 / R-E3） | 粘贴位图落为行内图片 |
| **M7** | `.lumi v2` 存储层落地（§8.1）+ `IRichTextDocument` 演化（§8.2）+ 自动保存接通（§8.3） | 主程序跑通：新内核便签可编辑、可保存、可重开 |
| **M8** | 视觉回归对照 + `RESULTS-Phase2.md` + Go/No-Go 判定 | §10.3 口径 |

依赖：M0 已完成；M1 无外部依赖，可立即开工；M2 依赖 M1；M3 依赖 M2；
M4 依赖 M3；M5/M6 依赖 M3；M7 依赖 M3+M5；M8 收尾。

**节奏建议**：一个里程碑一个工作 session，半成品不留过夜（半成品 = 下次重建上下文
= 重复烧 token）。M1/M2/M3 是纯 Core + WinUI 控件，无外部 API 不确定性，建议连续推进；
M4 是 TSF 硬骨头，单独留一个完整 session。

---

## 15. 实施纪律（2026-10-03 教训沉淀）

本次 M0 探针实施中暴露的流程问题，写成规则避免重演：

### 15.1 探针/spike 纪律

- **探针 = 脏代码**：单文件、`internal`、不抽辅助类、不写防御性代码、不追求产品化结构。
  探针的唯一目的是「验证某个假设是否成立」，不是「写出能进产品的雏形」。
  Phase 0 纪律原文：「spike 代码允许'脏'：不写防御性代码、不做 DI、不写产品级日志；
  但性能与渲染结论必须可复现」。
- **先反射拿签名，再写实现**：CsWin32/源生成器生成的代码**不落盘**，
  凭空想象其签名必然反复试错（本次教训：可见性/枚举名/指针风格/AsSpan 重载连踩四坑）。
  正确姿势：写一个 5 行的 `Program.cs` 用反射打印接口全部方法签名，照着写实现。
- **探针不追求一次写对**：先跑起来再修，不要在写的时候追求编译零警告。

### 15.2 设计文档瘦身原则

- **只写「不改就错」的决策**：开放决策、里程碑划分、红线、风险。
- **细节留给实施**：具体 API 签名（实施时反射拿）、文件路径、代码骨架——
  写进设计文档反而成了幻觉来源（本次教训：设计稿里写的 `ITextStoreACP2` 方法集
  与 CsWin32 实际生成的签名对不上）。
- **查证记录克制**：只查「不做就错」的事实（CsWin32 覆盖、API 存在性）；
  不查「知道也不改变做法」的事实（WPF 有现成实现 ≠ 我可以直接用）。

### 15.3 节奏控制

- **一个里程碑一个 session**：半成品不过夜。M0 探针写了一半被叫停，
  下次接续要重建全部上下文，等于重复烧 token。
- **状态不好直接叫停**：本次 20 分钟就叫停是对的；下次更早。
- **不在一个 session 里混做「设计调整 + 代码实施」**：先调完方案文档（本次），
  再单独开 session 写代码。混在一起容易改着改着方案又回去改代码。

---

> 下一步：按 M1 开工（此时才会开始写代码）。
