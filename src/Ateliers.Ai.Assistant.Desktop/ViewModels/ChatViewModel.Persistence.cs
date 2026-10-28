using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Conversations;
using Ateliers.Ai.Assistant.Application.Storage;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>会話を離れるときの利用者の選択。</summary>
public enum ConversationLeaveDecision
{
    /// <summary>保存して移動する。</summary>
    Save,
    /// <summary>保存せず現在の変更を破棄する。</summary>
    Discard,
    /// <summary>移動せず現在の会話を維持する。</summary>
    Cancel
}

/// <summary>会話一覧の表示項目。本文と画像は選択して開くまで取得しない。</summary>
public sealed class SavedConversationViewModel(ConversationSummary summary)
{
    /// <summary>保存先の識別子。</summary>
    public Guid Id => summary.Id;
    /// <summary>利用者指定の見出し。</summary>
    public string Title => summary.Title;
    /// <summary>ローカル時刻の更新日時と使用モデル。</summary>
    public string Detail => $"{summary.UpdatedAt.ToLocalTime():yyyy/MM/dd HH:mm} · {summary.Model ?? "モデル未選択"}";
}

/// <summary>会話の明示保存、再開、削除、下書きと変更破棄の制御を担当する部分。</summary>
public sealed partial class ChatViewModel
{
    private readonly IConversationStore? _conversationStore;
    private readonly IAppDiagnostics? _diagnostics;
    private readonly ObservableCollection<SavedConversationViewModel> _savedConversations = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Guid _conversationId = Guid.NewGuid();
    private DateTimeOffset _conversationCreatedAt = DateTimeOffset.UtcNow;
    private string _conversationTitle = "";
    private long _conversationVersion;
    private long _editRevision;
    private long _savedRevision;
    private long _conversationGeneration;
    private long _autoSaveRequest;
    private bool _conversationIsSaved;
    private bool _conversationBusy;
    private bool _leavingConversation;
    private bool _restoringConversation;
    private string _persistenceError = "";
    private SavedConversationViewModel? _selectedSavedConversation;
    private TaskCompletionSource<bool>? _sendFinished;

    /// <summary>保存・破棄・キャンセルの確認を UI に依頼する。未設定時はキャンセルする。</summary>
    public Func<Task<ConversationLeaveDecision>>? ConfirmLeaveAsync { get; set; }
    /// <summary>保存名の入力を UI に依頼する。null の戻り値は操作中止。</summary>
    public Func<string, Task<string?>>? RequestConversationTitleAsync { get; set; }
    /// <summary>選択スレッドの削除確認を UI に依頼する。未設定なら削除しない。</summary>
    public Func<string, Task<bool>>? ConfirmDeleteConversationAsync { get; set; }
    /// <summary>チャット画面内の保存済み会話一覧。</summary>
    public ReadOnlyObservableCollection<SavedConversationViewModel> SavedConversations { get; private set; } = null!;
    /// <summary>明示保存または名前変更を行うコマンド。</summary>
    public AsyncCommand SaveConversationCommand { get; private set; } = null!;
    /// <summary>一覧から選択した会話を開くコマンド。</summary>
    public AsyncCommand OpenConversationCommand { get; private set; } = null!;
    /// <summary>一覧の会話と関連画像を削除するコマンド。</summary>
    public AsyncCommand DeleteConversationCommand { get; private set; } = null!;
    /// <summary>保存済み会話一覧を再取得するコマンド。</summary>
    public AsyncCommand RefreshConversationsCommand { get; private set; } = null!;
    /// <summary>開く・削除の対象として選択した保存済み会話。</summary>
    public SavedConversationViewModel? SelectedSavedConversation
    {
        get => _selectedSavedConversation;
        set { if (SetProperty(ref _selectedSavedConversation, value)) NotifyConversationCommands(); }
    }
    /// <summary>表示・入力・添付のいずれかが存在するか。</summary>
    public bool HasConversationContent => Messages.Count != 0 || !string.IsNullOrWhiteSpace(InputText) || PendingImage is not null;
    /// <summary>明示保存されていない内容、または最後の保存後の変更があるか。</summary>
    public bool HasUnsavedConversationChanges => _conversationIsSaved ? _editRevision != _savedRevision : HasConversationContent;
    /// <summary>会話一覧が空かどうか。</summary>
    public bool IsSavedConversationsEmpty => SavedConversations.Count == 0;
    /// <summary>空状態と保存済み件数の説明。</summary>
    public string SavedConversationsStateText => IsSavedConversationsEmpty ? "保存済みの会話はありません。必要な会話だけ保存できます。" : $"保存済み: {SavedConversations.Count} 件";
    /// <summary>現在の会話名と、実際の保存状態を区別する表示。</summary>
    public string ConversationStorageStatus => !_conversationIsSaved ? "新しい会話 · 未保存"
        : $"{_conversationTitle} · {(HasUnsavedConversationChanges ? "未保存の変更あり" : "保存済み")}";
    /// <summary>DB・保存・復元の失敗。チャット通信のエラーとは別に保持する。</summary>
    public string PersistenceErrorMessage { get => _persistenceError; private set { if (SetProperty(ref _persistenceError, value)) OnPropertyChanged(nameof(HasPersistenceError)); } }
    /// <summary>保存関連のエラーが表示対象かどうか。</summary>
    public bool HasPersistenceError => PersistenceErrorMessage.Length != 0;

