# Phase 3 详细设计：超越原生（富排版与交互动画）

- 状态：**已评审通过（2026-10-03）**——O1–O7 拍板（§2）；M1（块背景）实施完成、
  待用户界面验收；按 §10 里程碑逐段实施、逐段验收。
- 前置：
  [custom-renderer-framework.md](custom-renderer-framework.md)（框架稿 §4 分期路线）、
  [phase2-editing-layer-design.md](phase2-editing-layer-design.md)（结构沿用：里程碑 / 开放决策 / 风险登记 / §15 实施纪律）、
  `src/LumiText/LumiText.Demo/RESULTS-Phase2.md`（§7.5 遗留评估并入，见 §0.1）
- 范围红线：Phase 1/2 红线原样生效——Core 零 UI 依赖、宿主窄接口、
  渲染只走 Composition surface（毛玻璃硬约束）、`.lumi v2` 私有格式为权威
  （改模型 = 改 v2 正文 schema，须论证兼容性）。
- 验收口径沿用 Phase 2 修订版：Core 单测全绿 + 主程序测试全绿 + **用户直接界面验收**
  （用户在本机 VS 启动检查、给调整要求），逐里程碑记入 `RESULTS-Phase3.md`。

---

## 0. 范围界定

### 0.1 本期起点（框架稿原文 + 并入评估）

框架稿 §4 Phase 3 原文：「H1–H3、真复选框待办 + 悬停自定义光标 + 勾选动画、
行/段落背景、图片浮动环绕、图片拖动时文字流动动画」。

先澄清一处范围歧义：**「图片浮动环绕」的排版本体 Phase 1/S2 已交付**
（Band + 行盒分割、锚定两遍排版、粘贴插图走浮动锚定通道，`FlowLayoutEngine.cs`）。
本期该项的实际含义是**环绕的编辑闭环**——拖动改锚点、缩放、OS 拖放插图，
以及「拖动时文字流动」。

Phase 2 顺延项与遗留的并入评估：

| 来源 | 项 | 处置 |
|---|---|---|
| phase2 §0.3 | 拖放插图（OS 拖入） | **并入**（M4） |
| phase2 §0.3 | 图片缩放（ImageAdorner 等价物） | **并入**（M4） |
| phase2 §0.3 | 浮动图片锚定编辑（拖动改锚点） | **并入**（M4） |
| RESULTS §7.5-1 | 行内图片混排（SetInlineObject） | **不并入**（O6 拍板，继续挂账） |
| RESULTS §7.5-2 | R7 设备丢失重建 | 继续挂账（与本期无耦合，便签场景罕见） |
| RESULTS §7.5-3 | 上下方向键 / Home / End | **并入**（O7 拍板，M6） |
| RESULTS §7.5-4 | 旧内核 RichEditorHost / ImageAdorner | 以其交互契约为对等基线（八手柄、四角等比、缩放上限），Phase 4 退役 |

### 0.2 做（本期交付清单）

1. **标题语义**：段落 ↔ H1/H2/H3 转换、加粗 + 段前距视觉预设、Enter/Backspace 语义、
   工具栏 3 按钮（态随光标块联动）；
2. **真复选框交互**：矢量复选框已交付（Phase 2 补全）——本期补悬停态、
   悬停自定义光标、勾选动画；
3. **块级背景**：`Block.Background` 字段 + 工具栏色板（含清除）；
4. **图片交互**：拖动改锚点（落点自动切浮动侧）+ 八手柄缩放 +
  OS 拖放插图 + 选中/悬停态；
5. **文字流动动画**：图片拖动时文字逐行流动（跨行补间）+ 降级策略；
6. **光标移动补齐**：上下方向键 / Home / End / Ctrl+Home/End（含 Shift 扩选）。

### 0.3 不做（防范围蔓延）

