using Ateliers.Ai.Assistant.Core.Chat;

namespace Ateliers.Ai.Assistant.Application.Storage;

/// <summary>複数時点の画像をメモリ内だけで保持する。清掃・選択・一括使用権の操作を直列化する。</summary>
public sealed class CapturedImageStore
{
    private sealed class Entry(CapturedImage value) { internal CapturedImage Value = value; internal int Uses; }
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly object _sync = new();
    private readonly CaptureRetentionPolicy _policy;
    private readonly TimeProvider _clock;
    /// <summary>内部保持ポリシーと検証用時計を指定する。</summary>
    public CapturedImageStore(CaptureRetentionPolicy? policy = null, TimeProvider? clock = null)
    {
        _policy = policy ?? new(); _clock = clock ?? TimeProvider.System;
        if (_policy.MaximumPerSession < 1 || _policy.MaximumBytes < 1 || _policy.MaximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(policy));
    }
    /// <summary>実取得時刻と識別子で登録する。ディスクへの退避はしない。</summary>
    public CapturedImageInfo Register(Guid sessionId, Guid targetId, DateTimeOffset capturedAt, ChatImage image)
    {
        if (sessionId == Guid.Empty || targetId == Guid.Empty) throw new ArgumentException("セッションと対象の ID が必要です。");
        ArgumentNullException.ThrowIfNull(image);
        var value = new CapturedImage(Guid.NewGuid(), sessionId, targetId, capturedAt.ToUniversalTime(), image);
        lock (_sync)
        {
            CleanupCore();
            if (value.CapturedAt > _clock.GetUtcNow() || value.CapturedAt < _clock.GetUtcNow() - _policy.MaximumAge)
                throw new TempStorageException("取得時刻が保持期間の範囲外です。");
            if (value.RetainedBytes > _policy.MaximumBytes) throw new TempStorageException("画像がメモリ保持上限を超えています。");
            // 削除計画を先に作り、使用中のため確保不能なら既存履歴を変えない。
            var remaining = _entries.Values.ToList(); var evict = new List<Entry>();
            bool Fits() => remaining.Where(e => e.Value.SessionId == sessionId).Count() < _policy.MaximumPerSession &&
                remaining.Sum(e => e.Value.RetainedBytes) <= _policy.MaximumBytes - value.RetainedBytes;
            while (!Fits())
            {
                var sessionFull = remaining.Count(e => e.Value.SessionId == sessionId) >= _policy.MaximumPerSession;
                var candidate = remaining.Where(e => e.Uses == 0 && (!sessionFull || e.Value.SessionId == sessionId)).OrderBy(e => e.Value.CapturedAt).FirstOrDefault();
                if (candidate is null) break;
                remaining.Remove(candidate); evict.Add(candidate);
            }
            if (!Fits()) throw new TempStorageException("使用中の画像を保持しているため、新しい画像を登録できません。");
            foreach (var candidate in evict) _entries.Remove(candidate.Value.Id);
            _entries.Add(value.Id, new(value)); return value.Info;
        }
    }
    /// <summary>同一セッション・対象の利用可能なメタデータを実時刻順で返す。</summary>
    public IReadOnlyList<CapturedImageInfo> List(Guid sessionId, Guid targetId)
    { lock (_sync) { CleanupCore(); return Ordered(sessionId, targetId).Select(e => e.Value.Info).ToArray(); } }
    /// <summary>指定 ID を一括取得する。別セッション・別対象の混入や欠損時は全件失敗する。</summary>
    public CaptureBatchLease Acquire(Guid sessionId, Guid targetId, IReadOnlyList<Guid> ids)
    {
        lock (_sync)
        {
            CleanupCore();
            var entries = ids.Distinct().Select(id => _entries.GetValueOrDefault(id)).ToArray();
            if (entries.Length == 0 || entries.Any(e => e is null || e.Value.SessionId != sessionId || e.Value.TargetId != targetId || IsExpired(e)))
                throw new TempStorageException("必要な画像が不足しているか、対象・セッション・保持期限が一致しません。");
            return Lease(entries.Select(e => e!).ToArray());
        }
    }
    /// <summary>実時刻で最も近い画像を許容差内から選び、一括取得する。同距離なら過去側を優先する。</summary>
    public CaptureBatchLease AcquireNearest(Guid sessionId, Guid targetId, IReadOnlyList<DateTimeOffset> times, TimeSpan tolerance)
    {
        if (tolerance < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(tolerance));
        lock (_sync)
        {
            CleanupCore(); var selected = new List<Entry>();
            foreach (var time in times)
            {
                var item = Ordered(sessionId, targetId).OrderBy(e => (e.Value.CapturedAt - time).Duration()).FirstOrDefault();
                if (item is null || (item.Value.CapturedAt - time).Duration() > tolerance) throw new TempStorageException("指定時刻の許容範囲に画像がありません。");
                selected.Add(item);
            }
            if (selected.Count == 0) throw new TempStorageException("画像の取得時刻を指定してください。");
            if (selected.Distinct().Count() != selected.Count) throw new TempStorageException("指定した複数時点に対応する異なる画像が不足しています。");
            return Lease(selected.ToArray());
        }
    }
    /// <summary>使用中でない期限切れ画像を解放する。</summary>
    public void Cleanup() { lock (_sync) CleanupCore(); }
    /// <summary>指定セッション・対象の非使用中画像を削除する。別対象・不存在・使用中なら false。</summary>
    public bool Remove(Guid sessionId, Guid targetId, Guid id)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Value.SessionId != sessionId || entry.Value.TargetId != targetId || entry.Uses != 0) return false;
            return _entries.Remove(id);
        }
    }
    private bool IsExpired(Entry entry) => entry.Value.CapturedAt < _clock.GetUtcNow() - _policy.MaximumAge;
    private IEnumerable<Entry> Ordered(Guid session, Guid target) => _entries.Values.Where(e => e.Value.SessionId == session && e.Value.TargetId == target && !IsExpired(e)).OrderBy(e => e.Value.CapturedAt).ThenBy(e => e.Value.Id);
    private void CleanupCore() { foreach (var entry in _entries.Values.Where(e => e.Uses == 0 && IsExpired(e)).ToArray()) _entries.Remove(entry.Value.Id); }
    private CaptureBatchLease Lease(Entry[] entries)
    {
        foreach (var entry in entries) entry.Uses++;
        return new(entries.OrderBy(e => e.Value.CapturedAt).Select(e => e.Value).ToArray(), () =>
        { lock (_sync) { foreach (var entry in entries) entry.Uses--; CleanupCore(); } });
    }
}
