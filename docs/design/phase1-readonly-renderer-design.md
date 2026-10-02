# Phase 1 详细设计：只读渲染器（文档模型 → 排版 → 绘制全链路）

- 状态：**M1、M2 已验收（2026-10-02）**，M3 待开工——验收记录见
  `src/LumiText/LumiText.Demo/RESULTS-Phase1.md`（U1/U2/U3 全 Go；slnx 独立构建通过；
  文档模型落地、.lumi v2 schema 冻结、T-S1–T-S6 + 既有 17 例共 23 测试全绿；
  U2 附带发现：带样式排版成本为无样式 4–5 倍，M3 基准须含多样式场景）
- 修订记录（v2）：对照 Phase 0 产物代码评审后补 4 处设计缺口——按批分组绘制（§4/§7.3）、
  交集重探弃批成本论证（§5.4/§5.5/§10.3）、滚动钉视口模型（§7.2）、Todo 复选框定位通路
  （§4/§6.2）；另补设备丢失风险（§12 R7）、工程自包含待办（§1/§2/§13 M1）、
  锚点契约简化（§3.3/§6.3）、Divider 高度基准（§4）、解码并行（§8.1）、对照前提（§9.2）。
- 前置：[custom-renderer-framework.md](custom-renderer-framework.md)（框架稿，已确认）、
  [phase0-spike-design.md](phase0-spike-design.md)（S1/S2 已验收，条件 Go）
- 范围红线：本文档只做方案，不修改任何现有代码；开工时按里程碑逐段实施
- 原则：Phase 0 的两个遗留口径原样带入——
  ① S2 判条件 Go 的「按段宽批量取行」接口改造（实测量级 7.86ms，见 `src/LumiText/LumiText.Demo/RESULTS.md` §2.3）；
  ② `FloatObject.cs` 注释中预留的「锚定到段落字符」浮动定位

---

## 0. Phase 1 目标与验收口径

框架稿原文：「新模型 → 排版 → 绘制全链路，能只读渲染便签内容。无编辑。此期结束即可对照
RichEditBox 做视觉回归。」

**做：**

1. 文档模型定稿（`LumiText.Core`，零 UI/零第三方依赖）：块级结构 + 行内样式 run，
   即未来 `.lumi v2` 的权威正文格式（schema 契约 + 序列化器 + 往返单测）。
2. 度量接口升级：`LayoutFirstLine` → 按段宽批量取行（S2 遗留性能待办）。
3. 排版引擎扩展：多样式 run、块级结构（标题/分割线/待办降级渲染）、浮动锚定。
4. 渲染层扩展：滚动支持（虚拟化 surface）、浮动图片位图绘制。
5. 只读宿主控件 `LumiDocumentView` + Demo 对照窗口，对照 RichEditBox 做视觉回归。

**不做（明确排除，防范围蔓延）：**

- 任何编辑能力（光标/选区/键入/撤销/IME/剪贴板）——Phase 2；
- `.lumi v2` 存储层落地（`LumiNoteStorage` 写 v2、自动保存接通）——Phase 2 随编辑一起接，
  Phase 1 只冻结 schema 与序列化器；
- 真复选框交互、行/段落背景、排版动画、拖动图片——Phase 3；
- 内嵌（行内）图片——技术已查证可行（见 §8.3），但 Phase 1 只渲染浮动图片，
  行内图片的排版/绘制接口在设计中预留；
- UIA 无障碍（沿用已确认的决策 4：留接口不实现）；
- 主程序（LumiMemo.WinUI）任何改动——Phase 1 产物只在 `LumiText.Demo` 中验收。

---

## 1. 现状盘点（Phase 0 产物，已迁入 `src/LumiText/`）

| 产物 | 位置 | Phase 1 的处理 |
|---|---|---|
| `FlowLayoutEngine`（Band+行盒分割） | `LumiText.Core/Layout/FlowLayoutEngine.cs` | 保留算法主体；行循环改为消费「批量行」（§5） |
| `ITextMeasurer`（首行探测） | `LumiText.Core/Layout/ITextMeasurer.cs` | **接口改造**（§5.2），这是 Phase 1 最大的结构性变更 |
| `ParagraphBlock(Text, Style, SpaceAfter)` | `LumiText.Core/Documents/ParagraphBlock.cs` | 演进为块级模型（§3），`Text` 单串 → `Runs` 序列 |
| `TextStyle(FontFamily, FontSize)` | `LumiText.Core/Documents/TextStyle.cs` | 拆分为段落样式 + 行内样式（§3.2） |
| `FloatObject(Id, Rect, Side, Margin)` | `LumiText.Core/Documents/FloatObject.cs` | 增加锚点定位模式（§6.3），矩形定位保留为兼容路径 |
| `CompositionTextSurface` | `LumiText.WinUI/Rendering/CompositionTextSurface.cs` | 保留为渲染基座；派生虚拟化版本（§7） |
| `Win2DTextMeasurer` | `LumiText.WinUI/Text/Win2DTextMeasurer.cs` | 实现新批量接口 + 样式 run 应用（§5.3、§6.1） |
| `FlowDocumentRenderer` | `LumiText.WinUI/Rendering/FlowDocumentRenderer.cs` | 改为「视口渲染器」：只画可见区行盒 + 图片（§7.3） |
| `FloatWrapView`（S2 演示控件） | `LumiText.WinUI/Controls/FloatWrapView.cs` | 角色降级为引擎回归 Demo；新宿主 `LumiDocumentView` 另建 |
| 17 个假字体单测 | `LumiText.Core.Tests` | 全部保留，接口改造后等价迁移（§10.1） |
| 截图验收管线 | `spikes/tools/screenshot.py` 等（S1 RESULTS §4 记录） | M1 迁入 `src/LumiText/tools/` 后复用；`spikes/` 其余内容（S1.GlassText、Probe）保留至 Phase 1 验收通过后整体删除——修订 Phase 0「迁移后即删」口径，Phase 1 期间 S1/Probe 仍是回归参照 |

