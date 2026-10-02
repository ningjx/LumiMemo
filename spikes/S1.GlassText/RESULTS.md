# S1 验收记录（2026-10-02）

环境：Windows 11，150% DPI，.NET 10.0.401，WASDK 2.5.1，Win2D 1.4.0。

| 假设 | 结果 | 证据 |
|---|---|---|
| H1 文字间隙透出毛玻璃 | ✅ **通过** | `shot_focused.png`：Win2D 自绘文字浮于玻璃之上，背后桌面内容模糊透出，无白/黑色块；半透明装饰条混合正常 |
| H2 失焦玻璃不降级 | ✅ **通过** | `shot_unfocused.png`：标题栏已呈失焦态，玻璃仍模糊透背景，未退实色（IsInputActive=true 对自绘区域同样成立） |
| H3 文字清晰无彩边 | ✅ **通过（150% DPI）** | `crop_text.png`（2 倍放大）：灰阶 AA 均匀，中英/emoji 无彩色边缘 |
| H4 多档 DPI / 跨屏 | ⏳ 待验 | 当前仅验证了 150%；100%/200% 与跨屏拖动在 Phase 1 前补测 |
| H5 20 窗内存/CPU | ⏳ 待验 | 单窗工作集 ~131MB（含自包含运行时基线，主程序基线同量级）；20 窗矩阵待 BenchmarkWindow 实施后测 |

## 构建结论（R1 排除）

- Win2D 1.4.0 与 WASDK 2.5.1 **编译运行正常**。WASDK 2.5.1 元包自身传递依赖 `Microsoft.WindowsAppSDK.WinUI 2.3.9`（WinUI 子包线无 2.4+），满足 Win2D 的 >= 1.8 约束；首次构建的 WMC9999 为代码错误引发的 XAML 编译器连锁崩溃，非包冲突。
- 运行时已确认：CompositionDrawingSurface + Win2D + DesktopAcrylic 共存，无 external content 限制问题。

## 过程记录（供迁移参考）

1. `ElementCompositionPreview` 在 `Microsoft.UI.Xaml.Hosting`；`ICompositionSupportsSystemBackdrop` 在 `Microsoft.UI.Composition`；Win2D 的像素格式枚举在 `Microsoft.Graphics.DirectX`（勿用 `Windows.Graphics.DirectX`）。
2. 透明 surface 绘文字必须 `TextAntialiasing = Grayscale`（实证无彩边）。
3. surface 按物理像素建（DIP × RasterizationScale），`SurfaceBrush.Stretch = Fill` 映射回 DIP → 150% 下文字锐利。
4. 截图验证管线：`spikes/tools/screenshot.py`（ctypes BitBlt，负高度 top-down BMP）+ `bmp2png.py` + `crop_zoom.py`，可复用于后续 spike 的自动化验收。

**S1 总评：Go。** 生死项 H1/H2 双双通过，方案 A 主线继续。
