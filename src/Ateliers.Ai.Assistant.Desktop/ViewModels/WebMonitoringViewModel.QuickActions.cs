using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Logging;
using Ateliers.Ai.Assistant.Application.WebMonitoring;

namespace Ateliers.Ai.Assistant.Desktop.ViewModels;

/// <summary>単体削除の確認結果。確認省略の保存は削除成功後にだけ行う。</summary>
public sealed record ArticleDeletionDecision(bool Confirmed, bool DontAskAgain = false);

/// <summary>一覧からの単体操作を既存の記事削除サービスへ接続する。</summary>
public sealed partial class WebMonitoringViewModel
{
    /// <summary>Desktop が記事タイトル・説明・確認省略チェックを表示する。</summary>
    public Func<WebNotificationViewModel, Task<ArticleDeletionDecision>>? ConfirmSingleArticleDeletionAsync { get; set; }
    /// <summary>削除確認・削除・メタデータ更新が重ならない場合だけ操作を許可する。</summary>
    public bool CanDeleteArticle => CanEdit && !_metadataBusy;
    /// <summary>一覧の独立ボタンから削除する。選択や既読状態を変更しない。</summary>
    public Task DeleteArticleAsync(WebNotificationViewModel row)
    {
        if (!CanDeleteArticle) return Task.CompletedTask;
        return _running = DeleteSingleArticleAsync(row);
    }
    /// <summary>確認、削除結果の反映、確認省略の保存を順番に行う。</summary>
    private async Task DeleteSingleArticleAsync(WebNotificationViewModel row)
    {
        IsRunning = true; ErrorMessage = ""; _queryRevision++;
        using var cancellation = new CancellationTokenSource(); _runCancellation = cancellation;
        try
        {
            var decision = new ArticleDeletionDecision(true);
            if (_settings.ConfirmSingleArticleDeletion)
            {
                if (ConfirmSingleArticleDeletionAsync is null) return;
                decision = await ConfirmSingleArticleDeletionAsync(row);
                if (!decision.Confirmed || _disposed || cancellation.IsCancellationRequested) return;
            }
            var result = await DeleteConfirmedArticlesAsync([row.Item.Notification.Id], cancellation.Token);
            if (decision.DontAskAgain && result.DeletedIds.Contains(row.Item.Notification.Id) && result.FailedIds.Count == 0 && !result.Canceled)
            {
                if (!await _settings.SetDeletionConfirmationAsync(false))
                    ErrorMessage = "記事は削除しましたが、確認設定を保存できませんでした。次回も確認します。設定画面のエラーを確認してください。";
            }
        }
        catch (Exception exception) { Failure(exception); }
        finally { _runCancellation = null; IsRunning = false; ResumePendingRead(); }
    }
    /// <summary>単体と一括で同じサービス・結果反映を使い、削除結果に応じて詳細と集計を更新する。</summary>
    private async Task<ArticleDeletionResult> DeleteConfirmedArticlesAsync(IReadOnlyList<Guid> ids, CancellationToken token)
    {
        StateText = $"{ids.Distinct().Count()}件の記事を削除しています…";
        var result = await _service.DeleteAsync(ids, token);
        ForgetReading(result.DeletedIds);
        if (SelectedNotification is { } selected && result.DeletedIds.Contains(selected.Item.Notification.Id)) { _openedArticleId = null; SelectedNotification = null; }
        (_diagnostics as IAppLog)?.Write(AppLogCategory.Application, result.FailedIds.Count == 0 && !result.Canceled ? AppLogLevel.Information : AppLogLevel.Warning,
            "web.article-deletion", $"記事削除: 成功 {result.DeletedIds.Count}件・失敗 {result.FailedIds.Count}件・中止 {result.Canceled}。");
        // 更新失敗と削除失敗を分ける。削除済み ID を再表示・再確認しない。
        foreach (var removed in Notifications.Where(item => result.DeletedIds.Contains(item.Item.Notification.Id)).ToArray()) Notifications.Remove(removed);
        ApplyFilter();
        try { await RefreshResultsAsync(); }
        catch (Exception exception) { Failure(exception); }
        if (!_disposed)
        {
            StateText = $"削除 {result.DeletedIds.Count}件・失敗 {result.FailedIds.Count}件" + (result.Canceled ? "（中止）" : "");
            if (!string.IsNullOrEmpty(result.Error)) ErrorMessage = result.Error;
        }
        return result;
    }
}
