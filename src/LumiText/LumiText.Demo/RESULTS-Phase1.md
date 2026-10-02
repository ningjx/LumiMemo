# Phase 1 验收记录（2026-10-02）

环境：Windows 11（10.0.26300）/ 150% DPI / .NET 10.0.401 / WASDK 2.5.1 / Win2D 1.4.0。
除注明外，性能数据取自 **Release**。

## 1. M1：开工前三个未验证点 + 工程自包含 —— 全部通过

### 1.1 工程自包含（D5 拆仓前置）：Go

- `src/LumiText/LumiText.slnx` 新建（含 Core / Core.Tests / WinUI / Demo 四工程），
  `dotnet build LumiText.slnx -c Release` **独立构建通过，0 警告 0 错误**。
- 排坑记录：slnx 没有解决方案平台映射，`Platform` 以全局属性「Any CPU」传入导致
  Win2D 报 WIN2D0001；全局属性在 csproj 内不可覆盖，解法是 Project 标签声明
  `TreatAsLocalProperty="Platform"` + 条件钉死 x64（主 sln 映射 x64 时条件不触发，两条构建路径兼容）。
- `README.md` 占位已建；截图验收管线 `spikes/tools/` 已 `git mv` 迁入 `src/LumiText/tools/`
  （screenshot / bmp2png / crop_zoom / drag 四件，与 S1/S2 同源）。
- 已知事项：`dotnet test`（Microsoft.Testing.Platform 通道）当前环境下跑不出测试
  （零发现、退出码 5，主仓测试工程同症状，判定为环境问题非代码回归）；
  直跑测试 dll（xunit.v3 进程内运行器）**17/17 通过**，与 S2 验收一致。

### 1.2 U1：滚动钉视口表达式动画（WASDK 2.5.1）：Go

探针：`ScrollPinWindow`（`--probe u1`）——ScrollViewer + 120 行 XAML 编号文本，
`PinHost` 内挂 `CompositionTextSurface`；表达式动画 `-ScrollManipulation.Translation.Y`
绑到 SpriteVisual 的 `Offset.Y`（PropertySet 存字段防 GC）。窗口激活 1.8s 后自动
`ChangeView(1600dip)`，滚动前后各截一图（`u1_top.png` / `u1_scrolled.png`）。

结论：

- `ElementCompositionPreview.GetScrollViewerManipulationPropertySet` 在 WASDK 2.5.1
  **运行时可用**，返回的 `CompositionPropertySet` 含 `Translation`（Vector3）。
- **符号假设成立**：`Offset.Y = -Translation.Y` 把 SpriteVisual 钉回视口原位——
  瞬时跳滚 1600dip 后，surface 红框/标尺/标题像素位置与滚动前完全一致，
  XAML 编号行（053–070）从透明 surface 后方滚过。**合成器线程驱动，无 UI 线程延迟**。
- Phase 1 §7.2 的钉视口模型可按此实施；surface 内可见区重绘（ViewChanged 驱动）留待 M6。

### 1.3 U2：行内样式 Set* 与 LineMetrics 的顺序约束：Go（含重要附带发现）

探针：`StyleOrderProbe`（`--probe u2`，原始数据见 exe 同目录 `m1-probe.txt`）。
400 字符中英混排、宽 520dip，6 段样式 run（粗/斜/删/下划/色/大字）。

结论：

- **约束实证（正确性）**：`Set*` 后不重读 `LineMetrics`，拿到的是**无样式旧度量**
  （累计行高 160.0 vs 带样式 178.5 dip）——顺序错误的代价是用错数据，不只是性能。
  §5.3 的「先设后读」纪律成立。
- **Set\* 调用本身微秒级**（6 次共 0.010ms），成本全在它触发的重排。
- **白排成本量化**：错误顺序（先读后设再重读）多付的是一次**无样式**排版 ≈ 0.28ms/400 字。
- **附带发现 1**：`CanvasTextLayout` 构造是**懒惰**的（0.024ms），真实排版发生在首次读
  `LineMetrics` 时——性能测量时「建布局」与「排版」必须分开计时。