- UIA 无障碍——沿用已确认决策：留接口不实现；
- 灰度切换与 RichEditBox 退役——Phase 4；
- 居中浮动（`FloatSide.Center` 维持「按右浮动处理」现状）——无需求输入；
- 行级背景（单独某一行铺色）——只做块级；
- Markdown 式 `# ` 行首自动转换——工具栏按钮已是明确入口，自动转换有误触风险，
  后续需要再加（纯增量、无兼容成本）；
- PageUp / PageDown——挂账（比 Up/Down 多一层视口耦合，便签场景少用）；
- 表格、超链接、行内代码——不在任何分期中。

---

## 1. 现状盘点（能踩的肩膀与缺口）

| 能力 | 现状 | 出处 |
|---|---|---|
| 浮动环绕排版（Band / 行盒分割 / 锚定两遍排版） | ✅ | `FlowLayoutEngine.cs` |
| 图片渲染 / 粘贴插图（280px 长边、浮动锚定） | ✅ | `FlowDocumentRenderer.TryGetFloatImage`、`WinClipboard` |
| 图片坐标命中（拖动 / 手柄的入口） | ✅ | `LayoutResult.FloatAt` |
| 拖动预览的 Rect 直给路径 | ✅ 已预留——注释明示「编辑器拖动图片时逐帧预览走这条路径，松手后把落点字符写回 Anchor」 | `FloatObject` / `FloatPlacement` |
| 标题 H1–H3 | 模型 / 排版 ✅（22/18/16 字号，不加粗）；编辑命令、Enter 行为、工具栏 ❌ | `HeadingBlock.cs` |
| 待办复选框 | 矢量绘制 ✅ + 点击切换 ✅（Phase 2 补全）；悬停光标 / 悬停态 / 勾选动画 ❌ | `FlowDocumentRenderer.DrawCheckbox`、`LumiEditor.TryToggleTodoAt` |
| 块级背景 | ❌ 无模型字段、无渲染（换内核动机之一） | — |
| 光标移动 | 左右 ✅；上下 / Home / End ❌ | `LumiEditor.OnPreviewKeyDownHandler` |
| 拖动期动画 | ❌——文本即时绘制进单张 virtual surface，无逐行 Composition 视觉 | `VirtualizedTextSurface` |
| 图片拖动 / 缩放 / OS 拖放 | ❌ 全部缺（旧交互层 `ImageAdorner`/`AdornerGeometry` 可移植其纯函数几何） | `src/LumiMemo.WinUI/Controls/` |

---

## 2. 决策记录（2026-10-03 拍板）

| # | 问题 | 结论 |
|---|---|---|
| O1 | 文字流动动画档位 | ✅ **A：实时重排 + 浮动矩形跨行插值补间**（§7）；B（逐行视觉重构）否决 |
| O2 | 行/段落背景形态 | ✅ **块级 `Background` 字段 + 色板**；粒度只做块级；不引入语义块类型、不动 schema 版本 |
| O3 | 标题入口与视觉 | ✅ **工具栏 3 个按钮**（态随光标块联动）+ H1–H3 加粗 + 段前距；Markdown 自动转换**不做** |
| O4 | 图片拖动落点语义 | ✅ **字符级重锚 + 紧跟锚字符**（写回 `FloatAnchor` + `AnchorToChar`，随文字重排走）：锚点 = 图片左上角挡住的第一条文本行上、紧挨图片左缘的插入位置（`HitTestFloatAnchor`，空行→行首、文字下方→末行）；拖拽期间实时重排跟手 |
| O5 | 缩放手柄形态 | ✅ **八手柄**（四角等比 + 四边单轴），对齐旧 `ImageAdorner` 契约；**左/上侧手柄另加**：左上角移动 → 松手重锚、右下边缘钉住（2026-10-04 用户补充） |
| O6 | 行内图片混排（SetInlineObject） | ✅ **不并入**，继续挂账 |
| O7 | 光标移动补齐 | ✅ **并入**（Up/Down + Home/End + Ctrl+Home/End + Shift 扩选）；PageUp/Down 不做 |

