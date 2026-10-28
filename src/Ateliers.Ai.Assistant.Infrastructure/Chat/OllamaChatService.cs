using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Application.Models;

namespace Ateliers.Ai.Assistant.Infrastructure.Chat;

/// <summary>
/// ateliers-learn-llm の HttpClient / api/chat 呼び出しをサービス化。
/// HTTP の所有者は Host。生成全体の期限はストリームの読み取りにも適用する。
/// </summary>
public sealed class OllamaChatService : IChatService, IModelControlService
{
    private readonly HttpClient _client;
    private readonly TimeSpan _modelTimeout;
    private readonly TimeSpan _replyTimeout;

    /// <summary>ホスト所有の HTTP クライアントと、一覧取得・回答全体の期限を受け取る。</summary>
    /// <param name="client">ホストが所有・破棄し、接続先を設定済みの HTTP クライアント。</param>
    /// <param name="modelTimeout">一覧取得の期限。省略時は10秒。</param>
    /// <param name="replyTimeout">回答全体とモデルのロード・停止要求の期限。省略時は10分。</param>
    public OllamaChatService(HttpClient client, TimeSpan? modelTimeout = null, TimeSpan? replyTimeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _modelTimeout = modelTimeout ?? TimeSpan.FromSeconds(10);
        _replyTimeout = replyTimeout ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>api/tags から利用可能モデル名を取得し、重複排除と並べ替えを行う。</summary>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>利用可能なモデル名の一覧。</returns>
    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_modelTimeout);
        try
        {
            using var response = await _client.GetAsync("api/tags", HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, deadline.Token).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaModelList,
                deadline.Token).ConfigureAwait(false);
            if (result?.Models is null || result.Models.Any(model => model is null || string.IsNullOrWhiteSpace(model.Name)))
                throw new ChatServiceException("モデル一覧の形式が正しくありません。");

