using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Application.Storage;

/// <summary>一時ファイルの用途。用途の追加は対応機能の実装を意味しない。</summary>
public enum TempPurpose
{
    /// <summary>画像をディスクへ置く必要がある場合の用途。</summary>
    Capture,
    /// <summary>将来の音声用データ。音声生成・再生は今回実装しない。</summary>
    Audio,
    /// <summary>その他の処理中作業データ。</summary>
    Work
}

/// <summary>同じデータ環境の全プロセスに適用する一時ファイルの上限。</summary>
public sealed record TempRetentionPolicy(int MaximumFiles = 64, long MaximumBytes = 256L * 1024 * 1024, TimeSpan? Retention = null)
{
    /// <summary>最長保持時間。使用中なら期限経過後も強制削除しない。</summary>
    public TimeSpan MaximumAge => Retention ?? TimeSpan.FromMinutes(30);
    /// <summary>無効な上限をホスト構成時に拒否する。</summary>
    public void Validate()
    { if (MaximumFiles < 1 || MaximumBytes < 1 || MaximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaximumFiles)); }
}
/// <summary>メモリ上の画像履歴の上限。Base64 UTF-16 文字列と管理情報の概算を数える。</summary>
public sealed record CaptureRetentionPolicy(int MaximumPerSession = 64, long MaximumBytes = 128L * 1024 * 1024, TimeSpan? Retention = null)
{
    /// <summary>実取得時刻に基づく保持期間。</summary>
    public TimeSpan MaximumAge => Retention ?? TimeSpan.FromMinutes(5);
}
/// <summary>メモリ内の正規化済み画像。対象の表示名等は記録しない。</summary>
public sealed record CapturedImage(Guid Id, Guid SessionId, Guid TargetId, DateTimeOffset CapturedAt, ChatImage Image)
{
    /// <summary>画像本体を持ち出さず時系列検索できる情報。</summary>
    public CapturedImageInfo Info => new(Id, SessionId, TargetId, CapturedAt, Image.Width, Image.Height, Image.ByteLength);
    /// <summary>Base64 文字列と1件512バイトの管理情報の概算。PNG・デコードの一時コピーは含まない。</summary>
    public long RetainedBytes => (long)Image.Base64Data.Length * sizeof(char) + 512;
}
/// <summary>使用権なしで列挙できる画像のメタデータ。</summary>
public sealed record CapturedImageInfo(Guid Id, Guid SessionId, Guid TargetId, DateTimeOffset CapturedAt, int Width, int Height, int ByteLength);
/// <summary>不変な一時ファイルの情報。利用者指定パスや本文を公開しない。</summary>
public sealed record TempFileInfo(Guid Id, TempPurpose Purpose, DateTimeOffset CreatedAt, long Length);
/// <summary>使用権が有効な間だけ使える読み取りストリーム。</summary>
public sealed record TempFileRead(TempFileInfo Info, Stream Content);
/// <summary>画像・一時データの不足、上限超過、破損を示す。内容をログへ渡さない。</summary>
public sealed class TempStorageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>すべて確保できた画像の一括使用権。利用後の参照保持は禁止する。</summary>
public sealed class CaptureBatchLease : IDisposable
{
    private Action? _release;
    /// <summary>内部ストアが確保済みの画像を返す。</summary>
    public CaptureBatchLease(IReadOnlyList<CapturedImage> images, Action release) { Images = images; _release = release; }
    /// <summary>実取得時刻順の画像。不変の画像データを共有する。</summary>
    public IReadOnlyList<CapturedImage> Images { get; private set; }
    /// <summary>すべての使用権を一度だけ解放する。</summary>
    public void Dispose() { Interlocked.Exchange(ref _release, null)?.Invoke(); Images = Array.Empty<CapturedImage>(); }
}
/// <summary>複数ファイルを一括して保護する。解析・再生・キャンセル時は using で解放する。</summary>
public sealed class TempFileBatchLease : IDisposable, IAsyncDisposable
{
    private Func<ValueTask>? _release;
    /// <summary>内部ストアが確保した読み取り対象と、解放処理を接続する。</summary>
    public TempFileBatchLease(IReadOnlyList<TempFileRead> files, Func<ValueTask> release) { Files = files; _release = release; }
    /// <summary>使用中のファイル。ストリームを使用権の外へ持ち出さない。</summary>
    public IReadOnlyList<TempFileRead> Files { get; }
    /// <summary>解放後の清掃まで非同期で待つ。</summary>
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _release, null)?.Invoke() ?? ValueTask.CompletedTask;
    /// <summary>読み取りハンドルを直ちに閉じ、清掃は背景で行う。</summary>
    public void Dispose() { var pending = DisposeAsync(); if (!pending.IsCompletedSuccessfully) _ = pending.AsTask(); }
}

/// <summary>呼び出し元プロセス専用の一時ファイル。別プロセスへの ID・パス受け渡しは提供しない。</summary>
public interface ITempFileStore : IAsyncDisposable, IDisposable
{
    /// <summary>書き込み前に最大容量を予約し、完了したファイルの使用権を返す。入力ストリームは閉じない。</summary>
    Task<TempFileBatchLease> CreateAsync(TempPurpose purpose, Stream input, long maximumBytes, CancellationToken token = default);
    /// <summary>同じ所有者のファイルをすべて確保する。不足・破損時は一件も使用権を返さない。</summary>
    Task<TempFileBatchLease> AcquireAsync(IReadOnlyList<Guid> ids, CancellationToken token = default);
    /// <summary>自プロセスの非使用中ファイルを削除する。不存在・使用中なら false。</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken token = default);
    /// <summary>自プロセスの非使用中データと、ロック取得可能な終了済み所有者の残存を回収する。失敗はログへ記録する。</summary>
    Task CleanupAsync(CancellationToken token = default);
}
