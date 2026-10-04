using CommunityToolkit.Mvvm.ComponentModel;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.WinUI.Controls;
using LumiMemo.WinUI.Services;
using Microsoft.Extensions.Logging;

namespace LumiMemo.WinUI.ViewModels;

/// <summary>单张便签的界面状态与保存编排。</summary>
/// <remarks>
/// <para>
/// <strong>不引用 Microsoft.UI 类型</strong>（可测）：窗口持有它、绑定它，反过来它不认识窗口。
/// 原实现把这些逻辑（去抖保存、标题事件订阅、状态文案）全放在 MainWindow 的 code-behind 里，
/// 既测不了也复用不了。
/// </para>
/// <para>
/// 状态分三类：<see cref="Note"/> 上是落盘数据；<see cref="NoteLayout"/> 上是设备状态；
/// 保存状态与标题生成指示只活在内存里（§18.1）。
/// </para>
/// </remarks>
public sealed class NoteViewModel : ObservableObject, IDisposable
{
    private readonly INoteStorage _storage;
    private readonly IClock _clock;
    private readonly ILayoutStore _layouts;
    private readonly AutoSaveService _autoSave;
    private readonly NoteTitleCoordinator _titles;
    private readonly Action _onNoteChanged;
    private readonly ILogger<NoteViewModel> _logger;
    private readonly NoteLayout _layout;

    private IRichTextDocument? _document;
    private string? _hint;
    private SaveStatus _status = SaveStatus.Saved;
    private bool _isTitleGenerating;
    private bool _isTopMost;
    private bool _hasPendingSave;
    private bool _saving;
    private int _documentRevision;
    private bool _isDisposed;

    public NoteViewModel(
        Note note,
        NoteLayout layout,
        INoteStorage storage,
        IClock clock,
        ILayoutStore layouts,
        AutoSaveService autoSave,
        NoteTitleCoordinator titles,
        Action onNoteChanged,
        ILogger<NoteViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(autoSave);
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(onNoteChanged);
        ArgumentNullException.ThrowIfNull(logger);

        Note = note;
        _layout = layout;
        _storage = storage;
        _clock = clock;
        _layouts = layouts;
        _autoSave = autoSave;
        _titles = titles;
        _onNoteChanged = onNoteChanged;
        _logger = logger;

        _isTopMost = layout.IsTopMost;
        _isTitleGenerating = titles.IsPending(note.Id);

        _titles.GenerationStateChanged += OnGenerationStateChanged;
        _titles.TitleUpdated += OnTitleUpdated;
        _titles.GenerationFailed += OnGenerationFailed;
    }

    /// <summary>本便签的数据模型。</summary>
    public Note Note { get; }

    /// <summary>便签 id。保存、标题、窗口操作都用它作标识。</summary>
    public Guid Id => Note.Id;

    /// <summary>显示标题，完全派生（<see cref="Note.Title"/> 自己带缓存，不在这里再缓一份）。</summary>
    public string Title => Note.Title;

    /// <summary>正文字数（按 Rune 计，emoji 与代理对算一个字）。</summary>
    public int CharacterCount => Note.Content.EnumerateRunes().Count();

    /// <summary>状态条文案：临时提示（读取失败、标题生成失败）优先，否则按保存状态。</summary>
    public string StatusText => _hint ?? _status switch
    {
        SaveStatus.Saving => $"正在保存 · {CharacterCount} 字",
        SaveStatus.Failed => $"保存失败 · {CharacterCount} 字",
        _ => $"已保存 · {CharacterCount} 字",
    };

    /// <summary>当前保存状态：右下角状态条据此换图标（转圈 / 对勾 / 警示）。</summary>
    public SaveStatus Status => _status;

    /// <summary>
    /// 是否正在显示临时提示（读取失败、插图失败等）。
    /// 提示是**文案**不是状态——视图要把它露出来，不能只塞进 Tooltip。
    /// </summary>
    public bool HasHint => _hint is not null;

    /// <summary>标题正在生成中（转圈指示）。</summary>
    public bool IsTitleGenerating
    {
        get => _isTitleGenerating;
        private set
        {
            if (SetProperty(ref _isTitleGenerating, value))
            {
                OnPropertyChanged(nameof(CanRegenerateTitle));
            }
        }
    }

    /// <summary>「刷新标题」按钮可点：生成中禁用（再点会作废进行中的请求，禁掉更直观）。</summary>
    public bool CanRegenerateTitle => !_isTitleGenerating;

    /// <summary>手动刷新标题；未启用自动标题（或没配模型）时在状态条上提示。</summary>
    public void RegenerateTitle()
    {
        if (!_titles.Regenerate(Note))
        {
            ShowHint("自动标题未启用 · 请先在设置里配置模型");
        }
    }