---

## 3. 文档模型与存储（核心增量）

### 3.1 块背景字段

```csharp
public abstract record Block
{
    [JsonPropertyName("bg")] public Color32? Background { get; init; }
}
```

- 放**块基类**：段落/标题/待办/分割线/图片一律可带底色（图片无行盒，实际不产出几何）；
- JSON 契约新增可选字段 `bg`（`"#AARRGGBB"`）；现有序列化选项
  `DefaultIgnoreCondition = WhenWritingDefault` —— 缺省 null **不写出**，
  旧文件读入为 null，**无需迁移、不动 `Document.SchemaVersion`**（纯增量字段）。

### 3.2 段落样式加粗

- `TextStyle` 增 `bool Bold = false`（序列化缺省省略）；
- `HeadingBlock.EffectiveStyle` 返回 `Bold = true` + 段前距；
- 度量层：`Win2DTextMeasurer.GetFormat` 设 `FontWeight`（格式缓存键含 Bold，天然生效），
  `MeasureLineHeight` 同步反映加粗行高；
- `InlineStyle.Bold = true` 语义不变（对段落内字符强制加粗）。

### 3.3 标题段前距

`HeadingBlock` 增**非序列化**属性 `SpaceBefore`（H1/H2/H3 初值 12/10/8 dip），
引擎在块排版前消费（`yCursor += SpaceBefore`，与 `SpaceAfter` 同纪律）。
初值走界面检查微调，不进 JSON（纯排版参数，与字号预设同性质）。

### 3.4 兼容纪律（陷阱清单）

- **重建块必须显式携带基类字段**：现有命令里大量 `new ParagraphBlock(p.Runs, p.Style, ...)`
  式重建（ToggleBullet/ToggleTodo/Split/Merge/InsertText/DeleteRange/BlockTextOps.WithRuns…）
  会丢 `Background`。注意派生块的位置化属性都是 get-only——`with` **改写不了它们**，
  只能走构造器；纪律：**重建时用对象初始化器补 `{ Background = 原块.Background }`**；
  仅改 `Background` 本身时用 `with`（基类 init 属性，允许）；
- 单测兜底：对带 `Background` 的块跑全部既有命令 + 新增命令，断言字段保留（§8.1）。

---

## 4. 排版产物：块几何

背景需要「块的行盒并集」，行盒只有单行几何——故引擎新增一个产物：

```csharp
public readonly record struct BlockExtent(int BlockIndex, LayoutRect Rect);
// LayoutResult 增：IReadOnlyList<BlockExtent> BlockExtents
```

- 只为「`Background != null` 且产生行盒」的块产出（其余块零成本）；
- 几何：X = 0、宽 = 内容区宽（整列口径，与 Word 段落底纹同构；不随 bullet/todo 缩进收窄）；
  Y = 首行顶缘 − padTop、底缘 = 末行底缘 + padBottom；
- **内边距钳制**（引擎侧，末段统一结算）：pad 初值垂直 4 dip，
  向上不超过「与上一块底缘间距的一半」、向下不超过「SpaceAfter 的一半」，
  防相邻块底色粘连/重叠；
- 浮动交叠：块内被浮动挤开的行盒仍计入并集（背景先画，浮动图片后画压在其上）。

---

## 5. 命令清单（新增）

沿用以快照撤销的 `IEditCommand` 体系（无 Inverse，自动进栈）：

| 命令 | 触发 | 语义 |
|---|---|---|
| `SetBlockBackgroundCommand(range, Color32?)` | 色板选色 / 清除 | range 覆盖的文本块整块设底色；不进 toggle 语义（选色即设、清除即 null） |
| `SetHeadingLevelCommand(range, level)` | H1/H2/H3 按钮 | level 0=正文，1–3=标题；对齐 ToggleBullet 的批量语义：范围「有非该级」→ 全设该级，全已是该级 → 全回正文（按钮 toggle）；Todo/Bullet 转标题时丢弃标记 |
| `MoveImageAnchorCommand(blockIndex, FloatAnchor anchor, FloatSide side)` | 图片拖动落点 | 改锚点 + 浮动侧 |
| `ResizeImageCommand(blockIndex, width, height)` | 缩放提交 | 改显示尺寸 |

命令合并/撤销边界：拖动与缩放**期间不走命令**（预览态），提交时各一条命令（§6.3）。

---

## 6. 交互与渲染

### 6.1 标题

- 按钮态：主程序 `SelectionChanged` → 查光标所在块类型 → 更新 3 个 ToggleButton；
- **Enter 语义**：`SplitBlockCommand` 按块类型定新块——标题内回车（含末尾）→
  新块为正文段，原块保持标题；
- **Backspace 语义**：标题行首退格 → 与上一块合并（沿用 Merge 语义，合并后类型随前块）；
  首块标题行首退格 → 降级为正文段。

### 6.2 真复选框交互

- **命中函数提取**：把现有 `TryToggleTodoAt` 的判定抽成无副作用的纯函数
  （todo 块首行行盒 + X 在缩进区），hover 与 click 共用；
- **悬停**：`PointerMoved`（非拖动中）命中复选框中 → 光标切 **Hand**
  （`ProtectedCursor`，`InputSystemCursor` 实例缓存复用，Phase 2 已查证），
  移出切回 IBeam；悬停块索引变化时重绘出「悬停高亮」（方框外圈浅色环）；
- **勾选动画**：点击 → 命令立即生效（模型即刻为勾选态）+ 记录
  `_checkAnim = (blockIndex, 起时, 时长≈160ms)`；渲染层对该块以进度值绘制
  「对勾描边 0→1 + 方框轻微缩放（1→1.06→1）」；16ms `DispatcherTimer`
  逐帧重绘，结束清理；再次点击打断重置；
- **实现路径**：动画在 surface 内逐帧绘制完成——**不引入独立 Composition 视觉**
  （避免与滚动/origin 重绘的坐标同步问题；整面重绘实测成本低，见 §9 R2）。

### 6.3 图片交互（M4）

**命中与选中**
- `PointerPressed` 顺序：todo 复选框 → **`FloatAt` 图片** → 文本；
- 点中图片：进入「潜在拖动」并选中（细边框 + 八手柄），指针捕获在 `_surfaceHost`；
- 点空白：取消选中。

**拖动改锚点**（O4 定稿：字符级锚 + 紧跟锚字符）
- 预览经 `FloatObject` 的 **Rect 直给路径**进入排版（该浮动临时不带 Anchor、`Margin = 0`），
  逐帧重排 + 重绘（M4 为瞬切；M5 加跨行补间，§7）——预览与落点逐像素一致；
- **落点 → 锚点**（松手时，`LayoutResult.HitTestFloatAnchor`）：取「图片矩形纵向覆盖到的
  第一条文本行」，在该行<u>图片左缘处</u>取插入位置——**在图片左侧紧挨着它的那个字之后
  插一个占位符**，其后的字与行照常环绕；图片在所有文字之下 → 末行行尾；行内无字 → 行首，
  有空格 → 空格之后；
- 参照版面是**「去掉这张图」的自然版面**（`FlowDocumentRenderer.LayoutTransient`）：
  预览版面里文字已被图片挤开一截，直接命中会差一格；
- 锚定后 `AnchorToChar = true`：X = 锚字符的行内位置、Y = 锚字符所在行盒顶缘，
  **随文字编辑/重排一起走**（`FloatAnchors` 在此维护锚点：插入/删除/分块/合并时同步偏移）；
- **边缘自动滚动**：指针进入视口上下 20 dip 内 → 16ms 定时滚动（8 dip/帧），
  滚动后按指针的**文档坐标**重算预览；
- 松手：提交 `MoveImageAnchorCommand`（锚点 + 侧）；单击未移动 = 仅选中，无命令；
  拖动全程只产生一条撤销记录；
