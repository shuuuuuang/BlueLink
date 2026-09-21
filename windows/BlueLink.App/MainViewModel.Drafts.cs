using System.Windows;
using BlueLink.Domain;

namespace BlueLink;

public sealed partial class MainViewModel
{
    private ComposerDrafts? _composerStore;
    internal ComposerDrafts ComposerStore => _composerStore ??= new(DataDirectory);
    internal event Action<string?>? ComposerCleared;
    private readonly DraftLedger _drafts = new();
    private readonly SemaphoreSlim _draftSaveGate = new(1, 1);
    private CancellationTokenSource? _draftDelay;
    private bool _draftsReady;
    private bool _draftSaveFailed;

    public bool CanEditDraft => _draftsReady && HasActiveConversation && !_resettingIdentity;
    public string DraftText
    {
        get => _activePeerId is { } peer ? _drafts.Get(peer).Text : "";
        set
        {
            if (!CanEditDraft || _activePeerId is not { } peer) return;
            _drafts.Edit(peer, value ?? "");
            Raise(nameof(DraftText));
            ScheduleDraftSave();
        }
    }

    private void LoadDrafts()
    {
        _drafts.Load(_storedConversations.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value.Draft)));
        _draftsReady = true;
        Raise(nameof(CanEditDraft)); Raise(nameof(DraftText));
    }
    private void ScheduleDraftSave()
    {
        _draftDelay?.Cancel(); _draftDelay?.Dispose();
        var delay = new CancellationTokenSource(); _draftDelay = delay;
        var token = delay.Token;
        _ = SaveLaterAsync(token);
    }
    private async Task SaveLaterAsync(CancellationToken token)
    {
        try { await Task.Delay(250, token); }
        catch (OperationCanceledException) { return; }
        // The debounce token must never cancel an already submitted database write.
        await FlushDraftsAsync();
    }
    internal async Task<bool> FlushDraftsAsync()
    {
        await _draftSaveGate.WaitAsync();
        try { return await PersistDraftsAsync(); }
        finally { _draftSaveGate.Release(); }
    }
    private async Task<bool> PersistDraftsAsync()
    {
        try
        {
            await _conversationPersistenceGate.WaitAsync();
            try
            {
                foreach (var draft in _drafts.Pending())
                {
                    if (!_storedConversations.TryGetValue(draft.PeerId, out var conversation))
                        throw new System.IO.IOException("Draft conversation is not available.");
                    await _database.UpdateDraftAsync(draft.PeerId, draft.Text);
                    _storedConversations[draft.PeerId] = conversation with { Draft = draft.Text };
                    _drafts.Acknowledge(draft);
                }
            }
            finally { _conversationPersistenceGate.Release(); }
            _draftSaveFailed = false;
            return true;
        }
        catch
        {
            if (!_draftSaveFailed && Application.Current?.Dispatcher is { HasShutdownStarted: false } dispatcher)
                await dispatcher.InvokeAsync(() => TransientNoticeRequested?.Invoke(
                    Localization.Strings.Get("草稿尚未保存，请检查存储空间后重试。"), ToastLevel.Error));
            _draftSaveFailed = true;
            return false;
        }
    }
    internal DraftSnapshot? CaptureDraft() => CanEditDraft && _activePeerId is { } peer ? _drafts.Get(peer) : null;
    internal async Task CompleteDraftSendAsync(DraftSnapshot sent)
    {
        if (!_draftsReady || !_drafts.ClearAfterSend(sent)) return;
        Raise(nameof(DraftText));
        await FlushDraftsAsync();
    }
    private async Task ClearDraftsAsync(string? peer = null)
    {
        await _draftSaveGate.WaitAsync();
        try
        {
            ComposerStore.Clear(peer);
            _drafts.Clear(peer); Raise(nameof(DraftText)); ComposerCleared?.Invoke(peer);
            if (!await PersistDraftsAsync()) throw new System.IO.IOException("Draft could not be cleared from storage.");
        }
        finally { _draftSaveGate.Release(); }
    }
}
