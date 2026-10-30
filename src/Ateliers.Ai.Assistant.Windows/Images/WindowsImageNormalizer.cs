using Ateliers.Ai.Assistant.Core.Chat;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
namespace Ateliers.Ai.Assistant.Platform.Windows.Images;

/// <summary>UI に依存しないメモリ内画像の縮小・PNG 化。Desktop と Worker で共有する。</summary>
public static class WindowsImageNormalizer
{
    /// <summary>送信画像の最大長辺。</summary>
    public const uint MaximumEdge = 1536;
    /// <summary>入力ストリームの最大バイト数。</summary>
    public const ulong MaximumSourceBytes = 32 * 1024 * 1024;
    /// <summary>入力画像の最大画素数。</summary>
    public const ulong MaximumSourcePixels = 40_000_000;
    /// <summary>既存の貼り付けと同じ制約・向き補正・PNG 表現で正規化する。</summary>
    public static async Task<ChatImage> NormalizeAsync(IRandomAccessStream stream, CancellationToken token)
    {
        if (stream.Size == 0 || stream.Size > MaximumSourceBytes) throw new InvalidOperationException("入力画像は32 MiB以下にしてください。");
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
        if ((ulong)decoder.PixelWidth * decoder.PixelHeight > MaximumSourcePixels) throw new InvalidOperationException("入力画像は4000万画素以下にしてください。");
        var scale = Math.Min(1d, MaximumEdge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
        var height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token);
        if (decoder.OrientedPixelWidth != decoder.PixelWidth) (width, height) = (height, width);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(token);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync().AsTask(token);
        if (output.Size > ChatImage.MaximumBytes) throw new InvalidOperationException("縮小後の画像が12 MiBを超えています。");
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size).AsTask(token);
        var bytes = new byte[(int)output.Size]; reader.ReadBytes(bytes);
        return new ChatImage(bytes, (int)width, (int)height);
    }
}
