using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.Conversations;
using Ateliers.Ai.Assistant.Application.Storage;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>単一会話の入力・履歴・送受信・モデル操作を調整する ViewModel。</summary>
/// <remarks>UI スレッドから利用する。HTTP、クリップボード、HTML、WebView2 は表示層・サービス層へ分離する。</remarks>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly IChatService _chatService;
    private readonly IModelLifecycleService? _lifecycle;
    private readonly Action<Action> _dispatch;
    private bool _isManagingModel;
    private bool _isPreparingImage;
    private ChatImage? _pendingImage;
    private readonly ObservableCollection<ChatMessageViewModel> _messages = new();
    private readonly ObservableCollection<string> _models = new();
    // 表示用コレクションから履歴を再構築しない。完了した送受信だけを確定する。
    private readonly List<ChatMessage> _history = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _generation;
    private string _inputText = string.Empty;
    private string? _selectedModel;
    private string _statusText = "モデル一覧を取得してください。";
    private string _errorMessage = string.Empty;
    private bool _isSending;
    private bool _isLoadingModels;
    private bool _disposed;

    /// <summary>チャットサービスと任意のモデル管理・UI 通知移送を受け取り、コマンドを構成する。</summary>
    /// <param name="chatService">回答生成と利用可能モデルの一覧取得を行うサービス。</param>
    /// <param name="lifecycle">単一チャット用のモデル管理サービス。null ならモデル管理機能を使わない。</param>
    /// <param name="dispatch">サービス通知を UI スレッドへ移送する処理。省略時はその場で実行する。</param>
    /// <param name="conversationStore">会話の保存先。null は永続化を使わない検証用構成。</param>
    /// <param name="diagnostics">本文・画像を出さない診断ログ。省略時は永続ログを出さない。</param>
    public ChatViewModel(IChatService chatService, IModelLifecycleService? lifecycle = null, Action<Action>? dispatch = null,
        IConversationStore? conversationStore = null, IAppDiagnostics? diagnostics = null)
    {
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _lifecycle = lifecycle;
        _dispatch = dispatch ?? (action => action());
        Messages = new ReadOnlyObservableCollection<ChatMessageViewModel>(_messages);
        Models = new ReadOnlyObservableCollection<string>(_models);
        SendCommand = new AsyncCommand(SendAsync, () => CanEdit &&
            (!string.IsNullOrWhiteSpace(InputText) || PendingImage is not null) && SelectedModel is not null && _models.Contains(SelectedModel));
        RefreshModelsCommand = new AsyncCommand(RefreshModelsAsync, () => CanEdit);
        CancelCommand = new RelayCommand(CancelReply,
            () => !_disposed && IsSending && _generation is { IsCancellationRequested: false });
        NewConversationCommand = new RelayCommand(() =>
        {
            if (_conversationStore is null) NewConversation();
            else _ = StartNewConversationAsync();
        }, () => CanEdit);
        RemoveImageCommand = new RelayCommand(() => PendingImage = null, () => CanEdit && PendingImage is not null);
        LoadModelCommand = new AsyncCommand(() => RunModelOperationAsync(() => _lifecycle!.LoadAsync(SelectedModel!, _lifetime.Token)),
            () => CanEdit && SelectedModel is not null && _lifecycle?.IsStateKnown == true && !IsSelectedModelLoaded);
        UnloadModelCommand = new AsyncCommand(() => RunModelOperationAsync(() => _lifecycle!.UnloadAsync(SelectedModel!, _lifetime.Token)),
            () => CanEdit && SelectedModel is not null && _lifecycle?.IsStateKnown == true && IsSelectedModelLoaded);
        if (_lifecycle is not null) _lifecycle.Changed += LifecycleChanged;
        _conversationStore = conversationStore;
        _diagnostics = diagnostics;
        InitializeConversationCommands();
    }

    /// <summary>中止・失敗したターンも含む、画面表示用のメッセージ一覧を取得する。</summary>
    public ReadOnlyObservableCollection<ChatMessageViewModel> Messages { get; }
    /// <summary>取得済みの利用可能モデル名一覧を取得する。</summary>
    public ReadOnlyObservableCollection<string> Models { get; }
    /// <summary>テキストまたは添付画像を送信するコマンドを取得する。</summary>
    public AsyncCommand SendCommand { get; }
    /// <summary>利用可能モデルとロード状態を更新するコマンドを取得する。</summary>
    public AsyncCommand RefreshModelsCommand { get; }
    /// <summary>現在の回答生成の中止を要求するコマンドを取得する。</summary>
    public RelayCommand CancelCommand { get; }
    /// <summary>モデル選択を維持し、入力・添付・表示・送信履歴を消去するコマンドを取得する。</summary>
    public RelayCommand NewConversationCommand { get; }
    /// <summary>送信前の画像添付を除去するコマンドを取得する。</summary>
    public RelayCommand RemoveImageCommand { get; }
    /// <summary>送信待ちの画像添付を取得する。会話履歴とは別に保持する。</summary>
    public ChatImage? PendingImage
    {
        get => _pendingImage;
        private set
        {
            if (!SetProperty(ref _pendingImage, value)) return;
            ConversationEdited();
            OnPropertyChanged(nameof(PendingImageDescription));
            SendCommand.NotifyCanExecuteChanged();
            RemoveImageCommand.NotifyCanExecuteChanged();
        }
    }
    /// <summary>送信待ち画像の画素サイズと容量の表示を取得する。</summary>
    public string PendingImageDescription => PendingImage is null ? "" :
        $"{PendingImage.Width} × {PendingImage.Height} px / {PendingImage.ByteLength / 1024d:F0} KiB";

    /// <summary>入力可能な場合だけ画像準備状態に入り、競合するチャット操作を無効にする。</summary>
    /// <returns>画像準備を開始できた場合は true。操作と競合する場合は false。</returns>
    public bool TryBeginImagePaste()
    {
        if (!CanEdit) return false;
        _isPreparingImage = true;
        NotifyCommands();
        return true;
    }
    /// <summary>画像準備状態を解除し、成功した場合だけ添付を置き換える。</summary>
    /// <param name="image">準備に成功した画像。null の場合は既存添付を維持して準備状態だけを解除する。</param>
    public void CompleteImagePaste(ChatImage? image)
    {
        _isPreparingImage = false;
        if (!_disposed && image is not null) PendingImage = image;
        NotifyCommands();
    }
    /// <summary>選択モデルをロードするコマンドを取得する。</summary>
    public AsyncCommand LoadModelCommand { get; }
    /// <summary>選択中のロード済みモデルを明示的に停止するコマンドを取得する。</summary>
    public AsyncCommand UnloadModelCommand { get; }
    /// <summary>直近のモデル選択変更に伴う解放・状態更新の完了タスクを取得する。</summary>
    public Task ModelChangeTask { get; private set; } = Task.CompletedTask;
    /// <summary>画面初期化時にモデル一覧の取得を開始する。</summary>
    /// <returns>操作の完了を表すタスク。</returns>
    public Task InitializeAsync() => RefreshModelsCommand.ExecuteAsync();

    /// <summary>送信前の入力テキストを取得または設定する。</summary>
    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value)) { SendCommand.NotifyCanExecuteChanged(); ConversationEdited(); }
        }
    }

    /// <summary>選択中モデルを取得または設定する。変更時は管理対象の旧モデルの解放を試みる。</summary>
    /// <remarks>変更中は送信・モデル操作を抑止する。会話を保持し、新モデルは自動ロードしない。</remarks>
    public string? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (_selectedModel == value) return;
            if (!CanEdit) { OnPropertyChanged(); return; }
            var previous = _selectedModel;
            SetProperty(ref _selectedModel, value);
            ConversationEdited();
            NotifyCommands();
            if (_lifecycle is not null) ModelChangeTask = RunModelOperationAsync(() => previous is null
                ? _lifecycle.RefreshAsync(_lifetime.Token) : _lifecycle.ReleasePreviousAsync(previous, _lifetime.Token), deferNotification: true);
        }
    }

    /// <summary>回答の送受信が進行中かどうかを取得する。</summary>
    public bool IsSending
    {
        get => _isSending;
        private set { if (SetProperty(ref _isSending, value)) NotifyCommands(); }
    }
    /// <summary>利用可能モデルの一覧を取得中かどうかを取得する。</summary>
    public bool IsLoadingModels
    {
        get => _isLoadingModels;
        private set { if (SetProperty(ref _isLoadingModels, value)) NotifyCommands(); }
    }
    /// <summary>送信・一覧取得・画像準備・モデル操作・終了の競合がなく、入力できるか取得する。</summary>
    public bool CanEdit => !_disposed && !_conversationBusy && !_leavingConversation && !IsSending && !IsLoadingModels && !_isManagingModel && !_isPreparingImage &&
        _lifecycle?.IsBusy != true && _lifecycle?.IsClosing != true;
    /// <summary>チャット、画像準備、一覧取得または共有モデル操作が進行中か取得する。</summary>
    public bool IsBusy => _conversationBusy || _leavingConversation || IsSending || IsLoadingModels || _isManagingModel || _isPreparingImage || _lifecycle?.IsBusy == true;
    /// <summary>最後に取得した状態で選択モデルがロード済みかどうかを取得する。</summary>
    public bool IsSelectedModelLoaded => SelectedModel is not null && _lifecycle?.LoadedModels.Any(
        model => ChatModelSession.SameModel(model.Name, SelectedModel)) == true;
    /// <summary>未確認状態も含む選択モデルのロード状態表示を取得する。</summary>
    public string ModelStateText => _lifecycle?.IsStateKnown != true ? "ロード状態未確認／取得失敗。一覧を更新してください。"
        : SelectedModel is null ? "モデルを選択してください。" : IsSelectedModelLoaded ? "選択中モデル: ロード済み" : "選択中モデル: 未ロード";
    /// <summary>会話表示にメッセージがないかどうかを取得する。</summary>
    public bool IsEmpty => Messages.Count == 0;

    /// <summary>チャット全体の現在の処理状態表示を取得する。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }
    /// <summary>利用者へ提示する直近のチャット・モデル操作エラーを取得する。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError));
        }
    }
    /// <summary>表示対象のエラーがあるかどうかを取得する。</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>モデル一覧を再取得し、可能なら以前の選択を維持してロード状態も更新する。</summary>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task RefreshModelsAsync()
    {
        var previous = SelectedModel;
        IsLoadingModels = true;
        ErrorMessage = string.Empty;
        StatusText = "モデル一覧を取得中…";
        SetProperty(ref _selectedModel, null, nameof(SelectedModel));
        _models.Clear();
        try
        {
            var models = await _chatService.GetModelsAsync(_lifetime.Token);
            if (_disposed) return;
            foreach (var model in models) _models.Add(model);
            if (previous is not null && _models.Contains(previous)) SetProperty(ref _selectedModel, previous, nameof(SelectedModel));
            if (_lifecycle is not null)
            {
                if (previous is not null && SelectedModel is null) await _lifecycle.ReleasePreviousAsync(previous, _lifetime.Token);
                else await _lifecycle.RefreshAsync(_lifetime.Token);
            }
            StatusText = Models.Count == 0
                ? "利用可能なモデルがありません。Ollama にモデルを導入して一覧を更新してください。"
                : "モデルを選択してメッセージを送信してください。";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception)
        {
            ReportFailure(exception, "モデル一覧を取得できませんでした。Ollama の状態を確認し、一覧を更新してください。");
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    /// <summary>表示用ターンを追加して回答を受信し、成功した送受信だけを次回の履歴へ確定する。</summary>
    /// <remarks>中止・失敗した入力と部分応答は画面に残すが、次回の要求には含めない。完了済み画像は履歴とともに再送する。</remarks>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task SendAsync()
    {
        var model = SelectedModel!;
        var user = new ChatMessage(ChatRole.User, InputText.Trim(), PendingImage);
        var userDisplay = new ChatMessageViewModel(user, model);
        var assistant = new ChatMessageViewModel(new ChatMessage(ChatRole.Assistant, ""), model, "応答待ち…");
        using var generation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _generation = generation;
        _sendFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsSending = true;
        ErrorMessage = string.Empty;
        StatusText = "応答を待っています…";
        _messages.Add(userDisplay);
        _messages.Add(assistant);
        ConversationEdited();
        OnPropertyChanged(nameof(IsEmpty));
        InputText = string.Empty;
        PendingImage = null;
        try
        {
            var request = _history.Append(user).ToArray();
            // UI スレッドの SynchronizationContext を維持し、コレクションと本文を更新する。
            await foreach (var chunk in _chatService.StreamReplyAsync(model, request, generation.Token))
            {
                generation.Token.ThrowIfCancellationRequested();
                if (_disposed) return;
                assistant.Append(chunk);
                assistant.StatusText = "受信中…";
                StatusText = "回答を受信中…";
            }
            generation.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            if (string.IsNullOrWhiteSpace(assistant.Content))
                throw new ChatServiceException("回答本文を取得できませんでした。");
            _history.Add(user);
            _history.Add(assistant.Message);
            userDisplay.IncludeInHistory();
            assistant.IncludeInHistory();
            assistant.StatusText = "完了";
            StatusText = "送信できます。";
        }
        catch (OperationCanceledException) when (generation.IsCancellationRequested)
        {
            if (!_disposed)
            {
                userDisplay.StatusText = "中止 · 次回の履歴には含めません";
                assistant.StatusText = "中止 · 次回の履歴には含めません";
                StatusText = "応答を中止しました。";
            }
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                userDisplay.StatusText = "失敗 · 次回の履歴には含めません";
                assistant.StatusText = "失敗 · 次回の履歴には含めません";
            }
            ReportFailure(exception, "応答を取得できませんでした。Ollama の状態を確認して、もう一度送信してください。");
        }
        finally
        {
            _generation = null;
            IsSending = false;
            ConversationEdited();
            if (!_disposed) await AutoSaveCompletedTurnAsync();
            _sendFinished?.TrySetResult(true);
        }
    }

    /// <summary>生成の中止を要求し、中止要求の重複操作を無効にする。</summary>
    private void CancelReply()
    {
        StatusText = "応答を中止しています…";
        _generation?.Cancel();
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>モデル操作中のガードと UI 通知を適用し、失敗を利用者に通知する。</summary>
    /// <param name="operation">共通の状態制御の中で実行するモデル操作。</param>
    /// <param name="deferNotification">ComboBox の選択イベントを抜けてから UI の無効化通知を行う場合は true。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task RunModelOperationAsync(Func<Task> operation, bool deferNotification = false)
    {
        _isManagingModel = true;
        // ComboBox の選択通知を抜けてから無効化する。操作のガード自体は即時に有効。
        if (deferNotification) await Task.Yield();
        if (_disposed) { _isManagingModel = false; return; }
        ErrorMessage = string.Empty;
        NotifyCommands();
        try { await operation(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { ReportFailure(exception, "モデル操作に失敗しました。ロード状態を更新して確認してください。"); }
        finally { _isManagingModel = false; NotifyCommands(); }
    }

    /// <summary>任意スレッドからのモデル状態通知を、注入された UI 移送処理へ渡す。</summary>
    /// <param name="sender">通知元のオブジェクト。</param>
    /// <param name="e">イベントまたは起動の引数。</param>
    private void LifecycleChanged(object? sender, EventArgs e) => _dispatch(() => { if (!_disposed) NotifyCommands(); });

    /// <summary>入力・添付・表示・送信履歴を消去し、選択モデルを維持して新しい会話を始める。</summary>
    private void NewConversation()
    {
        _history.Clear();
        _messages.Clear();
        InputText = string.Empty;
        PendingImage = null;
        ErrorMessage = string.Empty;
        StatusText = "新しい会話を開始しました。";
        OnPropertyChanged(nameof(IsEmpty));
        ResetConversationIdentity();
    }

    /// <summary>例外詳細を Trace へ記録し、利用者向けのエラーと状態を更新する。</summary>
    /// <param name="exception">記録または利用者向け表示へ変換する例外。</param>
    /// <param name="fallback">具体的な説明を公開できない例外の場合に表示する説明。</param>
    private void ReportFailure(Exception exception, string fallback)
    {
        _diagnostics?.Error("chat.operation", exception);
        Trace.TraceError("Chat operation failed: {0}", exception);
        if (_disposed) return;
        ErrorMessage = exception is ChatServiceException ? exception.Message : fallback;
        StatusText = "処理に失敗しました。";
    }

    /// <summary>派生プロパティと各コマンドの実行可能状態をまとめて再通知する。</summary>
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsSelectedModelLoaded));
        OnPropertyChanged(nameof(ModelStateText));
        SendCommand.NotifyCanExecuteChanged();
        RefreshModelsCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        NewConversationCommand.NotifyCanExecuteChanged();
        RemoveImageCommand.NotifyCanExecuteChanged();
        LoadModelCommand.NotifyCanExecuteChanged();
        UnloadModelCommand.NotifyCanExecuteChanged();
        NotifyConversationCommands();
    }

    /// <summary>モデル状態の購読を解除し、進行中処理と遅れて届く結果を無効にする。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lifecycle is not null) _lifecycle.Changed -= LifecycleChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        NotifyCommands();
    }
}
