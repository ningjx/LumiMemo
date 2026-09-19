using System.Diagnostics;
using System.IO;
using LumiMemo.App.Abstractions;

namespace LumiMemo.App.Services;

/// <summary>
/// <see cref="IShellLauncher"/> 的生产实现，内部是 <c>explorer.exe</c>。
/// </summary>
/// <remarks>
/// <para>
/// <strong>一律走 <see cref="ProcessStartInfo.ArgumentList"/>，不拼命令行字符串。</strong>
/// 路径里可能有空格、引号、中文（<c>我的笔记</c>），手工拼一个
/// <c>$"explorer.exe \"{path}\""</c> 就是在自己实现一遍引号转义——
/// 而转义写错的后果是用户点一下按钮，系统去执行了别的命令（§19.3）。
/// <c>ArgumentList</c> 由运行时负责加引号与转义。
/// </para>
/// <para>
/// <strong>直接调 <c>explorer.exe</c> 而不是 <c>Process.Start(folder)</c></strong>：
/// 后者会把路径交给 ShellExecute，而 Win32 的 ShellExecute 对 <c>&amp;</c> 之类的字符
/// 有它自己的一套解释。前者是明确的「打开这个目录」。
/// </para>
/// </remarks>
public sealed class ExplorerShellLauncher : IShellLauncher
{
    /// <inheritdoc />
    public bool OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(path);

        try
        {
            // 拿到的句柄用不上，但它必须被释放——不释放的话每次点按钮都会漏一个进程句柄。
            using Process? process = Process.Start(startInfo);

            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 外壳起不来（被组策略禁掉、系统文件被替换）。返回 false 让界面提示一句，
            // 而不是让异常顺着 ViewModel 的命令掀到 Dispatcher 上。
            return false;
        }
    }

    /// <inheritdoc />
    public bool RevealInExplorer(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false,
        };

        // `/select,<路径>` 是 explorer 自己的一个开关，**整个是同一个参数**——
        // 写成两个参数（"/select," 与路径）时 explorer 会把后一个当成要打开的目录。
        // 与 OpenFolder 一样交给 ArgumentList，路径里的空格与中文由运行时加引号，
        // 不自己拼 $"/select,\"{filePath}\""。
        startInfo.ArgumentList.Add($"/select,{filePath}");

        try
        {
            using Process? process = Process.Start(startInfo);

            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