已验证事实（直接引用验收记录，非推测）：

- Win2D 1.4.0 与 WASDK 2.5.1 编译/运行正常（S1 RESULTS「构建结论」）；
- 透明 surface + 灰阶 AA + DesktopAcrylic 共存成立（H1/H2/H3 通过）；
- 批量取行候选实测：「25×400 字段落整篇重排 = 7.86ms」（S2 RESULTS §3 原始数据）；
- 面板必须有非 null（可全透明）背景才参与命中测试（S2 RESULTS §4.1，`FloatWrapView` 已按此实现）。

---

## 2. 工程结构（Phase 1 完成后）

```text
src/LumiText/
├── LumiText.slnx                      ← 新（M1）：独立解决方案，拆仓前置（D5 要求工程自包含）
├── README.md                          ← 新（M1）：占位，开源时补全
├── tools/                             ← 新（M1）：自 spikes/tools 迁入的截图验收管线
├── LumiText.Core/                     ← 零依赖（仅 BCL）
│   ├── Documents/
│   │   ├── Document.cs                ← 新：一篇文档 = 块列表 + 浮动列表
│   │   ├── Blocks/                    ← 新：Paragraph/Heading/Todo/Divider/Image
│   │   ├── InlineStyle.cs             ← 新：行内样式
│   │   ├── TextRun.cs                 ← 新：文本 + 行内样式
│   │   ├── FloatObject.cs             ← 扩展：锚点定位
│   │   └── Serialization/             ← 新：System.Text.Json 源生成上下文（.lumi v2 正文）
│   └── Layout/
│       ├── ITextMeasurer.cs           ← 改造：批量行接口
│       ├── FlowLayoutEngine.cs        ← 改造：块驱动 + 批量消费
│       └── （PlacedLine/LayoutResult/LayoutRect 沿用，字段扩展）
├── LumiText.Core.Tests/               ← 假字体单测 + 序列化往返测试
├── LumiText.WinUI/
│   ├── Text/Win2DTextMeasurer.cs      ← 改造
│   ├── Rendering/
│   │   ├── CompositionTextSurface.cs  ← 沿用
│   │   ├── VirtualizedTextSurface.cs  ← 新：滚动 + 按需绘制 + Trim
│   │   └── FlowDocumentRenderer.cs    ← 改造：视口裁剪渲染
│   └── Controls/
│       ├── FloatWrapView.cs           ← 保留（引擎回归 Demo）
│       └── LumiDocumentView.cs        ← 新：只读宿主控件
└── LumiText.Demo/                     ← 新功能验收窗口（视觉回归主场）
```

命名与分层沿用 D5 决策；`LumiText.Core` 保持「可无头测试」，序列化只用 BCL 的
`System.Text.Json`（.NET 10 内置组件，不违反「零第三方依赖」）。

---

## 3. 文档模型（`.lumi v2` 权威正文）

### 3.1 模型总览

```text
Document
├── Blocks: IReadOnlyList<Block>
│   ├── ParagraphBlock   文本段落：Runs + ParagraphStyle + SpaceAfter
│   ├── HeadingBlock     标题 H1–H3：Runs + Level（本质 = 样式预设的段落）
│   ├── TodoBlock        待办：Runs + Checked（Phase 1 渲染为矢量复选框 + 悬挂缩进文本，见 §3.4 O2）
│   ├── DividerBlock     分割线：无内容，占一行高
│   └── ImageBlock       图片块：ImageId + 显示尺寸 + 浮动参数（见 §6.3）
├── Floats: IReadOnlyList<FloatObject>   ← 由 ImageBlock 派生，仍是引擎的输入
└── Images: IReadOnlyList<ImageResource> ← 位图资源表（ImageId → 数据）
```

设计要点：

- **不可变 record 全家桶**，与现有 `ParagraphBlock`/`FloatObject` 风格一致；
  编辑期的增量修改走「整块替换」，Phase 2 的编辑层再引入差异结构。
- **块即排版单元**：引擎输入从 `IReadOnlyList<ParagraphBlock>` 变为
  `IReadOnlyList<Block>`；非文本块（Divider/Image）产出「占位行盒」，
  复用现有行盒体系，不为它们发明第二条布局路径。
- **Todo/Heading 在排版层只是带预设样式的段落**：H1–H3 = 字号预设
  （已定：22/18/16 dip，2026-10-02 确认），Todo = 带**悬挂缩进**的段落
  （文本整体右移一个复选框宽度，复选框由渲染层画在缩进区里，不进文本流——见 §3.4 O2）。
  这样 Phase 1 的排版引擎**不需要**新增块类型分支，只有 Paragraph 一个文本路径
  + Divider/Image 两个占位路径；唯一新增的排版参数是段落级左缩进（`LeftIndent`，
  引擎在 Band 段宽基础上再减去该值，行盒 X 原点同步右移，是现有行盒体系的自然参数）。
- **编号稳定**：`ParagraphIndex`（现 `PlacedLine.ParagraphIndex`）语义改为 `BlockIndex`。

### 3.2 行内样式

```csharp
public sealed record InlineStyle(
    bool Bold = false, bool Italic = false,
    bool Strikethrough = false, bool Underline = false,
    Color32? Color = null,          // null = 继承主题墨色
    float? FontSizeRatio = null);   // null = 1.0；预留行内大小字，Phase 1 不暴露 UI

public sealed record TextRun(string Text, InlineStyle? Style = null);
```

- `Color32` 是 Core 侧的自持结构（ARGB 四字节），避免 Core 依赖任何 UI 颜色类型；
  WinUI 侧单向转换。
