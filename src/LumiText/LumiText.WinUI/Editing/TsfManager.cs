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
            _textStore.CompositionStarted += (_, _) => CompositionStarted?.Invoke(this, EventArgs.Empty);
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

    public void Dispose()
    {
        if (_documentMgr is not null)
        {
            try { _threadMgr?.SetFocus(null); } catch { } // 焦点交还系统（null = 无 TSF 文档）
            try { _documentMgr.Pop(0); } catch { }
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