    /// <summary>置顶镜像。界面绑定它；setter 负责写 <see cref="NoteLayout"/> 并请求落盘。</summary>
    public bool IsTopMost
    {
        get => _isTopMost;
        set
        {
            if (!SetProperty(ref _isTopMost, value) || _isDisposed)
            {
                return;
            }

            _layout.IsTopMost = value;
            _layouts.MarkDirty();
            TopMostChanged?.Invoke(value);
        }
    }

    /// <summary>窗口订阅它，把 AppWindow 的 presenter 跟上（窗口认识 AppWindow，本类不认识）。</summary>
    public event Action<bool>? TopMostChanged;

    /// <summary>还有没落盘的内容（窗口的关闭协议据此决定要不要先保存）。</summary>
    public bool HasPendingSave => _hasPendingSave;

    /// <summary>接上编辑区。只有真实窗口会调用；测试直接用 <see cref="ApplyUserEdit"/> 模拟输入。</summary>
    public void AttachDocument(IRichTextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _document = document;
        document.UserEdited += OnDocumentUserEdited;
    }

    /// <summary>用户改了内容：更新模型、通知标题与列表，排一轮去抖保存。</summary>
    public void ApplyUserEdit(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        Note.Content = plainText;
        Note.UpdatedAt = _clock.Now;
        _titles.ContentChanged(Note);
        _onNoteChanged();

        _hint = null;
        _status = SaveStatus.Saving;
        _hasPendingSave = true;
        _documentRevision++;
        NotifyDerivedChanged();

        _autoSave.ScheduleSave(Id, PersistAsync);
    }

    /// <summary>把一条临时提示显示在状态条上（读取失败、插图失败等）；下次编辑或保存会清掉。</summary>
    public void ShowHint(string message)
    {
        _hint = message;
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// 把当前状态整份落盘；失败置状态并返回 <see langword="false"/>，不向外抛。
    /// </summary>
    public async Task<bool> PersistAsync()
    {
        if (!_hasPendingSave || _saving)
        {
            return !_hasPendingSave;
        }

        _saving = true;
        int revision = _documentRevision;

        try
        {
            if (_document is not null)
            {
                Note.RichTextContent = _document.SaveContent();
            }

            Note.UpdatedAt = _clock.Now;
            await _storage.SaveAsync(Note);

            if (revision == _documentRevision)
            {
                _hasPendingSave = false;
                _status = SaveStatus.Saved;
                NotifyDerivedChanged();
                _titles.ContentSaved(Note);
            }

            return true;
        }
        catch (Exception ex)
        {
            // 保存失败不带走窗口：状态条亮出来，用户的下一次编辑会再排一轮。
            _logger.LogWarning("便笺保存失败：{NoteId}（{ExceptionType}）。", Id, ex.GetType().Name);
            _status = SaveStatus.Failed;
            NotifyDerivedChanged();
            return false;
        }
        finally
        {
            _saving = false;

            // 保存途中又编辑过：revision 对不上，重排一轮。
            if (_hasPendingSave && revision != _documentRevision)
            {
                _autoSave.ScheduleSave(Id, PersistAsync);
            }
        }
    }

    /// <summary>关窗前把还没落盘的存掉；返回 <see langword="false"/> 表示存失败、调用方不要关窗。</summary>
    public async Task<bool> TryPersistOnCloseAsync()
    {
        _autoSave.CancelScheduledSave(Id);

        // 上一轮保存可能还在途中（去抖刚触发时用户就点了关）；等它结束再决定。
        while (_saving)
        {
            await Task.Delay(20);
        }

        return await PersistAsync();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _titles.GenerationStateChanged -= OnGenerationStateChanged;
        _titles.TitleUpdated -= OnTitleUpdated;
        _titles.GenerationFailed -= OnGenerationFailed;

        if (_document is not null)
        {
            _document.UserEdited -= OnDocumentUserEdited;
            _document = null;
        }

        // 取消等待中的那一轮：窗口关了之后定时器还会到期，而那时本对象已经释放。
        _autoSave.CancelScheduledSave(Id);
    }

    private void OnDocumentUserEdited(object? sender, EventArgs e) =>
        ApplyUserEdit(_document?.PlainText ?? string.Empty);

    private void OnGenerationStateChanged(Guid noteId, bool generating)
    {
        if (noteId == Id)
        {
            IsTitleGenerating = generating;
        }
    }

    private void OnTitleUpdated(Guid noteId)
    {
        if (noteId == Id)
        {
            OnPropertyChanged(nameof(Title));
        }
    }

    private void OnGenerationFailed(Guid noteId)
    {
        if (noteId == Id)
        {
            _hint = "自动标题失败 · 请检查模型设置";
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>状态文案、字数、标题一起失效——它们都从内容与保存状态派生。</summary>
    private void NotifyDerivedChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CharacterCount));
        OnPropertyChanged(nameof(Title));
    }
}
