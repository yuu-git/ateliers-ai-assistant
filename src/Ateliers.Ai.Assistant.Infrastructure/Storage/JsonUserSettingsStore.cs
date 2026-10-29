using System.Text.Json;
using System.Text.Json.Serialization;
using Ateliers.Ai.Assistant.Application.Settings;
using Ateliers.Ai.Assistant.Application.Storage;

namespace Ateliers.Ai.Assistant.Infrastructure.Storage;

/// <summary>Roaming の小容量設定を排他的・原子的に読み書きする。読込だけではファイルを作らない。</summary>
public sealed class JsonUserSettingsStore(IApplicationPaths paths, IAppDiagnostics diagnostics) : IUserSettingsStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <inheritdoc/>
    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var stream = File.OpenRead(paths.SettingsFile);
            if (stream.Length > 64 * 1024) throw new JsonException("Settings exceed the permitted size.");
            var settings = await JsonSerializer.DeserializeAsync(stream, SettingsJsonContext.Default.UserSettings, cancellationToken).ConfigureAwait(false);
            if (settings is null || settings.LastSelectedModel?.Length > 512 || settings.WebSummaryModel?.Length > 512 || settings.WebFeedUrl?.Length > 4096) throw new JsonException("Invalid settings.");
            return settings;
        }
        catch (FileNotFoundException) { return new UserSettings(); }
        catch (DirectoryNotFoundException) { return new UserSettings(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics.Error("settings.load", exception);
            throw new StorageException("設定を読み込めませんでした。既定値で継続します。設定ファイルを確認してください。", exception);
        }
        finally { _gate.Release(); }
    }
    /// <inheritdoc/>
    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = paths.SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(paths.SettingsDirectory);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.UserSettings), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, paths.SettingsFile, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Error("settings.save", exception);
            throw new StorageException("設定を保存できませんでした。アクセス権と空き容量を確認してください。", exception);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { diagnostics.Error("settings.temporary.cleanup", exception); }
            _gate.Release();
        }
    }
}

/// <summary>設定 JSON のメタデータを生成する。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UserSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext { }
