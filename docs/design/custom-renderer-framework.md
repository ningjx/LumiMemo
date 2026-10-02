# 自研文本渲染器 — 框架设计方案（v0.1 框架稿）

- 状态：**Phase 0（spike）与 Phase 1（只读渲染器）均已验收；Phase 2（编辑层）待设计**
- 进度总览（2026-10-03 更新）：
  - 框架已确认（2026-10-02）；
  - **Phase 0 已验收（条件 Go）**：S1 玻璃自绘文本 / S2 浮动环绕排版——结果见
    `spikes/S1.GlassText/RESULTS.md`、`src/LumiText/LumiText.Demo/RESULTS.md`，
    详细设计 [phase0-spike-design.md](phase0-spike-design.md)；
  - **Phase 1 已验收（总判定 Go，2026-10-03）**：文档模型（.lumi v2 schema 冻结）+
    批量排版引擎 + 块级渲染（标题/待办/分割线）+ 浮动锚定 + 图片管线 + 滚动虚拟化 +
    视觉回归对照——Core 测试 54/54 全绿，验收记录
    `src/LumiText/LumiText.Demo/RESULTS-Phase1.md`，
    详细设计 [phase1-readonly-renderer-design.md](phase1-readonly-renderer-design.md)；
  - 下一步：Phase 2（编辑层：光标、选区、撤销、TSF 输入法）详细设计；
    遗留优化项见 RESULTS-Phase1 §10.4（窄段预筛/文本封顶/CJK 基准变体等，不阻塞）。
- 日期：2026-10-02
- 范围：替换 RichEditBox 的便签正文渲染/编辑内核
- 约束：不破坏现有毛玻璃窗口（`DesktopAcrylicController` 失焦不降级是迁移到 WinUI 的根本原因，属于硬约束）

---

## 1. 问题定位

现有正文 = WinUI 原生 `RichEditBox` + RTF 持久化，渲染完全交给系统 RichEdit 引擎。
已确认的硬天花板（见探索结论，均有代码佐证）：

| 需求 | RichEditBox 现状 |
|---|---|
| 图片浮动、文字环绕（文字被图片推挤、绕到另一侧） | ❌ 图片只能是内嵌字符（U+FFFC），基线对齐，无浮动概念 |
| 行/段落背景（代码块底色、引用条、高亮） | ❌ `ITextCharacterFormat.BackgroundColor` 在 WinUI 3 上不生效（平台已知限制） |
| 真复选框待办 + 悬停自定义光标 | ❌ `MarkerType` 只有内置符号；现为"行首 ☐/☑ 字符 + 命中模拟" |
| 类 Markdown 多级标题 | ⚠️ 引擎支持字号，但只是字符格式，没有块级语义 |
| 自定义光标颜色/形状、布局动画 | ❌ 光标是"反色像素"，无公开装饰层/动画钩子 |
| 排版动画（图片移动时文字流动过渡） | ❌ 引擎内部排版，外部无法参与 |

结论：**继续在 RichEditBox 上叠加 hack（覆盖层、RTF 正则改写）已到收益尽头，需要换内核。**

## 2. 候选方案与排除理由（均有查证，非推断）

### 方案 C：WebView2 + ProseMirror/Milkdown —— **排除**

- 功能是够的（CSS float / shape-outside / cursor / 标题都是现成能力），项目历史上也做过（ADR 0001）。
- 但微软官方文档（Visual layer overview）明确：WinUI 3 中 **WebView2 属于 external content**，"合成器内容不可能位于外部内容之后"，透明背景看不到后方内容；microsoft-ui-xaml issue #6527 实测确认透明失效。
- 即：WebView2 编辑器区域会成为一块不透明补丁，**直接违反毛玻璃硬约束**。另有多便签窗口下每窗一个浏览器进程的资源问题。
- 结论：排除，不进入原型阶段。

### 方案 B：无窗口 RichEdit 宿主（ITextHost / ITextServices）—— **保底**