- 首版不做的行内能力（显式排除）：超链接、行内代码底色、字体族切换。
  它们都只是 `InlineStyle` 加字段，模型可向后兼容演进。

### 3.3 序列化与 `.lumi v2` schema 契约

**v2 正文 JSON（`Document` 字段的草稿，开工后冻结）：**

```json
{
  "schema": 1,
  "blocks": [
    { "type": "paragraph", "runs": [{ "t": "普通文字" }, { "t": "加粗", "s": { "b": true } }] },
    { "type": "heading", "level": 1, "runs": [{ "t": "标题" }] },
    { "type": "todo", "checked": false, "runs": [{ "t": "待办事项" }] },
    { "type": "divider" },
    { "type": "image", "imageId": "img-1", "width": 240, "height": 160,
      "float": { "side": "right", "margin": 8, "anchor": { "block": 0, "x": 0, "y": 0 } } }
  ],
  "images": [
    { "id": "img-1", "mime": "image/png", "data": "<base64>" }
  ]
}
```

- **图片数据内嵌 base64**：沿用 ADR 0002「一张便笺一个文件 + 原子写入」的架构，
  不引入兄弟文件/媒体目录（那会破坏 `AtomicFileWriter` 的单文件原子性假设，
  且回收站/复制都要跟着处理多文件）。代价是大图便笺文件变大、
  加载时全量解码——便签场景图片少而小，可接受；风险记入 §12。
- **`schema` 字段**：正文结构自己的版本号，与 `.lumi` 文件级 `Version` 解耦，
  未来正文演进不必升文件版本。
- **`.lumi` 文件级变化（Phase 2 才落地，此处仅冻结契约）**：
  `Version: 1 → 2`，新增 `Document`（v2 正文 JSON），`Rtf` 字段退役
  （读取端：v1 文件按已确认决策**不做迁移**，直接跳过并记日志——程序在开发阶段无历史数据；
  写入端：不再写 `Rtf`/`Text` 由 v2 投影生成）。
  读取容错矩阵（坏文件跳过、高版本跳过、id 为准）原样沿用 `LumiNoteStorage` 现有实现。
- **序列化器放 Core**：`LumiText.Core/Documents/Serialization`，
  `System.Text.Json` 源生成上下文（AOT/裁剪友好，且避免运行时反射）；
  字段名、可空语义、未知字段忽略策略由往返单测钉死（§10.1 T-S 系列）。

### 3.4 开放决策状态（2026-10-02 更新）

| # | 问题 | 状态 |
|---|---|---|
| O1 | 标题字号预设 | ✅ **已定：22/18/16 dip**（2026-10-02 用户确认）；Demo 里目测可微调，验收时冻结 |
| O2 | Todo 渲染形态 | ✅ **已定：矢量图形**（2026-10-02 用户确认）——复选框不进文本流，渲染层画 `DrawRoundedRectangle` 描边框 + 对勾线段；排版侧引入段落级 `LeftIndent` 悬挂缩进（见 §3.1、§6.2）。注意：这使新渲染器的待办形态与现产品（行首 ☐/☑ 字符）**有意不同**，§9.2 的待办对照用例相应改为「矢量形态验收」而非「与现产品一致」 |
| O3 | 图片 base64 内嵌 vs 媒体目录 | ✅ **已定：base64 内嵌**（2026-10-02 用户确认，理由见 §3.3）；配套约束：插入图片时单图压缩后 ≤ 5MB（JPEG/PNG），超限拒绝并提示 |
| O4 | 浮动图片的锚点语义（§6.3） | ✅ **已定：锚定到块首 + 偏移**（2026-10-02 用户确认），与 Word「随文字移动」同构 |

---

## 4. 排版引擎改造总览

引擎主算法（Band + 行盒分割）**不动**——这是 Phase 0 验收冻结的资产。
Phase 1 的变更集中在三处：

1. **输入**：段落列表 → 块列表；每块先展开为「排版流」（文本流或占位流）。
2. **取行方式**：逐行首行探测 → 按段宽批量取行（§5）。
3. **度量维度**：单一样式字符串 → 样式 run 序列（§6）。

块 → 排版流的展开规则：

| 块 | 展开 |
|---|---|
| Paragraph/Heading | 一条文本流（runs 拼接，样式随行） |
| Todo | 一条文本流 + 段落级 `LeftIndent`（复选框宽 + 间隙，建议 20+6=26 dip）；复选框不进文本流 |
| Divider | 一个占位行盒（高度 = `TextStyle.Default` 空行高度，与所在位置的样式无关的固定占位；内容为空，渲染层画线） |
| Image | 不进文本流；转换为 `FloatObject` 交给既有浮动通道 |

`PlacedLine` 扩展字段（v2 补全渲染通路）：

- `BlockIndex`（原 `ParagraphIndex` 改名）；
- `Kind`：`Text / TodoText / Divider / ImagePlaceholder`——渲染层据此分派绘制路径，
  Todo 文本行必须与普通段落区分（复选框定位依赖，见 §6.2）；
- `IsBlockStart`：是否所属块的第一个行盒（Todo 复选框只画在首行）；
- `Batch`（`ILineBatch` 引用，接替现 `NativeLayout` 的角色）+ `LineOffsetY`
  （本行顶缘相对批布局顶缘的偏移，取自 `MeasuredLine.OffsetY`）——
  渲染层「按批分组绘制」的定位依据（见 §7.3）。

`LayoutResult` 增加 `Blocks`（源块列表只读透传）：渲染层画 Todo 复选框需要
`TodoBlock.Checked`、画占位块需要块类型，排版产物自身不带块元数据会让渲染层无路可查。
`LayoutResult.Dispose` 语义不变，改为按 `Batch` 去重释放（§5.2 所有权契约）。

---

## 5. 度量接口升级：按段宽批量取行（S2 遗留待办）