    /// <summary>永続化用コマンドを構成する。通常起動以外の検証ではストアを省略できる。</summary>
    private void InitializeConversationCommands()
    {
        SavedConversations = new(_savedConversations);
        SaveConversationCommand = new AsyncCommand(() => SaveWithTitleAsync(), () => CanEdit && _conversationStore is not null && (HasConversationContent || _conversationIsSaved));
        OpenConversationCommand = new AsyncCommand(OpenSelectedConversationAsync, () => CanEdit && _conversationStore is not null && SelectedSavedConversation is not null);
        DeleteConversationCommand = new AsyncCommand(DeleteSelectedConversationAsync, () => CanEdit && _conversationStore is not null && SelectedSavedConversation is not null);
        RefreshConversationsCommand = new AsyncCommand(RefreshSavedConversationsAsync, () => CanEdit && _conversationStore is not null);
    }

    /// <summary>現在の内容を変更済みにし、保存済み会話だけを遅延自動保存する。</summary>
    private void ConversationEdited()
    {
        if (_restoringConversation || _disposed) return;
        ++_editRevision;
        NotifyConversationCommands();
        if (_conversationIsSaved && !IsSending && !_isPreparingImage && !_leavingConversation)
            _ = DebouncedAutoSaveAsync(_conversationGeneration, ++_autoSaveRequest);
    }

    /// <summary>下書き・モデル選択の変更を500ms集約し、古い会話への書き込みを防ぐ。</summary>
    private async Task DebouncedAutoSaveAsync(long generation, long request)
    {
        try
        {
            await Task.Delay(500, _lifetime.Token);
            if (!_disposed && generation == _conversationGeneration && request == _autoSaveRequest && !_leavingConversation && !_conversationBusy && !IsSending && HasUnsavedConversationChanges)
                await SaveCurrentAsync(null, generation);
        }
        catch (OperationCanceledException) when (_disposed) { }
    }

    /// <summary>回答の完了・中止・失敗を確定してから自動保存する。途中の断片ごとには保存しない。</summary>
    private Task AutoSaveCompletedTurnAsync() => _conversationIsSaved && _conversationStore is not null
        ? SaveCurrentAsync(null, _conversationGeneration) : Task.CompletedTask;

    /// <summary>保存名を指定して明示保存する。ダイアログ中は他の変更を止める。</summary>
    private async Task<bool> SaveWithTitleAsync()
    {
        if (_conversationStore is null || !HasConversationContent && !_conversationIsSaved) return false;
        var wasBusy = _conversationBusy;
        _conversationBusy = true; NotifyCommands();
        try
        {
            var suggested = _conversationTitle.Length == 0 ? "新しい会話" : _conversationTitle;
            var title = RequestConversationTitleAsync is null ? suggested : await RequestConversationTitleAsync(suggested);
            return title is not null && await SaveCurrentAsync(title, _conversationGeneration);
        }
        catch (Exception exception) { PersistenceFailure("conversation.save.dialog", exception); return false; }
        finally { _conversationBusy = wasBusy; NotifyCommands(); }
    }

