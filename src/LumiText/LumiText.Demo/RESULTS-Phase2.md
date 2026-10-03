# Phase 2 验收记录

环境：Windows 11（10.0.26300）/ .NET 10.0.401 / WASDK 2.5.1 / CsWin32 0.3.333；
实机显示器 3072×1280 @125% 缩放（「文字发虚」问题的复现环境，见 §6）。

---

## 1. M0 前置：CsWin32 TSF 覆盖实测（U-TSF1）—— 通过

**目的**（Phase 2 设计 §6.1/§11）：验证 `Microsoft.Windows.SDK.Win32Metadata`
是否投影 `msctf.h` / `textstor.h` 的 TSF 接口——不覆盖则整个 `ITextStoreACP2`
实现要退回手写 COM vtable，工作量与出错率显著上升。

**方法**：`spikes/S3.TsfProbe/`（独立 Console 工程，不触主代码），把 TSF 接口名写进
`NativeMethods.txt`，编译看 CsWin32 是否生成对应类型。

**结论（2026-10-03 实测）**：**覆盖，可用**。

- 接口全部投影（`Windows.Win32.UI.TextServices` 命名空间）：
  `ITfThreadMgr` / `ITfDocumentMgr` / `ITfContext` / `ITfContextComposition` /
  `ITextStoreACP2` / `ITextStoreACP` / `ITextStoreACPSink` / `ITextStoreACPServices`；
- 结构体全投影：`TS_TEXTCHANGE` / `TS_SELECTION_ACP` / `TS_STATUS` / `TS_ATTRVAL`；
- 枚举可用：`TEXT_STORE_LOCK_FLAGS` / `TEXT_STORE_TEXT_CHANGE_FLAGS` / `TF_E_*`；
- `CLSID_TF_ThreadMgr = 529a9e6b-6587-4f23-ab9e-9c7d683e3c50` 投影正确；
- **不在 metadata、需手写**：
  - typedef：`TfClientId` / `TfEditCookie` / `TsViewCookie`（→ `uint`）、`TS_ATTRID`（→ `Guid`）；
  - 位常量：`TS_AS_ALL_SINKS` / `TEXT_STORE_SINK_FLAGS` / `TF_STATUS`（`TS_SD_*` / `TS_SS_*`）；
  - `IID_*`：走 `typeof(ITfThreadMgr).GUID` / `typeof(ITextStoreACPSink).GUID`。

**对设计的影响**：§12 风险 R-TSF-1 解除——`ITextStoreACP2` 直接经 CsWin32 生成的
COM 接口实现即可，无需手写 vtable；`TsfTextStore` 实现量约一个文件。

**产物**：`spikes/S3.TsfProbe/`（csproj + NativeMethods.txt + Program.cs）。
按 Phase 0 惯例，Phase 2 验收通过后整体删除；期间作为 CsWin32 覆盖的回归参照。

---

## 2. M1–M4：编辑内核 + 字符级几何 + LumiEditor + TSF 产品化 —— 通过（52f4b76）

### 2.1 交付物

- **Core 编辑层**（`LumiText.Core/Editing/`）：`EditorState`（不可变 Document + TextRange 快照）、
  `IEditCommand`（只含 `Apply`）、`EditorCore`（命令执行 + 撤销/重做 + 选区）、
  `UndoStack`（快照式，容量 1000，连续 `InsertTextCommand` 合并）；
  命令集 Insert/DeleteRange/Split/Merge/ApplyInlineStyle/SetInlineStyle/InsertImage；
- **重要设计修订**：撤销栈实施为**快照式**而非原设计的 Inverse 命令——`DeleteRangeCommand`
  无法从命令参数重建被删内容，Inverse 方案不成立；快照借 record 结构共享，内存代价可忽略；
- **字符级几何**（M2）：`ILineBatch` 扩展 `HitTestChar`/`GetCaretGeometry`/`GetCharRegions`
  （统一「批文本流坐标」）；`LayoutResult.HitTest` 字符级命中；`CaretGeometryCalculator`
  光标/选区矩形；
- **`LumiEditor` 骨架**（M3）：`VirtualizedTextSurface` + `FlowDocumentRenderer` + `EditorCore`
  组合；键盘（`PreviewKeyDown` + `_scroller.CharacterReceived`，`IsTabStop = true` 是
  CharacterReceived 触发的铁律）、指针拖动选区、光标闪烁；
