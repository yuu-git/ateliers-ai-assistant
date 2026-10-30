using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Logging;
using Ateliers.Ai.Assistant.Application.Models;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Infrastructure.Chat;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;
using Ateliers.Ai.Assistant.Infrastructure.Worker;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Ateliers.Ai.Assistant.Worker;

/// <summary>loopback API と2つの独立処理ループをホストするユーザーセッション常駐プロセス。</summary>
public static class WorkerHost
{
    /// <summary>単一インスタンスを取得し、API が準備できた後だけ接続情報を公開する。</summary>
    public static async Task<int> RunAsync(string[] arguments)
    {
        var paths = new AppDataPaths();
        IFeedSource? verificationFeed = null;
        IChatService? verificationChat = null;
        IModelControlService? verificationModels = null;
        TimeProvider? clock = null;
#if DEBUG
        if (arguments.Length == 2 && arguments[0] == "--verification-root")
        {
            paths = new AppDataPaths(Path.Combine(arguments[1], "r"), Path.Combine(arguments[1], "l"));
            var fake = new VerificationAdapters(); verificationFeed = fake; verificationChat = fake; verificationModels = fake;
            // 隔離ルートの作成時刻を共通基点とし、再起動でも加速時間を巻き戻さない。
            clock = new VerificationClock(new DateTimeOffset(Directory.GetCreationTimeUtc(arguments[1])));
        }
        else
#endif
        if (arguments.Length != 0) return 2;
        var diagnostics = new FileDiagnosticLog(paths, "Worker");
        var connection = new WorkerConnectionFiles(paths);
        IDisposable? instance = null;
        Ateliers.Ai.Assistant.Infrastructure.Storage.Temp.TempFileStore? temporaryFiles = null;
        try
        {
            instance = connection.TryEnterInstance();
            if (instance is null) return 0;
            temporaryFiles = new(paths, diagnostics);
            try { await temporaryFiles.CleanupAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception exception) { diagnostics.Error("temp.startup", exception); }
            using var chatClient = new HttpClient { BaseAddress = new Uri("http://localhost:11434/"), Timeout = Timeout.InfiniteTimeSpan };
            using var feedClient = new HttpClient(RssFeedSource.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            var ollama = new OllamaChatService(chatClient, keepAliveSeconds: 300);
            var store = new SqliteWebMonitoringStore(paths, diagnostics);
            var coordinator = new WorkerCoordinator(new WorkerStateStore(paths, store, new JsonUserSettingsStore(paths, diagnostics)), store,
                verificationFeed is not null ? verificationFeed : new ContentSourceRouter(new RssFeedSource(feedClient), new GitHubReleasesSource(feedClient)), new ArticleMarkdownWriter(paths), verificationChat ?? ollama, verificationModels ?? ollama,
                FileOperationGate.ForModels(paths), diagnostics, diagnostics, clock);
            await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(WorkerHost).Assembly.GetName().Name });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback, 0); options.Limits.MaxRequestBodySize = 256 * 1024; });
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, WorkerJsonContext.Default));
            await using var app = builder.Build();
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string expectedHost = "";
            app.Use(async (context, next) =>
            {
                if (context.Request.Host.Value != expectedHost || context.Request.Headers.ContainsKey("Origin") ||
                    context.Request.Headers.Keys.Any(name => name.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase)))
                { context.Response.StatusCode = 403; return; }
                var authorization = context.Request.Headers.Authorization;
                var expected = Encoding.ASCII.GetBytes("Bearer " + secret);
                if (authorization.Count != 1 || authorization[0]?.Length != expected.Length ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(authorization[0]!), expected))
                {
                    context.Response.StatusCode = 401;
                    diagnostics.Write(AppLogCategory.System, AppLogLevel.Warning, "api.authentication", "認証のない要求を拒否しました。"); return;
                }
                if (context.Request.Method is not ("GET" or "HEAD") && !context.Request.HasJsonContentType())
                { context.Response.StatusCode = 415; return; }
                try { await next(context); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Response.StatusCode = 499; }
                catch (Exception exception)
                {
                    diagnostics.Error("api.request", exception);
                    context.Response.StatusCode = exception is WorkerException or WebMonitoringException ? 400 : 500;
                    if (exception is WorkerException && exception.Message.Contains("別の画面", StringComparison.Ordinal)) context.Response.StatusCode = 409;
                    await context.Response.WriteAsync(exception is WorkerException or WebMonitoringException ? exception.Message : "操作を完了できません。状態と入力値・ログを確認してください。");
                }
            });
            app.MapGet("/api/v1/status", (CancellationToken token) => coordinator.StatusAsync(token));
            app.MapPut("/api/v1/configuration", (WorkerConfiguration configuration, CancellationToken token) => coordinator.ConfigureAsync(configuration, token));
            app.MapPost("/api/v1/fetch", (FetchRequest request, CancellationToken token) => coordinator.RequestFetchAsync(request, token));
            app.MapPost("/api/v1/ai/pause", (AiPauseRequest request, CancellationToken token) => coordinator.SetAiPausedAsync(request.Paused, token));
            app.MapGet("/api/v1/runs", (CancellationToken token) => store.RunsAsync(token));
            app.MapGet("/api/v1/notifications", (CancellationToken token) => store.NotificationsAsync(token));
            app.MapPost("/api/v1/notifications/query", (NotificationQuery query, CancellationToken token) => store.QueryAsync(query, token));
            app.MapPost("/api/v1/notifications/read", (SetArticleReadRequest request, CancellationToken token) => store.SetReadAsync(request, token));
            app.MapPost("/api/v1/notifications/bookmark", (SetArticleBookmarkRequest request, CancellationToken token) => store.SetBookmarkAsync(request, token));
            app.MapPost("/api/v1/notifications/tags/add", (AddArticleTagRequest request, CancellationToken token) => store.AddTagAsync(request, token));
            app.MapPost("/api/v1/notifications/tags/remove", (RemoveArticleTagRequest request, CancellationToken token) => store.RemoveTagAsync(request, token));
            app.MapPost("/api/v1/articles/delete", (DeleteArticlesRequest request, CancellationToken token) => coordinator.DeleteAsync(request.Ids, token));
            app.MapPost("/api/v1/shutdown", () =>
            {
                _ = Task.Run(async () => { await Task.Delay(200); app.Lifetime.StopApplication(); });
                return new FetchAccepted(coordinator.InstanceId, false);
            });
            await app.StartAsync().ConfigureAwait(false);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var uri = new Uri(address); expectedHost = uri.Authority;
            await connection.PublishAsync(new(WorkerCoordinator.Protocol, WorkerCoordinator.ImplementationVersion, address.TrimEnd('/') + "/", secret, coordinator.InstanceId), CancellationToken.None).ConfigureAwait(false);
            diagnostics.Write(AppLogCategory.System, AppLogLevel.Information, "api.started", "認証付き loopback API の準備が完了しました。");
            var stopping = app.Lifetime.ApplicationStopping;
            var fetchLoop = LoopAsync(coordinator.TickFetchAsync, diagnostics, stopping);
            var aiLoop = LoopAsync(coordinator.TickAiAsync, diagnostics, stopping);
            await app.WaitForShutdownAsync().ConfigureAwait(false);
            coordinator.CancelAi();
            try { await Task.WhenAll(fetchLoop, aiLoop).WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false); }
            catch (Exception exception) { diagnostics.Error("worker.stop", exception); }
            await connection.RemoveAsync(coordinator.InstanceId).ConfigureAwait(false);
            diagnostics.Write(AppLogCategory.System, AppLogLevel.Information, "worker.stopped", "Worker を終了しました。");
            return 0;
        }
        catch (Exception exception) { diagnostics.Error("worker.start", exception); return 1; }
        finally
        {
            if (temporaryFiles is not null)
            {
                try { await temporaryFiles.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception exception) { diagnostics.Error("temp.shutdown", exception); }
            }
            instance?.Dispose();
        }
    }
    /// <summary>失敗時もホストを停止させず、短時間の無限再試行を避ける。</summary>
    private static async Task LoopAsync(Func<CancellationToken, Task> tick, FileDiagnosticLog log, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await tick(token).ConfigureAwait(false); await Task.Delay(1000, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                log.Error("worker.loop", exception);
                try { await Task.Delay(5000, token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
