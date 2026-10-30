using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Application.ScreenAnalysis;

/// <summary>ユーザーセッションの対象選択・単発キャプチャ。画像を永続化しない。</summary>
public interface IScreenCaptureService : IDisposable
{
    /// <summary>OS から取得した対象名。未選択なら null。</summary>
    string? TargetName { get; }
    /// <summary>選択対象が閉じた時の通知。通知スレッドは保証しない。</summary>
    event EventHandler? TargetClosed;
    /// <summary>標準選択 UI を開く。キャンセルなら false で現在対象を維持する。</summary>
    Task<bool> PickAsync(CancellationToken token);
    /// <summary>選択対象から一枚だけ画像を取得し、送信用 PNG へ正規化する。</summary>
    Task<ChatImage> CaptureAsync(CancellationToken token);
}

/// <summary>画像一枚の解析。既存の排他・画像送信を使い、会話や DB へ書き込まない。</summary>
public sealed class ScreenAnalysisService(IChatService chat, IModelWorkCoordinator coordinator,
    Func<CancellationToken, Task> ensureAllowed)
{
    /// <summary>API 側で画像非対応を判定するため、モデル名から能力を推測せず一覧を返す。</summary>
    public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken token) => chat.GetModelsAsync(token);

    /// <summary>排他権の取得後にも実行許可を確認し、今回の画像・入力だけを送信する。</summary>
    public Task AnalyzeAsync(string model, string prompt, ChatImage image, Action<string> append, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(image);
        return coordinator.RunExclusiveAsync(async (scoped, cancellation) =>
        {
            await ensureAllowed(cancellation).ConfigureAwait(false);
            await foreach (var chunk in scoped.StreamReplyAsync(model, [new(ChatRole.User, prompt, image)], cancellation).ConfigureAwait(false))
                append(chunk);
        }, token);
    }
}