### 5.1 问题回顾（真实数据，引自 S2 RESULTS §2.3）

现行 `LayoutFirstLine` 每取一行都对「剩余全文」重建一次 `CanvasTextLayout`，
整段合计 O(字符数²/行高)：全量排版 10,000 字符 = 75.1ms（预算 8ms，超 9.4×）。
解药已实测：**一个段宽只建一次布局、一次读回该宽度下的全部行**，
候选「25×400 字段落整篇重排 = 7.86ms」。

### 5.2 新接口（签名级草案）

```csharp
namespace LumiText.Core.Layout;

/// <summary>一批行：同一段宽下对一段文本流一次排版得到的全部行。</summary>
public interface ILineBatch : IDisposable
{
    int LineCount { get; }
    MeasuredLine GetLine(int index);          // 第 index 行的度量
    object? NativeLayout { get; }             // 整批共享的布局产物（所有权随 ILineBatch）
}

/// <summary>批量行内一行的度量。</summary>
/// <param name="CharsConsumed">本行字符数（含样式 run 展开后的流内偏移由 GetLine 调用方维护）</param>
/// <param name="CharStart">本行在「本批文本流」中的起始偏移</param>
/// <param name="Width">行推进宽度（末字符后插入符 X）</param>
/// <param name="OffsetY">本行顶缘相对布局顶缘的偏移（渲染定位用）</param>
public readonly record struct MeasuredLine(
    int CharStart, int CharsConsumed, float Width,
    float Ascent, float Descent, float OffsetY);

public interface ITextMeasurer
{
    /// <summary>以 maxWidth 对整条文本流一次排版，返回全部行（批量接口）。</summary>
    ILineBatch LayoutLines(IReadOnlyList<TextRun> runs, TextStyle baseStyle, float maxWidth);

    /// <summary>空行高度（空段落占位），语义不变。</summary>
    LineHeightInfo MeasureLineHeight(TextStyle style);
}
```

所有权契约（沿用现有 `NativeLayout` 约定的自然延伸）：
`ILineBatch` 归调用方（引擎）持有，其生命周期内所有 `PlacedLine` 都可引用
`NativeLayout`；`LayoutResult.Dispose` 改为按 `ILineBatch` 去重释放。

### 5.3 Win2D 实现要点

`Win2DTextMeasurer.LayoutLines`：

1. 拼接 runs 全文，创建**一个** `CanvasTextLayout(device, fullText, format, maxWidth, LayoutHeight)`；
2. 逐 run 应用行内样式（API 已查证存在，签名见 §11 查证记录）：
   `SetFontWeight(start, count, FontWeights.Bold)`、`SetFontStyle(...)`、
   `SetStrikethrough(...)`、`SetUnderline(...)`、`SetColor(...)`；
   `FontSizeRatio` 用 `SetFontSize(start, count, baseSize * ratio)`。
   ——注意：**样式应用必须在读 `LineMetrics` 之前完成**（Set* 会触发重排，
   先读后设等于白排一次；开工后用 §10.3 的微基准确认顺序开销）。
3. 读 `LineMetrics`（每行 `CharacterCount/Height/Baseline`，1 万字符实测 0.011ms）
   逐行换算为 `MeasuredLine`；行推进宽度沿用现法 `GetCaretPosition(end, false).X`
   （9 次调用实测 0.0006ms 量级）。
4. `NativeLayout` = 该 `CanvasTextLayout` 本体；一行都不画时立即 Dispose。

### 5.4 引擎行循环改造

现有 `ProbeRow` 逐段 `LayoutFirstLine` 的循环，改为「批量消费器」：

```text
段内维护：当前批 ILineBatch + 批内消费游标
取下一行：
  若批内还有未消费行 且 当前段宽 == 批的段宽 → 直接取批内下一行（零布局创建）
  否则 → 以「剩余文本 + 当前段宽」新建一批，取第一行
```

关键不变式（必须在单测里钉死）：

- 同一 Band 多段（图片两侧）共享基线的行为**不变**（现有 T4 用例直接回归）；
- 窄段放弃、跨带交集重探的行为**不变**（T6、矮图片用例直接回归）；
- 批的复用判定以**段（X，宽）**为准而非仅宽度——段宽相同但 X 不同的两段不得共享一批
  （否则渲染分组的原点定位失效，见 §7.3）；
- 新批创建点 = 段（X，宽）变化点或文本消费完点；批数上界 = 不同段（X，宽）种数 + 弃批数。

**交集重探的弃批成本（v2 评审补充）：**现有「行横跨多带 → 交集段宽变窄 → 弃探测重排一次」
（`FlowLayoutEngine.LayoutRow`）在旧模式下弃掉的只是一行探测（0.33ms 量级），批量模式下
弃掉的是**整批**（一次完整布局，最坏 7.7ms 量级）。发生条件 = 行跨带且交集段宽与当前批
不同，次数上界 = 带边界数（每处至多一次弃批 + 一次重建），浮动密集场景的最坏额外成本
≈ 带边界数 × 单批布局成本。这是 [A] 预算的主要不确定来源：S2 实测的 7.86ms 解药是
**无浮动**场景，带浮动 + 行跨带场景从未实测——M3 必须补「10,000 字符 + 3 浮动」批量
微基准（含建批数/弃批数计数，见 §10.3），数据出来之前不得进 M4。

### 5.5 性能目标（沿用 S2 预算，这次要达标）

| 场景 | 预算 | 依据 |
|---|---|---|
| 全量排版 10,000 字符 + 3 浮动 | 目标 < 8ms（P95）；接受上限 ≤ 15ms | 无浮动候选实测 7.86ms，交集重探弃批可能叠加（§5.4），带浮动数据 M3 补齐；超 8ms 未破 15ms 按 S2 条件 Go 条款书面记录，破 15ms 停下来先优化再进 M4 |
| 全量排版 25×400 字段落 | < 8ms（P95） | 实测 7.86ms |
| 3000 字符文档整篇重排 | < 4ms | 批量后理论上 ~2ms（0.29ms/400字 × 7.5 + 开销） |