- **TSF 产品化**（M4）：`TsfTextStore`（`ITextStoreACP2` + `ITextStoreACP` + `ITfContextOwnerCompositionSink`
  **同一对象全实现**——CsWin32 投影的接口无继承链，QI 才能成功）、`TsfManager` 生命周期、
  组字期 `UserEdited` 抑制、候选窗屏幕坐标定位。

### 2.2 关键排坑（均有实测证据）

1. **WinUI 3 UI 线程非 STA**：`CoCreateInstance(CLSID_TF_ThreadMgr)` 返回 0x80040154 →
   改 `TF_CreateThreadMgr`（msctf.dll 导出，官方跨 apartment 入口）；
2. **`CreateContext` 的 punk 必须是 TextStore 本身**（否则 TSF QI 失败，IME 静默不工作）；
3. 候选窗位置：`TransformToVisual` 只给 island 相对坐标 → `ClientToScreen(0,0)` 补屏幕原点；
4. 键盘铁律：`_scroller.IsTabStop = true`——为 false 时 `Focus()` 静默失败，
   `CharacterReceived` 永不触发（字符输入全哑）；命中测试宿主 `Background` 必须
   设全透明画刷（S2 铁律延续，指针事件才触发）。

### 2.3 验收

用户实测：中文 IME 组字、候选窗跟随光标——「好使了」；候选窗位置修正后
「没啥大问题了」。（切窗后输入法保持问题在 M7 主程序验证时暴露，402708e 修复。）

---

## 3. M5：剪贴板互通 + RTF 投影 —— 通过（fa65e7b）

- 复制/剪切：纯文本 + RTF 双格式；粘贴：纯图拦截 → RTF → 纯文本降级链；
- RTF 投影最小集：`\b` `\i` `\ul` `\strike` + `colortbl` + `\par` + `\uN`（流式语义：
  样式无闭合持续生效）；元数据组跳过（SkipKind 栈）；
- 排坑：`\fonttbl` 生成缺右大括号→整篇被当未闭合组吞掉（全文本丢失），已修；
- 验收：用户实测（「已验证 功能正常」，2026-10-03）。

---

## 4. M6：粘贴插图（O4 降级形态）—— 通过（c9c0554）

- 纯位图剪贴板拦截（280px 长边上限，对齐现产品）→ `BitmapDecoder` 解码 →
  `ImageResource` + 浮动 `ImageBlock` **锚定当前字符**（O4 决策：不做 `SetInlineObject`
  行内混排，规避 R-E3；浮动锚定保住功能对等底线）；
- 验收：粘贴图片正常（用户实测）。

---

## 5. M7：`.lumi v2` 存储 + 主程序切换 —— 通过（0687fde，选 B 直接切换）

- **存储**：`StoredNote.Document`（v2 JSON 字节，不透明）+ `FormatVersion 2`；
  v1（RTF）文件不迁移、跳过并记日志（用户拍板：开发阶段无历史包袱）；
- **窄接口**：`IRichTextDocument.SaveRtf → SaveContent()` / `LoadAsync(byte[])`；
  `LumiEditorDocument` 薄适配器放主程序侧（避免 LumiText→LumiMemo 反向依赖）；
- **主程序直接切换**（用户拍板选 B）：`MainWindow` 换挂 `LumiEditor`，自动保存经
  `UserEdited` 事件（组字期由 TSF 层抑制）；
- 验收：便签可编辑、中英文输入正常、保存重开通过（用户实测）。

---

## 6. 切换后修复与对等补全（M7 后、M8 前）

### 6.1 拉大窗口卡死崩溃 —— 修复（89fe8ad）

surface 物理像素超 D3D11 纹理上限 16384 → `CreateDrawingSession` 抛 ArgumentException。
修法：`MaxSurfacePixelEdge = 16000` 钳制 + 异常兜底。

### 6.2 切窗后 IME 掉回英文 —— 修复（402708e）

