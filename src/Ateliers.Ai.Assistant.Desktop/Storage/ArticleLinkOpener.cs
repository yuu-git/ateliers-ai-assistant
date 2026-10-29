using System;
using System.IO;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Application.WebMonitoring;
using Ateliers.Ai.Assistant.Desktop.Presentation;
using Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;
using Windows.Storage;
using Windows.System;

namespace Ateliers.Ai.Assistant.Desktop.Storage;

/// <summary>利用者の明示操作で、安全な元 URL または ID から決定する保存 Markdown だけを開く。</summary>
public sealed class ArticleLinkOpener(IApplicationPaths paths)
{
    /// <summary>DB に含まれる任意パスは実行せず、固定フォルダーと内部 ID を使用する。</summary>
    public async Task OpenAsync(ArticleNotification notification, bool markdown)
    {
        bool opened;
        if (markdown)
        {
            var relative = ArticleMarkdownWriter.RelativePath(notification.Notification.Id);
            if (notification.MarkdownPath != relative) throw new StorageException("保存記事の参照先が不正です。");
            var file = await StorageFile.GetFileFromPathAsync(Path.Combine(paths.LocalDirectory, relative));
            opened = await Launcher.LaunchFileAsync(file);
        }
        else
        {
            if (!ExternalLinkPolicy.TryGetUri(notification.SourceUrl, out var uri)) throw new StorageException("元記事の URL を安全に開けません。");
            FeedIdentity.ValidateUrl(uri!.AbsoluteUri);
            opened = await Launcher.LaunchUriAsync(uri);
        }
        if (!opened) throw new StorageException("記事を開けませんでした。対応するアプリを確認してください。");
    }
}
