namespace LumiMemo.Infrastructure.Io;

/// <summary>
/// 检查一个目录能不能当笔记目录用：建得出来、写得进去。
/// </summary>
/// <remarks>
/// <para>
/// 用户选目录时可能选到只读盘、需要管理员权限的位置、或者已经拔掉的移动盘。
/// <c>Directory.CreateDirectory</c> 对「已存在但只读」的目录<strong>不报错</strong>，
/// 所以必须真写一个文件再删——否则问题会推迟到用户存第一张便签时才爆出来，
/// 那时候的报错离原因已经很远了。
/// </para>
/// <para>
/// 返回错误消息（<see langword="null"/> 表示可用）而不是抛异常：调用方是对话框，
/// 拿到消息直接显示即可；抛异常会把「目录不可用」这条正常路径走成崩溃路径。
/// </para>
/// </remarks>
public static class NotesFolderProbe
{
    /// <summary>探测用的文件名。以点开头，不干扰用户看得见的便签文件。</summary>
    private const string ProbeFileName = ".lumimemo-write-test";

    /// <summary>目录可用返回 <see langword="null"/>；否则返回给用户看的错误消息。</summary>
    /// <param name="path">用户选择的目录，可以是尚未创建的路径。</param>
    public static string? TryEnsureUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "请选择一个文件夹。";
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "这个路径不是有效的文件夹路径。";
        }

        try
        {
            Directory.CreateDirectory(LongPath.Ensure(full));

            string probe = Path.Combine(full, ProbeFileName);
            File.WriteAllBytes(LongPath.Ensure(probe), []);
            File.Delete(LongPath.Ensure(probe));
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"这个文件夹无法写入，请换一个位置。（{exception.GetType().Name}）";
        }

        return null;
    }
}
