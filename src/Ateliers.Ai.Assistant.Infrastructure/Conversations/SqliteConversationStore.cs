using Ateliers.Ai.Assistant.Application.Conversations;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Core.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ateliers.Ai.Assistant.Infrastructure.Conversations;

/// <summary>スナップショットを1トランザクションで保存する EF Core SQLite 実装。</summary>
/// <remarks>未作成 DB の一覧取得は空一覧。最初の保存時だけ Migration と必要フォルダーを作成する。</remarks>
public sealed class SqliteConversationStore(IApplicationPaths paths, IAppDiagnostics diagnostics) : IConversationStore
{
    private readonly SemaphoreSlim _gate = AssistantDatabase.Gate(paths);
    private bool _databaseSeen;

    /// <inheritdoc/>
    public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync<IReadOnlyList<ConversationSummary>>(async db =>
        {
            if (db is null) return Array.Empty<ConversationSummary>();
            var rows = await db.Threads.AsNoTracking().OrderByDescending(row => row.UpdatedUtcTicks).ThenBy(row => row.Id)
                .Select(row => new { row.Id, row.Title, row.UpdatedUtcTicks, row.SelectedModel }).ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Select(row => new ConversationSummary(row.Id, row.Title, Utc(row.UpdatedUtcTicks), row.SelectedModel)).ToArray();
        }, create: false, cancellationToken);

