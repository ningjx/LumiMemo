using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.UI.TextServices;
using LumiText.Core.Editing;

namespace LumiText.WinUI.Editing;

/// <summary>
/// TSF 生命周期管理（Phase 2 设计 §6.2）：ThreadMgr 激活 → DocumentMgr 创建 →
/// Context 创建（持有 <see cref="TsfTextStore"/>）→ Push。Dispose 时反向 Pop + Deactivate。
/// 组字期边界经 <see cref="ITfContextOwnerCompositionSink"/> 转发给 <see cref="TsfTextStore"/>。
/// </summary>
internal sealed class TsfManager : IDisposable
{
    private ITfThreadMgr? _threadMgr;
    private uint _clientId;
    private ITfDocumentMgr? _documentMgr;
    private ITfContext? _context;
    private uint _editCookie;
    private TsfTextStore? _textStore;
    private bool _activated;

    /// <summary>诊断日志前缀（窗口标识，区分多窗口日志）。</summary>
    public string Tag { get; set; } = "?";

    /// <summary>组字期边界事件（转发自 <see cref="TsfTextStore"/>）。</summary>
    public event EventHandler? CompositionStarted;
    public event EventHandler? CompositionEnded;

    /// <summary>当前 TextStore（宿主注册屏幕坐标回调用）。</summary>
    public TsfTextStore? TextStore => _textStore;

    /// <summary>
    /// 激活 TSF 并创建上下文。失败时静默降级（无 IME 环境不致命——编辑器仍可用键盘输入）。
    /// </summary>
    public unsafe bool Initialize(EditorCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        try
        {
            _textStore = new TsfTextStore(core);
            _textStore.CompositionStarted += (_, _) =>
            {
                CompositionStartCount++;
                CompositionStarted?.Invoke(this, EventArgs.Empty);
            };
            _textStore.CompositionEnded += (_, _) => CompositionEnded?.Invoke(this, EventArgs.Empty);

            // CoCreateInstance(CLSID_TF_ThreadMgr) 只服务 STA apartment 线程；
            // WinUI 3 UI 线程不是 STA（implicit MTA），CoCreateInstance 返回 0x80040154。
            // 改用 TF_CreateThreadMgr（msctf.dll 导出）——官方文档明确它是跨 apartment 的入口。
            var hr = TF_CreateThreadMgr(out var threadMgrObj);
            if (hr.Failed || threadMgrObj is not ITfThreadMgr threadMgr)
            {
                return false;
            }
            _threadMgr = threadMgr;

            _threadMgr.Activate(out _clientId);
            _activated = true;

            _threadMgr.CreateDocumentMgr(out _documentMgr);

            // CreateContext 的 punk 直接传 TextStore 本身（TSF 文档官方写法 (ITextStoreACP*)this）：
            // TSF 经 QueryInterface 找 ITextStoreACP/ITfContextOwnerCompositionSink，Push 时会
            // 主动调 AdviseSink 安装 sink——punk 必须就是文本存取对象。
            _documentMgr!.CreateContext(_clientId, 0, _textStore, out _context, out _editCookie);
            _documentMgr.Push(_context!);

            // SetFocus 是 IME 激活的关键：TSF 是 per-thread 的焦点管理，不 SetFocus 的话
            // IME 检测不到「当前文档」，按键走 pass-through 直接进应用。
            _threadMgr.SetFocus(_documentMgr);
            return true;
        }
        catch
        {
            // TSF 初始化失败（无 IME/线程模型不兼容）：降级为无 IME 编辑器
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// 把窗口 HWND 与文档管理器关联（<see cref="ITfThreadMgr.AssociateFocus"/>）：
    /// 之后窗口每次得焦，TSF 会自动调 <c>SetFocus</c>——窗口失焦再切回时 IME 不掉回英文。
    /// 必须在 <see cref="Initialize"/> 成功之后调用；重复调用以最后一次为准。
    /// </summary>
    public void AssociateWindowFocus(IntPtr hwnd)
    {
        if (_threadMgr is null || _documentMgr is null || hwnd == IntPtr.Zero)
        {
            return;
        }
        try
        {
            // GetWnd 的返回值：文档显示在屏幕上，就应报出宿主窗口（Phase 3 修复）
            if (_textStore is not null)
            {
                _textStore.OwnerWindow = hwnd;
            }
            _threadMgr.AssociateFocus(new Windows.Win32.Foundation.HWND(hwnd), _documentMgr, out _);
        }
        catch
        {
            // 关联失败不致命——Initialize 的 SetFocus 已保底，失焦切换由 Refocus 手动重设
        }
    }

    /// <summary>
    /// 手动重设文档焦点（宿主窗口 Activated / 编辑器得焦时调用）。
    /// 与 <see cref="AssociateWindowFocus"/> 互补：声明式关联 + 命令式重设双保险，
    /// 都是合法 TSF 用法，确保各种焦点路径下 IME 都能回到当前文档。
    /// </summary>
    public void Refocus()
    {
        if (_threadMgr is null || _documentMgr is null)
        {
            return;
        }
        if (_textStore?.IsComposing == true)
        {
            // 组字进行中：焦点已经是我们（组字只能发生在本店），不打扰
            return;
        }
        try
        {
            _threadMgr.SetFocus(_documentMgr);
            if (!HasFocus)
            {
                // 指示灯：SetFocus 未生效（激活瞬间的已知现象）——留给排查用，正常路径不打印
                System.Diagnostics.Debug.WriteLine(
                    $"[TSF {Tag}] Refocus 未生效：HasFocus=False，组字计数={CompositionStartCount}");
            }
        }
        catch
        {
            // 重设失败不致命——下一次按键/激活还会再试
        }
    }

    /// <summary>TSF 线程焦点是否在本文档（诊断用：便签列表重开窗口后 IME 失效时判定焦点归属）。</summary>
    public bool HasFocus
    {
        get
        {
            if (_threadMgr is null || _documentMgr is null)
            {
                return false;
            }
            try
            {
                _threadMgr.GetFocus(out var focused);
                return focused is not null && ReferenceEquals(focused, _documentMgr);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>组字开始计数（诊断用）：中文输入若绕开本店，计数不会增长。</summary>
    public int CompositionStartCount { get; private set; }

    public void Dispose()
    {
        if (_documentMgr is not null)
        {
            // 不调 SetFocus(null)：CsWin32 投影对非空参数先抛异常（ArgumentException，实测噪音），
            // 且 ITfThreadMgr::SetFocus 文档明言不接受 NULL。Pop 掉上下文 + Deactivate 即可清焦点。
            try { _documentMgr.Pop(_editCookie); } catch { }
            _documentMgr = null;
        }
        _context = null;
        if (_activated && _threadMgr is not null)
        {
            try { _threadMgr.Deactivate(); } catch { }
            _activated = false;
        }
        _threadMgr = null;
        _textStore = null;
    }

    [DllImport("msctf.dll", ExactSpelling = true)]
    private static extern HRESULT TF_CreateThreadMgr(
        [MarshalAs(UnmanagedType.Interface)] out object? pptim);
}
