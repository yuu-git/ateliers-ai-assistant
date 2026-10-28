using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Application.Models;

/// <summary>
/// Desktop の単一チャット用ライフサイクル。UI / HTTP に依存しない。
/// ps の一覧は管理対象に取り込まない。明示ロード・チャット使用時だけ追跡する。
/// </summary>
public sealed class ChatModelSession : IChatService, IModelLifecycleService
{
    private readonly IChatService _chat;
    private readonly IModelControlService _control;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _operations = new();
    private readonly HashSet<string> _managed = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private IReadOnlyList<LoadedModel> _loaded = Array.Empty<LoadedModel>();
    private volatile bool _busy;
    private volatile bool _closing;
    private volatile bool _known;
    private Task? _shutdown;

    /// <summary>チャット通信とモデル操作の実装を受け取り、単一チャット用セッションを構成する。</summary>
    /// <param name="chat">回答生成と利用可能モデルの一覧取得を行うサービス。</param>
    /// <param name="control">モデルのロード・停止とサーバー全体の状態取得を行うサービス。</param>
    public ChatModelSession(IChatService chat, IModelControlService control)
    {
        _chat = chat;
        _control = control;
    }

    /// <summary>モデル状態または操作状態が変わったときに発生する。</summary>
    /// <remarks>通知スレッドは保証しない。UI の購読側で必要なスレッドへ移送する。</remarks>
    public event EventHandler? Changed;
    /// <summary>チャットまたはモデル操作が排他実行中かどうかを取得する。</summary>
    public bool IsBusy => _busy;
    /// <summary>終了処理を開始し、新しい操作を受け付けない状態かどうかを取得する。</summary>
    public bool IsClosing => _closing;
    /// <summary>最後のロード状態取得が成功し、一覧を確認済みかどうかを取得する。</summary>
    public bool IsStateKnown => _known;
    /// <summary>Ollama 全体で最後に取得したロード状態の一覧を取得する。</summary>
    public IReadOnlyList<LoadedModel> LoadedModels => _loaded;
    /// <summary>本アプリがロード・使用を要求したモデル名のスナップショットを取得する。</summary>
    /// <remarks>他アプリとの共有や排他的な所有権を表すものではない。</remarks>
    public IReadOnlyList<string> ManagedModels { get { lock (_sync) return _managed.ToArray(); } }

