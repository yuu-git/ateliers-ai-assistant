namespace Ateliers.Ai.Assistant.Application.Chat;

/// <summary>ユーザーへ表示できるチャットサービスの失敗。通信方式には依存しない。</summary>
public sealed class ChatServiceException : Exception
{
    /// <summary>利用者に提示する通信・チャット処理のエラーを生成する。</summary>
    /// <param name="message">利用者へ表示可能なエラー説明。</param>
    /// <param name="innerException">原因となった例外。原因を保持しない場合は null。</param>
    public ChatServiceException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
