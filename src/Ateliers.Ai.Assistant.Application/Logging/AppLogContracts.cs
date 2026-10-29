namespace Ateliers.Ai.Assistant.Application.Logging;

/// <summary>利用者向けの運転記録と技術診断の分類。Windows イベントログではない。</summary>
public enum AppLogCategory
{
    /// <summary>利用者向けの運転記録。</summary>
    Application,
    /// <summary>開発・障害調査向けの技術記録。</summary>
    System
}
/// <summary>表示と絞り込みに使う重大度。</summary>
public enum AppLogLevel
{
    /// <summary>通常の運転状態。</summary>
    Information,
    /// <summary>保留・再試行などの注意。</summary>
    Warning,
    /// <summary>操作または内部処理の失敗。</summary>
    Error
}
/// <summary>秘密情報や本文を含まない共通ログ行。</summary>
public sealed record AppLogEntry(DateTimeOffset At, AppLogCategory Category, AppLogLevel Level, string Origin,
    string Operation, string Message, Guid? CorrelationId = null);
/// <summary>ログ閲覧の上限付き条件。</summary>
public sealed record LogQuery(AppLogCategory Category, AppLogLevel? MinimumLevel = null, DateTimeOffset? Since = null,
    string Keyword = "", int Limit = 500);
/// <summary>プロセスの分類付き記録。メッセージはアプリが組み立てた安全な文言だけを渡す。</summary>
public interface IAppLog
{
    /// <summary>固定操作名、件数などの運転記録を保存する。</summary>
    void Write(AppLogCategory category, AppLogLevel level, string operation, string message, Guid? correlationId = null);
}
/// <summary>Worker の通信に依存しない、永続ログの読取専用ポート。</summary>
public interface IAppLogReader
{
    /// <summary>書込中の部分行を無視し、上限内の最新行を返す。</summary>
    Task<IReadOnlyList<AppLogEntry>> ReadAsync(LogQuery query, CancellationToken token = default);
}
