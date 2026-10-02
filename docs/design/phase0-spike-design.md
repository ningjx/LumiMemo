# Phase 0 详细设计：S1 玻璃自绘文本 + S2 浮动环绕排版

- 状态：**S2 已实施并验收（条件 Go，2026-10-02）**——结果见
  `src/LumiText/LumiText.Demo/RESULTS.md`；S1 结果见 `spikes/S1.GlassText/RESULTS.md`
- 日期：2026-10-02
- 前置：[custom-renderer-framework.md](custom-renderer-framework.md)（框架稿，已确认）
- 原则：两个 spike 均为**独立小工程，不修改主项目任何代码**；验收通过后才允许产物迁入 `src/`
  （产物已按 D5 决策迁入 `src/LumiText/`）

---

## 0. 已锁定决策（2026-10-02 用户确认）

| # | 决策 | 结论 |
|---|---|---|
| 1 | 路线 | 方案 A（自研内核）为主线；方案 B（ITextHost 无窗口 RichEdit）为保底 |
| 2 | 权威格式 | 新文档模型取代 RTF，成为 .lumi v2 权威正文；RTF 仅用于旧数据导入与剪贴板互通 |
| 3 | Phase 0 范围 | 只做 S1 + S2；S3（TSF 输入法）延后到 Phase 2 开工前 |
| 4 | UIA 无障碍 | 首版只留接口不实现，读屏暂不可用（产品侧已接受） |
| 5 | 工程形态（D5） | 渲染器 = 独立库 `LumiText`（`LumiText.Core` 零依赖 + `LumiText.WinUI` 宿主/渲染），不依赖 LumiMemo 代码；主程序项目引用接入，未来拆仓开源为 NuGet 包 |

---

## 1. Spike 工程共性设置

```text
LumiMemo/
├── src/                     ← 主项目（本阶段零改动）
└── spikes/                  ← 新建，全部原型代码放这里
    ├── LumiMemo.Spikes.sln
    ├── Directory.Packages.props   ← 独立版本表，不污染主项目 CPM
    ├── S1.GlassText/              ← 独立可执行
    └── S2.WrapLayout/
        ├── S2.WrapLayout.Engine/      ← 纯算法库（net10.0，无 UI 依赖）
        ├── S2.WrapLayout.Tests/       ← xunit，假字体确定性测试
        └── S2.WrapLayout.Demo/        ← 可视化窗口（引用 S1 的宿主代码副本）
```

- TFM / SDK 与主项目对齐：`net10.0-windows10.0.26100.0`、WindowsAppSDK `2.5.1`、`WindowsPackageType=None`、x64 only、`TreatWarningsAsErrors`。
- Win2D 版本：**首选 `Microsoft.Graphics.Win2D 1.4.0`**（2026-03 发布，支持 net10.0-windows）。注意其依赖声明为 `Microsoft.WindowsAppSDK.WinUI ≥ 1.8`，与本项目 WASDK 2.5.1 的共存属于**未验证项**，S1 的第一个任务就是验证编译与运行（见 §2.6 风险 R1）。备选 `1.3.3`；最终兜底是抛弃 Win2D、用项目已有的 CsWin32 直接调 D2D/DWrite（架构不变，仅互操作代码变多）。
- spike 代码允许"脏"：不写防御性代码、不做 DI、不写产品级日志；但**性能与渲染结论必须可复现**（数据写进验收记录）。

---

## 2. S1：玻璃上的自绘文本

### 2.1 待验证假设

| # | 假设 | 不成立的后果 |
|---|---|---|
| H1 | `CompositionDrawingSurface`（Premultiplied、透明清屏）上的像素能与窗口系统背景正常混合，文字间隙透出 DesktopAcrylic 毛玻璃 | 方案 A 死刑 → 转方案 B |
| H2 | 失焦后玻璃不降级（沿用主项目 `IsInputActive=true` 的 `DesktopAcrylicController`，自绘区域同样生效） | 违反产品核心体验 → 转方案 B |
| H3 | 文字在玻璃上清晰可读、无彩边 | 需调抗锯齿/底色策略，可补救 |
| H4 | DPI 100%/150%/200% 及跨屏拖动不模糊、不撕裂 | 需调整光栅化策略，可补救 |
| H5 | 20 个窗口同时打开：总工作集、空闲 CPU 达标（参照 ADR 0001 的量级：10 窗 ≤ 300MB，20 窗空闲 CPU < 1%） | 需优化 surface 策略（如改虚拟化 surface），可补救 |