- **附带发现 2（影响 §5.5 预算）**：带 6 段样式的 400 字排版 = **1.1–1.6ms**，
  是无样式（0.28ms）的 **4–5 倍**（多样式 run 触发更多字体 shaping/回退）。
  §5.5 [A] 的 8ms 目标在样式密集文档上会非常紧，M3 的带浮动基准必须同时给出
  「多样式 run」场景的数据，不能只测纯文本。

### 1.4 U3：虚拟 surface 透明 + Acrylic 与 S1 结论等价：Go

探针：`VirtualSurfaceWindow`（`--probe u3`）——`CompositionVirtualDrawingSurface`
（B8G8R8A8 / Premultiplied）按区域更新（`CreateDrawingSession(surface, updateRect)`，
WASDK 重载第二参数为 `Windows.Foundation.Rect`），透明清屏 + 灰阶 AA，
叠在 DesktopAcrylic 上（`u3.png`，文字间隙 3 倍放大 `u3_crop_glassgap.png`）。

结论：

- 文字间隙透出桌面模糊内容（截屏可见后方窗口的明暗分布），**无黑底、无不透明补丁**；
  放大图确认灰阶 AA 无彩边。与 S1 普通 surface 的 H1/H3 结论**等价成立**。
- emoji 字体回退正常（灰阶 AA 下为单色字形，与普通 surface 行为一致）。
- Phase 1 §7.3 的 `VirtualizedTextSurface` 可按虚拟 surface 路线实施，R4 风险解除。

## 2. M1 对库的改动清单（随探针落地）

| 文件 | 改动 | 理由 |
|---|---|---|
| `LumiText.WinUI/LumiText.WinUI.csproj` | `TreatAsLocalProperty="Platform"` + AnyCPU→x64 条件 | slnx 无平台映射，消 WIN2D0001 |
| `LumiText.WinUI/Rendering/CompositionTextSurface.cs` | 新增只读属性 `Sprite` | 表达式动画挂载点（§7.2 钉视口的实施前提） |
| `LumiText.Demo/*` | 新增 `DemoBackdrop` / `StyleOrderProbe` / `ScrollPinWindow` / `VirtualSurfaceWindow`；`App.xaml.cs` 加 `--probe` 路由；`DemoWindow` 加 `autoRunU2`；`PerfBenchmark.Append` 加文件名参数 | M1 探针 |

## 3. 下一步

M2（文档模型 + 序列化器 + T-S 系列单测），可立即开工；
M3 的性能验收须包含 §1.3 附带发现 2 指出的「多样式 run」场景。

---

## 4. M2：文档模型 + 序列化器 + T-S 单测 —— 通过（schema 冻结）

### 4.1 交付物（全部在 `LumiText.Core/Documents/`，零 UI/零第三方依赖）

- **块模型**：`Block` 多态基类（JSON 判别字段 `type`）+ `ParagraphBlock`（v2 演进：
  `Text` 单串 → `Runs` 序列，保留 `string` 兼容构造与 `FromText`，S2 全部调用点零改动）
  / `HeadingBlock`（H1–H3 = 22/18/16dip 字号预设，O1）/ `TodoBlock`（`Checked` +
  `LeftIndent` 26dip 悬挂缩进，O2）/ `DividerBlock` / `ImageBlock`（`FloatPlacement`：
  side/margin + anchor/position 二选一）。
- **行内样式**：`TextRun` + `InlineStyle`（粗/斜/删/下划/`Color32`/`FontSizeRatio`，
  短键契约 `t/s/b/i/st/u/c/r`；`Color32` ↔ `#AARRGGBB`）。
- **浮动**：`FloatObject` 增加 `Anchor`（v2 简化：`FloatAnchor(BlockIndex, OffsetX, OffsetY)`，
  无 CharIndex）；`Document.GetFloats()` 由 ImageBlock 派生引擎输入。
- **序列化器**：`DocumentSerializer` + STJ 源生成上下文（构造传 options 以启用
  `UnsafeRelaxedJsonEscaping`——非 ASCII 原文写入，与宿主仓 JsonFileFormat 约定对齐；
  attribute 无法表达 Encoder）。`schema` 字段与 .lumi 文件级 Version 解耦。

### 4.2 测试：23/23 绿（直跑 dll，xunit.v3 进程内运行器）

