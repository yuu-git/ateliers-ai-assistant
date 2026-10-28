using Ateliers.Ai.Assistant.Application.Storage;

namespace Ateliers.Ai.Assistant.Infrastructure.Storage;

/// <summary>Windows の既知フォルダーを基準に、すべての保存先を一か所で解決する。</summary>
public sealed class AppDataPaths : IApplicationPaths
{
    /// <summary>Infrastructure のビルド構成から決まる保存環境。通常の構成ではホストと同じ構成でビルドする。</summary>
#if DEBUG
    public const AppDataEnvironment CurrentEnvironment = AppDataEnvironment.Debug;
#else
    public const AppDataEnvironment CurrentEnvironment = AppDataEnvironment.Release;
#endif

    /// <summary>現在ユーザーの既知フォルダーを使う通常の構成。フォルダーは作らない。</summary>
    public AppDataPaths() : this(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { }

    /// <summary>ホスト・テストで既知フォルダーの基準を注入する。任意パスの利用者設定は提供しない。</summary>
    public AppDataPaths(string roamingRoot, string localRoot) : this(roamingRoot, localRoot, CurrentEnvironment) { }

    /// <summary>ホスト・テストで保存環境を明示する。環境名を任意のパス文字列としては受け取らない。</summary>
    /// <param name="roamingRoot">Roaming の基準フォルダー。</param>
    /// <param name="localRoot">Local の基準フォルダー。</param>
    /// <param name="environment">検証する固定の保存環境。</param>
    public AppDataPaths(string roamingRoot, string localRoot, AppDataEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        var applicationDirectory = environment switch
        {
            AppDataEnvironment.Debug => "Ai-Assistant.Debug",
            AppDataEnvironment.Release => "Ai-Assistant",
            _ => throw new ArgumentOutOfRangeException(nameof(environment))
        };
        DataEnvironment = environment;
        SettingsDirectory = Path.GetFullPath(Path.Combine(roamingRoot, "Ateliers-Dev", applicationDirectory));
        LocalDirectory = Path.GetFullPath(Path.Combine(localRoot, "Ateliers-Dev", applicationDirectory));
    }
    /// <inheritdoc/>
    public AppDataEnvironment DataEnvironment { get; }
    /// <inheritdoc/>
    public string SettingsDirectory { get; }
    /// <inheritdoc/>
    public string LocalDirectory { get; }
    /// <inheritdoc/>
    public string DatabaseDirectory => Path.Combine(LocalDirectory, "data");
    /// <inheritdoc/>
    public string DatabaseFile => Path.Combine(DatabaseDirectory, "assistant.db");
    /// <inheritdoc/>
    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");
    /// <inheritdoc/>
    public string LogsDirectory => Path.Combine(LocalDirectory, "logs");
    /// <inheritdoc/>
    public string CacheDirectory => Path.Combine(LocalDirectory, "cache");
    /// <inheritdoc/>
    public string TempDirectory => Path.Combine(LocalDirectory, "temp");
}