注意沿用 S2 的口径纪律：Release 构建、热身 3 次取中位、判定看 P95 与超帧率不看均值；
此路径不吃 JIT 优化（成本在 WinRT 互操作），Debug/Release 差异极小，对比必须同配置。

---

## 6. 多样式 run、块级渲染与浮动锚定

### 6.1 行内样式的排版影响

混合字号/字重会让同一行内 ascent/descent 变大——**不需要特判**：
`CanvasTextLayout.LineMetrics` 的行高/基线已按行内最大字形计算
（DirectWrite 标准行为），引擎读到的就是混合后的行度量。
需要单测覆盖的边界：行内大字把行高撑高后，「行可能横跨多个带」的交集重探路径
（现有引擎逻辑天然兼容，加一例假字体用例即可）。

### 6.2 块级渲染（FlowDocumentRenderer 扩展）

| 块 | 渲染 |
|---|---|
| Paragraph/Heading | 逐行 `DrawTextLayout`（同现状，层裁剪到行盒） |
| Todo | 在 `Kind=TodoText && IsBlockStart` 的行盒左侧缩进区内画矢量复选框：`CanvasDrawingSession.DrawRoundedRectangle`（1.5px 描边、2px 圆角）+ 已勾选时 `DrawLine` 画对勾（两条线段）；复选框垂直居中于**首行行盒**（行盒顶 + (行高 − 框高)/2），勾选态经 `LayoutResult.Blocks[BlockIndex]` 取 `TodoBlock.Checked`（通路见 §4），颜色随主题墨色 |
| Divider | 占位行盒内画 1px 水平线（半透明墨色，居中） |
| Image（浮动） | `CanvasBitmap` 按 `FloatObject.Rect` 画圆角矩形裁剪的位图（替换 S2 的紫色调试矩形） |

### 6.3 浮动锚定（`FloatObject` 预留注释的兑现）

现状：浮动矩形由调用方直接给文档坐标（Demo 里拖动产生）。
正式文档需要「图片跟着锚点文字走」：

```csharp
// O4 已定锚定到「块首」：字符级锚定不做，CharIndex 从契约删除（v2 简化），
// 未来需要时升 schema 版本加回。
public sealed record FloatAnchor(int BlockIndex, float OffsetX, float OffsetY);

public sealed record FloatObject(...)
{
    // 二选一：Anchor != null → 排版时由锚点推导 Rect；否则用调用方直接给的 Rect（现状路径）
    public FloatAnchor? Anchor { get; init; }
}
```

- 推导规则：锚点块**首行行盒**的顶缘 + `OffsetY` 为矩形顶缘；
  `OffsetX` 相对内容区左/右缘（按 `Side`）。
- 锚点块解析的异常路径（v2 补全，单测钉死）：锚到非文本块（Divider/Image，无行盒）→
  顺延到其后第一个文本块；其后无文本块 → 钳到最后一个文本块；索引越界 → 钳到最后一块；
  全文无文本块 → 该浮动退化为 Rect 直给路径（兼容 S2 现状行为）。
- **先排版文本、后定浮动**会产生循环依赖（浮动位置影响行盒）。解法采用两遍排版：
  第一遍忽略浮动得到锚点行盒 Y → 推出浮动矩形 → 第二遍带浮动正式排版。
  两遍的成本 = 全量排版 ×2，以 §5.5 的预算看 3000 字符约 4ms×2，可接受；
  更省的单遍定点法（锚点所在行之后的带才应用浮动）留作优化项，不做首版。
- `PlaceFloats` 的归一化逻辑原样复用。

---

## 7. 渲染层：滚动与虚拟化 surface

### 7.1 查证结论（重要，纠正框架稿的隐含假设）

框架稿 §4 写「`CompositionVirtualDrawingSurface` 替换以支持长便签滚动」。经查证
（来源见 §11）：

- WASDK 的 `Microsoft.UI.Composition.CompositionVirtualDrawingSurface` **只有**
  `Trim(RectInt32[])` 一个自有成员，**没有** UWP 时代概念上的「合成器告诉你哪些
  区域需要绘制」的 `RectsNeeded` 属性；成员表确认仅继承
  `Resize/Scroll/ScrollWithClip` 等。
- 因此「按需绘制哪些区域」**由应用自己算**——好在可见区矩形本来就是已知量
  （ScrollViewer 偏移 + 视口尺寸），不构成障碍。
- `Trim` 的真实语义：清除已绘制区域、回收显存，区域回到「空」状态——这是滚动
  场景显存控制的关键手段。
- 虚拟 surface 上限 2²⁴ 像素（便签场景无意义，记录备查）；
  类标注 `ThreadingModel.Both` + Agile，绘制/Trim 可在非 UI 线程（首版仍 UI 线程，
  后台绘制留优化项）。
- Win2D 侧重载已确认：`CanvasComposition.CreateDrawingSession(surface, updateRect)`
  / `(surface, updateRect, dpi)` 可只更新 surface 的指定区域。

### 7.2 滚动同步：ScrollViewer + 表达式动画（官方样例模式）

已查证的官方模式（Microsoft Learn「Enhance existing ScrollViewer experiences」，
配套样例 ParallaxingListItems 收录于 WindowsAppSDK-Samples 仓库）：

1. `ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scrollViewer)`
   在 WinUI 3（WASDK 0.8–2.0）存在，返回含 `Vector3 Translation` 的 `CompositionPropertySet`；
