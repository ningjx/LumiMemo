# LumiMemo

Windows 便签应用：常驻托盘、免安装、毛玻璃外观，内置一套自研的富文本渲染与编辑内核 **LumiText**
（Composition + Win2D，不依赖 RichEditBox）。

## 特性

- **便签**：多张便签；启动只恢复便签、不弹管理器窗口（无感启动）；托盘常驻，单击开关、右键菜单
- **编辑器**：自研 LumiText 内核——粗体 / 斜体 / 下划线 / 删除线、H1–H3 标题、分点列表、
  待办复选框、行内文字底色、图片（粘贴 / 拖放插入、拖动改锚点、八向手柄缩放、文字实时绕排）
- **中文排版**：避头尾（标点不起行）、图片四周对称留白、拖动与缩放时文字实时重排
- **存储**：私有 `.lumi` 单文件便签（v2 JSON）——纯本地、不依赖任何云同步；回收站可恢复
- **外观**：Win11 毛玻璃（`DesktopAcrylicController`，窗口失焦不降级为实色）
- **可选**：用 OpenAI 兼容接口自动生成便签标题（自行配置服务地址与密钥）

## 平台与依赖

- Windows 10 1809 或更高（Windows 11 上毛玻璃效果最佳）
- .NET 10 / WinUI 3（Windows App SDK 2.5）/ Win2D / CommunityToolkit.Mvvm / H.NotifyIcon

## 运行

从 [Releases](../../releases) 下载 `LumiMemo-win-x64.zip`，解压后双击 `LumiMemo/LumiMemo.exe`。
免安装，也无需另装 .NET 或 Windows App SDK 运行时（全部自包含）。

> 压缩包约 110MB，解压后约 290MB——自包含的代价。首次启动会稍慢。
>
> 不使用单文件 exe：WinUI 3 不支持单文件发布（原生依赖必须与 exe 同目录），
> 单文件版会在启动时静默失败。

## 开发

```bash
dotnet build src/LumiMemo.sln
```

在 Visual Studio 里启动 `src/LumiMemo.WinUI`（WinUI 3 免打包应用）。

测试共四套，各自进目录用 `dotnet run` 跑（原生 MTP 模式）：

```bash
cd src/LumiText/LumiText.Core.Tests && dotnet run
```

其余三套：`src/LumiMemo.WinUI.Tests`、`src/LumiMemo.Core.Tests`、`src/LumiMemo.Integration.Tests`。

## 仓库结构

| 目录 | 内容 |
|---|---|
| `src/LumiMemo.WinUI` | 应用本体：便签窗口、托盘、管理器（列表/设置/回收站） |
| `src/LumiMemo.Core` | 领域模型与存储契约 |
| `src/LumiMemo.Infrastructure` | 存储、日志、单实例、标题生成等实现 |
| `src/LumiText/LumiText.Core` | 自研文本内核：文档模型 + 排版引擎（零 UI 依赖） |
| `src/LumiText/LumiText.WinUI` | Composition/Win2D 渲染、`LumiEditor` 编辑控件、TSF 输入接入 |
| `docs/design` | 设计文档与各阶段验收记录 |

## 设计文档

换内核的动机、模型与排版契约、编辑层、以及后续阶段的决策与验收都写在 `docs/design/`：

- `custom-renderer-framework.md`——为什么要自研渲染器
- `phase1-readonly-renderer-design.md` / `phase2-editing-layer-design.md`——排版与编辑层
- `phase3-beyond-native-design.md` / `phase4-old-kernel-retirement-design.md`——超越原生与旧内核退役

## 开源协议

[MIT](LICENSE)