    /// <summary>セッション終了の中止要求も伝播させて、利用可能モデルを取得する。</summary>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>利用可能なモデル名の一覧。</returns>
    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operations.Token);
        return await _chat.GetModelsAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>他の操作との競合を防ぎながらロード状態を再取得する。</summary>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operations.Token);
        using var operation = await EnterAsync(linked.Token).ConfigureAwait(false);
        await RefreshCoreAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>モデルを管理対象として追跡し、ロードと実状態の確認を行う。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    public async Task LoadAsync(string model, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operations.Token);
        using var operation = await EnterAsync(linked.Token).ConfigureAwait(false);
        // 中止や応答喪失後もサーバーにロード済みの可能性があるため、要求前に記録する。
        Track(model);
        try { await _control.LoadModelAsync(model, linked.Token).ConfigureAwait(false); }
        finally { await RefreshCoreAsync(linked.Token).ConfigureAwait(false); }
        if (!IsLoaded(model)) throw new ChatServiceException("起動要求後にモデルのロードを確認できませんでした。");
    }

    /// <summary>利用者が明示したモデルを停止し、実状態を再取得する。</summary>
    /// <remarks>管理対象外も停止できる。モデル変更時の限定停止には <see cref="ReleasePreviousAsync"/> を使用する。</remarks>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) =>
        UnloadOperationAsync(model, managedOnly: false, cancellationToken);

    /// <summary>選択変更前のモデルが管理対象の場合に限り停止し、ロード状態を更新する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    public Task ReleasePreviousAsync(string model, CancellationToken cancellationToken = default) =>
        UnloadOperationAsync(model, managedOnly: true, cancellationToken);

    /// <summary>停止対象の限定と排他制御を適用して、モデルの停止を実行する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="managedOnly">本セッションの追跡対象だけを停止する場合は true。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task UnloadOperationAsync(string model, bool managedOnly, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operations.Token);
        using var operation = await EnterAsync(linked.Token).ConfigureAwait(false);
        if (!managedOnly || IsManaged(model)) await UnloadCoreAsync(model, linked.Token).ConfigureAwait(false);
        else await RefreshCoreAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>使用モデルを追跡して回答をストリーミングし、終了時にロード状態を再確認する。</summary>
    /// <remarks>列挙中はモデル操作を排他制御する。状態再取得の失敗で、回答の成功・中止を上書きしない。</remarks>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="messages">確定済みの会話履歴と、最後の要素に置く今回の利用者入力。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>受信順に列挙する回答本文の断片。</returns>
    public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operations.Token);
        using var operation = await EnterAsync(linked.Token).ConfigureAwait(false);
        Track(model);
        try
        {
            await foreach (var chunk in _chat.StreamReplyAsync(model, messages, linked.Token).ConfigureAwait(false))
                yield return chunk;
        }
        finally
        {
            // 回答の成功・中止を状態確認の失敗で上書きしない。未知状態は UI に通知する。
            using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_operations.Token);
            refresh.CancelAfter(TimeSpan.FromSeconds(3));
            try { await RefreshCoreAsync(refresh.Token).ConfigureAwait(false); }
            catch (Exception exception) { Trace.TraceError("Model state refresh after chat: {0}", exception); }
        }
    }

    /// <summary>状態を一度未確認にし、取得に成功した一覧と確認済み状態を通知する。</summary>
    /// <param name="token">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task RefreshCoreAsync(CancellationToken token)
    {
        _known = false;
        Changed?.Invoke(this, EventArgs.Empty);
        var models = await _control.GetLoadedModelsAsync(token).ConfigureAwait(false);
        _loaded = models.ToArray();
        _known = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>モデルの停止を要求し、停止を確認できた場合に管理対象から除外する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="token">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task UnloadCoreAsync(string model, CancellationToken token)
    {
        try { await _control.UnloadModelAsync(model, token).ConfigureAwait(false); }
        finally { await RefreshCoreAsync(token).ConfigureAwait(false); }
        if (IsLoaded(model)) throw new ChatServiceException("停止要求後もモデルがロードされています。一覧を更新して確認してください。");
        lock (_sync)
        {
            foreach (var name in _managed.ToArray())
                if (SameModel(name, model)) _managed.Remove(name);
        }
    }

    /// <summary>最後に取得した一覧から、タグ表記を正規化してモデルを検索する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <returns>一致したモデル情報。見つからない場合は null。</returns>
    private LoadedModel? Find(string model) => _loaded.FirstOrDefault(loaded => SameModel(loaded.Name, model));
    // /api/ps が :latest を補完する場合にも同一モデルとして扱う。
    /// <summary>省略された latest タグを補完して、二つのモデル名が同一か判定する。</summary>
    /// <param name="left">比較する一方のモデル名。</param>
    /// <param name="right">比較するもう一方のモデル名。</param>
    /// <returns>正規化したモデル名が一致する場合は true。</returns>
    public static bool SameModel(string left, string right) => Canonical(left) == Canonical(right);
    /// <summary>モデル名の末尾にタグがない場合だけ latest を補完する。</summary>
    /// <param name="name">タグを補完するモデル名。</param>
    /// <returns>必要な場合だけ latest タグを補完したモデル名。</returns>
    private static string Canonical(string name) => name[(name.LastIndexOf('/') + 1)..].Contains(':') ? name : name + ":latest";
    /// <summary>最後に取得した一覧に対象モデルが含まれるか判定する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <returns>最後に取得した一覧でモデルがロード済みなら true。</returns>
    private bool IsLoaded(string model) => Find(model) is not null;
    /// <summary>対象モデルが本セッションの追跡対象か判定する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <returns>モデルが追跡対象なら true。</returns>
    private bool IsManaged(string model) { lock (_sync) return _managed.Any(name => SameModel(name, model)); }
    /// <summary>通信失敗・中止後の解放にも備えて、要求前にモデル名を追跡する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    private void Track(string model)
    {
        lock (_sync)
        {
            if (!_managed.Any(name => SameModel(name, model))) _managed.Add(model);
        }
    }

    /// <summary>競合操作を待たずに拒否し、取得した排他権を解放するためのハンドルを返す。</summary>
    /// <param name="token">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作終了時に一度だけ破棄する排他権の解放ハンドル。</returns>
    private async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        if (_closing) throw new OperationCanceledException("アプリケーションを終了しています。", token);
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
            throw new ChatServiceException("チャットまたはモデル操作の完了を待ってください。");
        if (_closing) { _gate.Release(); throw new OperationCanceledException(token); }
        _busy = true;
        Changed?.Invoke(this, EventArgs.Empty);
        return new Operation(this);
    }

    /// <summary>終了処理を一度だけ開始し、同じ完了タスクを返す。</summary>
    /// <remarks>進行中の操作を中止し、期限内に管理対象モデルだけの解放を試みる。停止失敗は記録し、終了を妨げない。</remarks>
    /// <param name="timeout">終了時の待機・解放処理全体に許容する時間。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    public Task ShutdownAsync(TimeSpan timeout)
    {
        lock (_sync) return _shutdown ??= ShutdownCoreAsync(timeout);
    }

    /// <summary>期限付きで排他権を待ち、管理対象のロード済みモデルを順に解放する。</summary>
    /// <param name="timeout">終了時の待機・解放処理全体に許容する時間。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task ShutdownCoreAsync(TimeSpan timeout)
    {
        _closing = true;
        _operations.Cancel();
        Changed?.Invoke(this, EventArgs.Empty);
        using var deadline = new CancellationTokenSource(timeout);
        var acquired = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            _busy = true;
            // 管理対象だけを処理する。失敗したモデルがあっても次の対象を試す。
            foreach (var model in ManagedModels)
            {
                try
                {
                    await RefreshCoreAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (IsLoaded(model))
                        await UnloadCoreAsync(model, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) { Trace.TraceError("Model cleanup ({0}): {1}", model, exception); }
                if (deadline.IsCancellationRequested) break;
            }
        }
        catch (Exception exception) { Trace.TraceError("Model cleanup deadline: {0}", exception); }
        finally
        {
            if (acquired) _gate.Release();
            _busy = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>処理終了時にセッションの排他権と実行中状態を解放するハンドル。</summary>
    /// <param name="owner">排他実行状態を管理するセッション。</param>
    private sealed class Operation(ChatModelSession owner) : IDisposable
    {
        /// <summary>排他権を解放し、操作可能になったことを通知する。</summary>
        public void Dispose()
        {
            owner._busy = false;
            owner._gate.Release();
            owner.Changed?.Invoke(owner, EventArgs.Empty);
        }
    }
}