            return result.Models.Select(model => model.Name!).Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (IsCommunicationFailure(exception, cancellationToken))
        {
            throw Translate(exception);
        }
    }

    /// <summary>テキスト・画像の会話を送信し、期限付きで回答本文を逐次返す。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="messages">確定済みの会話履歴と、最後の要素に置く今回の利用者入力。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>受信順に列挙する回答本文の断片。</returns>
    public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0 || messages[^1].Role != ChatRole.User)
            throw new ArgumentException("最後にユーザーメッセージが必要です。", nameof(messages));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_replyTimeout);
        await using var chunks = ReadReplyAsync(model, messages, deadline.Token).GetAsyncEnumerator(deadline.Token);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await chunks.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsCommunicationFailure(exception, cancellationToken))
            {
                throw Translate(exception);
            }
            if (!hasNext) yield break;
            yield return chunks.Current;
        }
    }

    /// <summary>api/ps のサーバー全体のロード状態を、UI 非依存のモデルへ変換する。</summary>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>サーバー全体のロード中モデル情報の一覧。</returns>
    public async Task<IReadOnlyList<LoadedModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_modelTimeout);
        try
        {
            using var response = await _client.GetAsync("api/ps", HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, deadline.Token).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaRunningModels,
                deadline.Token).ConfigureAwait(false);
            if (result?.Models is null || result.Models.Any(model => model is null ||
                string.IsNullOrWhiteSpace(model.Name) || model.Size < 0 || model.SizeVram < 0))
                throw new ChatServiceException("ロード中モデル一覧の形式が正しくありません。");
            return result.Models.Select(model => new LoadedModel(model.Name!, model.Digest ?? "", model.Size,
                model.SizeVram, model.Details?.Quantization ?? "不明", model.ExpiresAt)).ToArray();
        }
        catch (Exception exception) when (IsCommunicationFailure(exception, cancellationToken))
        {
            throw Translate(exception);
        }
    }

    /// <summary>空の messages と keep_alive=-1 で、指定モデルのロード保持を要求する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    public Task LoadModelAsync(string model, CancellationToken cancellationToken = default) =>
        SetModelLifetimeAsync(model, -1, cancellationToken);

    /// <summary>空の messages と keep_alive=0 で、指定モデルのアンロードを要求する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    public Task UnloadModelAsync(string model, CancellationToken cancellationToken = default) =>
        SetModelLifetimeAsync(model, 0, cancellationToken);

    /// <summary>空の会話を api/chat へ送り、保持時間の変更完了を確認する。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="keepAlive">ロード保持時間。-1 で無期限保持、0 で解放。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    private async Task SetModelLifetimeAsync(string model, int keepAlive, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_replyTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
            {
                Content = JsonContent.Create(new OllamaChatRequest(model, Array.Empty<OllamaRequestMessage>(),
                    false, keepAlive), OllamaJsonContext.Default.OllamaChatRequest)
            };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, deadline.Token).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaChatChunk,
                deadline.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result?.Error)) throw new ChatServiceException($"Ollama エラー: {result.Error}");
            if (result?.Done != true) throw new ChatServiceException("モデル操作の完了を確認できませんでした。");
        }
        catch (Exception exception) when (IsCommunicationFailure(exception, cancellationToken))
        {
            throw Translate(exception);
        }
    }

    /// <summary>画像を Base64 配列で送り、NDJSON の本文を返して異常応答や未完了切断を検出する。</summary>
    /// <remarks>thinking は本文に混ぜない。keep_alive はロード操作と同じ -1 を指定する。</remarks>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="messages">確定済みの会話履歴と、最後の要素に置く今回の利用者入力。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>受信順に列挙する回答本文の断片。</returns>
    private async IAsyncEnumerable<string> ReadReplyAsync(string model, IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(new OllamaChatRequest(model,
                messages.Select(message => new OllamaRequestMessage(
                    message.Role switch
                    {
                        ChatRole.User => "user",
                        ChatRole.Assistant => "assistant",
                        _ => throw new ArgumentOutOfRangeException(nameof(messages))
                    }, message.Content, message.Image is null ? null : new[] { message.Image.Base64Data })).ToArray(), true), OllamaJsonContext.Default.OllamaChatRequest)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var hasContent = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var chunk = JsonSerializer.Deserialize(line, OllamaJsonContext.Default.OllamaChatChunk);
            if (!string.IsNullOrWhiteSpace(chunk?.Error))
                throw new ChatServiceException($"Ollama エラー: {chunk.Error}");
            if (chunk?.Done is null)
                throw new ChatServiceException("Ollama の応答形式が正しくありません。");

            // thinking フィールドは回答本文として表示・履歴送信しない。
            if (!string.IsNullOrEmpty(chunk.Message?.Content))
            {
                hasContent |= !string.IsNullOrWhiteSpace(chunk.Message.Content);
                yield return chunk.Message.Content;
            }
            if (chunk.Done.Value)
            {
                if (!hasContent) throw new ChatServiceException("Ollama から回答本文を取得できませんでした。");
                yield break;
            }
        }
        throw new ChatServiceException("応答が完了する前に接続が終了しました。もう一度送信してください。");
    }

    /// <summary>HTTP エラーの本文を制限して読み、利用者向けの例外へ変換する。</summary>
    /// <param name="response">HTTP ステータスと必要に応じてエラー本文を確認する応答。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>操作の完了を表すタスク。</returns>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var hint = response.StatusCode == System.Net.HttpStatusCode.NotFound
            ? "モデルが利用可能か、モデル一覧を更新して確認してください。"
            : "Ollama の状態を確認して、もう一度お試しください。";
        // エラー本文は制限して読み、JSON の error をプレーンテキストとして表示する。
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[4096];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        string? detail = null;
        try
        {
            detail = JsonSerializer.Deserialize(new string(buffer, 0, count), OllamaJsonContext.Default.OllamaChatChunk)?.Error;
        }
        catch (JsonException) { }
        if (detail?.Length > 1000) detail = detail[..1000];
        throw new ChatServiceException($"Ollama API エラー ({(int)response.StatusCode})。{(string.IsNullOrWhiteSpace(detail) ? hint : detail)}");
    }

    /// <summary>呼び出し元の明示中止と、タイムアウト・通信・JSON の失敗を区別する。</summary>
    /// <param name="exception">記録または利用者向け表示へ変換する例外。</param>
    /// <param name="callerToken">タイムアウトと利用者の明示中止を区別する呼び出し元トークン。</param>
    /// <returns>利用者の明示中止ではなく、通信失敗として変換すべき場合は true。</returns>
    private static bool IsCommunicationFailure(Exception exception, CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested && exception is
            HttpRequestException or IOException or JsonException or OperationCanceledException;

    /// <summary>通信例外を、利用者に提示できる日本語のチャット例外へ変換する。</summary>
    /// <param name="exception">記録または利用者向け表示へ変換する例外。</param>
    /// <returns>元の例外を原因として保持する利用者向け例外。</returns>
    private static ChatServiceException Translate(Exception exception) => new(exception switch
    {
        OperationCanceledException => "Ollama の応答がタイムアウトしました。もう一度お試しください。",
        JsonException => "Ollama の応答を読み取れませんでした。応答形式を確認してください。",
        _ => "Ollama に接続できないか、通信が切断されました。localhost:11434 で起動しているか確認してください。"
    }, exception);
}
