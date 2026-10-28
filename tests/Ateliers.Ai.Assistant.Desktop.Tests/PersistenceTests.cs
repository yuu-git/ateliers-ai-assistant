using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Ateliers.Ai.Assistant.Application.Chat;
using Ateliers.Ai.Assistant.Application.Conversations;
using Ateliers.Ai.Assistant.Application.Settings;
using Ateliers.Ai.Assistant.Application.Storage;
using Ateliers.Ai.Assistant.Core.Chat;
using Ateliers.Ai.Assistant.Desktop.ViewModels;
using Ateliers.Ai.Assistant.Infrastructure.Conversations;
using Ateliers.Ai.Assistant.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Ateliers.Ai.Assistant.Desktop.Tests;

/// <summary>利用者のデータを使わず、一時ディレクトリの実 SQLite と ViewModel を検証する。</summary>
internal static class PersistenceTests
{
    /// <summary>同じ基準フォルダーでも環境別に分離され、Debug の保存・削除が Release データを変更しない。</summary>
    public static async Task DebugAndReleaseIsolation()
    {
        using var fixture = new Fixture();
        var roaming = Path.GetDirectoryName(Path.GetDirectoryName(fixture.Paths.SettingsDirectory))!;
        var local = Path.GetDirectoryName(Path.GetDirectoryName(fixture.Paths.LocalDirectory))!;
        var release = new AppDataPaths(roaming, local, AppDataEnvironment.Release);
        var debug = new AppDataPaths(roaming, local, AppDataEnvironment.Debug);
        Check(release.LocalDirectory == Path.Combine(local, "Ateliers-Dev", "Ai-Assistant") &&
            debug.LocalDirectory == Path.Combine(local, "Ateliers-Dev", "Ai-Assistant.Debug"), "fixed environment roots");
#if DEBUG
        Check(fixture.Paths.DataEnvironment == AppDataEnvironment.Debug, "Debug default build environment");
#else
        Check(fixture.Paths.DataEnvironment == AppDataEnvironment.Release, "Release default build environment");
#endif
        var releaseLog = new FileDiagnosticLog(release);
        var releaseStore = new SqliteConversationStore(release, releaseLog);
        var releaseSettings = new JsonUserSettingsStore(release, releaseLog);
        var snapshot = Snapshot() with { Title = "Release の既存データ" };
        await releaseStore.SaveAsync(snapshot);
        await releaseSettings.SaveAsync(new UserSettings("release-model"));
        releaseLog.Error("test.release", new IOException());
        var releaseFiles = Directory.GetFiles(release.LocalDirectory, "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(release.SettingsDirectory, "*", SearchOption.AllDirectories))
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var debugLog = new FileDiagnosticLog(debug);
        var debugStore = new SqliteConversationStore(debug, debugLog);
        var debugSettings = new JsonUserSettingsStore(debug, debugLog);
        Check((await debugStore.ListAsync()).Count == 0 && (await debugSettings.LoadAsync()).LastSelectedModel is null,
            "Debug starts empty rather than importing Release data");
        Check(!Directory.Exists(debug.LocalDirectory) && !Directory.Exists(debug.SettingsDirectory), "path resolution does not create Debug folders");
        await debugStore.SaveAsync(snapshot with { Title = "Debug だけのデータ" });
        await debugSettings.SaveAsync(new UserSettings("debug-model"));
        debugLog.Error("test.debug", new IOException());
        await debugStore.DeleteAsync(snapshot.Id);
        Check((await releaseStore.LoadAsync(snapshot.Id))!.Title == "Release の既存データ" &&
            (await releaseSettings.LoadAsync()).LastSelectedModel == "release-model", "Debug deletion cannot affect Release content");
        Check(releaseFiles.All(file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Key))) == file.Value), "existing Release files remain byte-identical");
        Check(debug.CacheDirectory != release.CacheDirectory && debug.TempDirectory != release.TempDirectory, "cache and temporary paths also separated");
        var settingsVm = new SettingsViewModel(debug, debugSettings, debugLog, _ => Task.CompletedTask);
        Check(settingsVm.DataEnvironmentText == "Debug（開発用データ）", "visible environment label");
    }

    /// <summary>パス表示・未保存会話・設定読込だけでは不要なデータを生成しない。</summary>
    public static async Task PathsAndLazyCreation()
    {
        using var fixture = new Fixture();
        var applicationDirectory = fixture.Paths.DataEnvironment == AppDataEnvironment.Debug ? "Ai-Assistant.Debug" : "Ai-Assistant";
        Check(fixture.Paths.DatabaseFile.EndsWith(Path.Combine("Ateliers-Dev", applicationDirectory, "data", "assistant.db")), "fixed DB path");
        Check((await fixture.Store.ListAsync()).Count == 0, "first empty list");
        var settings = new JsonUserSettingsStore(fixture.Paths, fixture.Log);
        Check((await settings.LoadAsync()).LastSelectedModel is null, "settings defaults");
        using var vm = await fixture.ViewModel();
        vm.SelectedModel = "preview"; vm.InputText = "保存しない会話";
        await vm.SendCommand.ExecuteAsync();
        Check(!File.Exists(fixture.Paths.DatabaseFile) && !Directory.Exists(fixture.Paths.CacheDirectory) &&
            !Directory.Exists(fixture.Paths.TempDirectory) && !Directory.Exists(fixture.Paths.SettingsDirectory), "lazy directories");
        vm.InputText = "";
        Check(vm.HasUnsavedConversationChanges, "memory only conversation");
        vm.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Discard);
        await vm.StartNewConversationAsync();
        Check(vm.Messages.Count == 0 && !File.Exists(fixture.Paths.DatabaseFile), "new conversation must not create empty DB");
    }

    /// <summary>Migration・BLOB・下書き・日時・版番号と関連データのカスケード削除を確認する。</summary>
    public static async Task SqliteRoundTripAndCascade()
    {
        using var fixture = new Fixture();
        var original = Snapshot();
        var saved = await fixture.Store.SaveAsync(original);
        Check(saved.Version == 1, "new version");
        var restarted = new SqliteConversationStore(fixture.Paths, fixture.Log);
        var loaded = (await restarted.LoadAsync(saved.Id))!;
        Check(loaded.Title == original.Title && loaded.Messages[0].Message.Content == original.Messages[0].Message.Content &&
            loaded.Messages[0].CreatedAt == original.Messages[0].CreatedAt && loaded.DraftText == original.DraftText, "raw Markdown, dates, draft");
        Check(loaded.Messages[0].Message.Image!.Base64Data == original.Messages[0].Message.Image!.Base64Data && loaded.DraftImage is not null, "image restore");
        using (var connection = new SqliteConnection($"Data Source={fixture.Paths.DatabaseFile};Pooling=False"))
        {
            connection.Open();
            Check(Scalar(connection, "SELECT COUNT(*) FROM __EFMigrationsHistory") == 1, "migration history");
            Check(Scalar(connection, "SELECT COUNT(*) FROM Images WHERE typeof(PngBytes)='blob'") == 2, "PNG BLOB storage");
        }
        var changed = await restarted.SaveAsync(loaded with { Title = "名前を変更", DraftText = "", DraftImage = null });
        Check(changed.Version == 2 && (await restarted.ListAsync())[0].Title == "名前を変更", "update and rename");
        await ThrowsStorage(() => fixture.Store.SaveAsync(loaded), "optimistic conflict");
        Check((await restarted.LoadAsync(saved.Id))!.Title == "名前を変更", "conflict preserves current DB");
        await restarted.DeleteAsync(saved.Id);
        using var check = new SqliteConnection($"Data Source={fixture.Paths.DatabaseFile};Pooling=False"); check.Open();
        Check(Scalar(check, "SELECT COUNT(*) FROM Conversations") == 0 && Scalar(check, "SELECT COUNT(*) FROM Messages") == 0 && Scalar(check, "SELECT COUNT(*) FROM Images") == 0, "cascade deletes all child data");
    }

    /// <summary>復元後も失敗ターンを次回要求から除外し、完了画像と下書きを保持する。</summary>
    public static async Task RestoreAndFollowUp()
    {
        using var fixture = new Fixture();
        var chat = new RecordingChat();
        using (var vm = await fixture.ViewModel(chat))
        {
            vm.SelectedModel = "preview";
            vm.InputText = "画像です"; vm.TryBeginImagePaste(); vm.CompleteImagePaste(Png());
            await vm.SendCommand.ExecuteAsync();
            chat.Fail = true; vm.InputText = "失敗する入力"; await vm.SendCommand.ExecuteAsync();
            vm.InputText = "復元する下書き"; vm.TryBeginImagePaste(); vm.CompleteImagePaste(Png());
            await vm.SaveConversationCommand.ExecuteAsync();
            Check(!vm.HasUnsavedConversationChanges && vm.Messages.Count == 4, "explicit save");
        }
        chat.Fail = false;
        using var restored = await fixture.ViewModel(chat);
        restored.SelectedSavedConversation = restored.SavedConversations.Single();
        await restored.OpenConversationCommand.ExecuteAsync();
        Check(restored.InputText == "復元する下書き" && restored.PendingImage is not null && restored.Messages.Count == 4 &&
            restored.Messages[2].StatusText.Contains("失敗") && restored.Messages[2].IncludedInHistory == false, "restore all display state");
        await restored.SendCommand.ExecuteAsync();
        var request = chat.Requests[^1];
        Check(request.Count == 3 && request[0].Image is not null && request[^1].Image is not null && request.All(message => message.Content != "失敗する入力"), "restored successful history and image resend");
        var persisted = (await fixture.Store.LoadAsync(restored.SavedConversations.Single().Id))!;
        Check(persisted.Messages.Count == 6 && persisted.DraftImage is null && persisted.DraftText == "", "automatic final-turn save");
    }

    /// <summary>会話切替・終了の保存／破棄／キャンセル、下書きだけの保存を確認する。</summary>
    public static async Task LeaveChoicesAndDrafts()
    {
        using var fixture = new Fixture();
        using var vm = await fixture.ViewModel();
        vm.InputText = "下書きだけ"; vm.TryBeginImagePaste(); vm.CompleteImagePaste(Png());
        vm.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Cancel);
        Check(!await vm.PrepareToLeaveConversationAsync() && vm.InputText == "下書きだけ" && !File.Exists(fixture.Paths.DatabaseFile), "cancel preserves memory");
        vm.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Save);
        vm.RequestConversationTitleAsync = _ => Task.FromResult<string?>(null);
        Check(!await vm.PrepareToLeaveConversationAsync() && !File.Exists(fixture.Paths.DatabaseFile), "save dialog cancellation");
        vm.RequestConversationTitleAsync = _ => Task.FromResult<string?>("下書き会話");
        Check(await vm.PrepareToLeaveConversationAsync(), "save on exit");
        var draft = (await fixture.Store.LoadAsync(vm.SavedConversations.Single().Id))!;
        Check(draft.Messages.Count == 0 && draft.DraftImage is not null && draft.DraftText == "下書きだけ", "draft-only is meaningful content");
        vm.InputText = ""; vm.RemoveImageCommand.Execute(null);
        await Wait(() => !vm.HasUnsavedConversationChanges, "empty existing draft autosave");
        Check((await fixture.Store.ListAsync()).Count == 1, "no extra empty threads");
        vm.RequestConversationTitleAsync = _ => Task.FromResult<string?>("空の既存会話の名前変更");
        await vm.SaveConversationCommand.ExecuteAsync();
        Check((await fixture.Store.ListAsync()).Single().Title == "空の既存会話の名前変更", "rename existing empty draft thread");
        await vm.StartNewConversationAsync();
        vm.InputText = "破棄する下書き";
        vm.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Discard);
        Check(await vm.PrepareToLeaveConversationAsync() && vm.InputText == "" && !vm.HasUnsavedConversationChanges, "discard clears draft");
        Check((await fixture.Store.ListAsync()).Count == 1, "discard does not persist new conversation");
    }

    /// <summary>自動保存失敗時の未保存表示と、キャンセル後のメモリ維持・再試行を確認する。</summary>
    public static async Task SaveFailuresAndLateEdits()
    {
        using var fixture = new Fixture();
        var failing = new SwitchableStore(fixture.Store);
        using var vm = await fixture.ViewModel(store: failing);
        vm.InputText = "最初"; await vm.SaveConversationCommand.ExecuteAsync();
        failing.Fail = true; vm.InputText = "ディスクに保存できない編集";
        await Wait(() => vm.HasPersistenceError, "auto-save failure");
        Check(vm.HasUnsavedConversationChanges && vm.ConversationStorageStatus.Contains("未保存"), "must not falsely claim saved");
        vm.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Cancel);
        Check(!await vm.PrepareToLeaveConversationAsync() && vm.InputText == "ディスクに保存できない編集", "failure and cancel preserve memory");
        failing.Fail = false;
        await vm.SaveConversationCommand.ExecuteAsync();
        Check(!vm.HasUnsavedConversationChanges, "manual retry");
        failing.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.InputText = "保存を開始する内容";
        var saving = vm.SaveConversationCommand.ExecuteAsync();
        await failing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.InputText = "保存開始後の新しい入力";
        failing.Block.SetResult(true); await saving;
        Check(vm.HasUnsavedConversationChanges, "old save does not cover newer edit");
        await Wait(() => !vm.HasUnsavedConversationChanges, "newest draft eventually saved");
        Check((await fixture.Store.LoadAsync(vm.SavedConversations.Single().Id))!.DraftText == "保存開始後の新しい入力", "latest draft persisted");
        vm.SelectedSavedConversation = vm.SavedConversations.Single();
        failing.LoadBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.InputText = "読込前の変更";
        var opening = vm.OpenConversationCommand.ExecuteAsync();
        await failing.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(700); // 読込済みデータを待つ間に、旧会話の遅延保存の期限を越える。
        failing.LoadBlock.SetResult(true); await opening;
        vm.InputText = "切替後の新しい変更"; await vm.SaveConversationCommand.ExecuteAsync();
        Check(!vm.HasUnsavedConversationChanges && !vm.HasPersistenceError, "stale auto-save must not invalidate reopened conversation version");
    }

    /// <summary>中止を待って終了確認し、復元時のモデル不存在と削除確認を扱う。</summary>
    public static async Task CancelCloseMissingModelAndDelete()
    {
        using var fixture = new Fixture();
        var chat = new RecordingChat { Slow = true };
        using var vm = await fixture.ViewModel(chat);
        vm.SelectedModel = "preview"; vm.InputText = "中止する質問";
        var send = vm.SendCommand.ExecuteAsync();
        await Wait(() => vm.Messages.Count == 2 && vm.Messages[1].Content.Length != 0, "partial reply");
        vm.ConfirmLeaveAsync = () => Task.FromResult(ConversationLeaveDecision.Save);
        Check(await vm.PrepareToLeaveConversationAsync(), "stop before close save"); await send;
        var saved = (await fixture.Store.LoadAsync(vm.SavedConversations.Single().Id))!;
        Check(saved.Messages.All(message => !message.IncludedInHistory) && saved.Messages[1].StatusText.Contains("中止"), "cancel state saved");
        await fixture.Store.SaveAsync(saved with { SelectedModel = "missing-model" });
        await vm.StartNewConversationAsync(); await vm.RefreshSavedConversationsAsync();
        vm.SelectedSavedConversation = vm.SavedConversations.Single(); await vm.OpenConversationCommand.ExecuteAsync();
        Check(vm.SelectedModel is null && vm.HasError, "missing saved model requires selection");
        vm.ConfirmDeleteConversationAsync = _ => Task.FromResult(false);
        await vm.DeleteConversationCommand.ExecuteAsync(); Check(vm.SavedConversations.Count == 1, "delete canceled");
        vm.ConfirmDeleteConversationAsync = _ => Task.FromResult(true);
        await vm.DeleteConversationCommand.ExecuteAsync(); Check(vm.SavedConversations.Count == 0 && vm.Messages.Count == 0, "delete current thread");
    }

    /// <summary>破損・ロック・DB 消失・Migration 失敗を通知し、破壊的な再初期化をしない。</summary>
    public static async Task DatabaseFailures()
    {
        using var fixture = new Fixture();
        var saved = await fixture.Store.SaveAsync(Snapshot());
        using (var connection = new SqliteConnection($"Data Source={fixture.Paths.DatabaseFile};Pooling=False"))
        {
            connection.Open(); using var transaction = connection.BeginTransaction();
            await ThrowsStorage(() => fixture.Store.SaveAsync(saved with { Title = "locked" }), "database locked");
        }
        Check((await fixture.Store.LoadAsync(saved.Id))!.Title == saved.Title, "lock failure preserves DB");
        File.Delete(fixture.Paths.DatabaseFile);
        await ThrowsStorage(() => fixture.Store.ListAsync(), "database disappeared");
        Check(!File.Exists(fixture.Paths.DatabaseFile), "must not recreate missing known DB");
        File.WriteAllBytes(fixture.Paths.DatabaseFile, new byte[] { 1, 2, 3, 4, 5 });
        await ThrowsStorage(() => new SqliteConversationStore(fixture.Paths, fixture.Log).ListAsync(), "corrupt database");
        File.Delete(fixture.Paths.DatabaseFile);
        using (var connection = new SqliteConnection($"Data Source={fixture.Paths.DatabaseFile};Pooling=False"))
        { connection.Open(); Execute(connection, "CREATE TABLE Conversations (Sentinel TEXT); INSERT INTO Conversations VALUES ('keep');"); }
        await ThrowsStorage(() => new SqliteConversationStore(fixture.Paths, fixture.Log).ListAsync(), "migration failure");
        using var preserved = new SqliteConnection($"Data Source={fixture.Paths.DatabaseFile};Pooling=False"); preserved.Open();
        Check(Scalar(preserved, "SELECT COUNT(*) FROM Conversations WHERE Sentinel='keep'") == 1, "migration must not drop existing data");
        using var invalidPath = new Fixture();
        Directory.CreateDirectory(invalidPath.Paths.DatabaseFile);
        await ThrowsStorage(() => invalidPath.Store.ListAsync(), "directory at DB file path must not look like empty history");
    }

    /// <summary>設定の初期値・復元・不正 JSON の保護・書込失敗と固定フォルダー操作を確認する。</summary>
    public static async Task SettingsAndFolders()
    {
        using var fixture = new Fixture();
        var store = new JsonUserSettingsStore(fixture.Paths, fixture.Log);
        var opened = new List<DataFolderKind>();
        var settings = new SettingsViewModel(fixture.Paths, store, fixture.Log, kind => { opened.Add(kind); return Task.CompletedTask; });
        string? restored = null; settings.ApplySelectedModelAsync = value => { restored = value; return Task.CompletedTask; };
        await settings.InitializeAsync();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => settings.RememberModelAsync("model-" + i)));
        await settings.InitializeAsync(); Check(restored == "model-19", "last model setting wins");
        await settings.OpenDatabaseCommand.ExecuteAsync(); await settings.OpenSettingsCommand.ExecuteAsync();
        Check(opened.SequenceEqual(new[] { DataFolderKind.Database, DataFolderKind.Settings }), "only fixed enum paths reach Explorer boundary");
        File.WriteAllText(fixture.Paths.SettingsFile, "{broken");
        await settings.InitializeAsync(); Check(settings.HasError, "invalid settings error");
        await settings.RememberModelAsync("must-not-overwrite");
        Check(File.ReadAllText(fixture.Paths.SettingsFile) == "{broken", "invalid settings protected");
        File.Delete(fixture.Paths.SettingsFile); await settings.InitializeAsync();
        Directory.CreateDirectory(fixture.Paths.SettingsFile); // ファイル位置がフォルダーなら書き込みは失敗する。
        await settings.RememberModelAsync("unwritable"); Check(settings.HasError, "settings write failure");
        var denied = new SettingsViewModel(fixture.Paths, store, fixture.Log, _ => throw new UnauthorizedAccessException());
        await denied.OpenLogsCommand.ExecuteAsync(); Check(denied.HasError, "Explorer failure is nonfatal");
    }

    /// <summary>ログは内容を漏らさず、最大3ファイルへローテーションする。</summary>
    public static Task PrivateBoundedLogs()
    {
        using var fixture = new Fixture();
        fixture.Log.Error("test.failure", new InvalidOperationException("SECRET prompt/image/token"));
        var file = Path.Combine(fixture.Paths.LogsDirectory, "assistant.log");
        Check(!File.ReadAllText(file).Contains("SECRET"), "no exception text in persistent log");
        for (var i = 0; i < 5; i++)
        {
            using (var stream = File.OpenWrite(file)) stream.SetLength(FileDiagnosticLog.MaximumFileBytes);
            fixture.Log.Error("test.rotation", new IOException());
        }
        Check(Directory.GetFiles(fixture.Paths.LogsDirectory).Length == 3, "bounded file count");
        using var blocked = new Fixture();
        Directory.CreateDirectory(blocked.Paths.LocalDirectory);
        File.WriteAllText(blocked.Paths.LogsDirectory, "not a directory");
        blocked.Log.Error("test.log.unavailable", new IOException()); // ログ自体が書けなくても例外を外へ出さない。
        return Task.CompletedTask;
    }

    private static ConversationSnapshot Snapshot() => new(Guid.NewGuid(), "保存テスト", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "preview",
        new[] { new ConversationMessage(Guid.NewGuid(), new ChatMessage(ChatRole.User, "# 原文\n\n**太字**", Png()), "preview", "", DateTimeOffset.UtcNow, true),
            new ConversationMessage(Guid.NewGuid(), new ChatMessage(ChatRole.Assistant, "```cs\nvar x = 1;\n```"), "preview", "完了", DateTimeOffset.UtcNow, true) }, "送信前の下書き", Png());
    private static ChatImage Png() => new(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5VQAAAAASUVORK5CYII="), 1, 1);
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static async Task ThrowsStorage(Func<Task> action, string label) { try { await action(); } catch (StorageException) { return; } throw new InvalidOperationException("Expected StorageException: " + label); }
    private static long Scalar(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static void Execute(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static async Task Wait(Func<bool> condition, string label) { for (var i = 0; i < 150; i++) { if (condition()) return; await Task.Delay(50); } throw new InvalidOperationException("Timed out: " + label); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ateliers-phase2-tests-" + Guid.NewGuid().ToString("N"));
        public AppDataPaths Paths { get; }
        public FileDiagnosticLog Log { get; }
        public SqliteConversationStore Store { get; }
        public Fixture() { Paths = new(Path.Combine(_root, "Roaming"), Path.Combine(_root, "Local")); Log = new(Paths); Store = new(Paths, Log); }
        public async Task<ChatViewModel> ViewModel(RecordingChat? chat = null, IConversationStore? store = null)
        {
            var vm = new ChatViewModel(chat ?? new RecordingChat(), conversationStore: store ?? Store, diagnostics: Log)
            { RequestConversationTitleAsync = _ => Task.FromResult<string?>("検証用会話") };
            await vm.InitializeAsync(); await vm.RefreshSavedConversationsAsync(); return vm;
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(_root);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(full).StartsWith("ateliers-phase2-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected temporary directory.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private sealed class RecordingChat : IChatService
    {
        public bool Fail { get; set; }
        public bool Slow { get; set; }
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = new();
        public Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(new[] { "preview" });
        public async IAsyncEnumerable<string> StreamReplyAsync(string model, IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Requests.Add(messages); yield return "## 応答\n"; if (Slow) await Task.Delay(Timeout.Infinite, cancellationToken); await Task.Delay(10, cancellationToken); if (Fail) throw new ChatServiceException("テスト失敗"); yield return "完了"; }
    }
    private sealed class SwitchableStore(IConversationStore inner) : IConversationStore
    {
        public bool Fail { get; set; }
        public TaskCompletionSource<bool>? Block { get; set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>? LoadBlock { get; set; }
        public TaskCompletionSource<bool> LoadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public async Task<ConversationSnapshot?> LoadAsync(Guid id, CancellationToken cancellationToken = default)
        { var snapshot = await inner.LoadAsync(id, cancellationToken); if (LoadBlock is { } block) { LoadEntered.TrySetResult(true); await block.Task; } return snapshot; }
        public async Task<ConversationSnapshot> SaveAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken = default)
        { if (Fail) throw new StorageException("テスト書込失敗"); if (Block is { } block) { Entered.TrySetResult(true); await block.Task; } return await inner.SaveAsync(snapshot, cancellationToken); }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
    }
}
