using System;
using System.IO;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Storage;
using Windows.System;

namespace Ateliers.Ai.Assistant.Desktop.Storage;

/// <summary>任意の文字列を実行せず、内部解決済みの4種類の保存先だけを Explorer で開く。</summary>
public sealed class FixedDataFolderOpener(IApplicationPaths paths)
{
    /// <summary>指定の固定フォルダーだけを必要時に作成し、Explorer 起動の結果を確認する。</summary>
    public async Task OpenAsync(DataFolderKind kind)
    {
        var path = kind switch
        {
            DataFolderKind.Settings => paths.SettingsDirectory,
            DataFolderKind.Local => paths.LocalDirectory,
            DataFolderKind.Database => paths.DatabaseDirectory,
            DataFolderKind.Logs => paths.LogsDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Directory.CreateDirectory(path);
        if (!await Launcher.LaunchFolderPathAsync(path)) throw new StorageException("エクスプローラーで保存先を開けませんでした。");
    }
}
