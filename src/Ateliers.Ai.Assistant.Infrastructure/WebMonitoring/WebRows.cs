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
    /// <summary>閲覧終了または明示的な既読指定の UTC ticks。null は未読で、通知生成前は集計しない。</summary>
    public long? ReadAtTicks { get; set; }
    /// <summary>手動のブックマーク。未読・タグから独立する。</summary>
    public bool IsBookmarked { get; set; }
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
    /// <summary>強制終了後の復旧対象となる処理中フラグ。</summary>
    public bool Processing { get; set; }
    /// <summary>短時間の無限再試行を防ぐ次回試行可能 UTC ticks。</summary>
    public long? RetryAfterTicks { get; set; }
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
        model.Entity<WorkerSourceStateRow>(entity => { entity.ToTable("WorkerSourceStates"); entity.HasKey(row => row.Id); });
        model.Entity<ArticleRow>(entity =>
        {
            entity.ToTable("WebArticles"); entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.Key).IsUnique();
            entity.HasIndex(row => new { row.Source, row.Notified });
            entity.HasIndex(row => row.FetchedTicks);
            entity.HasIndex(row => new { row.Source, row.FetchedTicks });
            entity.HasIndex(row => new { row.ReadAtTicks, row.FetchedTicks });
            entity.HasIndex(row => new { row.IsBookmarked, row.FetchedTicks });
        });
        model.Entity<RunRow>(entity => { entity.ToTable("WebCollectionRuns"); entity.HasKey(row => row.Id); entity.HasIndex(row => row.StartedTicks); });
        model.Entity<NotificationRow>(entity =>
        {
            entity.ToTable("WebNotifications"); entity.HasKey(row => row.ArticleId);
            entity.HasOne<ArticleRow>().WithOne().HasForeignKey<NotificationRow>(row => row.ArticleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(row => row.CreatedTicks);
        });
        model.Entity<TagRow>(entity =>
        {
            entity.ToTable("WebTags"); entity.HasKey(row => row.Id);
            entity.Property(row => row.Name).IsRequired().HasMaxLength(64);
            entity.Property(row => row.NormalizedName).IsRequired().HasMaxLength(128);
            entity.HasIndex(row => row.NormalizedName).IsUnique();
        });
        model.Entity<ArticleTagRow>(entity =>
        {
            entity.ToTable("WebArticleTags"); entity.HasKey(row => new { row.ArticleId, row.TagId });
            entity.HasOne<ArticleRow>().WithMany().HasForeignKey(row => row.ArticleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<TagRow>().WithMany().HasForeignKey(row => row.TagId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(row => new { row.TagId, row.ArticleId });
        });
    }
}
/// <summary>共有されるタグ定義。孤立しても再利用用に保持する。</summary>
internal sealed class TagRow
{
    /// <summary>永続タグ ID。</summary>
    public Guid Id { get; set; }
    /// <summary>最初に登録した表記。</summary>
    public string Name { get; set; } = "";
    /// <summary>NFC・大文字化した比較キー。SQLite NOCASE の ASCII 制限へ依存しない。</summary>
    public string NormalizedName { get; set; } = "";
}
/// <summary>記事と手動タグの多対多関連。同一記事の重複付与は複合主キーで防ぐ。</summary>
internal sealed class ArticleTagRow
{
    /// <summary>整理対象の記事 ID。</summary>
    public Guid ArticleId { get; set; }
    /// <summary>共有タグ ID。</summary>
    public Guid TagId { get; set; }
}
/// <summary>Worker が管理する巡回予定と取得中の復旧記録。</summary>
internal sealed class WorkerSourceStateRow
{
    /// <summary>情報源 ID。</summary>
    public Guid Id { get; set; }
    /// <summary>SourceState の JSON。</summary>
    public string Payload { get; set; } = "";
}
