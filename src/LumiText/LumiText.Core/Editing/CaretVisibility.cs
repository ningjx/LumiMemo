namespace LumiText.Core.Editing;

/// <summary>光标可见性的变化结果：不用管 / 该亮起 / 该收起。</summary>
public enum CaretVisibilityChange
{
    /// <summary>状态没变（或编辑器已拆）：宿主什么都不用做。</summary>
    None,

    /// <summary>该亮起来：点亮并重启闪烁周期。</summary>
    Shown,

    /// <summary>该收起来：抹掉光标并停掉闪烁。</summary>
    Hidden,
}

/// <summary>
/// 输入光标"该不该亮着"的纯规则：**编辑区持有键盘焦点** × **宿主窗口在前台**；
/// 编辑器拆掉之后一律 <see cref="CaretVisibilityChange.None"/>。
/// </summary>
/// <remarks>
/// 宿主（<c>LumiEditor</c>）只负责把结论落到定时器与重绘上，规则放这儿是为了能单测。
/// 之所以要有"拆过了"这道闸：关窗时 WinUI 仍会把"窗口失活 / 焦点走了"送进编辑器，
/// 而那时 surface 已经 Dispose——再去重绘就是在已释放的 Composition 对象上写属性
/// （实机：连续开两个便签、关掉其中一个 → <c>VirtualizedTextSurface.Redraw</c> 里
/// ObjectDisposedException / 0xC0000005）。
/// </remarks>
public sealed class CaretVisibility
{
    private bool _editorFocused = true;
    private bool _windowActive = true;
    private bool _active = true;

    /// <summary>编辑器是否已拆（Unloaded / Dispose 都算）。</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>此刻该不该闪：编辑器有焦点、窗口在前台，且编辑器还在。</summary>
    public bool ShouldBlink => !IsDisposed && _editorFocused && _windowActive;

    /// <summary>焦点 / 激活态变化：告诉宿主该亮起、该收起，还是不用管。</summary>
    public CaretVisibilityChange Update(bool editorFocused, bool windowActive)
    {
        if (IsDisposed)
        {
            // 拆过的编辑器不再对焦点/激活做任何反应：宿主拿到 None 就不会去动定时器、
            // 更不会去重绘已经 Dispose 的 surface（关窗时序里这一定会发生，见类型注释）。
            return CaretVisibilityChange.None;
        }

        _editorFocused = editorFocused;
        _windowActive = windowActive;
        bool show = _editorFocused && _windowActive;
        if (show == _active)
        {
            return CaretVisibilityChange.None;
        }

        _active = show;
        return show ? CaretVisibilityChange.Shown : CaretVisibilityChange.Hidden;
    }

    /// <summary>声明编辑器已拆（Unloaded / Dispose 时调）。</summary>
    public void MarkDisposed()
    {
        IsDisposed = true;
        _active = false;
    }
}
