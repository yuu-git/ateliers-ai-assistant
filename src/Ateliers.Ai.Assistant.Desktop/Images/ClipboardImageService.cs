using System;
using System.Threading;
using System.Threading.Tasks;
using Ateliers.Ai.Assistant.Core.Chat;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Ateliers.Ai.Assistant.Desktop.Images;

/// <summary>クリップボード画像をメモリ内で縮小・PNG 化する。ファイルは作成しない。</summary>
public static class ClipboardImageService
{
    /// <summary>正規化後の画像の長辺に許容する最大ピクセル数。</summary>
    public const uint MaximumEdge = Ateliers.Ai.Assistant.Platform.Windows.Images.WindowsImageNormalizer.MaximumEdge;
    /// <summary>デコード前に受け入れる元画像の最大バイト数（32 MiB）。</summary>
    public const ulong MaximumSourceBytes = Ateliers.Ai.Assistant.Platform.Windows.Images.WindowsImageNormalizer.MaximumSourceBytes;
    /// <summary>デコード対象に許容する元画像の最大画素数。</summary>
    public const ulong MaximumSourcePixels = Ateliers.Ai.Assistant.Platform.Windows.Images.WindowsImageNormalizer.MaximumSourcePixels;

    /// <summary>クリップボードの Bitmap を読み取り、メモリ内の PNG 添付へ正規化する。</summary>
    /// <param name="content">取得済みのクリップボード内容。Bitmap 形式を含むこと。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>メモリ内で保持する正規化済み PNG 添付。</returns>
    public static async Task<ChatImage> ReadAsync(DataPackageView content, CancellationToken cancellationToken)
    {
        var reference = await content.GetBitmapAsync().AsTask(cancellationToken);
        using var stream = await reference.OpenReadAsync().AsTask(cancellationToken);
        return await NormalizeAsync(stream, cancellationToken);
    }

    /// <summary>元画像の負荷を制限し、縦横比を維持した縮小と PNG 化を行う。</summary>
    /// <remarks>小さい画像は拡大せず、向き情報を考慮する。ディスクには保存しない。</remarks>
    /// <exception cref="InvalidOperationException">元画像の容量・画素数、または正規化後の容量が許容値を超える場合。</exception>
    /// <param name="stream">デコードする画像のランダムアクセスストリーム。呼び出し元が破棄する。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>メモリ内で保持する正規化済み PNG 添付。</returns>
    internal static Task<ChatImage> NormalizeAsync(IRandomAccessStream stream, CancellationToken cancellationToken)
        => Ateliers.Ai.Assistant.Platform.Windows.Images.WindowsImageNormalizer.NormalizeAsync(stream, cancellationToken);

    /// <summary>添付 PNG から幅240pxをデコード幅に指定した WinUI のプレビュー画像を作る。</summary>
    /// <param name="image">メモリ内で保持する正規化済み PNG 添付。</param>
    /// <param name="cancellationToken">処理の中止要求を受け取るトークン。</param>
    /// <returns>UI スレッドで利用するプレビュー画像。</returns>
    public static async Task<BitmapImage> CreatePreviewAsync(ChatImage image, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(image.GetPngBytes());
            await writer.StoreAsync().AsTask(cancellationToken);
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = 240 };
        await bitmap.SetSourceAsync(stream).AsTask(cancellationToken);
        return bitmap;
    }
}