- 锚点几何取「插入位置」：行内用 `<n>` 号字符**左缘**（`isTrailing=false`），
  行尾用上一字符右缘——索引即插入位置，与 `TextPosition` 同坐标系
  （恒定 `isTrailing=true` 会偏右一个字，A1 探针实测，见 §8.1）。

**八手柄缩放**
- 覆盖层用 **XAML 元素层**（移植旧 `ImageAdorner` 骨架：虚线框 + 8 手柄 + 尺寸标签），
  挂在 `LumiEditor` 滚动内容里、surface 之上（空白处不吃指针＝旧方案已验证的纪律）；
- 几何用**纯函数移植**（`ImageResizeGeometry`）：四角等比（取两轴变化较大者）、
  四边单轴、最小边长 24 dip、宽上限 = 内容区宽；**对边/对角固定**——
  左/上侧手柄动左/上边缘（右下边缘不动），右/下侧手柄动右/下边缘；
- **左/上侧手柄的左上角移动** → 松手按新左上角重锚（与拖动落点同一规则），
  并把**右下边缘钉回原位**（`ResolveAnchoredResize`：锚点决定落位，重锚后尺寸跟着让）；
  右/下侧手柄不动锚点，纯改尺寸；
- 光标：角/边对应八个 resize 光标（`CursorShapes` 映射）；
- **拖动期预览不动排版**（只动覆盖层几何 + 尺寸标签，左/上侧手柄的预览是「右下固定、
  左上跟手」的自由矩形），松手提交 `ResizeImageCommand`（尺寸 + 可选新锚点，
  **同一条撤销记录**）后一次重排——与旧产品提交语义一致。

**OS 拖放插图**
- `LumiEditor` 设 `AllowDrop`；`DragOver` 接受位图 / 图片文件（`StorageItems`），
  在落点字符处显示插入指示线（surface 内临时叠加）；`Drop` 解码
  （`BitmapDecoder`）→ 280 dip 长边上限 → `InsertImageCommand` 锚定落点字符；
- 拖动悬停/离开时清理指示线。

**悬停态**：未选中时悬停图片 → 细边框提示 + Hand 光标（对齐旧产品 hover 语义）。

### 6.4 光标移动补齐（M6）

- Core 新增纯函数导航器（无头可测）：
  - `Vertical(layout, position, caretX, direction)`：当前视觉行 → 上下相邻行
    （行盒列表为全文档序，跨块自然成立）→ 目标行内以 `caretX` 命中字符
    （行中点 Y 喂 `HitTestChar`）；
  - Home/End = 当前行首/末字符（行盒 `CharStart` / `CharStart + CharCount`）；
  - Ctrl+Home / Ctrl+End = 文档首 / 尾；
- **goal-X**：连续 Up/Down 记忆期望列（其它按键清除），短行穿越时不过早贴边；
- Shift 扩选沿用现有 `SetSelection(new TextRange(anchor, newPos))` 模式。

---

## 7. 文字流动动画（O1-A）

**机制（M5）**
- 拖动中预览矩形变化且**非首次落位** → 启动补间：
  `from = 当前预览矩形`，`to = 新预览矩形`，时长 ≈120ms，缓出；
- 每帧：以插值矩形重排 + 重绘（16ms `DispatcherTimer` 驱动，沿用光标闪烁的既有机制）；
- **打断规则**：拖动中新的跨行/换侧事件到达 → 丢弃当前补间，从当前中间位置起新补间
  （补间纯属观感，可随时截断，工作量有界）；
- 松手**无收尾动画**：预览矩形已是锚定语义（量化到行顶 + 贴缘），与提交后的
  模型结果逐像素一致，定格即终态。

**降级（实测驱动，写死为纪律）**
- 若拖动期间单次重排 > 8ms（或补间帧实际帧长 > 16ms）→ 关闭补间回退「瞬切」
  （拖动本身不受影响，文字仍随跨行即时重排）；
