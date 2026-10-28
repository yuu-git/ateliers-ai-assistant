using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Application.Chat;

/// <summary>
/// チャット応答の境界。Desktop は Ollama 等の具体的な通信方法に依存しない。
/// モデル名・Markdown 原文・任意の画像添付を扱い、HTTP・Ollama の DTO は公開しない。
/// </summary>
public interface IChatService
{
    /// <summary>利用可能なモデル名の一覧を取得する。</summary>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>利用可能なモデル名の一覧。</returns>
    /// <exception cref="ChatServiceException">通信またはモデル一覧の取得に失敗した場合。</exception>
    /// <exception cref="OperationCanceledException">呼び出し元が処理を中止した場合。</exception>
    Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>会話履歴と今回の入力から、回答本文の断片を受信順に返す。</summary>
    /// <remarks>履歴の選別は呼び出し元が行う。画像は UI 非依存の添付データとして受け取り、thinking や表示用 HTML は本文に混ぜない。</remarks>
    /// <param name="model">操作または送信に使用するモデル名。</param>
    /// <param name="messages">確定済みの会話履歴と、最後の要素に置く今回の利用者入力。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>受信順に列挙する回答本文の断片。</returns>
    /// <exception cref="ArgumentException">履歴が空、または最後のメッセージが利用者入力でない場合。</exception>
    /// <exception cref="ChatServiceException">通信失敗、異常な応答、空の回答または未完了の切断を検出した場合。</exception>
    /// <exception cref="OperationCanceledException">呼び出し元が処理を中止した場合。</exception>
    IAsyncEnumerable<string> StreamReplyAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);
}