2. 用 `ExpressionAnimation` 引用 `ScrollManipulation.Translation.Y`，
   `SetReferenceParameter` 绑定，`StartAnimation("Offset.Y", ...)` 挂到承载 surface 的
   SpriteVisual 上——**合成器线程驱动偏移，滚动不掉帧**；
   **钉视口模型（v2 明确）**：SpriteVisual 在 ScrollViewer 内容（Grid）内部，本身随内容
   被合成器平移；表达式的职责是**抵消**这个平移，把 SpriteVisual 钉回视口原位，
   方向为 `Offset.Y = -ScrollManipulation.Translation.Y`（符号与钳制以 U1 实测为准）。
   虚拟 surface 只有 N 屏高，不钉视口就会随内容滚出屏幕。钉住之后，surface 内的内容
   按滚动偏移重绘可见区（§7.3），滚动偏移由 `ViewChanged`（UI 线程）提供给重绘调度；
3. 官方注意事项：PropertySet 必须存为字段防 GC（表达式动画不持有强引用）。

结构：

```text
ScrollViewer (VerticalScrollBarVisibility=Auto)
└── Grid (Height = LayoutResult.TotalHeight, 仅撑开滚动范围)
     └─ LumiDocumentView 的可视宿主：SpriteVisual(虚拟 surface)
         Offset.Y ← 表达式动画绑 ScrollManipulation.Translation.Y
```

### 7.3 `VirtualizedTextSurface` 设计

```
VirtualizedTextSurface（CompositionTextSurface 的姊妹实现，不动旧类）
├── 虚拟 surface 尺寸 = 内容宽 × min(文档总高, 窗口高 × 3 屏) 物理像素（当前屏 ±1 屏预取）
├── 可见区变化（滚动/重排/尺寸变化）→ 计算需绘区域
│    → CreateDrawingSession(surface, updateRect) 只画该区域的行盒
├── 区域滚出预取范围 → Trim(rects) 回收显存
└── 退避策略：文档总高 ≤ 3 屏时直接用普通 CompositionDrawingSurface 整面绘
    （便签常态，虚拟化纯粹是为长文档兜底）
```

**按批分组绘制（v2 新增，修复批量接口与渲染器的衔接）：**批量接口下同一段（X，宽）的
多个行盒共享一个 `CanvasTextLayout`（`PlacedLine.Batch`）。若沿用现状「逐行盒调
`DrawTextLayout` + 裁剪到行盒」，每个行盒都会把整批光栅化一遍，一段 N 行就是 N 次整批
光栅化——渲染成本 O(行数²)，下文「< 1ms/屏」的估计必然落空。规则改为：

- 渲染按 `(Batch, 段 X)` 分组；组内逐行校验「引擎放置与批内堆叠一致」
  （前一行 Y + Height ≈ 本行 Y，容差沿用引擎 Epsilon）→ 一致并入当前组，不一致切新组。
  跨段基线抬升会让引擎放置间距大于批内堆叠，此时宁可多画一组也要保证位置正确；
- 每组一次 `DrawTextLayout`，原点 =（组首行 X, 组首行 Y − 组首行 `LineOffsetY`），
  裁剪到组内行盒矩形并集（`CreateLayer` + 几何组）；
- 常态（无跨段基线抬升）一批 = 一组，绘制调用数 = 批数；混合字号跨段行时退化为多组，
  正确性优先。光栅化总量 ≈ 文档一次 + 基线抬升处的少量重复。

- 首版**不做** `Scroll()`/`ScrollWithClip` 的像素搬移优化（滚动时整块重绘可见区）：
  按上述分组口径，一屏重绘 = 可见区涉及的批组各画一次，估计 < 1ms，
  开工后用 §10.3 基准验证；若实测不达标再启用 Scroll 优化（API 已确认存在）。
- DPI 处理沿用 `CompositionTextSurface` 既有方案（物理像素建 surface、
  `Stretch = Fill` 映射回 DIP）。
- 渲染器改造：`FlowDocumentRenderer.Render` 增加视口参数
  `Render(session, layout, Rect viewport, ...)`，只画与 viewport 相交的批组
  （行盒 Y 有序，二分定位起始行，分组在可见行范围内进行）。

---

## 8. 图片管线

### 8.1 解码

`CanvasBitmap.LoadAsync(ICanvasResourceCreator, IRandomAccessStream)`——签名已查证。
加载时机：文档载入时把 `Images` 表的 base64 解码为 `CanvasBitmap` 字典
（`ImageId → CanvasBitmap`），由渲染器持有；文档释放时统一 Dispose。
**排版不依赖解码结果**（显示尺寸在 `ImageBlock` 模型里），解码与排版**并行**：
文档载入即排版上屏，位图就绪后 `Invalidate` 补画图片；解码失败的图片画占位框并记日志，
不阻塞正文（v2 修订，原「解码 → 排版 → 渲染」串行描述作废）。
解码放后台线程（`CanvasBitmap` 与设备关联，绘制回 UI/合成线程安全——
Win2D 资源创建本就支持非 UI 线程，开工时以异常为信号验证）。

### 8.2 浮动图片绘制

渲染层把 `ImageBlock` 对应的 `FloatObject.Rect` 画为：
`CreateLayer(1f, Geometry = RoundedRectangle)` + `DrawImage(bitmap, destRect, sourceRect)`，
圆角 6px 与 S2 调试矩形观感一致。

### 8.3 行内图片（Phase 1 预留接口，不实现）

已查证 `CanvasTextLayout.SetInlineObject(characterIndex, characterCount, ICanvasTextInlineObject)`
在 Win2D 中存在——行内图片不需要 U+FFFC + 字符间距 hack，
可实现自定义 `ICanvasTextInlineObject`（DirectWrite 行内对象的标准托管封装，
回调提供度量与 Draw）。Phase 1 只在 `InlineStyle`/模型层留 `ImageRun` 的位置，
实现排入 Phase 2（与「功能对等：内嵌图片」同期）。

