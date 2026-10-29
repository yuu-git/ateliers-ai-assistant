using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Storage;

namespace Ateliers.Ai.Assistant.Application.WebMonitoring;

/// <summary>手動収集を取得・重複判定・要約・成果物・通知の順に進める。UI と HTTP の詳細は持たない。</summary>
public sealed class WebCollectionService(IFeedSource source, IWebMonitoringStore store,
    IArticleMarkdownWriter writer, IModelWorkCoordinator models, IAppDiagnostics diagnostics) : IWebCollectionOperations
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>1フィードを処理する。記事単位の失敗は記録して続行し、次回に未完了段階から再開する。</summary>
    public async Task CollectAsync(string url, string? model, IProgress<string>? progress, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) throw new WebMonitoringException("収集は既に実行中です。");
        var run = new CollectionRun(Guid.NewGuid(), DateTimeOffset.UtcNow, Source: url, Model: model);
        try
        {
            await store.SaveRunAsync(run, token).ConfigureAwait(false);
            await models.RunExclusiveAsync(async (chat, operationToken) =>
            {
                if (string.IsNullOrWhiteSpace(model)) throw new WebMonitoringException("要約モデルを選択してください。");
                if (!(await chat.GetModelsAsync(operationToken).ConfigureAwait(false)).Contains(model, StringComparer.Ordinal))
                    throw new WebMonitoringException("要約モデルが見つかりません。モデル一覧を更新してください。");
                progress?.Report("フィードを取得しています…");
                var articles = FeedSelection.Latest(await source.FetchAsync(url, operationToken).ConfigureAwait(false));
                var added = await store.RegisterAsync(articles, DateTimeOffset.UtcNow, operationToken).ConfigureAwait(false);
                run = run with { Fetched = articles.Count, New = added, Duplicates = articles.Count - added };
                await store.SaveRunAsync(run, operationToken).ConfigureAwait(false);
                var pending = await store.PendingAsync(FeedIdentity.Source(url), operationToken).ConfigureAwait(false);
                var keys = articles.Select(article => article.Key).ToHashSet(StringComparer.Ordinal);
                pending = pending.Where(item => keys.Contains(item.Article.Key))
                    .OrderByDescending(item => item.Article.PublishedAt ?? DateTimeOffset.MinValue).ToArray();
                var position = 0;
                foreach (var item in pending)
                {
                    operationToken.ThrowIfCancellationRequested();
                    progress?.Report($"記事を処理しています（{++position}/{pending.Count}）: {item.Article.Title}");
                    var current = item;
                    try
                    {
                        if (current.Summary is null)
                        {
                            var text = await ArticleSummary.GenerateAsync(chat, model, current.Article, operationToken).ConfigureAwait(false);
                            current = current with { Summary = text, Model = model, Error = null };
                            await store.SaveArticleAsync(current, operationToken).ConfigureAwait(false);
                            run = run with { Summarized = run.Summarized + 1 };
                        }
                        var path = await writer.WriteAsync(current, operationToken).ConfigureAwait(false);
                        await store.CompleteAsync(current with { MarkdownPath = path, Notified = true, Error = null }, operationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        diagnostics.Error("web.article", exception);
                        var error = Message(exception);
                        await store.SaveArticleAsync(current with { Error = error }, operationToken).ConfigureAwait(false);
                        run = run with { Failed = run.Failed + 1, Error = error };
                    }
                    await store.SaveRunAsync(run, operationToken).ConfigureAwait(false);
                }
            }, token).ConfigureAwait(false);
            run = run with { Status = run.Failed == 0 ? "成功" : "一部失敗" };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { run = run with { Status = "中止", Error = "利用者が中止しました。未完了の記事は次回再試行します。" }; }
        catch (Exception exception)
        {
            diagnostics.Error("web.collect", exception);
            run = run with { Status = "失敗", Error = Message(exception) };
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await store.SaveRunAsync(run with { FinishedAt = DateTimeOffset.UtcNow }, cleanup.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
    }
    /// <summary>収集と同じ排他権を使い、選択した記事だけを削除する。各記事の失敗で他の対象を巻き戻さない。</summary>
    public async Task<ArticleDeletionResult> DeleteAsync(IReadOnlyList<Guid> ids, CancellationToken token)
    {
        if (ids.Count > 100 || ids.Any(id => id == Guid.Empty)) throw new WebMonitoringException("削除対象は一覧の100件以内で指定してください。");
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) throw new WebMonitoringException("収集または削除の完了を待ってください。");
        var deleted = new List<Guid>(); var failed = new List<Guid>(); var error = ""; var canceled = false;
        try
        {
            foreach (var id in ids.Distinct())
            {
                try { token.ThrowIfCancellationRequested(); await store.DeleteArticleAsync(id, token).ConfigureAwait(false); deleted.Add(id); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled = true; break; }
                catch (Exception exception)
                {
                    diagnostics.Error("web.delete", exception); failed.Add(id);
                    error = exception is StorageException ? exception.Message : "記事を削除できませんでした。保存先を確認して再試行してください。";
                }
            }
            return new ArticleDeletionResult(deleted, failed, canceled, error);
        }
        finally { _gate.Release(); }
    }
    /// <summary>外部本文や接続詳細を含まない、段階に応じた失敗表示を返す。</summary>
    private static string Message(Exception exception) => exception switch
    {
        WebMonitoringException or StorageException => exception.Message,
        OperationCanceledException => "通信がタイムアウトしました。次回再試行できます。",
        ChatServiceException => "Ollama の要約に失敗しました。モデル・接続状態を確認してください。",
        _ => "情報収集に失敗しました。接続状態・保存先を確認してください。"
    };
}
