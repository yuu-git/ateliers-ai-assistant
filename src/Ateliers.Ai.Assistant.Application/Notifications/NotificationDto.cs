namespace Ateliers.Ai.Assistant.Application.Notifications;

/// <summary>将来の通知フィードで表示する種別。チャットの Role とは共有しない。</summary>
public enum NotificationKind
{
    /// <summary>通常のお知らせ。</summary>
    Information,
    /// <summary>利用者の注意を要する事象。</summary>
    Warning,
    /// <summary>失敗の通知。</summary>
    Error
}

/// <summary>将来の通知表示に受け渡す最小 DTO。配信処理や永続化機能は含まない。</summary>
/// <param name="Id">通知の識別子。</param>
/// <param name="OccurredAt">事象の発生日時。</param>
/// <param name="Kind">通知種別。</param>
/// <param name="Title">通知見出し。</param>
/// <param name="MarkdownBody">HTML へ変換する前の表示用 Markdown 原文。</param>
public sealed record NotificationDto(Guid Id, DateTimeOffset OccurredAt, NotificationKind Kind, string Title, string MarkdownBody);