H1/H2 是**生死项**，开工第一天就能验证（最小 demo 一屏文字）；H3–H5 是调优项。

### 2.2 结构

```text
S1.GlassText
├── GlassTextWindow.xaml(.cs)     ← 窗口：复制主项目 AcrylicBackdrop 的
│                                    DesktopAcrylicController 配置（IsInputActive=true）
├── CompositionTextSurface.cs     ← 核心对象：持有 surface/brush/visual 三元组
│                                    负责 尺寸同步 / DPI 缩放 / 失效重绘
├── TextRenderer.cs               ← CanvasTextLayout 的创建、缓存与绘制
└── BenchmarkWindow.cs            ← 一键开 20 窗口 + 采样内存/CPU 的测试窗口
```

**渲染链路（关键路径）：**

```text
XAML 宿主元素 (Grid, Background=Transparent)
  └─ ElementCompositionPreview.SetElementChildVisual → ContainerVisual
       └─ SpriteVisual.Brush = CompositionSurfaceBrush(surface)
            └─ CompositionDrawingSurface (B8G8R8A8, Premultiplied)
                 ↑ CanvasComposition.CreateDrawingSession(surface)
                 ↑ session.Clear(透明) → DrawTextLayout(...)
Compositor ← Compositor
   ↑ CanvasComposition.CreateCompositionGraphicsDevice(compositor, CanvasDevice.GetSharedDevice())
```

要点（均有据）：

1. **绝不引入 external content**：不用 `SwapChainPanel` / Win2D `CanvasControl`（后者内部即 swapchain）。只走 `CompositionDrawingSurface` —— 它是合成器内容，官方文档确认支持 Win2D/D2D 互操作绘制，因此与 acrylic 系统背景共存无冲突。这是 H1 的理论依据，但仍需实测。
2. **抗锯齿**：`session.TextAntialiasing = CanvasTextAntialiasing.Grayscale`。ClearType 依赖不透明背景做子像素混合，在透明 surface 上会产生彩边——这是透明载体绘文字的常识性约束，灰阶 AA 是唯一正确选择。
3. **DPI**：surface 物理尺寸 = 元素 DIP 尺寸 × `XamlRoot.RasterizationScale`；`SpriteVisual.Scale = 1/RasterizationScale` 使文本按物理像素光栅化，200% DPI 不模糊。监听 `RasterizationScaleChanged` 重建 surface。
4. **尺寸同步**：宿主元素 `SizeChanged` → `surface.Resize(...)` + 失效重绘；`CompositionSurfaceBrush.Stretch = None`，不做 GPU 拉伸。
5. **重绘策略**：S1 整面重绘即可；`CompositionDrawingSurface.Scroll` 和 `CompositionVirtualDrawingSurface`（按需绘制可见区）留到 Phase 1 长文档滚动时启用。
6. **文本内容**：中英混排 + emoji + 不同字号各一段，验证字体回退链（CanvasTextFormat 默认回退即 DWrite 系统回退）。

### 2.3 类职责（接口级，非实现）

| 类型 | 职责 | 关键成员（签名级） |
|---|---|---|
| `CompositionTextSurface` | surface/brush/visual 生命周期；尺寸与 DPI 同步；向外部暴露 `Invalidate()` | `Attach(UIElement host)`；`Invalidate()`；`event EventHandler<Rect>? RedrawRequested` |
| `TextRenderer` | 把"一段带样式文本"画进指定 session；布局缓存 | `Render(CanvasDrawingSession, TextBlock content, Rect bounds)`；`Measure(TextBlock) → Size` |
| `GlassTextWindow` | 毛玻璃窗口外壳；承载若干 `CompositionTextSurface` | 复用主项目 `AcrylicBackdrop` 同款参数（Tint `#FFF4ECFF` 等） |

### 2.4 验收方法与脚本