    /// <summary>同じ会話の保存を直列化し、実際に確定した変更番号だけを保存済みとする。</summary>
    private async Task<bool> SaveCurrentAsync(string? title, long generation)
    {
        if (_conversationStore is null || _disposed) return false;
        await _saveGate.WaitAsync();
        try
        {
            if (_disposed || generation != _conversationGeneration) return false;
            if (title is null && !HasUnsavedConversationChanges) return true;
            var revision = _editRevision;
            var snapshot = new ConversationSnapshot(_conversationId, title ?? _conversationTitle, _conversationCreatedAt,
                DateTimeOffset.UtcNow, SelectedModel, Messages.Select(message => message.Capture()).ToArray(), InputText, PendingImage, _conversationVersion);
            var saved = await _conversationStore.SaveAsync(snapshot, _lifetime.Token);
            if (_disposed || generation != _conversationGeneration) return false;
            _conversationVersion = saved.Version;
            _conversationTitle = saved.Title;
            _conversationIsSaved = true;
            _savedRevision = revision;
            PersistenceErrorMessage = "";
            await RefreshSavedConversationsAsync();
            return true;
        }
        catch (OperationCanceledException) when (_disposed) { return false; }
        catch (Exception exception) { PersistenceFailure("conversation.save", exception); return false; }
        finally { _saveGate.Release(); NotifyConversationCommands(); }
    }