- 原理：加载 `msftedit.dll`，`CreateTextServices` 拿到微软的完整编辑引擎（IME、撤销、选区、拼写、emoji），自己实现 `ITextHost` 接管绘制表面。
- 能解锁：自定义绘制（行背景、装饰）、自定义光标、部分动画；编辑行为与系统一致，IME 零成本。
- **锁死项**：图片浮动环绕。RichEdit 排版引擎只支持内嵌图片对象（RichEdit 8 起支持 PNG/JPG/GIF 的 `ITextRange2::InsertImage`），没有 Word 式的浮动/排除区排版能力；查证未见任何官方支持证据。
- 定位：若方案 A 的原型验证失败，退到此方案，接受"无图片环绕"。

### 方案 A：自研排版渲染内核（DirectWrite + Composition）—— **推荐**

- 全部需求的所有权都回到自己手里：排版、绘制、光标、动画、命中。
- 关键技术事实（已查证）：
  - `CompositionDrawingSurface` / `CompositionVirtualDrawingSurface` 是**合成器内容**（非 external content），支持 Win2D/D2D 互操作绘制、预乘 Alpha、Resize/Scroll/虚拟化按需绘制 → **透明 surface 叠在窗口上，毛玻璃可从文字间隙透出**，与 acrylic 共存无冲突。
  - DirectWrite 的 `IDWriteTextLayout` 不支持浮动排除区，但它提供逐行/逐字形度量 → 浮动环绕用**行盒分割算法**自己实现（把每行可用宽度按图片排除矩形切成若干段分别排版，行高越过图片底部后恢复全宽——这就是"文字飞到另一边"的数学本质，杂志式排版）。
  - Composition 动画引擎可对任何 visual 属性做补间/隐式动画 → 勾选动画、图片拖动时文字流动过渡等"排版动画"成为可能，这是 RichEditBox 结构性给不了的。
- 代价：编辑层全部自建（选区、撤销、剪贴板、**TSF 输入法**、UIA 无障碍），其中 TSF 是最大风险点。

## 3. 推荐架构（方案 A，框架视图）

```
┌─────────────────────────────────────────────────┐
│ 宿主控件层  LumiEditor (WinUI Control)           │
│ · 实现现有 IRichTextDocument 窄接口 → VM 层零改动  │
│ · 输入路由（键鼠/触摸/笔）、焦点、滚动             │
│ · 自定义光标（悬停待办框/图片手柄）                │
│ · RichEditBox 保留为降级路径（开关切换）           │
├─────────────────────────────────────────────────┤
│ 编辑层  Editor Core                              │
│ · 光标/选区状态机、命令系统、撤销栈               │
│ · 剪贴板（RTF/纯文本/位图互通，兼容外部粘贴）       │
│ · IME：TSF (ITextStoreACP2) ← 最大风险点         │
│ · 无障碍：UIA TextProvider（可延后，留接口）       │
├─────────────────────────────────────────────────┤
│ 渲染层  Renderer                                 │
│ · CompositionVirtualDrawingSurface + Win2D       │
│ · 逐行盒/字形绘制、图片、行背景、装饰              │
│ · Composition 动画（隐式动画驱动排版过渡）         │
├─────────────────────────────────────────────────┤
│ 排版引擎层  Layout Engine（纯计算，无 UI 依赖）    │
│ · DirectWrite 逐段排版、字体回退、emoji           │
│ · 浮动系统：排除矩形 + 行盒分割（环绕核心）        │
│ · 产出 LayoutTree：行盒/字形坐标 + 命中测试映射    │
├─────────────────────────────────────────────────┤
│ 文档模型层  Document Model（LumiMemo.Core）      │
│ · Block：段落/标题(H1-H3)/待办/图片块/分割线      │
│ · Inline：样式 run（粗/斜/删/下划/色）+ 行内图片  │
│ · 持久化：.lumi v2（JSON 内嵌新正文结构）         │
└─────────────────────────────────────────────────┘
```

**设计红线：**

1. 排版引擎与文档模型**零 UI 依赖**，放 Core/独立工程，可无头单元测试（延续项目现有 VM 可测传统）。
2. 宿主对上层只暴露 `IRichTextDocument` 同款窄接口，`NoteViewModel` / `AutoSaveService` / 存储层**不感知换内核**。
3. 不引入任何 external content（WebView2 / SwapChainPanel），渲染只走 Composition surface，保毛玻璃。
4. RTF 不再作为权威格式；新模型为权威，RTF 仅用于剪贴板互通。

**新引入依赖（需确认）：**

