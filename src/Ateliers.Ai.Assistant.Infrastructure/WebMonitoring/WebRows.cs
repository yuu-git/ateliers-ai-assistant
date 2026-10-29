using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Microsoft.EntityFrameworkCore;

namespace Ateliers.Ai.Assistant.Infrastructure.WebMonitoring;

/// <summary>記事の重複キーと処理段階。正規化入力は再試行用 JSON として保持する。</summary>
internal sealed class ArticleRow
{
    /// <summary>成果物名にも使用する内部 ID。</summary>
    public Guid Id { get; set; }
    /// <summary>フィードを含む一意の重複キー。</summary>
    public string Key { get; set; } = "";
    /// <summary>正規化したフィード URL。</summary>
    public string Source { get; set; } = "";
    /// <summary>正規化済みフィード入力。</summary>
    public string Payload { get; set; } = "";
    /// <summary>初回取得日時。</summary>
    public long FetchedTicks { get; set; }
    /// <summary>成功済みの要約。null なら要約を再試行する。</summary>
    public string? Summary { get; set; }
    /// <summary>要約を生成したモデル。</summary>
    public string? Model { get; set; }
    /// <summary>保存が確定した Markdown 相対パス。</summary>
    public string? MarkdownPath { get; set; }
    /// <summary>ファイル保存と通知登録を確定済みか。</summary>
    public bool Notified { get; set; }
    /// <summary>直近の失敗概要。</summary>
    public string? Error { get; set; }
}
/// <summary>収集1回の履歴。開始時に登録し、各記事と終了時に更新する。</summary>
internal sealed class RunRow
{
    /// <summary>実行識別子。</summary>
    public Guid Id { get; set; }
    /// <summary>一覧ソート用開始 UTC ticks。</summary>
    public long StartedTicks { get; set; }
    /// <summary>実行結果と段階別件数の JSON。</summary>
    public string Payload { get; set; } = "";
}
/// <summary>記事1件につき最大1件の永続通知。記事と同じ ID を主キーにする。</summary>
internal sealed class NotificationRow
{
    /// <summary>通知元の記事 ID。主キーかつ外部キー。</summary>
    public Guid ArticleId { get; set; }
    /// <summary>通知を確定した UTC ticks。</summary>
    public long CreatedTicks { get; set; }
}
/// <summary>既存会話スキーマと独立した Web 収集テーブルの構成。</summary>
internal static class WebSchema
{
    /// <summary>重複キーと記事ごとの通知を一意制約で保護する。</summary>
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ArticleRow>(entity =>
        {
            entity.ToTable("WebArticles"); entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.Key).IsUnique();
            entity.HasIndex(row => new { row.Source, row.Notified });
        });
        model.Entity<RunRow>(entity => { entity.ToTable("WebCollectionRuns"); entity.HasKey(row => row.Id); entity.HasIndex(row => row.StartedTicks); });
        model.Entity<NotificationRow>(entity =>
        {
            entity.ToTable("WebNotifications"); entity.HasKey(row => row.ArticleId);
            entity.HasOne<ArticleRow>().WithOne().HasForeignKey<NotificationRow>(row => row.ArticleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(row => row.CreatedTicks);
        });
    }
}