- 既有 17 例环绕矩阵**零改动通过**（演进兼容性实证）；
- 新增 T-S1–T-S6：全块型往返（JSON 二次序列化字符串相等 + 结构抽查）、缺省字段降级、
  未知字段忽略、高版本跳过（schema 99 → null）、base64 图片往返字节一致、GetFloats 派生。

### 4.3 过程排坑（记录备查）

- STJ 源生成上下文属性可访问性必须与根 DTO 一致（internal 上下文 + internal envelope）；
- `DefaultIgnoreCondition.WhenWritingDefault` 会把全零 `FloatAnchor` 吞成 `"anchor": {}`——
  0 是锚点主流值，已对 `FloatAnchor`/`FloatPosition` 全字段加 `JsonIgnore(Never)` 恒写出；
- JSON 缺省 `runs` 经构造器注入 null——块构造按缺省降级契约改为 `runs ?? []`（T-S2 实证）。

### 4.4 冻结的 schema 样例（实际序列化输出，与 §3.3 草稿一致）

```json
{
  "schema": 1,
  "blocks": [
    { "type": "paragraph", "runs": [{ "t": "普通文字" }, { "t": "加粗", "s": { "b": true } }] },
    { "type": "heading", "runs": [{ "t": "标题" }], "level": 1 },
    { "type": "todo", "runs": [{ "t": "待办事项" }] },
    { "type": "divider" },
    { "type": "image", "imageId": "img-1", "width": 240, "height": 160,
      "float": { "side": "right", "margin": 8, "anchor": { "block": 0, "x": 0, "y": 0 } } }
  ],
  "images": [
    { "id": "img-1", "mime": "image/png", "data": "AQIDBA==" }
  ]
}
```

## 5. 下一步

M3（批量度量接口改造 + 引擎行循环 + 等价迁移单测）。M3 的性能验收须包含：
① 带浮动批量基准（10,000 字符 + 3 浮动，建批/弃批计数，§10.3）；
② 多样式 run 场景（M1-U2 附带发现：带样式排版 = 无样式 4–5 倍）。

---

## 6. M3：批量度量接口 + 引擎行循环 —— 代码完成、测试全绿，性能未达标（优化方案已定）

### 6.1 交付物

- **Core**：`ILineBatch`/`MeasuredLine`（含批内 `OffsetY`）批量接口替换逐行首行探测；
  `PlacedLine` 改持 `Batch` + `LineOffsetY`（渲染分组原点定位的依据）；引擎行循环改
  「单游标批量消费器」——段（X，宽）不变时零布局创建；`LayoutResult` 按批去重释放；
  `LayoutStats`（建批/弃批）计数。
- **WinUI**：`Win2DTextMeasurer.LayoutLines` 一次布局读回全部行度量（先 Set* 后读
  `LineMetrics` 的 U2 纪律不变）；`FlowDocumentRenderer` 按 (批, 段 X) 分组 + 堆叠一致性
  切组绘制，防整批重复光栅化（设计 §7.3）。
- **测试 43/43 全绿**（直跑 dll）：17 例环绕矩阵断言值**零改动** + T-S×6 + T-B1（批数上界）/
  T-B2（14 例与逐行参照实现逐行一致）/ T-B3（行内大字跨带交集重探）/ T-B4（批所有权）。
- **T-B4 对应的实机崩溃已修**：`LayoutResult` 曾持有已被引擎释放的批——一行横跨多段
  （浮动块两侧留缝）时，后一段建新批会释放前一段的批，而前一段的行盒已挂在本行待提交
  列表上；渲染层按批调 `DrawTextLayout` 即抛 RO_E_CLOSED（"Cannot access a disposed
  object."）。修法：批释放改为 `Layout` 收尾按行盒引用一次性结算（`DisposeOrphans`），
  删除 `CommittedAny` 即时释放记账；假字体的 `Dispose` 是空实现，T-B1–T-B3 对此无感，
  由 T-B4 在度量器侧记账守住该契约。Demo 实机验证不再崩。

### 6.2 基准（Release，2026-10-02 22:34 基线）