| 假设 | 方法 | 通过标准 |
|---|---|---|
| H1 | 肉眼 + 截屏比对：同一窗口分别用 RichEditBox 版与 S1 版渲染同一段文字，截编辑器区域对比玻璃透感 | 文字间隙可见桌面模糊内容，非纯色块 |
| H2 | 打开窗口 → 点击桌面使失焦 → 观察 | 玻璃观感与聚焦时一致（无退实色） |
| H3 | 100%/150% 各截屏放大 400% 检查 | 无彩色边缘；正文灰度均匀 |
| H4 | 三档 DPI 逐一切换 + 跨屏拖动窗口 | 文本始终锐利，无残影/撕裂 |
| H5 | `BenchmarkWindow` 开 20 窗，PowerShell 采样：`Get-Process \| Select WorkingSet64`，空闲 30s 采 CPU | 10 窗 ≤ 300MB 外推 20 窗 ≤ ~500MB；空闲 CPU < 1% |

产出：`spikes/S1.GlassText/RESULTS.md` 记录原始数据与结论截图。

### 2.5 Go / No-Go

- **Go**：H1–H4 全过，H5 达标或可解释地微调达标 → 进 S2（S2 其实可与 S1 并行，S2 不依赖 S1 结论，但 S1 失败则全线转方案 B，S2 成果作废，因此建议 S1 先行 1–2 天拿到 H1/H2 结论后再全力做 S2）。
- **No-Go**：H1 或 H2 失败 → 停止 A 线，启动方案 B 详细设计；图片环绕需求降级为"不支持"。

### 2.6 风险登记

| # | 风险 | 概率 | 应对 |
|---|---|---|---|
| R1 | Win2D 1.4.0（依赖 WASDK.WinUI 1.8 包系）与本项目 WASDK 2.5.1 包冲突，编译/运行失败 | 中 | 降级 1.3.3 → 仍不行则用 CsWin32 直连 D2D/DWrite + `ICompositionDrawingSurfaceInterop`，架构不变 |
| R2 | 透明 surface 在某些驱动/合成路径下呈现黑底 | 低 | 换 `CompositionVirtualDrawingSurface` 复测；仍黑 → H1 判败 |
| R3 | 每窗口一个 surface 的显存开销超预期 | 中 | 改共享 `CompositionGraphicsDevice`（S1 即按共享设计）；必要时 surface 池化 |

---

## 3. S2：浮动环绕排版引擎

### 3.1 范围（有意收窄）

- **做**：多段落纯文本 + 浮动矩形（图片抽象）的环绕排版；图片拖动实时重排；命中映射表（坐标 → 字符索引）的雏形。
- **不做**：多样式 run（粗斜体混排）、字号混合、编辑、撤销、IME、滚动虚拟化。这些属于 Phase 1+。
- spike 内文本样式统一（单一字体字号），把变量全部压到"浮动与行盒分割"这一个轴上——这是本项目的独创部分，其余都是成熟工程。

### 3.2 核心算法：带（Band）+ 行盒分割

**术语：**

- **浮动对象** `FloatRect`：图片抽象为文档坐标系中的矩形 + `side(Left/Right)` + `margin`。spike 只支持左右浮动（覆盖便签场景 95% 需求），居中浮动留接口不实现。
- **带（Band）**：垂直区间 `[yTop, yBottom)`，区间内与浮动矩形的相交关系恒定。
- **段（Segment）**：带内的可用水平区间。左浮图 → 右侧一段；右浮图 → 左侧一段；左右双浮 → 中间一段（若中间宽度不足则为零段）。

**算法主流程：**