- 补间帧数与单帧耗时记入调试输出，进 `RESULTS-Phase3.md` 验收记录。

---

## 8. 测试与验收

### 8.1 Core 单测（无头）

- **序列化**：`bg` / `bold` 往返；缺省不写出；旧 JSON（无字段）读入为默认值；
- **命令**：背景设/清批量语义；标题级别转换（含 Todo/Bullet → 标题丢标记、取消回正文）；
  **字段保留回归**：带 `Background` 的块跑全部既有命令 + 新增命令后字段仍在；
- **块几何**：单块 / 多行并集 / 缩进块 / 相邻块间距钳制 / 浮动交叠并集；
- **标题语义**：Split 后新块类型、Merge 后类型、首块降级；
- **图片交互**：拖动落点规则的 5 条（行尾右侧 / 覆盖文字 / 跨行取首行 / 文字下方 / 空行）；
  `AnchorToChar` 落位与边距清零；锚点随编辑维护（插入 / 删除 / 分块 / 合并 / 上方插入行跟随）；
  缩放手柄的「对边固定」几何；左/上侧手柄重锚后**右下边缘不动**（`ResolveAnchoredResize`）；
- **锚点几何语义（2026-10-04 修复）**：`GetCaretGeometry(index, isTrailing)` 里 index 是**字符索引**
  ——`isTrailing=false` 取左缘（插入位置）、`true` 取右缘；恒定 `true` 会让图片偏右一个字。
  A1 探针实测（索引 4：左缘 56 / 右缘 70，锚定落到了 70）。`FakeTextMeasurer` 同步按真实语义
  实现（此前忽略 `isTrailing`，把单测全绿而应用偏一格的坑盖住了）；
- **批偏移换算（2026-10-04 修复）**：批的文本 = 块文本从 `PlacedLine.BatchStart` 起的切片
  （段一变——绕图、缩进变化——引擎就按剩余文本重建批），命中 / 光标 / 选区几何都必须做
  「块内偏移 ↔ 批内偏移」换算。漏了它，**绕图段落的点击只会往段落前面偏**，而第一行图左段
  因为批恰从 0 起看着正常（`BatchOffsetTests` 四条，含图片下方同段落的行）；
- **光标导航**：Up/Down 跨行跨块 / 短行 goal-X / Home/End / Ctrl+Home/End / 空块。

### 8.2 WinUI 侧（用户检查）

- M3：悬停复选框光标变手型、勾选动画顺滑；
- M4：图片拖动（含边缘自动滚动、切侧）、缩放提交、OS 拖入插图；
- M5：拖动时文字流动观感 + 帧长记录；
- M6：方向键/Home/End 手感。

### 8.3 Go / No-Go

Core 单测全绿 + 主程序测试全绿 + 用户检查通过 + 帧长实测在预算内（M5）
→ 判定 Go，进 Phase 4（切换与退役）设计。单点失败按条件 Go 书面记录。

---

## 9. 风险登记

| # | 风险 | 概率 | 应对 |
|---|---|---|---|
| R1 | 拖动 / 补间期「重排 + 整面重绘」帧长超标（大文档 / 低端机） | 中 | 补间可截断 + 阈值降级瞬切（§7）；M4 起实测帧长钉预算，数据进验收记录 |
| R2 | 复选框悬停 / 勾选动画的逐帧整面重绘开销 | 低 | 动画仅覆盖 `viewport×3` 表面、时长 160ms；实测超标则悬停态改为仅光标（不重绘） |
| R3 | 拖动 / 缩放期间误入撤销栈（每帧一条命令） | 中 | 预览态与命令分离；Core 测试覆盖「拖动全程 0 条、提交 1 条」 |
| R4 | 块背景大面积不透明遮住毛玻璃（硬约束观感冲突） | 中 | 色板 Alpha 上限（≤ 0x40）+ 圆角；观感走用户检查流程 |
| R5 | WinUI 3 OS 拖放与 TSF / 指针捕获交互冲突；StorageItems 权限 | 低 | M0 查证（§11）+ M4 实测 |
| R6 | 模型演进破坏 `.lumi v2` 兼容 | 低 | 只加可选字段（`bg`/`bold`）+ WhenWritingDefault 省略；单测钉死往返与旧文件读取 |
| R7 | 命令「重建块」丢 `Background`（§3.4 陷阱） | 中 | 全量改 `with` 表达式 + 字段保留回归单测 |
| R8 | 工具栏按钮态（标题/背景）与光标块不同步 | 低 | `SelectionChanged` 统一刷新；M2/M1 验收含此项 |

