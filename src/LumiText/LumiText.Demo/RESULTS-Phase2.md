# Phase 2 验收记录

环境：Windows 11（10.0.26300）/ .NET 10.0.401 / WASDK 2.5.1 / CsWin32 0.3.333。

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
