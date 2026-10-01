using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.Core.Services;

/// <summary>按便笺管理标题生成任务；任务独立于便笺窗口生命周期。</summary>
public sealed class NoteTitleCoordinator : IDisposable
{
    private readonly INoteStorage _storage;
    private readonly IClock _clock;
    private readonly AppSettings _settings;
    private readonly ITitleGenerator _generator;
    private readonly Dictionary<Guid, State> _states = [];
    private bool _disposed;

    public NoteTitleCoordinator(IEnumerable<Note> notes, INoteStorage storage, IClock clock,
        AppSettings settings, ITitleGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(generator);
        _storage = storage;
        _clock = clock;
        _settings = settings;
        _generator = generator;
        foreach (Note note in notes)
        {
            Register(note);
        }
    }

    public event Action<Guid, bool>? GenerationStateChanged;
    public event Action<Guid>? TitleUpdated;
    public event Action<Guid>? GenerationFailed;

    public void Register(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        _states.TryAdd(note.Id, new State(note.Content));
    }

    public bool IsPending(Guid noteId) => _states.TryGetValue(noteId, out State? state) && state.Pending;

    /// <summary>正文真正变化时取消旧请求；保存完成后再启动新请求。</summary>
    public void ContentChanged(Note note)
    {
        if (_disposed)
        {
            return;
        }

        State state = GetState(note);
        state.Revision++;
        state.Request?.Cancel();
        bool pending = _settings.Llm.Enabled && !string.IsNullOrWhiteSpace(_settings.Llm.Model)
            && TitleChangePolicy.ShouldGenerate(state.Baseline, note.Content,
                !string.IsNullOrWhiteSpace(note.AutoTitle));
        SetPending(note.Id, state, pending);
    }

    /// <summary>富文本已落盘后排队生成；关窗不会取消这项工作。</summary>
    public void ContentSaved(Note note)
    {
        if (_disposed || !GetState(note).Pending)
        {
            return;
        }

        State state = GetState(note);
        state.Request?.Cancel();
        var request = new CancellationTokenSource();
        state.Request = request;
        _ = GenerateAndSaveAsync(note, state, state.Revision, note.Content, request);
    }

    private async Task GenerateAndSaveAsync(Note note, State state, int revision,
        string content, CancellationTokenSource request)
    {
        try
        {
            await Task.Delay(900, request.Token);
            LlmSettings config = _settings.Llm;
            if (!config.Enabled || revision != state.Revision)
            {
                if (revision == state.Revision)
                {
                    SetPending(note.Id, state, false);
                }
                return;
            }

            string title = await _generator.GenerateAsync(content, config, request.Token);
            if (_disposed || request.IsCancellationRequested || revision != state.Revision
                || !ReferenceEquals(config, _settings.Llm) || !config.Enabled
                || !string.Equals(note.Content, content, StringComparison.Ordinal))
            {
                if (revision == state.Revision)
                {
                    SetPending(note.Id, state, false);
                }
                return;
            }

            string? previousTitle = note.AutoTitle;
            DateTimeOffset previousUpdatedAt = note.UpdatedAt;
            note.AutoTitle = title;
            note.UpdatedAt = _clock.Now;
            try
            {
                await _storage.SaveAsync(note, request.Token);
            }
            catch
            {
                note.AutoTitle = previousTitle;
                if (revision == state.Revision)
                {
                    note.UpdatedAt = previousUpdatedAt;
                }
                throw;
            }
            if (revision != state.Revision || !string.Equals(note.Content, content, StringComparison.Ordinal))
            {
                return;
            }

            state.Baseline = content;
            SetPending(note.Id, state, false);
            TitleUpdated?.Invoke(note.Id);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"自动标题生成失败：{exception}");
            if (!_disposed && revision == state.Revision)
            {
                SetPending(note.Id, state, false);
                GenerationFailed?.Invoke(note.Id);
            }
        }
        finally
        {
            if (ReferenceEquals(state.Request, request))
            {
                state.Request = null;
            }

            request.Dispose();
        }
    }

    private State GetState(Note note)
    {
        Register(note);
        return _states[note.Id];
    }

    private void SetPending(Guid id, State state, bool pending)
    {
        if (state.Pending == pending)
        {
            return;
        }

        state.Pending = pending;
        GenerationStateChanged?.Invoke(id, pending);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (State state in _states.Values)
        {
            state.Request?.Cancel();
        }
    }

    private sealed class State(string baseline)
    {
        public string Baseline { get; set; } = baseline;
        public int Revision { get; set; }
        public bool Pending { get; set; }
        public CancellationTokenSource? Request { get; set; }
    }
}
