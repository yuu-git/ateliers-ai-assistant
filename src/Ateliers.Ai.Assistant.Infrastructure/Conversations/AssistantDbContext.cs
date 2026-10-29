using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Ateliers.Ai.Assistant.Infrastructure.Conversations;

/// <summary>会話・表示メッセージ・画像 BLOB の関係を定義する。UI オブジェクトは保存しない。</summary>
public sealed class AssistantDbContext(DbContextOptions<AssistantDbContext> options) : DbContext(options)
{
    /// <summary>明示保存された会話。</summary>
    internal DbSet<ThreadRow> Threads => Set<ThreadRow>();
    /// <summary>会話に属する順序付きの表示メッセージ。</summary>
    internal DbSet<MessageRow> Messages => Set<MessageRow>();
    /// <summary>メッセージまたは下書きの PNG BLOB。</summary>
    internal DbSet<ImageRow> Images => Set<ImageRow>();

    /// <summary>外部キー、順序、1メッセージ1画像と楽観的同時実行を設定する。</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        WebMonitoring.WebSchema.Configure(modelBuilder);
        modelBuilder.Entity<ThreadRow>(entity =>
        {
            entity.ToTable("Conversations");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Title).IsRequired().HasMaxLength(120);
            entity.Property(row => row.DraftText).IsRequired();
            entity.Property(row => row.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<MessageRow>(entity =>
        {
            entity.ToTable("Messages");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.ThreadId, row.Position }).IsUnique();
            entity.HasOne<ThreadRow>().WithMany().HasForeignKey(row => row.ThreadId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(row => row.Content).IsRequired();
        });
        modelBuilder.Entity<ImageRow>(entity =>
        {
            entity.ToTable("Images");
            entity.HasKey(row => row.Id);
            entity.HasOne<ThreadRow>().WithMany().HasForeignKey(row => row.ThreadId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<MessageRow>().WithOne().HasForeignKey<ImageRow>(row => row.MessageId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(row => row.MessageId).IsUnique().HasFilter("MessageId IS NOT NULL");
            entity.HasIndex(row => row.ThreadId).IsUnique().HasFilter("MessageId IS NULL");
            entity.Property(row => row.PngBytes).IsRequired();
        });
    }
}

/// <summary>会話の識別・更新版・下書き。日時は SQLite でソート可能な UTC ticks。</summary>
internal sealed class ThreadRow
{
    /// <summary>会話識別子。</summary>
    public Guid Id { get; set; }
    /// <summary>利用者指定の見出し。</summary>
    public string Title { get; set; } = "";
    /// <summary>会話作成日時の UTC ticks。</summary>
    public long CreatedUtcTicks { get; set; }
    /// <summary>最終保存日時の UTC ticks。</summary>
    public long UpdatedUtcTicks { get; set; }
    /// <summary>再開時に選択するモデル。未選択なら null。</summary>
    public string? SelectedModel { get; set; }
    /// <summary>未送信の入力文。</summary>
    public string DraftText { get; set; } = "";
    /// <summary>保存の競合を検出する版番号。</summary>
    public long Version { get; set; }
}

/// <summary>表示内容と次回送信への採否を持つメッセージ行。</summary>
internal sealed class MessageRow
{
    /// <summary>メッセージ識別子。</summary>
    public Guid Id { get; set; }
    /// <summary>所属する会話。</summary>
    public Guid ThreadId { get; set; }
    /// <summary>会話内の0始まり表示順。</summary>
    public int Position { get; set; }
    /// <summary>Core の ChatRole の数値。</summary>
    public int Role { get; set; }
    /// <summary>Markdown 原文。</summary>
    public string Content { get; set; } = "";
    /// <summary>送信時のモデル。</summary>
    public string Model { get; set; } = "";
    /// <summary>表示用の完了・中止・失敗状態。</summary>
    public string StatusText { get; set; } = "";
    /// <summary>作成日時の UTC ticks。</summary>
    public long CreatedUtcTicks { get; set; }
    /// <summary>完了したターンとして次回送信に含めるか。</summary>
    public bool IncludedInHistory { get; set; }
}

/// <summary>1メッセージ1枚、または会話の下書き1枚に対応する正規化済み PNG。</summary>
internal sealed class ImageRow
{
    /// <summary>画像行の識別子。</summary>
    public Guid Id { get; set; }
    /// <summary>所属会話。会話削除で必ず削除する。</summary>
    public Guid ThreadId { get; set; }
    /// <summary>所属メッセージ。null は会話の下書き画像。</summary>
    public Guid? MessageId { get; set; }
    /// <summary>Base64 化せず保存する PNG バイト列。</summary>
    public byte[] PngBytes { get; set; } = Array.Empty<byte>();
    /// <summary>画像幅。</summary>
    public int Width { get; set; }
    /// <summary>画像高。</summary>
    public int Height { get; set; }
}

/// <summary>Migration 作成専用のコンテキスト構成。構成・生成だけでは DB やフォルダーを作らない。</summary>
public sealed class AssistantDbContextFactory : IDesignTimeDbContextFactory<AssistantDbContext>
{
    /// <summary>通常保存先の接続設定を返す。ツールによる database update は明示操作時だけ実行する。</summary>
    public AssistantDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AssistantDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = new AppDataPaths().DatabaseFile, ForeignKeys = true }.ToString()).Options);
}
