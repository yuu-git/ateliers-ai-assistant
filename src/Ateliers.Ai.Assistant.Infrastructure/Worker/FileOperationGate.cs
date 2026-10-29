using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.Worker;

namespace Ateliers.Ai.Assistant.Infrastructure.Worker;

/// <summary>終了・クラッシュで OS が解放するファイルハンドルのプロセス間排他。</summary>
public sealed class FileOperationGate(string path) : IModelUseGate
{
    /// <inheritdoc/>
    public async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }
    /// <summary>同一データ環境の UI と Worker が共有するモデル操作ゲートを返す。</summary>
    public static FileOperationGate ForModels(IApplicationPaths paths) => new(Path.Combine(Path.GetDirectoryName(paths.LocalDirectory)!, "ollama-model-use.lock"));
}
