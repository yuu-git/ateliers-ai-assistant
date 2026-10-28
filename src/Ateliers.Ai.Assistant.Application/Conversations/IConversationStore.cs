using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Application.Conversations;

/// <summary>表示状態と送信履歴への採否を保持する保存用メッセージ。HTML は含めない。</summary>
/// <param name="Id">メッセージの識別子。</param>
/// <param name="Message">役割・Markdown 原文・任意の画像。</param>
/// <param name="Model">送信時のモデル名。</param>
/// <param name="StatusText">完了・中止・失敗等の表示状態。</param>
/// <param name="CreatedAt">メッセージ作成日時。</param>
/// <param name="IncludedInHistory">完了した送受信として次回の要求へ含める場合は true。</param>
public sealed record ConversationMessage(Guid Id, ChatMessage Message, string Model, string StatusText,
    DateTimeOffset CreatedAt, bool IncludedInHistory);

/// <summary>保存・復元する会話全体。未送信の入力と添付も含む。</summary>
/// <param name="Id">会話の識別子。</param>
/// <param name="Title">利用者が指定したタイトル。</param>
/// <param name="CreatedAt">新規会話の作成日時。</param>
/// <param name="UpdatedAt">最終変更日時。</param>
/// <param name="SelectedModel">最後に選択していたモデル。</param>
/// <param name="Messages">表示順のメッセージ。</param>
/// <param name="DraftText">未送信の入力文。</param>
/// <param name="DraftImage">未送信の画像添付。</param>
/// <param name="Version">上書き競合を検出する保存版番号。新規保存は0。</param>
public sealed record ConversationSnapshot(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? SelectedModel, IReadOnlyList<ConversationMessage> Messages, string DraftText, ChatImage? DraftImage, long Version = 0);

/// <summary>会話一覧に必要な情報。本文・画像を読み込まずに取得する。</summary>
/// <param name="Id">会話識別子。</param>
/// <param name="Title">会話タイトル。</param>
/// <param name="UpdatedAt">最終保存日時。</param>
/// <param name="Model">最後の選択モデル。</param>
public sealed record ConversationSummary(Guid Id, string Title, DateTimeOffset UpdatedAt, string? Model);

/// <summary>明示保存済みの会話を永続化する境界。UI・SQLite・EF Core に依存しない。</summary>
public interface IConversationStore
{
    /// <summary>保存済み会話を更新日時の降順で取得する。</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default);
    /// <summary>指定会話の本文・画像・下書きを復元する。存在しなければ null。</summary>
    Task<ConversationSnapshot?> LoadAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>一つのトランザクションで全体を保存し、確定した版番号を返す。空会話は保存しない。</summary>
    Task<ConversationSnapshot> SaveAsync(ConversationSnapshot conversation, CancellationToken cancellationToken = default);
    /// <summary>会話と関連する全メッセージ・画像を削除する。</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
