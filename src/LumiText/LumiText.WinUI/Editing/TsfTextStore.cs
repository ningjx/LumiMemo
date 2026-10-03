using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.TextServices;
using LumiText.Core.Editing;

namespace LumiText.WinUI.Editing;

/// <summary>
/// <see cref="ITextStoreACP2"/> 实现（Phase 2 设计 §6.3）：TSF 与 <see cref="EditorCore"/> 之间的桥。
/// ACP（应用字符位置）与 <see cref="TextPosition"/> 的映射经扁平化文本缓存（§6.3）。
/// 组字期行为：OnTextChange 更新 CompositionRange 显示但不触发 DocumentChanged（§6.3）。
/// </summary>
/// <remarks>
/// 一个对象同时实现 <see cref="ITextStoreACP"/> + <see cref="ITextStoreACP2"/> +
/// <see cref="ITfContextOwnerCompositionSink"/>：CreateContext 的 punk 直接传本对象
/// （TSF 文档的官方写法 (ITextStoreACP*)this），组字期边界回调也走同一对象。
/// ITextStoreACP2 不继承 ITextStoreACP（CsWin32 投影无继承链），必须都实现——
/// TSF 对 ITextStoreACP 的 QueryInterface 才能成功。
/// </remarks>
internal sealed class TsfTextStore : ITextStoreACP2, ITextStoreACP, ITfContextOwnerCompositionSink
{
    private readonly EditorCore _core;
    private ITextStoreACPSink? _sink;
    private uint _lockType;
    private string _flatText = string.Empty;
    private int[] _blockStartAcp = [];
    private bool _composing;

    public TsfTextStore(EditorCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
        RebuildFlatTextCache();
        _core.DocumentChanged += (_, _) => RebuildFlatTextCache();
    }

    /// <summary>组字期状态（宿主据此抑制自动保存，§6.3）。</summary>
    public bool IsComposing => _composing;

    /// <summary>
    /// 文档所属窗口（Phase 3 修复）：<see cref="ITextStoreACP.GetWnd"/> 的返回值。
    /// 文档显示在屏幕上就应报出宿主窗口——TSF 借此定位 IME UI 并判定窗口归属；
    /// 恒返回 NULL 在单窗口下可用，多窗口（便签 + 管理器）下会让 IME 绑定判断失据。
    /// </summary>
    public IntPtr OwnerWindow { get; set; }

    /// <summary>组字期边界事件（宿主据此切换光标加粗/抑制自动保存）。</summary>
    public event EventHandler? CompositionStarted;
    public event EventHandler? CompositionEnded;

    // ------------------------------------------------------------------
    // ACP ↔ TextPosition 映射（§6.3）
    // ------------------------------------------------------------------

