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
