namespace Ateliers.Ai.Assistant.Core.Chat;

/// <summary>UI 非依存・メモリ内の PNG 添付。Base64 を一度だけ生成し、履歴でも同じ画像を共有する。</summary>
public sealed class ChatImage
{
    /// <summary>メモリ内添付として受け入れる PNG の最大バイト数（12 MiB）。</summary>
    public const int MaximumBytes = 12 * 1024 * 1024;

    /// <summary>PNG バイト列を Base64 化し、画素サイズとともに不変の添付として保持する。</summary>
    /// <remarks>長さと画素サイズを検証する。PNG のデコード・縮小は呼び出し元の責務とする。</remarks>
    /// <exception cref="ArgumentException">画像が空、または許容バイト数を超える場合。</exception>
    /// <exception cref="ArgumentOutOfRangeException">幅または高さが正でない場合。</exception>
    /// <exception cref="ArgumentNullException">PNG のバイト列が null の場合。</exception>
    /// <param name="pngBytes">正規化済み PNG のバイト列。入力配列の参照は保持しない。</param>
    /// <param name="width">画像の幅。単位はピクセルで、正の値。</param>
    /// <param name="height">画像の高さ。単位はピクセルで、正の値。</param>
    public ChatImage(byte[] pngBytes, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0 || pngBytes.Length > MaximumBytes)
            throw new ArgumentException("画像は空にできず、12 MiB 以下の PNG が必要です。", nameof(pngBytes));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Base64Data = Convert.ToBase64String(pngBytes);
        ByteLength = pngBytes.Length;
        Width = width;
        Height = height;
    }

    /// <summary>Data URL の接頭辞を含まない、送信用 PNG の Base64 文字列を取得する。</summary>
    public string Base64Data { get; }
    /// <summary>Base64 化前の PNG バイト数を取得する。</summary>
    public int ByteLength { get; }
    /// <summary>正規化後の画像幅をピクセル単位で取得する。</summary>
    public int Width { get; }
    /// <summary>正規化後の画像高をピクセル単位で取得する。</summary>
    public int Height { get; }
    /// <summary>保持する Base64 をデコードし、呼び出しごとに独立した PNG バイト配列を返す。</summary>
    /// <returns>保持内容から復元した、新しい PNG バイト配列。</returns>
    public byte[] GetPngBytes() => Convert.FromBase64String(Base64Data);
}