| 场景 | 预算 | 实测（中位 / P95） | 判定 |
|---|---|---|---|
| [A] 10,000 字符 + 3 浮动 | 目标 <8ms；上限 ≤15ms | 22.45 / 23.64ms（建批 63、弃批 38） | **破上限** |
| [B] 3,000 字符拖动逐帧 | <4ms | 7.14 / 8.70ms | 未达标（每帧全量重排，增量重排未实施） |
| [D] 25×400「多样式」+1 浮动 | 参照 <15ms | 2.21 / 2.38ms | 数字达标，但场景不代表真实成本（见 6.5） |
| [C] Demo 拖动帧率 | ≥55fps | 平均 142.7fps，P95 帧长 8.55ms，超 18ms 占 0.5% | 达标 |

说明：19:22 那轮（[A] 27.16 / [B] 8.03 / [D] 3.00ms）是在 WER 僵尸事故期间测的、整机被
拖累，数字作废；同代码 22:34 复测即上表，**以 22:34 为基线**。

### 6.3 [E] 高度截断判决（决定优化路线）

1 万字单批，布局高度 40 / 400 / 4096 dip：**5.94 / 5.93 / 5.99ms，读回行数均 195**
→ **DirectWrite 不按布局高度短路**（高度只影响绘制裁剪，不影响排版计算）。
原设想的"给 `LayoutLines` 传带高"路线作废，换批优化改走**按带容量做文本长度封顶**
（喂"当前带能消费的字符数 + 余量"，而非段落剩余全文；注意封顶批的末行断行结果可能与
全文不同，消费时必须丢弃，见设计文档 §5.4）。

### 6.4 成本构成（推算，依据 6.2/6.3 实测）

单批成本 ≈ 0.29ms/400 字（15:34「度量拆解」），且线性于喂进的字符数。[A] 的 22.45ms：

- **25 个「每段起步批」≈ 7.3ms**——即无浮动整篇的固有价（实测 7.86ms），不可省；
- **其余 ≈ 15ms 全是换批流失**：① 窄缝段探测每行驱逐并重建宽段批（两段带内每行 =
  2 次布局 + 1 次弃批，而非"整条带复用 1 批"）；② 交错段文本位置每行都变，批无法复用；
  ③ 跨带交集重探弃批（设计 §5.4 v2 已论证）。

结论：**批数（而非单批体积）是主要成本变量**。

### 6.5 下一步

1. ~~优化（未开工，设计方案见设计文档 §5.5）~~ **性能处置已定（2026-10-02 用户拍板）**：
   接受当前性能（[A] 22.45ms 破 ≤15ms 上限），按条件 Go 条款书面记录——依据同 S2：
   真实便签普遍远小于 1 万字符，[C] 拖动 142.7fps 的实际手感已达标；
   §5.5 优化清单（① 窄段预筛不驱逐当前批；② 按带容量文本长度封顶）转为
   **后续优化项**（Phase 1 之后或真实数据证明需要时再做），不阻塞 M4 开工。
2. **补基准变体（未开工，随优化项一起做）**：[D] 用的是纯拉丁假文（单批 0.063ms），
   未覆盖 CJK 样式成本（M1-U2 实测中文 + 6 段样式 400 字 = 1.1–1.6ms，差约一个量级）。
   补「CJK + 6 段样式 + 3 浮动」的 [A] 变体后再定优化优先级（设计文档 §10.3）。

---

## 7. M4：样式 run + 块级渲染 + 浮动锚定 —— 通过（测试 54/54 全绿）

### 7.1 交付物

- **块驱动引擎（§4）**：`Layout(IReadOnlyList<Block>)` + `Layout(Document)` 重载
  （`IReadOnlyList<out T>` 协变，S2 全部调用点零改动编译）。Paragraph/Heading/Todo
  展开为文本流；**Todo 悬挂缩进**（段宽收窄之后再减 `LeftIndent` 26dip、行盒 X 右移，
  段宽不足缩进 + 最小字宽时按窄段放弃）；**Divider 占位行盒**（`TextStyle.Default`
  空行高度，跨带取段交集的首个可用段）；ImageBlock 不进文本流（走 `GetFloats` 浮动通道）。