    /// <inheritdoc/>
    public Task<ConversationSnapshot?> LoadAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteAsync<ConversationSnapshot?>(async db =>
        {
            if (db is null) return null;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Threads.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
            if (row is null) return null;
            var messages = await db.Messages.AsNoTracking().Where(message => message.ThreadId == id).OrderBy(message => message.Position).ToListAsync(cancellationToken).ConfigureAwait(false);
            var images = await db.Images.AsNoTracking().Where(image => image.ThreadId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var restored = messages.Select(message => new ConversationMessage(message.Id,
                new ChatMessage((ChatRole)message.Role, message.Content, Image(images.SingleOrDefault(image => image.MessageId == message.Id))),
                message.Model, message.StatusText, Utc(message.CreatedUtcTicks), message.IncludedInHistory)).ToArray();
            var snapshot = new ConversationSnapshot(row.Id, row.Title, Utc(row.CreatedUtcTicks), Utc(row.UpdatedUtcTicks), row.SelectedModel,
                restored, row.DraftText, Image(images.SingleOrDefault(image => image.MessageId is null)), row.Version);
            Validate(snapshot);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }, create: false, cancellationToken);

    /// <inheritdoc/>
    public Task<ConversationSnapshot> SaveAsync(ConversationSnapshot conversation, CancellationToken cancellationToken = default)
    {
        Validate(conversation);
        return ExecuteAsync(async context =>
        {
            var db = context ?? throw new StorageException("保存済みの会話 DB が見つかりません。現在の内容を維持しました。");
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Threads.SingleOrDefaultAsync(row => row.Id == conversation.Id, cancellationToken).ConfigureAwait(false);
            if ((row is null && conversation.Version != 0) || (row is not null && row.Version != conversation.Version))
                throw new StorageException("別の操作で会話が変更・削除されています。現在の会話を維持しました。他のウィンドウの操作を確認してください。");
            if (row is null)
            {
                row = new ThreadRow { Id = conversation.Id, CreatedUtcTicks = conversation.CreatedAt.UtcTicks };
                db.Threads.Add(row);
            }
            row.Title = conversation.Title.Trim();
            row.UpdatedUtcTicks = conversation.UpdatedAt.UtcTicks;
            row.SelectedModel = conversation.SelectedModel;
            row.DraftText = conversation.DraftText;
            row.Version = checked(conversation.Version + 1);
            // 画像とメッセージは同じトランザクション内で置換し、途中失敗時は旧会話を保つ。
            await db.Images.Where(image => image.ThreadId == conversation.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Messages.Where(message => message.ThreadId == conversation.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            for (var position = 0; position < conversation.Messages.Count; position++)
            {
                var message = conversation.Messages[position];
                db.Messages.Add(new MessageRow { Id = message.Id, ThreadId = row.Id, Position = position,
                    Role = (int)message.Message.Role, Content = message.Message.Content, Model = message.Model,
                    StatusText = message.StatusText, CreatedUtcTicks = message.CreatedAt.UtcTicks, IncludedInHistory = message.IncludedInHistory });
                if (message.Message.Image is { } image) db.Images.Add(Image(row.Id, message.Id, image));
            }
            if (conversation.DraftImage is { } draft) db.Images.Add(Image(row.Id, null, draft));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return conversation with { Title = row.Title, Version = row.Version };
        }, create: conversation.Version == 0, cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => ExecuteAsync(async db =>
    {
        if (db is not null) await db.Threads.Where(row => row.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }, create: false, cancellationToken);

    /// <summary>アクセスを直列化し、既存 DB には Migration を適用する。失敗時に DB を作り直さない。</summary>
    private async Task<T> ExecuteAsync<T>(Func<AssistantDbContext?, Task<T>> operation, bool create, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Microsoft.Data.Sqlite の非同期 API は内部で同期 I/O を行うため、UI スレッドから切り離す。
            return await Task.Run(async () =>
            {
                var exists = DatabaseExists();
                if (!exists && _databaseSeen) throw new StorageException("使用中の会話 DB が削除・移動されています。現在の会話を維持しました。");
                if (!create && !exists) return await operation(null).ConfigureAwait(false);
                if (create) Directory.CreateDirectory(paths.DatabaseDirectory);
                var connection = new SqliteConnectionStringBuilder { DataSource = paths.DatabaseFile,
                    Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, ForeignKeys = true, Pooling = false, DefaultTimeout = 3 };
                await using var db = new AssistantDbContext(new DbContextOptionsBuilder<AssistantDbContext>().UseSqlite(connection.ToString()).Options);
                await db.Database.MigrateAsync(token).ConfigureAwait(false);
                _databaseSeen = true;
                return await operation(db).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            diagnostics.Error("conversation.storage", exception);
            if (exception is StorageException) throw;
            throw new StorageException("会話データを読み書きできませんでした。DB の使用中・破損・アクセス権・空き容量を確認してください。", exception);
        }
        finally { _gate.Release(); }
    }

    /// <summary>未作成とアクセス拒否を区別する。File.Exists による失敗の隠蔽を避ける。</summary>
    private bool DatabaseExists()
    {
        try
        {
            if ((File.GetAttributes(paths.DatabaseFile) & FileAttributes.Directory) != 0)
                throw new StorageException("会話 DB のファイル位置にフォルダーが存在します。保存先を確認してください。");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <summary>UTC ticks をタイムゾーンに依存しない日時へ復元する。</summary>
    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);
    /// <summary>画像行を UI 非依存の添付へ復元する。</summary>
    private static ChatImage? Image(ImageRow? row) => row is null ? null : new ChatImage(row.PngBytes, row.Width, row.Height);
    /// <summary>正規化済みの PNG バイト列を画像行へ変換する。</summary>
    private static ImageRow Image(Guid thread, Guid? message, ChatImage image) => new() {
        Id = Guid.NewGuid(), ThreadId = thread, MessageId = message, PngBytes = image.GetPngBytes(), Width = image.Width, Height = image.Height };
    /// <summary>空スレッド・不正な識別子・役割・画像サイズを DB 境界で拒否する。</summary>
    private static void Validate(ConversationSnapshot snapshot)
    {
        if (snapshot.Id == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.Title) || snapshot.Title.Trim().Length > 120 ||
            snapshot.Version == 0 && snapshot.Messages.Count == 0 && string.IsNullOrWhiteSpace(snapshot.DraftText) && snapshot.DraftImage is null ||
            snapshot.Messages.Select(message => message.Id).Distinct().Count() != snapshot.Messages.Count ||
            snapshot.Messages.Any(message => message.Id == Guid.Empty || !Enum.IsDefined(message.Message.Role)))
            throw new StorageException("空の会話、120文字を超えるタイトル、または不正な会話データは保存・復元できません。");
        foreach (var image in snapshot.Messages.Select(message => message.Message.Image).Append(snapshot.DraftImage).OfType<ChatImage>())
            if (Math.Max(image.Width, image.Height) > 1536) throw new StorageException("会話画像の長辺は1536px以下にしてください。");
    }
}