    private void RebuildFlatTextCache()
    {
        var blocks = _core.Document.Blocks;
        _blockStartAcp = new int[blocks.Count];
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < blocks.Count; i++)
        {
            _blockStartAcp[i] = builder.Length;
            builder.Append(BlockTextOps.GetPlainText(blocks[i]));
            if (i < blocks.Count - 1)
            {
                builder.Append('\n');
            }
        }
        _flatText = builder.ToString();
    }

    private TextPosition AcpToTextPosition(int acp)
    {
        int blockIndex = 0;
        for (int i = _blockStartAcp.Length - 1; i >= 0; i--)
        {
            if (acp >= _blockStartAcp[i])
            {
                blockIndex = i;
                break;
            }
        }
        return new TextPosition(blockIndex, acp - _blockStartAcp[blockIndex]);
    }

    private int TextPositionToAcp(TextPosition pos)
    {
        if (pos.BlockIndex < 0 || pos.BlockIndex >= _blockStartAcp.Length)
        {
            return 0;
        }
        return _blockStartAcp[pos.BlockIndex] + pos.CharIndex;
    }

    // ------------------------------------------------------------------
    // ITextStoreACP2 实现
    // ------------------------------------------------------------------

    public unsafe void AdviseSink(Guid* riid, object punk, uint dwMask)
    {
        if (*riid != typeof(ITextStoreACPSink).GUID)
        {
            Marshal.ThrowExceptionForHR(unchecked((int)0x80004003)); // E_POINTER
        }
        _sink = (ITextStoreACPSink)punk;
    }

    public void UnadviseSink(object punk)
    {
        _sink = null;
    }

    public unsafe void RequestLock(uint dwLockFlags, HRESULT* phrSession)
    {
        if (_sink is null)
        {
            *phrSession = new HRESULT(unchecked((int)0x80004005)); // E_FAIL
            return;
        }
        if (_lockType != 0)
        {
            if ((dwLockFlags & 0x100) != 0) // TS_LF_SYNC
            {
                *phrSession = new HRESULT(0); // S_OK
            }
            else
            {
                *phrSession = new HRESULT(unchecked((int)0x80070057)); // E_INVALIDARG
            }
            return;
        }
        _lockType = dwLockFlags;
        *phrSession = new HRESULT(0);
        _sink.OnLockGranted((TEXT_STORE_LOCK_FLAGS)dwLockFlags);
        _lockType = 0;
    }

    public unsafe void GetStatus(TS_STATUS* pdcs)
    {
        pdcs->dwDynamicFlags = 0;
        pdcs->dwStaticFlags = 0;
    }

    public void QueryInsert(int acpTestStart, int acpTestEnd, uint cch,
        out int pacpResultStart, out int pacpResultEnd)
    {
        pacpResultStart = acpTestStart;
        pacpResultEnd = acpTestEnd;
    }

    public unsafe void GetSelection(uint ulIndex, uint ulCount,
        TS_SELECTION_ACP* pSelection, out uint pcFetched)
    {
        var sel = _core.Selection;
        int start = TextPositionToAcp(sel.Start);
        int end = TextPositionToAcp(sel.End);
        pSelection->acpStart = start;
        pSelection->acpEnd = end;
        pSelection->style.ase = sel.Anchor <= sel.Active
            ? TsActiveSelEnd.TS_AE_NONE
            : TsActiveSelEnd.TS_AE_START;
        pSelection->style.fInterimChar = new BOOL(_composing ? 1 : 0);
        pcFetched = 1;
    }

    public unsafe void SetSelection(uint ulCount, TS_SELECTION_ACP* pSelection)
    {
        if (ulCount == 0)
        {
            return;
        }
        var start = AcpToTextPosition(pSelection->acpStart);
        var end = AcpToTextPosition(pSelection->acpEnd);
        var anchor = pSelection->style.ase == TsActiveSelEnd.TS_AE_START ? end : start;
        var active = pSelection->style.ase == TsActiveSelEnd.TS_AE_START ? start : end;
        _core.SetSelection(new TextRange(anchor, active));
    }

    public unsafe void GetText(int acpStart, int acpEnd, PWSTR pchPlain, uint cchPlainReq,
        out uint pcchPlainRet, TS_RUNINFO* prgRunInfo, uint cRunInfoReq,
        out uint pcRunInfoRet, out int pacpNext)
    {
        if (acpEnd == -1)
        {
            acpEnd = _flatText.Length;
        }
        int length = Math.Min(acpEnd - acpStart, (int)cchPlainReq);
        if (pchPlain.Value != null && length > 0)
        {
            fixed (char* src = _flatText)
            {
                Buffer.MemoryCopy(src + acpStart, pchPlain.Value, length * 2, length * 2);
            }
        }
        pcchPlainRet = (uint)length;
        pcRunInfoRet = 0;
        pacpNext = acpStart + length;
    }

    public unsafe void SetText(uint dwFlags, int acpStart, int acpEnd, PCWSTR pchText,
        uint cch, TS_TEXTCHANGE* pChange)
    {
        var text = pchText.Value is not null
            ? new string(pchText.Value, 0, (int)cch)
            : string.Empty;
        var start = AcpToTextPosition(acpStart);
        var end = AcpToTextPosition(acpEnd);
        _core.SetSelection(new TextRange(start, end));
        _core.ApplyCommand(new Core.Editing.Commands.InsertTextCommand(text));
        pChange->acpStart = acpStart;
        pChange->acpOldEnd = acpEnd;
        pChange->acpNewEnd = acpStart + text.Length;
    }

    public void GetFormattedText(int acpStart, int acpEnd, out IDataObject ppDataObject)
    {
        ppDataObject = null!;
    }

    public unsafe void GetEmbedded(int acpPos, Guid* rguidService, Guid* riid, out object ppunk)
    {
        ppunk = null!;
    }

    public unsafe void QueryInsertEmbedded(Guid* pguidService, FORMATETC* pFormatEtc, BOOL* pfInsertable)
    {
        *pfInsertable = new BOOL(0);
    }

    public unsafe void InsertEmbedded(uint dwFlags, int acpStart, int acpEnd,
        IDataObject pDataObject, TS_TEXTCHANGE* pChange)
    {
        Marshal.ThrowExceptionForHR(unchecked((int)0x80004001)); // E_NOTIMPL
    }

    public unsafe void InsertTextAtSelection(uint dwFlags, PCWSTR pchText, uint cch,
        out int pacpStart, out int pacpEnd, TS_TEXTCHANGE* pChange)
    {
        var text = pchText.Value is not null
            ? new string(pchText.Value, 0, (int)cch)
            : string.Empty;
        int beforeStart = TextPositionToAcp(_core.Selection.Start);
        _core.ApplyCommand(new Core.Editing.Commands.InsertTextCommand(text));
        pacpStart = beforeStart;
        pacpEnd = TextPositionToAcp(_core.Selection.Active);
        pChange->acpStart = pacpStart;
        pChange->acpOldEnd = pacpStart;
        pChange->acpNewEnd = pacpEnd;
    }

    public unsafe void InsertEmbeddedAtSelection(uint dwFlags, IDataObject pDataObject,
        out int pacpStart, out int pacpEnd, TS_TEXTCHANGE* pChange)
    {
        pacpStart = 0;
        pacpEnd = 0;
        Marshal.ThrowExceptionForHR(unchecked((int)0x80004001)); // E_NOTIMPL
    }

    public unsafe void RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, Guid* paFilterAttrs)
    {
    }

    public unsafe void RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, Guid* paFilterAttrs, uint dwFlags)
    {
    }

    public unsafe void RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs,
        Guid* paFilterAttrs, uint dwFlags)
    {
    }

    public unsafe void FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs,
        Guid* paFilterAttrs, uint dwFlags, out int pacpNext, BOOL* pfFound, out int plFoundOffset)
    {
        pacpNext = acpHalt;
        *pfFound = new BOOL(0);
        plFoundOffset = 0;
    }

    public void RetrieveRequestedAttrs(uint ulCount, TS_ATTRVAL[] paAttrVals, out uint pcFetched)
    {
        pcFetched = 0;
    }

    public void GetEndACP(out int pacp)
    {
        pacp = _flatText.Length;
    }

    public void GetActiveView(out uint pvcView)
    {
        pvcView = 1;
    }

    public unsafe void GetACPFromPoint(uint vcView, System.Drawing.Point* ptScreen, uint dwFlags, out int pacp)
    {
        pacp = 0;
    }

    public unsafe void GetTextExt(uint vcView, int acpStart, int acpEnd, RECT* prc, BOOL* pfClipped)
    {
        if (CaretScreenRectProvider is { } provider)
        {
            var rect = provider(acpStart, acpEnd);
            prc->left = rect.left;
            prc->top = rect.top;
            prc->right = rect.right;
            prc->bottom = rect.bottom;
            *pfClipped = new BOOL(0);
        }
        else
        {
            prc->left = prc->top = prc->right = prc->bottom = 0;
            *pfClipped = new BOOL(0);
        }
    }

    public unsafe void GetScreenExt(uint vcView, RECT* prc)
    {
        if (ScreenRectProvider is { } provider)
        {
            var rect = provider();
            prc->left = rect.left;
            prc->top = rect.top;
            prc->right = rect.right;
            prc->bottom = rect.bottom;
        }
        else
        {
            prc->left = prc->top = prc->right = prc->bottom = 0;
        }
    }

    /// <summary>宿主注册的「ACP 区间 → 屏幕包围盒」回调（候选窗定位用）。</summary>
    public Func<int, int, (int left, int top, int right, int bottom)>? CaretScreenRectProvider { get; set; }

    /// <summary>宿主注册的「文档显示面屏幕包围盒」回调（候选窗不越界用）。</summary>
    public Func<(int left, int top, int right, int bottom)>? ScreenRectProvider { get; set; }

    // ------------------------------------------------------------------
    // ITextStoreACP 显式实现（与 ITextStoreACP2 签名相同，转发共享）。
    // ITextStoreACP2 不继承 ITextStoreACP，TSF QI(IID_ITextStoreACP) 需要本接口独立应答。
    // ------------------------------------------------------------------

    unsafe void ITextStoreACP.AdviseSink(Guid* riid, object punk, uint dwMask) => AdviseSink(riid, punk, dwMask);
    void ITextStoreACP.UnadviseSink(object punk) => UnadviseSink(punk);
    unsafe void ITextStoreACP.RequestLock(uint dwLockFlags, HRESULT* phrSession) => RequestLock(dwLockFlags, phrSession);
    unsafe void ITextStoreACP.GetStatus(TS_STATUS* pdcs) => GetStatus(pdcs);
    void ITextStoreACP.QueryInsert(int acpTestStart, int acpTestEnd, uint cch,
        out int pacpResultStart, out int pacpResultEnd) =>
        QueryInsert(acpTestStart, acpTestEnd, cch, out pacpResultStart, out pacpResultEnd);
    unsafe void ITextStoreACP.GetSelection(uint ulIndex, uint ulCount, TS_SELECTION_ACP* pSelection, out uint pcFetched) =>
        GetSelection(ulIndex, ulCount, pSelection, out pcFetched);
    unsafe void ITextStoreACP.SetSelection(uint ulCount, TS_SELECTION_ACP* pSelection) => SetSelection(ulCount, pSelection);
    unsafe void ITextStoreACP.GetText(int acpStart, int acpEnd, PWSTR pchPlain, uint cchPlainReq,
        out uint pcchPlainRet, TS_RUNINFO* prgRunInfo, uint cRunInfoReq, out uint pcRunInfoRet, out int pacpNext) =>
        GetText(acpStart, acpEnd, pchPlain, cchPlainReq, out pcchPlainRet, prgRunInfo, cRunInfoReq,
            out pcRunInfoRet, out pacpNext);
    unsafe void ITextStoreACP.SetText(uint dwFlags, int acpStart, int acpEnd, PCWSTR pchText, uint cch, TS_TEXTCHANGE* pChange) =>
        SetText(dwFlags, acpStart, acpEnd, pchText, cch, pChange);
    void ITextStoreACP.GetFormattedText(int acpStart, int acpEnd, out IDataObject ppDataObject) =>
        GetFormattedText(acpStart, acpEnd, out ppDataObject);
    unsafe void ITextStoreACP.GetEmbedded(int acpPos, Guid* rguidService, Guid* riid, out object ppunk) =>
        GetEmbedded(acpPos, rguidService, riid, out ppunk);
    unsafe void ITextStoreACP.QueryInsertEmbedded(Guid* pguidService, FORMATETC* pFormatEtc, BOOL* pfInsertable) =>
        QueryInsertEmbedded(pguidService, pFormatEtc, pfInsertable);
    unsafe void ITextStoreACP.InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, IDataObject pDataObject, TS_TEXTCHANGE* pChange) =>
        InsertEmbedded(dwFlags, acpStart, acpEnd, pDataObject, pChange);
    unsafe void ITextStoreACP.InsertTextAtSelection(uint dwFlags, PCWSTR pchText, uint cch,
        out int pacpStart, out int pacpEnd, TS_TEXTCHANGE* pChange) =>
        InsertTextAtSelection(dwFlags, pchText, cch, out pacpStart, out pacpEnd, pChange);
    unsafe void ITextStoreACP.InsertEmbeddedAtSelection(uint dwFlags, IDataObject pDataObject,
        out int pacpStart, out int pacpEnd, TS_TEXTCHANGE* pChange) =>
        InsertEmbeddedAtSelection(dwFlags, pDataObject, out pacpStart, out pacpEnd, pChange);
    unsafe void ITextStoreACP.RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, Guid* paFilterAttrs) =>
        RequestSupportedAttrs(dwFlags, cFilterAttrs, paFilterAttrs);
    unsafe void ITextStoreACP.RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, Guid* paFilterAttrs, uint dwFlags) =>
        RequestAttrsAtPosition(acpPos, cFilterAttrs, paFilterAttrs, dwFlags);
    unsafe void ITextStoreACP.RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs,
        Guid* paFilterAttrs, uint dwFlags) =>
        RequestAttrsTransitioningAtPosition(acpPos, cFilterAttrs, paFilterAttrs, dwFlags);
    unsafe void ITextStoreACP.FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs,
        Guid* paFilterAttrs, uint dwFlags, out int pacpNext, BOOL* pfFound, out int plFoundOffset) =>
        FindNextAttrTransition(acpStart, acpHalt, cFilterAttrs, paFilterAttrs, dwFlags,
            out pacpNext, pfFound, out plFoundOffset);
    void ITextStoreACP.RetrieveRequestedAttrs(uint ulCount, TS_ATTRVAL[] paAttrVals, out uint pcFetched) =>
        RetrieveRequestedAttrs(ulCount, paAttrVals, out pcFetched);
    void ITextStoreACP.GetEndACP(out int pacp) => GetEndACP(out pacp);
    void ITextStoreACP.GetActiveView(out uint pvcView) => GetActiveView(out pvcView);
    unsafe void ITextStoreACP.GetACPFromPoint(uint vcView, System.Drawing.Point* ptScreen, uint dwFlags, out int pacp) =>
        GetACPFromPoint(vcView, ptScreen, dwFlags, out pacp);
    unsafe void ITextStoreACP.GetTextExt(uint vcView, int acpStart, int acpEnd, RECT* prc, BOOL* pfClipped) =>
        GetTextExt(vcView, acpStart, acpEnd, prc, pfClipped);
    unsafe void ITextStoreACP.GetScreenExt(uint vcView, RECT* prc) => GetScreenExt(vcView, prc);
    unsafe void ITextStoreACP.GetWnd(uint vcView, HWND* phwnd) =>
        *phwnd = OwnerWindow == IntPtr.Zero ? default : new HWND(OwnerWindow);

    // ------------------------------------------------------------------
    // ITfContextOwnerCompositionSink（组字期边界，TSF 直接回调本对象）
    // ------------------------------------------------------------------

    public unsafe void OnStartComposition(ITfCompositionView pComposition, BOOL* pfOk)
    {
        _composing = true;
        CompositionStarted?.Invoke(this, EventArgs.Empty);
        *pfOk = new BOOL(1);
    }

    public void OnUpdateComposition(ITfCompositionView pComposition, ITfRange pRangeNew)
    {
    }

    public void OnEndComposition(ITfCompositionView pComposition)
    {
        _composing = false;
        CompositionEnded?.Invoke(this, EventArgs.Empty);
    }
}