| 依赖 | 用途 | 说明 |
|---|---|---|
| Microsoft.Graphics.Win2D（WinAppSDK 版） | D2D/DWrite 托管封装、CanvasTextLayout、surface 互操作 | 微软官方维护，MIT |
| Microsoft.Windows.CsWin32（已有） | TSF / 未托管互操作补充 | 仅 Infrastructure/渲染工程开 unsafe（沿用现有先例） |

## 4. 分期路线（每期独立验收，前一期间不过不进入下一期）

### Phase 0 — 原型验证（spike，不动主代码，独立小工程）

| Spike | 验证内容 | Go 标准 |
|---|---|---|
| S1 玻璃上的自绘文本 | Composition surface + Win2D 画文本，叠在 DesktopAcrylic 上 | 毛玻璃从文字间隙透出；失焦不降级；100%/150%/200% DPI 清晰；20 窗口内存达标 |
| S2 环绕排版 | 行盒分割算法：图片左/右浮动，文字两侧流动，越过图片底部恢复全宽 | 环绕正确；图片拖动时重排版 < 一帧预算 |
| S3 TSF 输入法 | 最小 ITextStoreACP2 闭环 | 中文 IME 组字、候选窗跟随光标、组合期不触发自动保存 |

**任一 Spike 失败 → 转入方案 B 详细设计，图片环绕需求标记为放弃。**

### Phase 1 — 只读渲染器

新模型 → 排版 → 绘制全链路，能只读渲染便签内容。无编辑。此期结束即可对照 RichEditBox 做视觉回归。

### Phase 2 — 基础编辑

光标/选区、键入/删除、IME、撤销重做、剪贴板、自动保存接通。达到与现有 RichEditBox **功能对等**（粗斜删下划、分点、伪待办、内嵌图片）。

### Phase 3 — 超越原生（本次换内核的动机所在）

H1–H3、真复选框待办 + 悬停自定义光标 + 勾选动画、行/段落背景、**图片浮动环绕**、图片拖动时文字流动动画。

### Phase 4 — 切换与退役

开关灰度切换 → 全量默认 → RichEditBox 路径退役（或长期保留为设置项，届时再定）。

## 5. 决策记录（2026-10-02 已全部确认）

1. **路线选择**：方案 A（自研，全功能，高投入）为主线、方案 B（ITextHost，无环绕）为保底 —— ✅ 同意。
2. **权威格式**：新文档模型取代 RTF 成为 .lumi v2 权威格式（RTF 只做剪贴板互通）—— ✅ 同意。
   - 2026-10-02 修订：**不做旧 RTF 迁移器**。程序处于开发阶段，无历史数据需要迁移，
     新模型直接作为唯一正文格式；计划中凡涉及"迁移器/旧数据导入"的表述一并清除。
3. **Phase 0 范围**：只做 S1（玻璃+自绘）+ S2（环绕排版）；S3（TSF 输入法）延后至 Phase 2 开工前 —— ✅ 确认。
4. **UIA 无障碍**：首版留接口不实现 —— ✅ 接受。
5. **工程形态（D5，2026-10-02 追加）**：渲染器做成**独立工程，不依赖 LumiMemo 任何代码**；主程序以项目引用接入，后续可拆出独立维护并开源为可单独安装的 NuGet 包 —— ✅ 确认。
   - 命名：`LumiText`（开源友好、不带宿主产品名；正式发布前可改）。
   - 拆分为两个程序集：
     - `LumiText.Core`：文档模型 + 排版引擎 + `ITextMeasurer` 等抽象，**零 UI 依赖、零第三方依赖**，可无头测试；
     - `LumiText.WinUI`：Win2D 度量实现 + Composition 渲染层 + 编辑层 + `LumiEditor` 宿主控件，依赖 Win2D/WASDK。
   - 主程序（LumiMemo.WinUI）只引用 `LumiText.WinUI`，经 `IRichTextDocument` 同款窄接口对接。
   - 目录按"未来可直接拆仓"组织：工程自包含（自带测试、props、README 占位），不引用宿主仓的 CPM 版本表以外的任何东西；开源时整体平移即可。

---

> 下一步产出（已完成）：Phase 0 详细设计 [phase0-spike-design.md](phase0-spike-design.md)。