---

## 9. 宿主控件与视觉回归

### 9.1 `LumiDocumentView`（只读）

```csharp
public sealed class LumiDocumentView : ScrollViewer
{
    public void SetDocument(Document document);   // 载入 → 排版上屏；图片后台解码，就绪后补画（§8.1）
    public event Action<double>? LayoutStatsChanged;  // 沿用 FloatWrapView 的耗时上报
    public bool DebugOverlay { get; set; }              // 行盒/带/段调试框线
}
```

- 组合：`VirtualizedTextSurface`（§7.3）+ 表达式动画滚动同步（§7.2）；
- 背景必须设全透明画刷（命中测试铁律，S2 RESULTS §4.1）；
- 不响应任何编辑输入；指针只用于滚动。浮动拖动不迁移过来（Phase 3 交互）。

### 9.2 视觉回归方法（对照 RichEditBox）

主场：`LumiText.Demo` 新增「对照页」——左 `RichEditBox`（喂等价内容）、
右 `LumiDocumentView`（喂同内容的新模型），窗口用同一 `AcrylicBackdrop` 配置。

**前置确认（M7 开工前，v2 新增）：**

- 正文字号对齐：`TextStyle.Default` = Segoe UI 15dip，须与现产品 RichEditBox 正文字号
  实测一致（不一致则以现产品为准改默认样式）——否则「同字体字号同宽度」的对照前提不成立；
- 对照侧 `RichEditBox` 的内容用手写 RTF 字符串喂入（粗/斜/删/下划/分点/伪待办各一例），
  不引入 RTF 生成器。

| 用例 | 对照点 |
|---|---|
| 纯文本长段落 | 断行位置逐行一致（同字体字号同宽度下 DirectWrite vs RichEdit 断行应基本一致；逐行截图比对，允许个别行尾差异并记录） |
| 粗/斜/删/下划混排 | 样式生效、行高一致 |
| 标题三段 | 字号梯度目测合理（O1） |
| 待办五行（含换行长待办） | 矢量复选框视觉验收：描边/对勾清晰、垂直居中于首行、换行后文本与首行文本左缘对齐（悬挂缩进正确）；勾选/未勾选两态各截一张 |
| 浮动图片 + 环绕 | 现产品无此能力，只验收新渲染器自身正确性 |
| 滚动长文档（300 行） | 滚动流畅、无上屏残影、Trim 后回滚内容完整；快速甩动滚动时允许短暂透出玻璃底，滚动停止后下一帧内容完整（§7.2 钉视口 + §7.3 重绘调度的验收口径） |

截图证据沿用 S1 管线（M1 迁入 `src/LumiText/tools/` 后使用），
结果写入 `src/LumiText/LumiText.Demo/RESULTS-Phase1.md`。

---

## 10. 测试与验收

### 10.1 Core 单测（假字体，无头）

- **等价迁移**：现有 17 例全部保留语义，适配批量接口（假字体实现 `ILineBatch`）；
  T1–T10 环绕矩阵断言值**不得因接口改造而改变**（接口改造是纯性能变更）。
- **批量新增断言**：
  - T-B1 段（X，宽）不变时批数 = 1；批总数 ≤ 不同段（X，宽）种数 + 弃批数（§5.4 不变式）；
  - T-B2 批量结果与逐行旧逻辑（保留为内部参照实现）逐行一致；
  - T-B3 行内大字撑高行高后跨带交集重探正确。
- **块级**：T-C1 空段落/ Divider 占位高度；T-C2 Todo 悬挂缩进——所有行（含换行）
  X 原点 = 段 X + `LeftIndent`，且与浮动排除区叠加时缩进在段宽收窄之后生效；
  段宽 < `LeftIndent` + 最小可排版宽度时按窄段放弃处理（不死循环）；
  T-C3 浮动锚定两遍排版的锚点解析（索引越界钳制、锚到非文本块顺延、全文无文本块退化，§6.3）。
- **序列化**（T-S 系列）：全块型往返相等；缺省字段降级；未知字段忽略；
  `schema` 高于当前跳过；base64 图片往返字节一致。

### 10.2 引擎回归

`FloatWrapView` + Demo「性能验收」按钮保留，验收标准同 S2（T1–T10 + [A]/[B]/[C]），
其中 [A] 本次必须达标或按条件 Go 条款书面记录。

### 10.3 新增微基准（挂到现有「性能验收」）

- **带浮动批量排版基准（M3 必出数据，v2 新增）**：10,000 字符 + 3 浮动，
  记录耗时（P95）、建批数、弃批数——[A] 预算判定的唯一依据（§5.4/§5.5）；
- 样式应用顺序开销（§5.3 第 2 点）；
- 一屏可见区重绘耗时（§7.3 按批分组口径的退避决策依据）；
- 图片解码耗时（100KB/1MB PNG 各一）。

### 10.4 Go / No-Go

- **Go**：单测全绿 + §5.5 性能达标（或按条款记录）+ 视觉回归对照通过
  → 文档模型与渲染管线冻结，进 Phase 2（编辑层）详细设计。
- **No-Go**：样式 run 或滚动虚拟化出现结构性问题 → 回退到「单样式段落 +
  非虚拟化整面渲染」的最小只读链路（视觉回归仍可完成），问题点记入 Phase 2 前置风险。

---

## 11. 查证记录（本方案依赖的外部事实，均有来源）