```text
输入：段落列表、FloatRect 列表、内容宽度 W
1. 归一化排除区：所有 FloatRect 的 yTop/yBottom 排序去重 → 切出若干 Band；
   每个 Band 预计算其 Segments（[0,W] 减去相交浮动矩形及其 margin）
2. 逐段落排版，维护"当前 y 光标"：
   loop:
     a. 定位 y 光标所在 Band → 取其 Segments
     b. 对每个非空 Segment：用剩余文本以"段宽"做布局，只取第一行
        （CanvasTextLayout(remaining, segmentWidth, bandHeight)，
          读 LineMetrics[0] 得首行字符数 / 行高 / 基线，行宽取末字符后的插入符 X）
        —— 2026-10-02 修正：原方案用 DrawToTextRenderer 探基线 + GetCharacterRegions 求范围，
           实测单次 8.44ms（占首行探测 97%），换成 LineMetrics 后 0.33ms；见 S2 验收记录 §2.2
     c. 同 Band 多段共享基线：各段行高取 max(ascent)/max(descent)，
        统一基线后各自放置 —— 这就是"文字在图片两侧同一行对齐"的做法
     d. 消费文本，y 光标 += 行高；若 y 光标越过 Band 底 → 进入下一 Band 重取 Segments
3. 特殊情形：
   - 段宽 < 最小可排版宽度（借 GetMinimumLineLength 判定）→ 该段放弃，
     文本顺延至下一个有空间的段/Band（Word 同款行为，避免无限循环）
   - Band 剩余高度不足一行 → 跳到下一 Band（产生图片下方的自然留白）
4. 输出 LayoutResult：PlacedLine[](文本区间, 原点, 宽高, 基线) +
   FloatRect 终位置 + 文档总高 + 命中映射（行盒反查字符索引）
```

**"飞到另一边"就是本算法的自然推论**：拖动图片 → FloatRect 变化 → 步骤 1 的 Band/Segments 重算 → 文字自动从一侧让出、绕到另一侧。无需任何特判代码。

**增量重排（拖动流畅的关键）：**

- 排版结果按段落缓存；拖动浮动对象时，只使"与该浮动对象 Y 区间相交的段落 + 其后第一段"失效重排，其余段落只做 Y 平移。
- 拖动期间复用 `CanvasTextLayout` 对象池，不重建字体/格式资源。

### 3.3 接口草图（签名级）

```csharp
namespace LumiMemo.Layout;              // spike 期放 S2.WrapLayout.Engine

public record TextStyle(string FontFamily, float FontSize, /* spike 期仅此 */ );
public record FloatRect(int Id, Rect Rect, FloatSide Side, float Margin);

// 度量抽象：测试用假字体注入，Demo 用 Win2D 实现
public interface ITextMeasurer
{
    // 以 maxWidth 排版 text，返回首行信息
    FirstLineInfo LayoutFirstLine(string text, TextStyle style, float maxWidth);
}
public record FirstLineInfo(int CharsConsumed, float Width, float Ascent, float Descent, object? NativeLayout);

public interface ILayoutEngine
{
    LayoutResult Layout(IReadOnlyList<string> paragraphs, IReadOnlyList<FloatRect> floats, float contentWidth);
}
public record PlacedLine(int Paragraph, int CharStart, int CharCount,
                         Point Origin, float Width, float Baseline, object? NativeLayout);
public record LayoutResult(IReadOnlyList<PlacedLine> Lines,
                           IReadOnlyList<FloatRect> PlacedFloats, float TotalHeight);
```

- `ITextMeasurer` 是引擎与 Win2D 之间唯一的耦合点；单测注入等宽假字体（每字符宽 10、行高 20），让所有环绕用例**确定性可断言**（不需要 GPU、不需要窗口）。
- 命中映射：`LayoutResult` 上提供 `HitTest(Point) → (paragraph, charIndex)`，spike 只实现行盒级反查（行内字符级用 `GetCaretPosition`，留到 Phase 2）。

### 3.4 测试矩阵（单测，假字体驱动）

| # | 用例 | 断言 |
|---|---|---|
| T1 | 无浮动 | 逐行全宽，行数 = ⌈字符数×字宽 / W⌉ |
| T2 | 左浮图（高 3 行） | 前 3 行缩进图右缘，第 4 行起恢复全宽 |
| T3 | 右浮图 | 前 N 行右端截断于图左缘 |
| T4 | 左右双浮 | 两侧同时收窄，行中段对齐基线 |
| T5 | 图片高度 < 行高 | 仅 1 行受影响 |
| T6 | 段宽 < 最小宽度 | 该段放弃，文本顺延，不死循环 |
| T7 | 浮动跨段落边界 | 环绕作用于两段，段落间距正确 |
| T8 | 图片从左拖到右（序列） | 每帧 Band 重算正确，文字逐帧"流过"图片 |
| T9 | 两浮动 Y 区间部分重叠 | 归一化正确，无段丢失 |
| T10 | 图片底缘与行边界恰重合 | 无 1px 缝隙/重叠（浮点容差策略） |

