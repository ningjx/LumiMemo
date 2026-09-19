namespace LumiMemo.App.Abstractions;

/// <summary>
/// 把「交给系统外壳去打开」抽象出来，使 ViewModel 不直接 <c>Process.Start</c>（§18.6）。
/// </summary>
/// <remarks>
/// <para>
/// 它存在的两个理由与 <see cref="IDialogService"/> 一样：让 ViewModel 依赖抽象
/// （测试里换成记录型替身，不去真开一个资源管理器窗口），以及把
/// <strong>路径拼接与参数转义</strong>收紧到一个地方（§19.3）。
/// </para>
/// <para>
/// <strong>不提供「用默认程序打开文件」</strong>。§19.3 明说不要用
/// <c>Process.Start(路径)</c> 去打开用户的内容——那是把用户给的东西直接塞进
/// ShellExecute，参数注入与意外执行都在那条路上。真需要时（§15.6 的预览态点链接）
/// 也要单开一个方法、单写一层校验，不能顺手加在这里。
/// </para>
/// </remarks>
public interface IShellLauncher
{
    /// <summary>
    /// 用资源管理器打开一个文件夹。
    /// </summary>
    /// <param name="path">文件夹的完整路径。</param>
    /// <returns>真的把它交出去了返回 <c>true</c>；路径不存在或外壳拒绝启动时返回 <c>false</c>。</returns>
    /// <remarks>
    /// 返回 <c>bool</c> 而不是抛异常：打开一个不存在的目录是用户点到了过期按钮，
    /// 该给他一句提示，而不是让异常掀掉整个界面。
    /// </remarks>
    bool OpenFolder(string path);

    /// <summary>
    /// 在资源管理器里打开一个<strong>文件所在的目录并选中该文件</strong>（§15.8 右键菜单的「在资源管理器中显示」）。
    /// </summary>
    /// <param name="filePath">文件的完整路径。</param>
    /// <returns>同上：交出去了给 <c>true</c>，文件不在或外壳拒绝启动给 <c>false</c>。</returns>
    /// <remarks>
    /// <para>
    /// <strong>它和 <see cref="OpenFolder"/> 是两件事，不能合并。</strong>
    /// 只打开目录的话，用户在几十个 <c>.md</c> 里还得自己找那一个——而「在资源管理器中显示」
    /// 这个动作的全部意义就是替他找到。文件所在目录与便签的 <c>FilePath</c> 是两回事：
    /// 同一个目录下可能有几十张便签。
    /// </para>
    /// <para>
    /// 单开一个方法而不是给 <see cref="OpenFolder"/> 加个开关参数：两者的命令行形状不同
    /// （<c>explorer.exe &lt;目录&gt;</c> 对 <c>explorer.exe /select,&lt;文件&gt;</c>），
    /// 而参数拼错在这里的后果是把用户给的路径当成一个开关去执行（§19.3）。
    /// </para>
    /// </remarks>
    bool RevealInExplorer(string filePath);
}
