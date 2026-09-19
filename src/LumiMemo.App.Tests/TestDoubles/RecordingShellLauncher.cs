using LumiMemo.App.Abstractions;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// <see cref="IShellLauncher"/> 的记录型替身。
/// </summary>
/// <remarks>
/// 它保证用例跑起来不会真的弹出资源管理器窗口——那会在无人值守的构建机上
/// 挂出一个谁也看不见、也不会自己关掉的进程。
/// </remarks>
public sealed class RecordingShellLauncher : IShellLauncher
{
    /// <summary>所有被请求打开的文件夹，按发生顺序。</summary>
    public List<string> OpenedFolders { get; } = [];

    /// <summary>所有被请求「显示」的文件，按发生顺序。</summary>
    public List<string> RevealedFiles { get; } = [];

    /// <summary>外壳的答复。改成 <see langword="false"/> 模拟「资源管理器没能打开」。</summary>
    public bool Result { get; set; } = true;

    /// <inheritdoc />
    public bool OpenFolder(string path)
    {
        OpenedFolders.Add(path);

        return Result;
    }

    /// <inheritdoc />
    public bool RevealInExplorer(string filePath)
    {
        RevealedFiles.Add(filePath);

        return Result;
    }
}
