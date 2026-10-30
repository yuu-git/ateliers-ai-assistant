using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ateliers.Ai.Assistant.Application.Storage;

namespace Ateliers.Ai.Assistant.Infrastructure.Storage.Temp;

/// <summary>プロセス専有 Temp と環境全体の容量予約を管理する。PID や時刻だけで使用中を判定しない。</summary>
public sealed class TempFileStore : ITempFileStore
{
    private static readonly string[] Purposes = ["capture", "audio", "work"];
    private readonly string _root, _owner;
    private readonly ITempFileSystem _fs;
    private readonly IAppDiagnostics _log;
    private readonly TempRetentionPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, int> _leases = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileStream? _ownerLock;
    private IDisposable? _ownerGuard;
    private int _operations;
    private bool _disposed;
    private Task _cleanupTask = Task.CompletedTask;
    private bool _cleanupPending;
    private bool _cleanupRunning;
    private readonly CancellationTokenSource _lifetime = new();
    private sealed record Entry(string Path, string Marker, string Owner, TempFileInfo Info, string Hash);

    /// <summary>既存パス・診断ログを再利用する。参照するだけではファイルやフォルダーを作らない。</summary>
    public TempFileStore(IApplicationPaths paths, IAppDiagnostics log, TempRetentionPolicy? policy = null,
        TimeProvider? clock = null, ITempFileSystem? fileSystem = null)
    {
        _root = Path.Combine(paths.TempDirectory, "v1"); _owner = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        _log = log; _policy = policy ?? new(); _policy.Validate(); _clock = clock ?? TimeProvider.System;
        _fs = fileSystem ?? new SafeTempFileSystem(_root);
    }
    /// <inheritdoc/>
    public Task<TempFileBatchLease> CreateAsync(TempPurpose purpose, Stream input, long maximumBytes, CancellationToken token = default)
    {
        if (!Enum.IsDefined(purpose) || maximumBytes < 1 || maximumBytes > _policy.MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ArgumentNullException.ThrowIfNull(input); BeginOperation();
        return Task.Run(async () =>
        {
            using var operationToken = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            var cancellation = operationToken.Token;
            var id = Guid.NewGuid(); var directory = Path.Combine(_owner, Purposes[(int)purpose]);
            var part = Path.Combine(directory, id.ToString("N") + ".part"); var reservation = Path.Combine(directory, id.ToString("N") + ".reserve");
            string? final = null; bool committed = false;
            try
            {
                using (var gate = await EnterAsync(true, cancellation).ConfigureAwait(false))
                {
                    ThrowIfDisposed(); EnsureOwner(); CleanupCore();
                    MakeRoom(maximumBytes);
                    using var scope = _fs.GuardDirectory(directory, true);
                    using var budget = _fs.Open(reservation, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    budget.Write(Encoding.ASCII.GetBytes(maximumBytes.ToString(CultureInfo.InvariantCulture)));
                }
                long length = 0; string digest;
                using (var scope = _fs.GuardDirectory(directory, false))
                using (var output = _fs.Open(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[64 * 1024]; int count;
                    while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                    {
                        if (count > maximumBytes - length) throw new TempStorageException("予約した一時データ容量を超えました。");
                        hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false); length += count;
                    }
                    await output.FlushAsync(cancellation).ConfigureAwait(false); digest = Convert.ToHexString(hash.GetHashAndReset());
                }
                cancellation.ThrowIfCancellationRequested();
                using (var gate = await EnterAsync(true, cancellation).ConfigureAwait(false))
                {
                    ThrowIfDisposed(); var now = _clock.GetUtcNow();
                    final = Path.Combine(directory, $"{now.UtcTicks:D19}_{id:N}_{digest}.bin");
                    // ディレクトリ固定を緩めず、空の確定マーカーを原子的に作成する。
                    using (var marker = _fs.Open(final, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    _fs.Delete(reservation);
                    var lease = await AcquireCoreAsync([new(part, final, _owner, new(id, purpose, now, length), digest)], cancellation).ConfigureAwait(false);
                    committed = true; return lease;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log.Error("temp.create", exception);
                if (exception is TempStorageException) throw;
                throw new TempStorageException("一時データを作成できません。容量・アクセス権を確認してください。", exception);
            }
            finally
            {
                if (!committed)
                {
                    try
                    {
                        if (_fs.DirectoryExists(directory))
                        {
                            using var gate = await EnterAsync(false, CancellationToken.None).ConfigureAwait(false);
                            _fs.Delete(part); _fs.Delete(reservation); if (final is not null) _fs.Delete(final);
                        }
                    }
                    catch (Exception exception) { _log.Error("temp.rollback", exception); }
                }
                EndOperation(); await CleanupAsync().ConfigureAwait(false);
            }
        });
    }
    /// <inheritdoc/>
    public Task<TempFileBatchLease> AcquireAsync(IReadOnlyList<Guid> ids, CancellationToken token = default)
    {
        var requested = ids.Distinct().ToArray();
        if (requested.Length == 0 || requested.Any(id => id == Guid.Empty)) throw new ArgumentException("一時ファイル ID が必要です。");
        BeginOperation();
        return Task.Run(async () =>
        {
            using var operationToken = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            try
            {
                using var gate = await EnterAsync(false, operationToken.Token).ConfigureAwait(false); ThrowIfDisposed();
                var available = Entries(_owner).Where(entry => !Expired(entry)).ToArray();
                var entries = requested.Select(id => available.SingleOrDefault(entry => entry.Info.Id == id) ?? throw new TempStorageException("一時データが存在しないか、所有者・期限が一致しません。")).ToArray();
                return await AcquireCoreAsync(entries, operationToken.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log.Error("temp.acquire", exception);
                if (exception is TempStorageException) throw;
                throw new TempStorageException("必要な一時データが消失・破損しているか、取得できません。", exception);
            }
            finally { EndOperation(); }
        });
    }
    /// <inheritdoc/>
    public Task<bool> DeleteAsync(Guid id, CancellationToken token = default)
    {
        BeginOperation();
        return Task.Run(async () =>
        {
            try
            {
                if (!_fs.DirectoryExists(_owner)) return false;
                using var gate = await EnterAsync(false, token).ConfigureAwait(false); ThrowIfDisposed();
                if (Used(id)) return false;
                var entry = Entries(_owner).SingleOrDefault(entry => entry.Info.Id == id);
                if (entry is null) return false;
                DeleteEntry(entry); return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { _log.Error("temp.delete", exception); throw new TempStorageException("一時データを削除できません。", exception); }
            finally { EndOperation(); }
        });
    }
    /// <inheritdoc/>
    public Task CleanupAsync(CancellationToken token = default)
    {
        lock (_sync)
        {
            if (_closed.Task.IsCompleted) return Task.CompletedTask;
            if (_cleanupRunning) { _cleanupPending = true; return _cleanupTask; }
            _cleanupRunning = true;
            return _cleanupTask = Task.Run(async () =>
            {
                while (true)
                {
                    lock (_sync) _cleanupPending = false;
                    try
                    {
                        if (_fs.DirectoryExists(_root))
                        { using var gate = await EnterAsync(false, token).ConfigureAwait(false); CleanupCore(); }
                    }
                    catch (Exception exception) { _log.Error("temp.cleanup", exception); }
                    lock (_sync)
                    {
                        if (_cleanupPending) continue;
                        _cleanupRunning = false; CloseOwnerIfIdle(); return;
                    }
                }
            });
        }
    }

    /// <summary>起動ごとの所有権を OS ハンドルで保持する。作成と公開は環境のロック内で行う。</summary>
    private void EnsureOwner()
    {
        if (_ownerLock is not null) return;
        var guard = _fs.GuardDirectory(_owner, true);
        try { _ownerLock = _fs.Open(Path.Combine(_owner, "owner.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); _ownerGuard = guard; }
        catch { guard.Dispose(); throw; }
    }
    /// <summary>ロック解放済みの所有者だけを回収する。生存中の他所有者は列挙対象にも混ぜない。</summary>
    private void CleanupCore()
    {
        foreach (var owner in _fs.Directories(_root).Where(IsOwner))
        {
            if (owner == _owner)
            {
                foreach (var entry in Entries(owner).Where(e => (Expired(e) || IsDisposed || e.Info.Length < 0) && !Used(e.Info.Id))) TryDeleteEntry(entry);
                CleanupIncomplete();
                continue;
            }
            try
            {
                // ガード・ロックを取得できない領域は使用中または安全性不明として触れない。
                using (var ownerGuard = _fs.GuardDirectory(owner, false))
                {
                    using var orphanLock = OpenOrphanLock(owner);
                    foreach (var purpose in Purposes)
                    {
                        var directory = Path.Combine(owner, purpose); if (!_fs.DirectoryExists(directory)) continue;
                        foreach (var file in _fs.Files(directory).Where(KnownFile)) TryDelete(file);
                    }
                }
                foreach (var purpose in Purposes) TryDeleteDirectory(Path.Combine(owner, purpose));
                TryDelete(Path.Combine(owner, "owner.lock")); TryDeleteDirectory(owner);
            }
            catch (IOException exception) when (exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 }) { /* 使用中は正常。 */ }
            catch (Exception exception) { _log.Error("temp.orphan", exception); }
        }
    }
    private FileStream? OpenOrphanLock(string owner)
    {
        try { return _fs.Open(Path.Combine(owner, "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) when (exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }) { return null; }
    }
    /// <summary>確定ファイルと予約分を環境全体で集計し、自分の非使用中ファイルから回収する。</summary>
    private void MakeRoom(long bytes)
    {
        while (true)
        {
            var (count, used) = Usage();
            if (count < _policy.MaximumFiles && used <= _policy.MaximumBytes - bytes) return;
            var victim = Entries(_owner).Where(e => !Used(e.Info.Id)).OrderBy(e => e.Info.CreatedAt).FirstOrDefault();
            if (victim is null) throw new TempStorageException("一時データの上限です。使用中のデータが解放されるまで新規生成できません。");
            DeleteEntry(victim);
        }
    }
    private (long Count, long Bytes) Usage()
    {
        long count = 0, bytes = 0;
        foreach (var owner in _fs.Directories(_root).Where(IsOwner))
        foreach (var purpose in Purposes)
        {
            var directory = Path.Combine(owner, purpose); if (!_fs.DirectoryExists(directory)) continue;
            var files = _fs.Files(directory);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (IsStaging(file, ".part") && (files.Contains(Path.ChangeExtension(file, ".reserve")) || files.Any(candidate => Parse(candidate, out var id, out _, out _) && id.ToString("N") == Path.GetFileNameWithoutExtension(file)))) continue;
                using var guard = _fs.GuardDirectory(directory, false);
                using var input = _fs.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                count = checked(count + 1);
                if (IsStaging(file, ".reserve"))
                {
                    if (input.Length > 20) throw new TempStorageException("一時容量の予約情報が不正です。");
                    using var reader = new StreamReader(input, Encoding.ASCII, false, 128, true);
                    if (!long.TryParse(reader.ReadToEnd(), NumberStyles.None, CultureInfo.InvariantCulture, out var reserved) || reserved < 1) throw new TempStorageException("一時容量の予約情報が不正です。");
                    bytes = checked(bytes + reserved);
                }
                else if (Parse(file, out var id, out _, out _))
                {
                    var length = PayloadLength(Path.Combine(directory, id.ToString("N") + ".part"));
                    bytes = checked(bytes + Math.Max(0, length));
                }
                else bytes = checked(bytes + input.Length);
            }
        }
        return (count, bytes);
    }
    /// <summary>全件を開き検証した後にだけ参照数を増やす。途中失敗では全ハンドルを戻す。</summary>
    private async Task<TempFileBatchLease> AcquireCoreAsync(Entry[] entries, CancellationToken token)
    {
        var held = new List<(FileStream Stream, IDisposable Guard)>();
        try
        {
            foreach (var entry in entries)
            {
                var guard = _fs.GuardDirectory(Path.GetDirectoryName(entry.Path)!, false);
                FileStream stream;
                try
                {
                    using var marker = _fs.Open(entry.Marker, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (marker.Length != 0) throw new TempStorageException("一時データの確定情報が破損しています。");
                    stream = _fs.Open(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                catch { guard.Dispose(); throw; }
                held.Add((stream, guard));
                if (stream.Length != entry.Info.Length || Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != entry.Hash) throw new TempStorageException("一時データが破損しています。");
                stream.Position = 0;
            }
            token.ThrowIfCancellationRequested();
            lock (_sync) foreach (var entry in entries) _leases[entry.Info.Id] = _leases.GetValueOrDefault(entry.Info.Id) + 1;
            return new(entries.Select((e, i) => new TempFileRead(e.Info, held[i].Stream)).ToArray(), () =>
            {
                ReleaseHandles(held);
                lock (_sync)
                {
                    foreach (var entry in entries) { if (--_leases[entry.Info.Id] == 0) _leases.Remove(entry.Info.Id); }
                    return new ValueTask(CleanupAsync());
                }
            });
        }
        catch { ReleaseHandles(held); throw; }
    }
    private void ReleaseHandles(IEnumerable<(FileStream Stream, IDisposable Guard)> held)
    {
        foreach (var item in held)
        {
            try { item.Stream.Dispose(); }
            catch (Exception exception) { _log.Error("temp.release", exception); }
            finally { item.Guard.Dispose(); }
        }
    }
    private Entry[] Entries(string? onlyOwner = null)
    {
        var entries = new List<Entry>();
        foreach (var owner in onlyOwner is null ? _fs.Directories(_root).Where(IsOwner) : [onlyOwner])
        foreach (var purpose in Purposes)
        {
            var directory = Path.Combine(owner, purpose); if (!_fs.DirectoryExists(directory)) continue;
            foreach (var file in _fs.Files(directory))
            {
                if (!Parse(file, out var id, out var at, out var hash)) continue;
                using var guard = _fs.GuardDirectory(directory, false);
                var payload = Path.Combine(directory, id.ToString("N") + ".part");
                entries.Add(new(payload, file, owner, new(id, (TempPurpose)Array.IndexOf(Purposes, purpose), at, PayloadLength(payload)), hash));
            }
        }
        return entries.ToArray();
    }
    private static bool Parse(string path, out Guid id, out DateTimeOffset at, out string hash)
    {
        id = default; at = default; hash = "";
        if (Path.GetExtension(path) != ".bin") return false;
        var parts = Path.GetFileNameWithoutExtension(path).Split('_');
        if (parts.Length != 3 || parts[0].Length != 19 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks || !Guid.TryParseExact(parts[1], "N", out id) || parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit)) return false;
        at = new(ticks, TimeSpan.Zero); hash = parts[2]; return true;
    }
    private static bool IsStaging(string path, string extension) => Path.GetExtension(path) == extension && Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _);
    private static bool KnownFile(string path) => Parse(path, out _, out _, out _) || IsStaging(path, ".part") || IsStaging(path, ".reserve");
    private static bool IsOwner(string path) => Guid.TryParseExact(Path.GetFileName(path), "N", out _);
    private bool Expired(Entry entry) => entry.Info.CreatedAt < _clock.GetUtcNow() - _policy.MaximumAge;
    /// <summary>確定後に消失した本体を負数で区別し、残った確定情報を安全に回収できるようにする。</summary>
    private long PayloadLength(string path)
    {
        try { using var input = _fs.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); return input.Length; }
        catch (IOException exception) when (exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }) { return -1; }
    }
    private bool Used(Guid id) { lock (_sync) return _leases.ContainsKey(id); }
    /// <summary>自プロセスの操作が終了した時だけ、確定されなかったファイルを回収する。</summary>
    private void CleanupIncomplete()
    {
        lock (_sync) { if (_operations != 0) return; }
        foreach (var purpose in Purposes)
        {
            var directory = Path.Combine(_owner, purpose); if (!_fs.DirectoryExists(directory)) continue;
            var files = _fs.Files(directory);
            var committed = files.Select(file => Parse(file, out var id, out _, out _) ? id : Guid.Empty).ToHashSet();
            foreach (var file in files.Where(file => IsStaging(file, ".part") || IsStaging(file, ".reserve")))
            {
                var id = Guid.ParseExact(Path.GetFileNameWithoutExtension(file), "N");
                if (!committed.Contains(id) && !Used(id)) TryDelete(file);
            }
        }
    }
    private void DeleteEntry(Entry entry) { _fs.Delete(entry.Path); _fs.Delete(entry.Marker); }
    private void TryDeleteEntry(Entry entry) { try { DeleteEntry(entry); } catch (Exception exception) { _log.Error("temp.delete", exception); } }
    private bool IsDisposed { get { lock (_sync) return _disposed; } }
    private void ThrowIfDisposed() { lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this); }
    private void BeginOperation() { lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); _operations++; } }
    private void EndOperation() { lock (_sync) { _operations--; if (_disposed) _ = CleanupAsync(); } }
    private void TryDelete(string file) { try { _fs.Delete(file); } catch (Exception exception) { _log.Error("temp.delete", exception); } }
    private void TryDeleteDirectory(string directory)
    { try { if (_fs.DirectoryExists(directory) && _fs.Files(directory).Length == 0 && _fs.Directories(directory).Length == 0) _fs.DeleteEmptyDirectory(directory); } catch (Exception exception) { _log.Error("temp.directory", exception); } }
    /// <summary>環境全体の短い排他。永久待機せず、使用中なら期限内に失敗する。</summary>
    private async Task<IDisposable> EnterAsync(bool create, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var guard = _fs.GuardDirectory(_root, create);
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try { return new Gate(_fs.Open(Path.Combine(_root, "manager.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), guard); }
                catch (IOException exception) when (exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 }) { await Task.Delay(25, timeout.Token).ConfigureAwait(false); }
            }
        }
        catch { guard.Dispose(); throw; }
    }
    private sealed class Gate(FileStream file, IDisposable guard) : IDisposable { public void Dispose() { file.Dispose(); guard.Dispose(); } }
    private void CloseOwnerIfIdle()
    {
        lock (_sync)
        {
            if (!_disposed || _operations != 0 || _leases.Count != 0) return;
            _ownerLock?.Dispose(); _ownerLock = null; _ownerGuard?.Dispose(); _ownerGuard = null; _closed.TrySetResult();
        }
    }
    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_sync) { _disposed = true; _ = CleanupAsync(); }
        // 入力ストリームが中止に応答しない場合も、Host は期限付きで終了し次回起動時に回収する。
        try { _lifetime.Cancel(); }
        catch (Exception exception) { _log.Error("temp.cancel", exception); }
    }
    /// <inheritdoc/>
    public async ValueTask DisposeAsync() { Dispose(); Task cleanup; lock (_sync) cleanup = _cleanupTask; await cleanup.ConfigureAwait(false); await _closed.Task.ConfigureAwait(false); }
}