    /// <summary>一覧だけを再取得し、本文や画像を一括読み込みしない。</summary>
    public async Task RefreshSavedConversationsAsync()
    {
        if (_conversationStore is null || _disposed) return;
        try
        {
            var selected = SelectedSavedConversation?.Id;
            var summaries = await _conversationStore.ListAsync(_lifetime.Token);
            if (_disposed) return;
            _savedConversations.Clear();
            foreach (var summary in summaries) _savedConversations.Add(new(summary));
            SelectedSavedConversation = _savedConversations.FirstOrDefault(item => item.Id == selected);
            NotifyConversationCommands();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { PersistenceFailure("conversation.list", exception); }
    }

    /// <summary>未保存内容を確認した後に新しいメモリ内の会話を開始する。</summary>
    public async Task StartNewConversationAsync()
    {
        if (!CanEdit || !await PrepareToLeaveConversationAsync()) return;
        NewConversation();
    }

    /// <summary>離脱・終了前に生成を中止して待ち、保存・破棄・キャンセルを解決する。</summary>
    public async Task<bool> PrepareToLeaveConversationAsync()
    {
        if (_leavingConversation || _conversationBusy || _isPreparingImage) return false;
        ++_autoSaveRequest; // 遷移前の遅延保存が、読込済みスナップショットの後から DB を更新することを防ぐ。
        _leavingConversation = true; NotifyCommands();
        try
        {
            if (IsSending)
            {
                CancelReply();
                if (_sendFinished is { } finished) await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            // 先行していた自動保存の結果を待ってから、残った変更だけを確認する。
            await _saveGate.WaitAsync(); _saveGate.Release();
            if (_conversationIsSaved && HasUnsavedConversationChanges && await SaveCurrentAsync(null, _conversationGeneration)) return true;
            if (!HasUnsavedConversationChanges) return true;
            var choice = ConfirmLeaveAsync is null ? ConversationLeaveDecision.Cancel : await ConfirmLeaveAsync();
            if (choice == ConversationLeaveDecision.Save) return await SaveWithTitleAsync();
            if (choice != ConversationLeaveDecision.Discard) return false;
            NewConversation();
            return true;
        }
        catch (Exception exception) { PersistenceFailure("conversation.leave", exception); return false; }
        finally { _leavingConversation = false; NotifyCommands(); }
    }

    /// <summary>保存済み会話を読み込めた場合だけ現在の内容と置き換える。</summary>
    private async Task OpenSelectedConversationAsync()
    {
        var selected = SelectedSavedConversation;
        if (selected is null || _conversationStore is null || !await PrepareToLeaveConversationAsync()) return;
        _conversationBusy = true; NotifyCommands();
        try
        {
            var snapshot = await _conversationStore.LoadAsync(selected.Id, _lifetime.Token);
            if (_disposed) return;
            if (snapshot is null) throw new StorageException("会話が見つかりません。一覧を更新してください。");
            await RestoreSelectedModelAsync(snapshot.SelectedModel);
            _restoringConversation = true;
            ++_conversationGeneration; ++_autoSaveRequest;
            _messages.Clear(); _history.Clear();
            foreach (var stored in snapshot.Messages)
            {
                _messages.Add(new(stored.Message, stored.Model, stored.StatusText, stored.Id, stored.CreatedAt, stored.IncludedInHistory));
                if (stored.IncludedInHistory) _history.Add(stored.Message);
            }
            _conversationId = snapshot.Id; _conversationTitle = snapshot.Title; _conversationCreatedAt = snapshot.CreatedAt;
            _conversationVersion = snapshot.Version; _conversationIsSaved = true;
            InputText = snapshot.DraftText; PendingImage = snapshot.DraftImage;
            _editRevision = _savedRevision = 0;
            PersistenceErrorMessage = "";
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception exception) { PersistenceFailure("conversation.open", exception); }
        finally { _restoringConversation = false; _conversationBusy = false; NotifyCommands(); }
    }

    /// <summary>モデルが存在する場合だけ選択し、旧モデルの管理対象解放は既存サービスへ委譲する。</summary>
    public async Task RestoreSelectedModelAsync(string? requested)
    {
        var target = requested is not null && _models.Contains(requested) ? requested : null;
        var previous = _selectedModel;
        SetProperty(ref _selectedModel, target, nameof(SelectedModel));
        if (_lifecycle is not null && previous is not null && previous != target)
            await RunModelOperationAsync(() => _lifecycle.ReleasePreviousAsync(previous, _lifetime.Token));
        if (requested is not null && target is null)
            ErrorMessage = "保存されたモデルは現在の一覧にありません。利用可能なモデルを選択してください。自動ダウンロードは行いません。";
        NotifyCommands();
    }

    /// <summary>確認後に指定スレッドを削除する。現在の会話なら未保存変更の扱いも先に確認する。</summary>
    private async Task DeleteSelectedConversationAsync()
    {
        var selected = SelectedSavedConversation;
        if (selected is null || _conversationStore is null || ConfirmDeleteConversationAsync is null) return;
        if (selected.Id == _conversationId && !await PrepareToLeaveConversationAsync()) return;
        _conversationBusy = true; NotifyCommands();
        try
        {
            if (!await ConfirmDeleteConversationAsync(selected.Title)) return;
            await _saveGate.WaitAsync();
            try { await _conversationStore.DeleteAsync(selected.Id, _lifetime.Token); }
            finally { _saveGate.Release(); }
            if (_conversationId == selected.Id) NewConversation();
            await RefreshSavedConversationsAsync();
        }
        catch (Exception exception) { PersistenceFailure("conversation.delete", exception); }
        finally { _conversationBusy = false; NotifyCommands(); }
    }

    /// <summary>メモリ内の新規会話へ切り替え、旧会話の遅延保存を無効にする。</summary>
    private void ResetConversationIdentity()
    {
        ++_conversationGeneration; ++_autoSaveRequest;
        _conversationId = Guid.NewGuid(); _conversationCreatedAt = DateTimeOffset.UtcNow;
        _conversationTitle = ""; _conversationVersion = 0; _conversationIsSaved = false;
        _editRevision = _savedRevision = 0;
        PersistenceErrorMessage = "";
        NotifyConversationCommands();
    }

    /// <summary>保存失敗を通知し、本文・画像をログへ出力せず現在の内容を維持する。</summary>
    private void PersistenceFailure(string operation, Exception exception)
    {
        _diagnostics?.Error(operation, exception);
        if (!_disposed) PersistenceErrorMessage = exception is StorageException ? exception.Message : "会話の保存・読込操作に失敗しました。現在の内容を維持しました。";
    }

    /// <summary>ホストの保存・画面遷移・終了処理の失敗を、保存エラー表示へ接続する。</summary>
    public void ReportStorageFailure(string operation, Exception exception) => PersistenceFailure(operation, exception);

    /// <summary>永続化状態の派生表示とコマンドを更新する。</summary>
    private void NotifyConversationCommands()
    {
        OnPropertyChanged(nameof(HasConversationContent)); OnPropertyChanged(nameof(HasUnsavedConversationChanges));
        OnPropertyChanged(nameof(ConversationStorageStatus)); OnPropertyChanged(nameof(IsSavedConversationsEmpty));
        OnPropertyChanged(nameof(SavedConversationsStateText));
        SaveConversationCommand?.NotifyCanExecuteChanged(); OpenConversationCommand?.NotifyCanExecuteChanged();
        DeleteConversationCommand?.NotifyCanExecuteChanged(); RefreshConversationsCommand?.NotifyCanExecuteChanged();
    }
}
