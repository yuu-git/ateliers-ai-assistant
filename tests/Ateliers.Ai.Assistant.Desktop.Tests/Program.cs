using Ateliers.Ai.Assistant.Desktop.Tests;

if (args.Length == 2 && args[0] == "--ollama-model")
{
    await ServiceTests.LiveConversation(args[1]);
    return;
}
var tests = new (string Name, Func<Task> Run)[]
{
    ("RSS/Atom 正規化・空フィード", WebMonitoringTests.Parsing),
    ("通知の公開日時順・日時不明・最新100件", WebMonitoringTests.NotificationOrdering),
    ("通知の記事別要約モデル・復元・モデル変更後の維持", WebMonitoringTests.NotificationModelHistory),
    ("最新10件の選択・範囲外の再試行保留", WebMonitoringTests.LatestTen),
    ("複数記事削除・単一削除・新規再取得", WebMonitoringTests.BulkDeleteAndRestore),
    ("削除の部分失敗・欠損ファイル・再試行", WebMonitoringTests.DeleteFailures),
    ("削除確認キャンセル・プレビュー消去・再取得", WebMonitoringTests.DeleteViewModel),
    ("記事識別・URL 安全性・追跡パラメーター", WebMonitoringTests.IdentityAndUrls),
    ("XML/HTTP/サイズ/リダイレクト/中止の拒否", WebMonitoringTests.FetchFailures),
    ("連続実行・再起動後の通知重複防止", WebMonitoringTests.DedupAndRestart),
    ("要約失敗・空応答後の再試行", WebMonitoringTests.SummaryRetry),
    ("Markdown 保存失敗後の段階別再試行", WebMonitoringTests.FileRetry),
    ("モデル未選択・停止・中止と回復", WebMonitoringTests.ModelsAndCancel),
    ("収集とチャット・モデル停止の排他", WebMonitoringTests.SharedModelExclusion),
    ("Phase 2 DB Migration と既存会話保持", WebMonitoringTests.MigrationAndConversation),
    ("Web/チャット設定・ViewModel・破損保護", WebMonitoringTests.SettingsAndViewModel),
    ("Web 保存先の環境分離", WebMonitoringTests.EnvironmentIsolation),
    ("Debug/Release 保存先分離・既存データの維持", PersistenceTests.DebugAndReleaseIsolation),
    ("固定保存先・未保存会話・遅延作成", PersistenceTests.PathsAndLazyCreation),
    ("実SQLite・Migration・BLOB・競合・カスケード削除", PersistenceTests.SqliteRoundTripAndCascade),
    ("会話復元・下書き画像・失敗ターン除外・自動保存", PersistenceTests.RestoreAndFollowUp),
    ("保存・破棄・キャンセル・下書きのみの保存", PersistenceTests.LeaveChoicesAndDrafts),
    ("保存失敗・遅い保存・新しい編集の維持", PersistenceTests.SaveFailuresAndLateEdits),
    ("終了中止待機・モデル不存在・会話削除確認", PersistenceTests.CancelCloseMissingModelAndDelete),
    ("DBロック・消失・破損・Migration失敗", PersistenceTests.DatabaseFailures),
    ("設定JSON・連続更新・書込失敗・固定フォルダー", PersistenceTests.SettingsAndFolders),
    ("ログの内容保護・保持上限", PersistenceTests.PrivateBoundedLogs),
    ("画像のみ・削除・置換・会話履歴", ImageMessageTests.SendingAndHistory),
    ("画像の中止・失敗・履歴除外", ImageMessageTests.CancelAndFailure),
    ("Ollama の images JSON 契約", ImageMessageTests.HttpPayload),
    ("画像非対応モデルのエラー本文", ImageMessageTests.ServerError),
    ("画像データの保持と表示経路", ImageMessageTests.MemoryAndDisplay),
    ("Markdown 書式・コード原文", MarkdownTests.Formatting),
    ("HTML 安全化・URL・リソース制限", MarkdownTests.Security),
    ("未完了 Markdown・本文キャッシュ・長い履歴", MarkdownTests.StreamingAndCache),
    ("表示差分・旧会話・逆順結果の破棄", MarkdownTests.StaleUpdates),
    ("モデル管理 API・保持時間・ps DTO", ModelLifecycleTests.HttpContract),
    ("管理対象の追跡・限定停止・手動停止", ModelLifecycleTests.OwnershipAndManualStop),
    ("モデル切替の排他・両画面の同期", ModelLifecycleTests.SelectionAndSharedState),
    ("チャット中の停止禁止・終了時の停止", ModelLifecycleTests.ChatExclusionAndCleanup),
    ("停止失敗・状態不明・終了期限", ModelLifecycleTests.FailuresAndDeadline),
    ("モデル選択・空白入力・ダミー送受信", ViewModelTests.InitialState),
    ("逐次表示と二重送信防止", ViewModelTests.Streaming),
    ("会話継続・モデル変更・新しい会話", ViewModelTests.Conversation),
    ("中止した送受信を履歴から除外", ViewModelTests.Cancellation),
    ("部分応答の失敗・空応答・再送信", ViewModelTests.FailureRecovery),
    ("モデル一覧の失敗・空一覧・再取得", ViewModelTests.ModelFailures),
    ("終了時キャンセルと遅れて届く結果", ViewModelTests.DisposeDuringWork),
    ("モデル一覧 HTTP 契約", ServiceTests.Models),
    ("NDJSON・UTF8 分割・送信履歴", ServiceTests.Streaming),
    ("異常応答・未完了ストリーム", ServiceTests.ProtocolErrors),
    ("HTTP エラー・接続拒否", ServiceTests.ConnectionErrors),
    ("読み取り中のキャンセルとタイムアウト", ServiceTests.CancellationAndTimeout),
};
foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}
Console.WriteLine($"{tests.Length} checks passed.");
