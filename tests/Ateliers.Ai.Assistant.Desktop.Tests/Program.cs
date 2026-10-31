using Ateliers.Ai.Assistant.Desktop.Tests;

if (args.Length == 3 && args[0] == "--temp-owner")
{
    await TempManagementTests.RunOwner(args[1], args[2]);
    return;
}

if (args.Length == 2 && args[0] == "--github-repository")
{
    await GitHubReleaseTests.LiveFetch(args[1]);
    return;
}
if (args.Length == 2 && args[0] == "--ollama-model")
{
    await ServiceTests.LiveConversation(args[1]);
    return;
}
var tests = new (string Name, Func<Task> Run)[]
{
    ("複数監視の独立設定・公平実行・単独削除・上限", MultiScreenMonitorTests.FairnessAndIsolation),
    ("取消を無視する推論の実終了待ち・同一モデル保護", MultiScreenMonitorTests.CancellationRetainsSlotAndModel),
    ("複数監視の停止再開・10分期限・共有 Temp 使用権", MultiScreenMonitorTests.PauseTimeoutAndQuota),
    ("単一設定移行の冪等性・保存失敗・削除後復活防止", MultiScreenMonitorTests.MigrationAndPersistenceFailure),
    ("全監視本文上限・個別件数・保存境界・設定競合", MultiScreenMonitorTests.CommentQuotaAndReload),
    ("監視切替の下書き保持・履歴削除・古い詳細と失敗の破棄", MultiScreenMonitorTests.ViewSwitchAndHistoryEviction),
    ("監視別の自動ロード禁止・既存モデル使用・部分失敗の分離", MultiScreenMonitorTests.ModelPolicyAndFailure),
    ("対象消失の分離・Pickerの実完了待ち・選択中の削除拒否", MultiScreenMonitorTests.TargetAndPickerIsolation),
    ("継続監視モデル保護・UI再接続・編集保持・旧更新破棄", ScreenMonitorTests.ModelProtectionAndReconnect),
    ("要約と監視が共有するモデルの保持期限と停止要求", ScreenMonitorTests.SharedKeepAlive),
    ("監視Pickerの前面許可・要求順序・Worker再起動・拒否時の保護", ScreenMonitorTests.PickerForegroundGrant),
    ("Temp 時系列画像・近傍選択・一括使用権・メモリ上限", TempManagementTests.MemoryTimeAndBatch),
    ("Temp 世代・期限・全体容量・使用中保護", TempManagementTests.DiskQuotaAndLeases),
    ("Temp 原子的確定・所有者・UI終了・環境分離", TempManagementTests.AtomicityFailureAndSeparation),
    ("Temp 破損・消失・一括取得失敗・清掃障害", TempManagementTests.CorruptionAndCleanupErrors),
    ("Temp 範囲外・再解析ポイント・パス差し替え防止", TempManagementTests.ReparseAndRangeSafety),
    ("Temp 実プロセスの使用中・異常終了・未完了回収", TempManagementTests.ProcessOwnershipAndCrash),
    ("手動キャプチャ・同一画像送信・結果分離・旧更新破棄", ScreenAnalysisTests.WorkflowAndIsolation),
    ("画面解析の失敗・中止・対象消失・終了待機", ScreenAnalysisTests.FailuresAndCancellation),
    ("解析の排他取得後の AI 停止再確認", ScreenAnalysisTests.GuardWithinLease),
    ("GitHub API 契約・対象選別・入力・識別", GitHubReleaseTests.FetchContractAndSelection),
    ("GitHub HTTP 障害・制限・JSON・中止・サイズ", GitHubReleaseTests.FailuresAndBounds),
    ("GitHub リダイレクト制限・空リリース", GitHubReleaseTests.RedirectAndEmpty),
    ("GitHub Worker・旧記事保持・編集重複・削除再取得", GitHubReleaseTests.WorkerPersistenceAndMetadata),
    ("GitHub 全体のレート制限・RSS 継続・再起動復元", GitHubReleaseTests.SharedRateLimitAndRecovery),
    ("GitHub 設定・旧 RSS JSON 互換", GitHubReleaseTests.LegacyConfigurationAndValidation),
    ("GitHub 入力編集の保持・設定 API エラー表示", GitHubReleaseTests.SourceEditorAndApiErrors),
    ("一覧の単体削除・確認の副作用・連打・一括確認の独立", NotificationQuickActionTests.DeleteAndConfirmation),
    ("確認設定の旧 JSON・保存順序・失敗時の維持", NotificationQuickActionTests.SettingsOrderingAndFailures),
    ("閲覧終了時の既読化・手動未読優先・詳細保持", NotificationViewModelTests.ExplicitReadAndLocalGroups),
    ("既読化の連続選択・保存失敗・終了待機", NotificationViewModelTests.DeferredReadQueueAndFailure),
    ("グローバル Worker 操作・実状態・設定編集保持", NotificationViewModelTests.GlobalWorkerControls),
    ("AI 即時操作が他設定と RSS 予定を維持", WorkerTests.PauseOnlyConfiguration),
    ("通知全件の未読集計・100件外検索・初回取得順", NotificationManagementTests.QueriesAndCounts),
    ("既読・ブックマーク・タグの独立性と削除再取得", NotificationManagementTests.MetadataAndDelete),
    ("既存通知の Migration・ロールバック・タグ規則", NotificationManagementTests.MigrationAndTagRules),
    ("情報源の正確な絞込・選択維持・表示対象の一括削除", UiRefactoringTests.FiltersAndSelection),
    ("設定の編集保持・保存単位・再読込確認・失敗保持", UiRefactoringTests.EditingAndRefresh),
    ("AI 停止中の取得と範囲外待機記事の処理", WorkerTests.PauseFetchAndBacklog),
    ("Worker モデル自動ロード・解放ポリシー", WorkerTests.ModelPolicies),
    ("一時停止のキャンセル・再起動後復元", WorkerTests.CancelAndRestart),
    ("周期変更・追いつき1回・手動要求の冪等性", WorkerTests.ScheduleAndRequests),
    ("処理中復旧・要約とファイルの段階別再試行", WorkerTests.QueueRecoveryAndFiles),
    ("情報源削除・再登録・記事削除後再取得", WorkerTests.SourceAndArticleDeletion),
    ("Worker 旧設定移行・競合・検証・破損保護", WorkerTests.SettingsMigrationAndValidation),
    ("UI/Worker のモデルゲートと終了時の保護", WorkerTests.CrossProcessModelGate),
    ("ログ分類・絞込・コピー・秘密除外・単一起動", WorkerTests.LogsOfflineAndPrivacy),
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
var selectedTests = args.SequenceEqual(new[] { "--temp-tests" }) ? tests.Where(test => test.Name.StartsWith("Temp ", StringComparison.Ordinal)).ToArray() : tests;
foreach (var test in selectedTests)
{
    await test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}
Console.WriteLine($"{selectedTests.Length} checks passed.");
