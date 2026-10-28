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
    public const uint MaximumEdge = 1536;
    /// <summary>デコード前に受け入れる元画像の最大バイト数（32 MiB）。</summary>
    public const ulong MaximumSourceBytes = 32 * 1024 * 1024;
    /// <summary>デコード対象に許容する元画像の最大画素数。</summary>
    public const ulong MaximumSourcePixels = 40_000_000;

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
    internal static async Task<ChatImage> NormalizeAsync(IRandomAccessStream stream, CancellationToken cancellationToken)
    {
        if (stream.Size == 0 || stream.Size > MaximumSourceBytes)
            throw new InvalidOperationException("貼り付け元の画像は 32 MiB 以下にしてください。");
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        if ((ulong)decoder.PixelWidth * decoder.PixelHeight > MaximumSourcePixels)
            throw new InvalidOperationException("貼り付け元の画像が大きすぎます。4000万画素以下にしてください。");
        var scale = Math.Min(1d, MaximumEdge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
        var height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
        if (decoder.OrientedPixelWidth != decoder.PixelWidth) (width, height) = (height, width);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(cancellationToken);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync().AsTask(cancellationToken);
        if (output.Size > ChatImage.MaximumBytes) throw new InvalidOperationException("縮小後の画像が 12 MiB を超えています。");
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size).AsTask(cancellationToken);
        var bytes = new byte[(int)output.Size];
        reader.ReadBytes(bytes);
        return new ChatImage(bytes, (int)width, (int)height);
    }

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
