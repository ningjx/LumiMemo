# WinUI 富文本实验版：Visual Studio 调试

## 启动

1. 用 Visual Studio 打开 `src/LumiMemo.sln`。
2. 在解决方案资源管理器中右键 `LumiMemo.WinUI`，选择“设为启动项目”。
3. 工具栏选择 `Debug`、`x64` 和 `LumiMemo.WinUI (Unpackaged)` 启动配置。
4. 按 `F5`。该启动配置已启用托管代码与本机代码混合调试。

项目以 `WindowsPackageType=None` 构建，因此不要改成 MSIX Package 启动。

## 已知崩溃的复现路径

1. 在便笺列表中打开一张 `.lumi` 富文本便笺，尤其是含图片的便笺。
2. 关闭便笺窗口，再从列表重新打开；可能需要重复一次。
3. 当前用户实测有时会在重新打开时整个进程退出。事件查看器显示 `0xC000027B`，其内部异常为 `0x80004003` (`E_POINTER`)。当前代码尚未修复这个问题。

相关代码：`MainWindow.xaml.cs` 的 `OnEditorHostLoaded`、`OnWindowClosing` 和 `RichEditorHost.cs` 的 `LoadAsync`、`SaveRtf`、`Dispose`。便笺数据由 `LumiNoteRepository.cs` 保存在 `.lumi` 文件中；旧 `.md` 文件不会由这个 WinUI 实验版读取。

Visual Studio 中可在“调试 > 窗口 > 异常设置”观察异常，并在崩溃时查看“调用堆栈”。由于这是 WinUI 的延迟抛出异常，最终崩溃栈可能并非最初出错的位置；也要留意此前的首次异常和“输出”窗口。