---

## 10. 里程碑

| 里程碑 | 内容 | 验收 |
|---|---|---|
| **M0** | 设计评审（本稿定稿）+ 查证：WinUI 3 OS 拖放 API 面貌（§11） | 本稿定稿 + 查证记录 |
| **M1** | 块背景：`Block.Background` + 序列化 + `BlockExtents` 产物 + 渲染 + `SetBlockBackgroundCommand` + 工具栏色板 | Core 单测 + 用户检查 |
| **M2** | 标题：`TextStyle.Bold` + `SpaceBefore` + `SetHeadingLevelCommand` + Enter/Backspace 语义 + 3 按钮 + 态联动 | Core 单测 + 用户检查 |
| **M3** | 复选框：命中纯函数 + 悬停态 + Hand 光标 + 勾选动画 | 用户检查 |
| **M4** | 图片：拖动改锚点（量化预览 + 边缘自动滚动 + 切侧）+ 选中态 + 八手柄缩放 + OS 拖放插图 | Core 单测 + 用户检查 |
| **M5** | 文字流动动画：跨行补间 + 打断/降级策略 + 帧长记录 | 帧长实测 + 用户检查 |
| **M6** | 光标移动：上下 / Home / End / Ctrl+Home/End / Shift / goal-X | Core 单测 |
| **M7** | 收尾验收：`RESULTS-Phase3.md` + Go/No-Go + 遗留登记 | 总判定 |

依赖：M1 / M2 / M3 / M6 相互独立；**M4 → M5 串行**；M7 收尾。
建议顺序 M1 → M2 → M3 → M4 → M5 → M6 → M7（先低风险后高风险；
M4/M5 是本期硬骨头，各留完整 session；M3 的动画与 M5 的补间共用帧驱动机制，
实施时 M3 先行可为 M5 试水）。

---

## 11. 查证记录

已查证（Phase 2 已有结论，直接引用）：

- `ProtectedCursor` + `InputSystemCursor`：WinUI 3 自定义光标（Phase 2 §11 V-CU1）；
- `LayoutResult.FloatAt` / `FloatObject` Rect 直给路径：拖动预览的既有入口（本库源码）；
- 复选框命中/绘制现状：`LumiEditor.TryToggleTodoAt`、`FlowDocumentRenderer.DrawCheckbox`。

M0 待查证（「不做就错」的最小集）：

- U1：WinUI 3 下自定义控件的 OS 拖放——`AllowDrop`、`DragOver` / `DragEventArgs`
  取 `StorageItems` 与位图的路径、拖放默认光标（`DragUI`）；
- U2（复核）：`DispatcherTimer` 16ms 逐帧驱动在 125% DPI 大窗口下的实际帧间隔
  （若抖动明显，退 `CompositionTarget.Rendering`）。

---

## 12. 实施纪律（沿用 Phase 2 §15）

- 一个里程碑一个 session，半成品不过夜；
- 探针 = 脏代码（单文件、internal、不产品化），先反射拿签名再写实现；
- 设计稿只写「不改就错」的决策，细节留给实施（幻觉来源）；
- 不在一个 session 里混做「设计调整 + 代码实施」；
- 查证记录克制：只查「不做就错」的事实。
