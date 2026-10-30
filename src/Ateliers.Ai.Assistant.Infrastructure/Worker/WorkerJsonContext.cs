using System.Text.Json.Serialization;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.ScreenAnalysis;

namespace Ateliers.Ai.Assistant.Infrastructure.Worker;

/// <summary>Worker API と小容量設定の共通 JSON メタデータ。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkerConfiguration))]
[JsonSerializable(typeof(WorkerStatus))]
[JsonSerializable(typeof(SourceState))]
[JsonSerializable(typeof(FetchRequest))]
[JsonSerializable(typeof(FetchAccepted))]
[JsonSerializable(typeof(DeleteArticlesRequest))]
[JsonSerializable(typeof(ArticleDeletionResult))]
[JsonSerializable(typeof(IReadOnlyList<CollectionRun>))]
[JsonSerializable(typeof(IReadOnlyList<ArticleNotification>))]
[JsonSerializable(typeof(WorkerEndpoint))]
[JsonSerializable(typeof(NotificationQuery))]
[JsonSerializable(typeof(NotificationPage))]
[JsonSerializable(typeof(SetArticleReadRequest))]
[JsonSerializable(typeof(SetArticleBookmarkRequest))]
[JsonSerializable(typeof(AddArticleTagRequest))]
[JsonSerializable(typeof(RemoveArticleTagRequest))]
[JsonSerializable(typeof(ArticleMetadata))]
[JsonSerializable(typeof(AiPauseRequest))]
[JsonSerializable(typeof(ScreenMonitorSettings))]

[JsonSerializable(typeof(ScreenMonitorList))]
[JsonSerializable(typeof(ScreenMonitorOverview))]
[JsonSerializable(typeof(ScreenMonitorDetail))]
[JsonSerializable(typeof(CreateScreenMonitorRequest))]
[JsonSerializable(typeof(UpdateScreenMonitorRequest))]
public partial class WorkerJsonContext : JsonSerializerContext { }
/// <summary>同じユーザーだけが読める loopback 接続情報。ログや API 応答へは出さない。</summary>
public sealed record WorkerEndpoint(int Protocol, string Version, string Address, string Token, Guid InstanceId);