M7 实测暴露：打开便签 → 在别的窗口复制 → 切回便签，输入法锁死英文。
根因：TSF 文档焦点在窗口切换中丢失（WinUI 3 非 STA 下的焦点路径差异）。
修法：`AssociateFocus`（声明式关联）+ 窗口 `Activated` 时 `Refocus`（命令式重设）
双保险——都是合法 TSF 用法，覆盖不同焦点路径。修复后用户确认「现在正常了」。

### 6.3 功能对等补全 —— 通过（c225b3d）

- **bullet/todo 命令**：`ParagraphBlock.IsBullet`（v2 JSON 新字段，缺省 false 兼容）+
  `ToggleBullet`/`ToggleTodo`/`ToggleTodoChecked` 命令；工具栏两按钮不再 no-op；
  渲染层块首行缩进区画实心圆点/矢量复选框；
- **复选框点击**：点 todo 行首缩进区切换勾选；
- **双击选词/三击选段**（`PointerUpdateKind` 无双击语义，按下计数自维护：500ms+4px 窗口）；
- **计划外必要修复**：空块原本没有行盒——空 bullet/todo 标记画不出、空行点击无光标落点；
  引擎为空块补零字符占位行盒。

### 6.4 文字发虚 —— 修复（f61f7f9）

- **根因**（对照实验钉死）：surface 建纹理用 `ceil(DIP × scale)` 取整，sprite 尺寸却用
  原始 DIP 值——`Stretch=Fill` 把 ceil 多出的亚像素压回屏幕，整面纹理重采样，笔画发虚。
  125% 缩放下非整数 DIP 尺寸必然触发；
- **修法**：`sprite.Size = 纹理像素 / scale`（像素反推 DIP），纹理→屏幕严格 1:1；
  同类三处（`VirtualizedTextSurface`/`CompositionTextSurface`/Demo 探针窗口）一并修；
- 验收：`--probe edit` 与主程序均清晰（用户实测）。

---

## 7. M8：收尾验收 —— 总判定 **Go**

### 7.1 验收口径修订（用户拍板，2026-10-03）

设计稿 §10.2 的「视觉回归对照（与 `LumiDocumentView` 并排像素一致）」**取消**——
用户决策：不需要一比一还原，改为用户直接检查界面并给出调整要求
（本文件 §6.4 的发虚修复即该流程的首次闭环）。

### 7.2 测试底数

- `LumiText.Core.Tests`：**153/153 全绿**（命令/撤销/选区提取/字符命中/几何/序列化/排版）；
- `LumiMemo.WinUI.Tests`：**77/77 全绿**；
- `LumiText.slnx`、主程序 `LumiMemo.WinUI` 均构建通过。

### 7.3 用户实测清单（逐里程碑验证记录）

- **M5 剪贴板**：「已验证 功能正常」；
- **M6 粘贴插图**：「粘贴图片正常」（同时暴露拉大窗口崩溃，修复后验证通过）；
- **M7 主程序切换**：验证要点（便签可编辑/自动保存「已保存」/关闭重开）——
  编辑可输入、自动保存与重开闭环通过；编辑中发现「切窗后 IME 掉回英文」，
  修复后「现在正常了」。
- **文字清晰度**：`--probe edit`「上下都清晰了」、主程序「也清晰了」；
- 分点/待办工具栏、复选框点选、双击/三击选段（c225b3d）：Core 单测覆盖，
  界面按「用户检查给调整要求」流程随用随验（本次收尾未单独报问题）。

### 7.4 Go / No-Go 判定

§10.3 口径：**Core 测试全绿 ✅ + S3 探针三条件满足 ✅（M4 实测替代独立三截图）+
编辑冒烟全过 ✅**；「视觉回归一致」项经用户拍板以「直接界面验收」替代 ✅。

**判定：Go——编辑层冻结，可进 Phase 3（超越原生）设计。**

### 7.5 已知遗留（不阻塞 Phase 3）

1. **行内图片混排**：O4 降级为浮动锚定，`SetInlineObject` 路线未走——按需在 Phase 3 重新评估；
2. **R7 设备丢失重建**：仍未实装（Phase 1 挂账延续，便签场景罕见）；
3. **上下方向键 / Home / End** 光标移动未接入（左右可用）；
4. **旧内核 `RichEditorHost`/`ImageAdorner`** 保留在库中，待 Phase 4 退役；
5. **`spikes/` 目录**（含 `S3.TsfProbe`）按惯例可删，保留作回归参照。