| # | 事实 | 来源 |
|---|---|---|
| V1 | WASDK 版 `CompositionVirtualDrawingSurface` 仅自有 `Trim(RectInt32[])`；无 `RectsNeeded`；稀疏分配、Trim 回收显存；上限 2²⁴ 像素；`ThreadingModel.Both` + Agile | Microsoft Learn：Microsoft.UI.Composition.CompositionVirtualDrawingSurface 类页（成员表 + remarks）；winapps-winrt-api 源文档 |
| V2 | `CompositionDrawingSurface.Scroll/ScrollWithClip/Resize` 签名 | 同上（继承成员表） |
| V3 | `ElementCompositionPreview.GetScrollViewerManipulationPropertySet` 存在于 WASDK 0.8–2.0；PropertySet 含 `Vector3 Translation`；表达式动画绑定模式 + 防 GC 注意 | Microsoft Learn「Enhance existing ScrollViewer experiences」+ API 页（Applies to 表）；配套样例 ParallaxingListItems（WindowsAppSDK-Samples） |
| V4 | `CanvasComposition.CreateDrawingSession(surface, updateRect[, dpi])` 区域更新重载 | Win2D 官方文档（microsoft.github.io/Win2D，Overload List） |
| V5 | `CanvasTextLayout` 行内样式范围 API：`SetColor/SetFontFamily/SetFontSize/SetFontWeight/SetFontStyle/SetStrikethrough/SetUnderline/SetCharacterSpacing`，均为 `(int startIndex, int characterCount, ...)` | Win2D 官方文档 CanvasTextLayout 类页 |
| V6 | `CanvasTextLayout.SetInlineObject(int, int, ICanvasTextInlineObject)` 存在（行内图片的正路） | Win2D 官方文档 SetInlineObject 方法页 |
| V7 | `CanvasBitmap.LoadAsync` 重载：文件/URI/`IRandomAccessStream`，可带 DPI/AlphaMode | Win2D 官方文档 LoadAsync Overload List |
| V8 | `LineMetrics` 读回成本（1 万字符 0.011ms）与批量候选 7.86ms | 本项目 S2 验收实测（`src/LumiText/LumiText.Demo/RESULTS.md` §3） |

未验证、开工第一天需要以最小代码确认的点：

- U1：`GetScrollViewerManipulationPropertySet` 在本项目 WASDK 2.5.1 上的运行时行为
  （文档覆盖到 2.0；本项目 2.5.1 超出文档标注范围，属新版本，理论兼容但需跑通）；
- U2：Set* 样式应用在 `LineMetrics` 读取之前的顺序约束（§5.3）；
- U3：虚拟 surface 在透明 + Acrylic 场景与 S1 结论等价（理论同宗，但 S1 只验过普通 surface）。

---

## 12. 风险登记

| # | 风险 | 概率 | 应对 |
|---|---|---|---|
| R1 | 两遍锚定排版在大文档上翻倍成本超标 | 中 | 预算内接受（§6.3）；超标则锚定降级为「文档加载时一次性解析，编辑期随块位移增量修正」 |
| R2 | 图片 base64 内嵌导致便笺文件过大、加载慢 | 低 | 已按 O3 定稿：单图压缩后 ≤ 5MB 上限约束；真实数据证明仍有问题再拆媒体目录 |
| R3 | 滚动视觉问题两类：表达式动画方向/钳制错误（内容错位或双倍位移）；快速滚动时 `ViewChanged` 重绘跟不上（短暂透出玻璃底） | 中 | 钉视口表达式的符号与钳制第一天 U1 实测（§7.2）；重绘跟不上的兜底是「±1 屏预取 + 退化为普通 surface 整面绘」，验收口径见 §9.2 滚动用例 |
| R4 | 虚拟 surface 透明表现与普通 surface 不一致（黑底，S1 R2 同类） | 低 | U3 第一天验证；失败则全程用普通 surface + 文档高度上限保护 |
| R5 | 批量接口改造破坏 T1–T10 既有行为 | 中 | 等价迁移纪律（§10.1）：断言值不许变；参照实现双跑比对 |
| R6 | `LumiDocumentView` 在 20 窗口场景的显存占用（虚拟 surface 每窗一份） | 中 | Trim 策略 + S1 H5 搁置项在本期有条件时一并补测（BenchmarkWindow 改造） |
| R7 | 设备丢失（GPU 重置/驱动更新）后 `CanvasTextFormat` 缓存、`CanvasBitmap` 字典、surface 全部失效 | 低 | **本期记录为已知未处理项**（便签场景罕见）；`Win2DTextMeasurer` / `VirtualizedTextSurface` / 图片字典三处在实现时留出统一的 `RecreateDeviceResources()` 重建入口，异常信号驱动的真实重建排入 Phase 2 |

---

## 13. 里程碑（建议顺序，每段独立可验收）

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M1 | U1/U2/U3 三个未验证点的最小代码确认（在 Demo 工程内）＋ 工程自包含补齐（`LumiText.slnx`、`README.md` 占位、截图工具迁入 `tools/`） | 三条结论写入 RESULTS-Phase1；slnx 独立构建通过 |
| M2 | 文档模型 + 序列化器 + T-S 系列单测（§3） | Core 测试全绿；schema 冻结 |
| M3 | 批量度量接口改造 + 引擎行循环 + 等价迁移单测（§5） | T1–T10 等价迁移全绿 + 带浮动批量基准出数（§10.3）+ §5.5 达标或按条款记录 |
| M4 | 样式 run + 块级渲染 + 浮动锚定（§6） | T-B/T-C 系列全绿 |
| M5 | 图片解码与浮动绘制（§8.1/8.2） | Demo 浮动图片渲染正确 |
| M6 | `VirtualizedTextSurface` + `LumiDocumentView` + 滚动（§7、§9.1） | 300 行滚动验收用例通过 |
| M7 | 视觉回归对照（§9.2）+ RESULTS-Phase1.md | 对照表全过，产出验收记录 |

M2 不依赖 M1，可与 M1 并行；M3 依赖 M2（TextRun 类型）；M4–M6 顺序依赖；M7 收尾。

---

> O1–O4 已全部拍板（2026-10-02）。下一步：从 M1 开工（此时才会开始写代码）。
