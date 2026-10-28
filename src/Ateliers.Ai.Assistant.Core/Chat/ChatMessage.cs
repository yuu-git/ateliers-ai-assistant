namespace Ateliers.Ai.Assistant.Core.Chat;

/// <summary>会話でのメッセージ送信者の役割。</summary>
public enum ChatRole
{
    /// <summary>利用者からの入力。</summary>
    User,
    /// <summary>モデルが生成した回答。</summary>
    Assistant
}

/// <summary>Markdown 原文と任意の画像を保持する、UI 非依存の会話メッセージ。</summary>
/// <param name="Role">メッセージ送信者の役割。</param>
/// <param name="Content">表示用 HTML ではない Markdown 原文。</param>
/// <param name="Image">任意のメモリ内 PNG 添付。添付がない場合は null。</param>
public sealed record ChatMessage(ChatRole Role, string Content, ChatImage? Image = null);
