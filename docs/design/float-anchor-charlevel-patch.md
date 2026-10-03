# 浮动锚定字符级化 — 设计补丁（Phase 2 前置优化）

- 状态：**已落地（2026-10-03）**——`LumiText.slnx` Release 构建 0 警 0 错，
  Core 测试 **56/56 全绿**（原 54 + 新增 TC3c CharIndex 越界钳制 + T-S7 旧格式降级）
- 提出：用户在 Phase 2 设计评审时要求「开工前先优化图片排版逻辑：图片位置锚定在
  左上角紧挨着的文字后面，重排时图片在这个位置定位」
- 影响面：`LumiText.Core` 的 `FloatAnchor` 契约 + `FlowLayoutEngine.ResolveAnchoredFloats`
  + 测试 + Demo 构造点 + `.lumi v2` schema（破坏性变更）

---

## 1. 需求语义（经 2026-10-03 两轮回答确认）

| 维度 | 结论 | 理由 |
|---|---|---|
| 锚定身份 | **方案 3：锚到插入点字符**（编辑器记录图片属于哪个字符，重排时图片跟着那个字符走） | 业界唯一合理做法（Word/Google Docs 同构）。方案 1「左上角最近字符」是排版输出反推输入，重排过程中「最近」会漂移，形成反馈环，用户无法预期。方案 3 是用户意图的一次性快照，锚点稳定 |
| 水平位置 | **`Side` 保留，只决定 X 贴边**（左浮贴内容区左缘，右浮贴右缘） | 与 Word「随文字移动 + 左右对齐」同构，更可预期；字符级 X 跟随会让图片随文字流动水平漂移，观感不稳定 |
| 垂直位置 | 锚字符**所在行的行盒顶缘**（图片顶 = 锚字符行的行盒顶） | 字符在行内的精确 Y 与行盒顶一致（行内所有字符共享行盒）；取行盒顶是自然选择 |
| 偏移量 | **去掉 OffsetX/OffsetY**，纯字符锚定 | 用户拍板；契约更简。失去微调能力，但锚字符本身已能精确定位 |

## 2. 契约变更

### 2.1 `FloatAnchor` record

```csharp
// 旧（Phase 1 冻结，v2 schema）：
public sealed record FloatAnchor(int BlockIndex, float OffsetX, float OffsetY);
// JSON: {"block":N, "x":X, "y":Y}

// 新（本补丁）：
public sealed record FloatAnchor(int BlockIndex, int CharIndex);
// JSON: {"block":N, "char":C}
```

- `CharIndex` 语义：**块内字符偏移**（与该块 `Runs` 拼接后的文本流对齐，与 Phase 2
  `TextPosition.CharIndex` 同坐标系）；
- 锚点 = 「块 `BlockIndex` 的第 `CharIndex` 个字符**前面**的位置」——即图片插入点语义；
- 三字段恒写出（`JsonIgnore(Never)`）纪律沿用。

### 2.2 schema 演化

Phase 1 冻结的 `.lumi v2` 正文 schema = 1。本补丁将其**推进到 schema = 2**：

- `Document.SchemaVersion` 常量 `1 → 2`；
- 读取端：`schema = 1` 的 `FloatAnchor`（`block/x/y`）→ 按「块首字符」降级解析
  （`CharIndex = 0`，丢弃 OffsetX/OffsetY）——开发阶段无历史数据，这是纯防御性兜底；
- 写入端：一律写 schema 2。

## 3. 排版引擎改动（`ResolveAnchoredFloats`）

现状：第一遍排版得到「锚点块首行行盒顶缘」，加 OffsetY 得矩形顶缘；X 按 Side 由 OffsetX 推出。

新逻辑：

