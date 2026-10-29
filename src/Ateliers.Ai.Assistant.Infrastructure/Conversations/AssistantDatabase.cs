using System.Collections.Concurrent;
using Ateliers.Ai.Assistant.Application.Storage;

namespace Ateliers.Ai.Assistant.Infrastructure.Conversations;

/// <summary>同一プロセス内の会話保存と Web 収集の Migration / SQLite アクセスを直列化する。</summary>
internal static class AssistantDatabase
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>解決済み DB ファイルごとの排他権を返す。</summary>
    public static SemaphoreSlim Gate(IApplicationPaths paths) => Gates.GetOrAdd(Path.GetFullPath(paths.DatabaseFile), _ => new SemaphoreSlim(1, 1));
}