- **`PlacedLine` 扩展（§4）**：`ParagraphIndex` → `BlockIndex`（改名）、
  `Kind`（Text/TodoText/Divider/ImagePlaceholder）、`IsBlockStart`；
  `LayoutResult.Blocks` 只读透传（渲染层取 `TodoBlock.Checked` 的通路）。
- **浮动锚定两遍排版（§6.3）**：第一遍只用矩形直给浮动排版得锚点块首行 Y →
  推导锚定矩形（OffsetX 相对左/右缘按 Side）→ 第二遍正式排版。异常路径：
  锚到非文本块/空块顺延、越界钳制、全文无文本行盒退化 Rect 直给。
- **块级渲染（§6.2）**：Todo 矢量复选框（`Kind=TodoText && IsBlockStart` 的行盒缩进区内，
  垂直居中，1.5px 描边 2px 圆角 + 对勾两线段，颜色随墨色）；Divider 1px 半透明水平线。

### 7.2 测试：54/54 全绿（直跑 dll）

- 既有 43 例（17 环绕矩阵 + T-S×6 + T-B1–B4 + M3 批量）**零行为改动通过**
  （仅 `ParagraphIndex` → `BlockIndex` 改名适配 3 处断言）；
- 新增 **T-C1**（Divider 占位高度/空段落叠加）、**T-C2**（Todo 悬挂缩进/浮动叠加/窄段放弃）、
  **T-C3a–e**（锚定块首、偏移、锚到 Divider 顺延、越界钳制、无文本退化）+ Document 入口透传。

### 7.3 可视化验证（`--probe m4`，截图 `m4.png`）

同一份 Document 一次渲染：H1/H3 标题层级、粗/斜/下划/删除/彩色/大字混排、
未勾选与已勾选 Todo（长待办换行后文本与首行左缘对齐）、Divider 线、
锚定浮动（块首 + 偏移）与直给浮动共存、文字在双浮间环绕、毛玻璃透出——全部正确。

---

## 8. M5：图片解码与浮动绘制 —— 通过

### 8.1 交付物

- **`DocumentImageStore`（§8.1）**：ImageResource → `CanvasBitmap.LoadAsync(device, stream)`
  解码缓存（ImageId → bitmap）；`Task.Run` 后台并行预热（上限 4），解码完成一张回调一张
  （宿主封送 UI 线程 Invalidate 补画）；**排版不依赖解码**（显示尺寸在模型里）——
  文档载入即排版上屏；解码失败记失败态、渲染画占位框，不阻塞正文；文档释放统一 Dispose。
- **浮动图片绘制（§8.2）**：渲染器对每个浮动 ImageBlock（`FloatObject.Id` = 源块索引）
  用 `CreateLayer(1f, RoundedRectangleGeometry)` + `DrawImage(bitmap, destRect, sourceRect)`
  画到 Rect，圆角 6px 与调试矩形观感一致；未就绪/失败/S2 调试浮动共用占位矩形路径。
- **Demo**：`RichDocWindow` 接真实图片（`tools/gen_test_images.py` 生成的 54KB 渐变 /
  983KB 噪点 PNG，存 `LumiText.Demo/Assets/`），双浮动位图共存验证（截图 `m5.png`）。

### 8.2 解码实测（§10.3，Release，2026-10-02）

| 图片 | 首轮（冷，含 WIC/Win2D 管线初始化） | 二轮（热，稳态） |
|---|---|---|
| 54 KB PNG（800×533 渐变） | 34.88 ms | **2.23 ms** |
| 983 KB PNG（1300×975 噪点） | 42.97 ms | **9.96 ms** |

解读：冷启动 ~35–43ms 为管线一次性初始化（两图并行同档完成是典型特征），与图片大小无关；
稳态 ≈ 3–10ms/张，与 §8.1 的经验值（500KB ≈ 3–8ms）同量级。**结论：预热缓存策略成立——
便签级图片（≤5MB 约束内）稳态解码均为毫秒级，且完全不阻塞排版上屏。**

### 8.3 可视化验证

`m5.png`：左浮动为彩噪位图、右浮动为蓝紫渐变位图（圆角层 + 描边），
文字在双浮间环绕；标题/样式/待办/分割线全部保持 M4 行为。测试 54/54 全绿（无回归）。