1. 第一遍排版（同现状，只排矩形直给的浮动）；
2. 对每个锚定浮动，定位「块 `BlockIndex` 内第 `CharIndex` 个字符**所在的行盒**」：
   - 遍历该块的行盒，累计字符数，找到第一个 `累计 > CharIndex` 的行盒；
   - `CharIndex` 越界（≥ 块总字符数）→ 钳到该块**末行**；
   - 块不是文本块（无行盒）→ 沿用现状的顺延/钳制/退化路径（不变）；
3. **Y = 该行盒的 `Y`**（行盒顶缘）；
4. **X 按 Side 贴边**：`Left → 0`；`Right → contentWidth - Rect.Width`（Center 按 Right，
   沿用既有约定）；
5. 矩形 = `(X, Y, Rect.Width, Rect.Height)`，交给第二遍排版（不变）。

**与现状的差异**：
- Y 从「块首行 + OffsetY」变为「锚字符所在行」；
- X 从「OffsetX 相对边缘」变为「Side 直接贴边」（OffsetX 消失）；
- 锚点解析的异常路径（顺延/钳制/退化）原样保留。

## 4. 改动清单

| 文件 | 改动 |
|---|---|
| `LumiText.Core/Documents/FloatObject.cs` | `FloatAnchor` 三字段 → 两字段；XML 注释更新 |
| `LumiText.Core/Documents/Document.cs` | `SchemaVersion` 常量 1 → 2（若有；需核实位置） |
| `LumiText.Core/Layout/FlowLayoutEngine.cs` | `ResolveAnchoredFloats` 按 §3 重写；需新增「按字符定位行盒」辅助 |
| `LumiText.Core.Tests/FloatAnchorTests.cs` | 5 用例全部改写为字符级锚定；新增 `CharIndex` 越界钳制用例 |
| `LumiText.Core.Tests/DocumentSerializationTests.cs` | 序列化断言更新（`x/y` → `char`）；新增 schema 1 → 2 降级读取用例 |
| `LumiText.Demo/CompareWindow.xaml.cs:170` | `new FloatAnchor(10, 8f, 12f)` → 字符级等价 |
| `LumiText.Demo/RichDocWindow.xaml.cs:142` | `new FloatAnchor(5, 8f, 24f)` → 字符级等价 |
| `LumiText.Demo/ScrollDocWindow.xaml.cs:99` | `new FloatAnchor(1, 8f, 24f)` → 字符级等价 |
| `docs/design/phase2-editing-layer-design.md` | 补一节「Phase 2 前置：浮动锚定字符级化」，链到本文档 |
| `docs/design/phase1-readonly-renderer-design.md` | §6.3 加修订注记（v4）：字符级锚定取代块级，链到本文档 |

## 5. 测试策略

- **字符定位行盒**：假字体（字宽 10、行高 20、内容宽 100 → 全宽行 10 字符）下，
  `CharIndex = 0/5/15/越界` 分别落到第 0/0/1/末行；
- **贴边**：左浮 X=0、右浮 X=contentWidth−宽；
- **环绕**：锚字符行被浮动推挤的行为与现状一致（T-C3a 语义平移）；
- **schema 1 降级**：`{"block":N,"x":X,"y":Y}` 读入后 `CharIndex=0`，Offset 丢弃；
- **现有 T1–T10 环绕矩阵**：不触碰锚定路径，应保持全绿（回归保证）。

## 6. 与 Phase 2 的衔接

- `CharIndex` 坐标系与 Phase 2 `TextPosition(BlockIndex, CharIndex)` **天然对齐**——
  编辑器拖动图片落下时，把落点的 `TextPosition` 直接存为 `FloatAnchor`，无需换算；
- 拖动过程中的逐帧预览仍走「矩形直给」路径（`Anchor = null`，现状保留），
  松手时才把落点字符写回 `Anchor`——这是编辑器层的决策，引擎层无需感知；
- Phase 2 §13 开放决策 O4（内嵌图片失败降级为浮动锚定当前块）的「当前块」
  语义同步升级为「当前字符」，与本补丁一致。

---

> 评审通过后：先实施本补丁（独立提交），再按 Phase 2 的 M0 开工。
