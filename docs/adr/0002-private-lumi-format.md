# ADR 0002：私有 .lumi 格式，放弃外部编辑兼容

- 状态：已实施
- 日期：2026-10-01
- 决策范围：文件格式、外部同步、编辑器方向、WPF 项目的去留

## 1. 背景

1. WPF 版的毛玻璃有一个无解硬伤：DWM 系统亚克力在窗口**失焦时按系统设计退成实色**，
   应用侧无法改变（两条修复路径实测无效并回退，见提交 efa930f → 637de2b、3910ac2）。
   WinUI 3 的 `DesktopAcrylicController` 由应用自控 `IsInputActive`，失焦玻璃不降级。
   便签外观是产品的核心体验，这促成主线从 WPF 转向 WinUI。
2. 转向 WinUI 后编辑器改为原生 `RichEditBox`，内容载体是 RTF。而原「Markdown 为唯一
   真实数据源、与 Obsidian 互通」的前提（v2 文档 §5/§10/§11 与 ADR 0001）建立在一整套
   外部编辑兼容机制上：文件监听、三路比较、冲突裁决、Front Matter 保留、
   编码/行尾/BOM 逐字节保真。这套机制的每一个部件都在服务 WPF/Markdown 组合。

## 2. 决策

1. 便笺采用私有 `.lumi` 格式（JSON：`version/id/rtf/text/color/tags/createdAt/updatedAt/autoTitle`），
   一张便笺一个文件，**只由本程序读写**。
2. 放弃外部编辑兼容：不接受、不检测、不同步外部修改；文件监听、冲突裁决、
   Front Matter/编码保真整条机制退役。
3. `rtf` 为权威内容；`text` 是纯文本投影，供标题派生、搜索与字数统计。
4. WPF 项目（`LumiMemo.App`、`LumiMemo.App.Tests`）与 WebView2/Milkdown 前端（`editor/`）
   整体删除；WinUI 版成为唯一产品线。
5. 单实例成为必需（防两个本程序实例互写同一文件），接入 `SingleInstanceGuard`。

## 3. 后果

- 存储实现简化为 `LumiNoteStorage`（`Core.INoteStorage`：LoadAll / Save / Create），
  容错降级为「坏文件跳过 + 日志」的容错矩阵。
- `Note` 模型去掉 `LineEnding/HadBom/FrontMatterTail/UnknownFrontMatterKeys/ParseIssues`
  与 `CopyFrom/HasSameUserDataAs`。
- 旧 `.md` 便笺不再被读取（由用户自行处置，不做导入）。
- 既有 `.lumi` 文件的字段形态（PascalCase 属性名、数字色值、RTF base64）
  被确立为**格式契约**，由 `LumiNoteStorageTests` 手写契约钉住。
- v2 文档 §5 / §10 / §16 等章节以「2026-10-01 决策注记」标记失效；其余章节仍然权威。

## 4. 被取代的决策

- [ADR 0001](0001-markdown-editor.md)（Milkdown + WebView2CompositionControl 编辑内核）：
  随 WPF 删除而废弃。
