# LumiText

为毛玻璃（Acrylic）窗口而生的自研文本渲染内核：文档模型 + 浮动环绕排版引擎 + Composition/Win2D 渲染层。

> 本 README 为占位（Phase 1 M1 工程自包含验收项），正式内容随开源拆仓补全。

## 工程结构

| 工程 | 说明 |
|---|---|
| `LumiText.Core` | 文档模型 + 排版引擎（Band + 行盒分割的浮动环绕算法）。零 UI 依赖、零第三方依赖，可无头单元测试。 |
| `LumiText.Core.Tests` | 等宽假字体驱动的确定性单测（无需 GPU/窗口）。 |
| `LumiText.WinUI` | Win2D 文本度量实现 + Composition surface 渲染层 + 宿主控件。 |
| `LumiText.Demo` | 演示与验收 app（环绕排版 Demo、性能验收、视觉回归主场）。 |
| `tools/` | 截图验收管线（screenshot / bmp2png / crop_zoom / drag）。 |

## 构建

```bash
dotnet build LumiText.slnx
dotnet test LumiText.Core.Tests/LumiText.Core.Tests.csproj
```

要求：.NET 10 SDK、Windows SDK 10.0.26100、x64。

## 设计文档

开发期设计文档暂存于宿主仓 `docs/design/`（`custom-renderer-framework.md` 及 Phase 系列详细设计），拆仓时随库平移。
