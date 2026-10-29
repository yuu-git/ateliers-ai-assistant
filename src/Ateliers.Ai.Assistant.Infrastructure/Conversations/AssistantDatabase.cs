using System.Collections.Concurrent;
using Ateliers.Ai.Assistant.Application.Storage;

namespace Ateliers.Ai.Assistant.Infrastructure.Conversations;

/// <summary>同一プロセス内の会話保存と Web 収集の Migration / SQLite アクセスを直列化する。</summary>
internal static class AssistantDatabase
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>解決済み DB ファイルごとの排他権を返す。</summary>
    public static SemaphoreSlim Gate(IApplicationPaths paths) => Gates.GetOrAdd(Path.GetFullPath(paths.DatabaseFile), _ => new SemaphoreSlim(1, 1));
    /// <summary>Migration と SQLite 操作をプロセス間でも直列化する。未作成 DB の読込では呼び出さない。</summary>
    public static async Task<IDisposable> EnterProcessAsync(IApplicationPaths paths, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        try { return await new Worker.FileOperationGate(paths.DatabaseFile + ".lock").EnterAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        { throw new StorageException("DB を別の処理が使用しています。少し待って再試行してください。", exception); }
    }
}
