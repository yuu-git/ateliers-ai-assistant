using System.Runtime.CompilerServices;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Infrastructure.Chat;

/// <summary>UI・テストの検証用。通常の Desktop 起動では利用しない。</summary>
public sealed class DummyChatService : IChatService
{
    /// <summary>Ollama に接続せず、検証用モデル dummy の一覧を返す。</summary>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>利用可能なモデル名の一覧。</returns>
    public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(new[] { "dummy" });
    }

    /// <summary>最後の入力に対する固定の応答を、待機を挟んだ断片として返す。</summary>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="messages">確定済みの会話履歴と、最後の要素に置く今回の利用者入力。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>受信順に列挙する回答本文の断片。</returns>
    public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0 || messages[^1].Role != ChatRole.User)
            throw new ArgumentException("最後にユーザーメッセージが必要です。", nameof(messages));

        await Task.Delay(150, cancellationToken);
        yield return $"「{messages[^1].Content}」を受け取りました。\n";
        await Task.Delay(150, cancellationToken);
        yield return "これは検証用のダミー応答です。";
    }
}
