using System.Text.Json;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.Settings;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Application.Worker;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;

namespace Ateliers.Ai.Assistant.Infrastructure.Worker;

/// <summary>設定は Roaming の専用 JSON、予定は既存 SQLite。UI の設定ファイルを更新しない。</summary>
public sealed class WorkerStateStore(IApplicationPaths paths, SqliteWebMonitoringStore database, IUserSettingsStore legacy) : IWorkerStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string FilePath => Path.Combine(paths.SettingsDirectory, "worker-settings.json");
    /// <inheritdoc/>
    public async Task<WorkerConfiguration> LoadConfigurationAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (File.Exists(FilePath)) return await ReadAsync(token).ConfigureAwait(false);
            var previous = await legacy.LoadAsync(token).ConfigureAwait(false);
            var sources = new List<SourceConfiguration>();
            if (!string.IsNullOrWhiteSpace(previous.WebFeedUrl))
                sources.Add(new(Guid.NewGuid(), "Phase 3 の情報源", FeedIdentity.Source(previous.WebFeedUrl), 60, false));
            var initial = WorkerConfiguration.Default with { SummaryModel = previous.WebSummaryModel, Sources = sources };
            await WriteAsync(initial, token).ConfigureAwait(false);
            return initial;
        }
        finally { _gate.Release(); }
    }
    /// <inheritdoc/>
    public async Task<WorkerConfiguration> SaveConfigurationAsync(WorkerConfiguration configuration, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(token).ConfigureAwait(false);
            if (configuration.Version != current.Version) throw new WorkerException("別の画面で設定が変更されました。再読み込みしてから保存してください。");
            var next = configuration with { Version = checked(current.Version + 1) };
            await WriteAsync(next, token).ConfigureAwait(false); return next;
        }
        finally { _gate.Release(); }
    }
    /// <summary>不正な設定を初期化せずエラーにする。</summary>
    private async Task<WorkerConfiguration> ReadAsync(CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(FilePath);
            if (stream.Length > 256 * 1024) throw new JsonException();
            return await JsonSerializer.DeserializeAsync(stream, WorkerJsonContext.Default.WorkerConfiguration, token).ConfigureAwait(false) ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { throw new StorageException("Worker 設定を読み込めません。ファイルを確認して Worker を再起動してください。既存ファイルは上書きしていません。", exception); }
    }
    /// <summary>同一フォルダーの一時ファイルから原子的に置換する。</summary>
    private async Task WriteAsync(WorkerConfiguration configuration, CancellationToken token)
    {
        Directory.CreateDirectory(paths.SettingsDirectory);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(configuration, WorkerJsonContext.Default.WorkerConfiguration), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Move(temporary, FilePath, true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }
    /// <inheritdoc/>
    public Task<IReadOnlyList<SourceState>> LoadStatesAsync(CancellationToken token) => database.LoadSourceStatesAsync(token);
    /// <inheritdoc/>
    public Task SaveStateAsync(SourceState state, CancellationToken token) => database.SaveSourceStateAsync(state, token);
}