### 3.5 可视化 Demo

复用 S1 的 `CompositionTextSurface`（代码副本）：窗口内一段固定长文本 + 一个可拖动矩形；拖动时实时重排重绘；叠加绘制 Band/Segment 调试框线（F12 开关）。该 demo 同时是 S1 H3/H4 的第二验证场。

### 3.6 性能预算与测量

| 场景 | 预算 | 测量 |
|---|---|---|
| 10,000 字符 + 3 个浮动，全量排版 | < 8ms | Stopwatch，Release，热身 3 次取中位 |
| 拖动单浮动，增量重排（3000 字符文档） | < 4ms/帧 | 拖动循环内逐帧记录，P95 达标 |
| Demo 拖动帧率 | ≥ 55fps | 逐帧时间直方图 |

### 3.7 Go / No-Go

- **Go**：T1–T10 全过 + 性能达标 + Demo 目视流畅 → 排版引擎设计冻结，接口原样进入 Phase 1 正式工程。
- **条件 Go**：正确性全过但性能不达标 → 先优化（段缓存粒度、布局对象池），最多一轮迭代；仍不达标则下调目标（拖动期降采样重排：拖动中按节流到 30fps 重排，松手后精排——这是可接受的产品级折中，不算失败）。
- **No-Go**：环绕正确性做不到（理论上不应发生——算法是自包含的）→ 转方案 B 并放弃环绕。

**实际结果（2026-10-02）**：T1–T10 全过；性能经一轮优化（首行探测由 `DrawToTextRenderer` +
`GetCharacterRegions` 换成 `LineMetrics`，单次 8.44ms → 0.33ms，全量排版 1900ms → 75.1ms）后，
[A] 75.1ms / [B] 24.4ms 仍超预算、[C] Demo 拖动帧率 143.6fps 达标（小文档口径），判**条件 Go**：
引擎对外接口沿用，把"按段宽批量取行"的结构优化列为 Phase 1 待办
（解药量级实测 7.86ms，见 `src/LumiText/LumiText.Demo/RESULTS.md` §2.3）。
判据是真实便签普遍远小于 1 万字符，当前结构在小文档上已达标（Demo 约 3–4ms/帧，用户实测流畅）。

---

## 4. Spike 之后的衔接（预览，非本阶段任务）

- S1 的 `CompositionTextSurface` → 迁入 `LumiText.WinUI` 作为渲染层基座；`CompositionVirtualDrawingSurface` 替换以支持长便签滚动。
- S2 的 `ITextMeasurer` / `ILayoutEngine` / `LayoutResult` → 迁入 `LumiText.Core`（零 UI 依赖）；Win2D 度量实现进 `LumiText.WinUI`。文档模型也放 `LumiText.Core`——按 D5 决策，**不进 LumiMemo.Core**，保证库可独立拆仓开源。
- S3（TSF）在 Phase 2 开工前补做，其 spike 复用 S1 宿主 + S2 引擎。
- 正式库工程自带独立的 `Directory.Packages.props` 与测试工程，不依赖宿主仓基础设施；spike 目录在产物迁移完毕后整体删除（git 历史保留）。

## 5. 风险汇总与回退触发器

| 触发器 | 动作 |
|---|---|
| S1 H1/H2 失败 | 全线转方案 B（ITextHost），放弃图片环绕 |
| S1 R1 无法解决（Win2D 与 WASDK 2.5.1 不兼容且 CsWin32 兜底成本失控） | 重新评估是否主项目降级 WASDK；仍不可行 → 方案 B |
| S2 正确性失败 | 方案 B，放弃环绕 |
| S2 性能失败（节流折中后仍卡） | 环绕降级为"拖动结束才重排"，继续 A 线 |
| S1/S2 全过 | 冻结引擎接口，产出 Phase 1（只读渲染器）详细设计 |

---

> 确认本设计后，下一步：搭建 `spikes/` 工程骨架并开始 S1 的 H1/H2 最小验证（此时才会开始写代码）。
