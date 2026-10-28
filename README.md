# Ateliers AI Assistant — Phase 2: 履歴と共通基盤

WinUI 3 + MVVM のローカル AI チャットです。Ollama のモデル一覧から選択し、回答を逐次表示して複数ターンの会話を続けられます。生成中の中止、エラー表示、新しい会話に対応しています。

チャットの表示領域には、ローカル資産だけを使う WebView2 を 1 つ配置しています。Markdown の見出し・太字・リスト・表・コードブロックと、対応言語のシンタックスハイライトを表示できます。原文は変更せず、変換と安全化は Desktop の表示層で行います。

## ソースとドキュメントの取得

設計文書は別リポジトリで管理し、`docs/` から Git Submodule として参照します。

```powershell
git clone --recurse-submodules https://github.com/yuu-git/ateliers-ai-assistant.git
# 既にクローン済みの場合
git submodule update --init --recursive
```

文書の入口は [docs/README.md](docs/README.md)、現在のチャットの仕様と制約は [実装状況](docs/features/ai-chat-implementation-status.md) を参照してください。

## 実行

Windows、.NET 8 対応 SDK、WinUI 開発環境、Windows App Runtime 2.6.4、および起動済みの Ollama が必要です。元の Desktop プロジェクトのターゲットと既存 NuGet のバージョンを維持しています。

Markdown 表示には別途 WebView2 Runtime が必要です。利用できない場合はエラーと原文表示へ切り替えます。Windows App Runtime と WebView2 Runtime は別のランタイムです。

1. Ollama を `http://localhost:11434` で起動し、使用するモデルをあらかじめ導入します。
2. Visual Studio でソリューションを開き、Desktop / Debug / x64 を選んで Package 起動プロファイルを実行します。
3. モデル一覧から使用モデルを選択し、入力後に「送信」を押します。Enter は改行です。
4. 「中止」で応答を止め、「新しい会話」で履歴をリセットできます。モデル変更では会話を引き継ぎます。
5. 選択したモデルの「起動」「停止」、および左ナビゲーションの「モデル管理」からロード状態の確認・手動停止を行えます。
6. 入力欄にフォーカスして Ctrl+V でクリップボード画像を貼り付けると、入力付近にプレビューを表示します。「画像を削除」で外せます。画像だけでも送信でき、貼り直しは1枚の添付を置き換えます。画像対応モデルは手動で選択してください。

Unpackaged 起動の場合は、次の設定でビルドし、出力先の EXE を実行できます。ランタイム未導入の環境で検証する場合に限り `WindowsAppSDKSelfContained=true` を指定できます。

```powershell
dotnet build src/Ateliers.Ai.Assistant.Desktop/Ateliers.Ai.Assistant.Desktop.csproj -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=false
```

## 会話と失敗の扱い

- 初回は利用者がモデルを選択し、以後は最後の選択が存在すれば起動時に復元します。自動ロードやダウンロードは行いません。「一覧を更新」は利用可能なら現在の選択を維持します。
- 独自のシステム指示は送信しません。モデルの生成オプションを変更せず、`message.content` を回答として表示します。`thinking` は表示・履歴に含めません。
- 完了した送受信と今回のユーザー入力を毎回送信します。アプリ側では履歴を切り詰めません。Ollama・モデル側のコンテキスト上限の制約は残ります。
- 中止・失敗した送受信は、部分本文を含め画面に状態付きで残し、そのターンの入力と回答は両方とも次回の履歴から除外します。
- 一覧取得の期限は 10 秒、生成全体は既存の試験コードと同じ 10 分です。ストリームの読み取り待機にも期限を適用します。
- 生成中は中止だけを有効にし、終了時にもキャンセルを伝播します。通信失敗後は操作可能な状態へ戻し、エラーを表示します。自動再試行は行いません。
- 新規会話はメモリ内で開始します。必要な会話だけ「会話を保存」で名前を指定して保存し、チャット内部の一覧から再開・削除できます。Windows Service は実装していません。

## 会話の保存と再開（Phase 2）

- 未保存会話は DB に記録せず、空スレッドも作りません。会話名は保存ダイアログで指定し、同じボタンから変更できます。
- 保存内容はメッセージ順・役割・Markdown 原文・日時・送信時モデル・状態・画像と、未送信の入力文・添付です。HTML・DOM・縮小前の原画像は保存しません。
- 保存済み会話の応答は完了・中止・失敗時にまとめて自動保存します。下書き・モデル選択の変更は500ms集約します。中止・失敗ターンは復元しますが次回要求には送りません。
- 「新しい会話」・別会話・画面離脱・通常終了では、必要に応じて保存／保存せず破棄／キャンセルを選びます。保存済みの変更は先に保存を試み、失敗時も確認します。保存失敗では未保存表示とメモリ内容を維持します。
- 保存モデルが一覧にない会話はモデル未選択として通知します。存在する場合も選択だけでロードせず、旧モデルの解放は既存の管理方針に従います。
- 削除はスレッド単位で確認し、メッセージ・画像をカスケード削除します。ごみ箱・復元・検索・自動削除・バックアップ UI はありません。

## 固定保存先と運用

Debug / Release でデータを分離します。下表は Release 用で、Debug は `Ai-Assistant` を `Ai-Assistant.Debug` に置き換えます。Roaming / Local の両方で設定・会話 DB・画像・ログ・キャッシュ・一時領域を分離し、設定画面に環境名を表示します。環境はビルド構成で自動決定し、利用者向けの切替設定はありません。

**従来のデータはそのまま Release 用に残します。** Debug は空の状態から開始し、既存会話・設定の移動・コピー・削除は行いません。Ollama の接続先とモデル管理の対象は共通です。

| 対象 | 保存先 |
| --- | --- |
| 設定 | `%APPDATA%/Ateliers-Dev/Ai-Assistant/settings.json` |
| SQLite DB | `%LOCALAPPDATA%/Ateliers-Dev/Ai-Assistant/data/assistant.db` |
| 診断ログ | `%LOCALAPPDATA%/Ateliers-Dev/Ai-Assistant/logs/` |
| 将来のキャッシュ・一時領域 | 同 Local ルートの `cache/`・`temp/`（現在は作成・利用しない） |

設定画面で4種類の固定パスを表示・コピーでき、「開く」で必要な対象フォルダーだけを作成して Explorer を開きます。任意パス設定や DB 編集 UI はありません。設定は最後の選択モデルだけで、不正 JSON は自動上書きせず、修復・削除後に「設定を再読み込み」で復帰します。

DB は最初の保存時に作成し、既存 DB は Migration で更新します。破損・ロック・アクセス失敗・Migration 失敗を通知し、削除して作り直す処理はありません。使用中 DB の消失も検知します。会話更新はトランザクション単位で置き換え、版番号で別ウィンドウからの上書きを拒否します。SQLite I/O は UI スレッド外で行います。

診断ログは各2 MiB・最大3ファイルです。固定操作識別子・例外型・HRESULT だけを記録し、会話全文・画像・SQL・資格情報・例外メッセージは記録しません。ログの書込失敗自体で処理を落とさず、元の保存・設定エラーは画面に通知します。

**運用上の制約:** 保存は暗号化しません。長い会話・多数の画像では DB とメモリ使用量が増えます。自動保存は変更ごとに全スナップショットをトランザクションで置き換える MVP で、差分永続化・複数プロセスの調停・自動容量管理は行いません。強制終了は保存されていない変更を失う場合があります。SQLite は `assistant.db-wal` / `assistant.db-shm` 等を作る場合があるため、稼働中 DB の単純コピーは正式なバックアップ手順ではありません。移行・バックアップ前にはアプリを終了し、DB と補助ファイルの整合性を確保してください。

MSIX では `desktop6:FileSystemWriteVirtualization=disabled` と `unvirtualizedResources` を指定し、AppData の書込リダイレクトを抑止します。パッケージ削除後も固定保存先のデータは自動削除されません。Store 配布時の制限付き機能の承認や別ユーザー・別 OS での挙動は別途確認が必要です。[Microsoft の説明](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)を参照してください。

## EF Core と Migration

追加 NuGet は Infrastructure の `Microsoft.EntityFrameworkCore.Sqlite` 8.0.33 と `Microsoft.EntityFrameworkCore.Design` 8.0.33（開発用・`PrivateAssets=all`）です。推移依存に EF Core / Microsoft.Data.Sqlite / SQLitePCLRaw が含まれます。プロジェクトは増やしていません。Release は合意済みの `PublishTrimmed=false` とし、EF Core のリフレクションと Migration を保持します。

**Phase 1 からの発行設定切替:** 古い trimming 済みの `publish/` へそのまま重ねると、以前のランタイム DLL が残り、起動時の `ComInterfaceEntry` 型読込で失敗する場合があります。既存の発行用生成物だけを整理するか、空の別フォルダーを発行先にしてから発行してください。設定・会話 DB の保存先を削除する必要はありません。

初期 Migration は `Conversations/Migrations/20261028185039_InitialConversations`。会話・メッセージ・画像の3テーブルと外部キー・一意制約を作ります。通常起動は `EnsureDeleted` / `EnsureCreated` を使わず、`MigrateAsync` を使用します。

```powershell
dotnet tool restore
dotnet ef migrations list --project src/Ateliers.Ai.Assistant.Infrastructure --startup-project src/Ateliers.Ai.Assistant.Infrastructure --no-connect
# スキーマを変更するとき（既存のユーザーデータをバックアップしてから適用する）
dotnet ef migrations add <MigrationName> --project src/Ateliers.Ai.Assistant.Infrastructure --startup-project src/Ateliers.Ai.Assistant.Infrastructure --output-dir Conversations/Migrations
```

通知は Application の `NotificationDto` / `NotificationKind` だけを追加しました。チャットモデルと分け、将来の通知フィードへ日時・種別・見出し・Markdown 原文を渡す契約に限定し、通知配信・Web 収集・Service は実装していません。

### Phase 2 の主な変更ファイル

| 配置 | 追加・変更と責務 |
| --- | --- |
| `Application/Conversations/IConversationStore.cs` | スナップショット・一覧 DTO と会話ストア契約 |
| `Application/Storage`・`Application/Settings`・`Application/Notifications` | 固定パス、診断、設定、通知表示の最小契約 |
| `Infrastructure/Conversations` | `AssistantDbContext`、`SqliteConversationStore`、初期 Migration と ModelSnapshot |
| `Infrastructure/Storage` | `AppDataPaths`、`JsonUserSettingsStore`、`FileDiagnosticLog` |
| `Desktop/ViewModels/ChatViewModel.Persistence.cs`・`ChatMessageViewModel.cs`・`SettingsViewModel.cs` | 保存状態、確認依頼、復元、設定操作。既存 `ChatViewModel.cs` は編集・送信の確定点と接続 |
| `Desktop/MainWindow.xaml(.cs)`・`App.xaml.cs`・`Storage/FixedDataFolderOpener.cs` | 画面、ダイアログ、終了待機、DI、固定フォルダー起動 |
| `Desktop/Package.appxmanifest`・Desktop / Infrastructure の csproj | 書込仮想化設定、EF 依存、Release trimming 設定 |
| `Desktop/Diagnostics`・`tests/PersistenceTests.cs`・`tests/Verify-Storage*.ps1`・`tests/Verify-ReleaseSettings.ps1` | 生成物・利用者データを区別した保存と実画面検証。テスト C# は既存 `tests/Ateliers.Ai.Assistant.Desktop.Tests/` 内 |
| `.gitignore`・`dotnet-tools.json` | DB・補助ファイルの除外と EF ツール固定 |

## 構成

- `Core`: UI や外部サービスに依存しない会話モデル。
- `Application`: `IChatService`（モデル一覧・回答ストリーム）と表示可能なサービス例外。
- `Application/Models`: `LoadedModel`、`IModelControlService`、`IModelLifecycleService`、`ChatModelSession`。UI・HTTP に依存せず、管理対象の追跡・排他・状態共有・期限付き終了を扱います。
- `Infrastructure`: `OllamaChatService`。`ateliers-learn-llm` の `HttpClient` / `/api/chat` 呼び出しを移植し、`GET /api/tags` と NDJSON の読み取りを追加しています。JSON のメタデータをソース生成し、既存の trimming 設定に対応します。`DummyChatService` はテスト用です。
- `Desktop`: View、ViewModel、表示用コンバーター。`App.OnLaunched` で HTTP の所有権・接続先とサービスを構成します。ViewModel は HTTP や Ollama の DTO を参照しません。
- `ModelManagementViewModel` / `LoadedModelViewModel`: ロード中モデル一覧と手動停止、サイズ・VRAM・量子化形式・ロード期限の表示。既存の `ChatViewModel` と同じ `ChatModelSession` を利用します。
- `tests/Ateliers.Ai.Assistant.Desktop.Tests`: UI 非依存の ViewModel と偽の HTTP 応答による自動検証。外部テストパッケージ不要の実行プログラムです。

依存方向は Core ← Application ← Infrastructure、Desktop → Application / Infrastructure を維持しています。Phase 2 の保存契約は Application、EF Core・JSON・固定パス・制限付きログは Infrastructure、確認ダイアログ・Explorer・保存表示は Desktop に置いています。

## モデル管理と終了

- 起動は空の `messages` による `POST /api/chat` と `keep_alive: -1`、停止は `keep_alive: 0` を使います。通常のチャット送信も `keep_alive: -1` とし、保持方針を一致させます。CLI や Ollama サーバープロセスは操作しません。
- 起動・停止後は `/api/ps` を再取得します。ロード状態に応じてボタンを切り替え、状態未確認・取得失敗時は起動／停止を禁止して更新を促します。
- 「モデル管理」は Ollama 全体のロード一覧です。手動停止は任意モデルに適用でき、他アプリの利用にも影響します。
- 管理対象は、この起動中にロード要求またはチャット使用要求を送ったモデルだけです。一覧に表示されたという理由で管理対象にはしません。要求応答が失われた場合も終了時の確認対象にできるよう、要求直前に追跡します。
- モデル変更では管理対象の旧モデルだけをアンロードし、新モデルは自動ロードしません。切替中とモデル操作中は送信・他のモデル操作を禁止します。停止失敗を表示し、旧モデルは終了時の確認対象に残します。
- チャット中はチャット画面・モデル管理画面の両方で停止を禁止し、サービス層でも競合要求を拒否します。
- 通常終了は `AppWindow.Closing` で一度保留し、生成の中止待ちと会話の保存確認を行います。保存・破棄が完了した後に、管理対象のロード状態を確認・停止します。モデル解放は全体5秒、Window側6秒の上限を維持し、失敗を理由に終了を妨げません。保存確認のキャンセルでは終了しません。
- 強制終了やクラッシュ時、および通信失敗・終了期限超過時のアンロードは保証できません。
- **共有利用の制約:** 同じモデルを他アプリも利用しているかは Ollama API で判定できません。このアプリが使用したモデルの切替・終了時停止は他アプリにも影響し得るため、運用で調整してください。
- このモデルライフサイクルは単一チャット用仕様を維持しています。将来、情報収集・画面監視や Windows Service を導入する際には、複数機能の利用権と停止条件を見直します。Ollama サーバー操作、Service、モデルのダウンロード・削除・更新は追加していません。

手動確認では、未ロードのモデルを選んで起動し、「モデル管理」の表示と停止、モデル切替後の旧モデル停止・新モデル未ロード、チャット中の停止禁止、モデルがロード済みの状態でウィンドウを閉じた後の `/api/ps` を確認してください。

## ビルドとテスト

画像貼り付けの追加で新しい NuGet パッケージやプロジェクトは追加していません。クリップボード・縮小・プレビューには Windows の標準 API を利用しています。

Markdown 対応で追加した依存は Desktop の `Markdig` 1.5.0、`HtmlSanitizer` 9.2.1039（推移依存 `AngleSharp` 1.7.2）です。WebView2 SDK は既存の Windows App SDK 経由の参照を利用します。highlight.js 11.11.1 は `Assets/ChatTranscript/vendor` にライセンス付きで同梱しています。新しいプロジェクトは追加していません。

ソリューションのビルドとテストは、同じ中間ファイルを利用するため順番に実行してください。

```powershell
dotnet build ateliers-ai-assistant.slnx -p:Platform=x64
dotnet run --project tests/Ateliers.Ai.Assistant.Desktop.Tests
dotnet run --project tests/Ateliers.Ai.Assistant.Desktop.Tests -c Release
```

テストは `dotnet test` ではなく `dotnet run` で実行します。失敗時は非 0 の終了コードになります。Ollama を起動せずに、選択・空白入力、逐次表示、会話継続、モデル変更、中止・失敗した履歴の除外、部分応答、一覧取得失敗、異常 JSON、UTF-8 分割、未完了ストリーム、接続拒否、タイムアウト、終了時キャンセルを検証できます。

Markdown の書式・HTML 安全化・URL 制限・変換キャッシュ・世代と更新順の検証もこのプログラムで実行します。

Phase 2 では実 SQLite・Migration・BLOB・下書き・上書き競合・カスケード削除・保存失敗・離脱確認・DBロック／消失／破損・不正設定・固定フォルダー境界・ログ上限・Debug/Release の分離も含め、全36シナリオを実行します。保存先は一意の一時ディレクトリで、利用者の会話 DB は変更しません。

```powershell
# Debug / Unpackaged ビルド後。Ollama と利用者の DB は使いません。
./tests/Verify-StorageDisplay.ps1
./tests/Verify-StorageDialogs.ps1
```

前者は実ウィンドウの終了待機・キャンセルと、別プロセスでの Markdown・画像・下書き復元を確認します。後者は UI Automation で実際の保存名・離脱・終了・削除ダイアログと、読み取り専用の保存先4項目を操作します。Debug 専用の検証入口は Release に含めません。

発行した Release を確認する場合は、`Verify-ReleaseSettings.ps1` で起動・設定表示・終了を確認できます。`-OpenExplorer` は実際の DB 格納フォルダーを開き、検証で新しく開いた Explorer だけを閉じます。

```powershell
./tests/Verify-ReleaseSettings.ps1 -OpenExplorer
# 通常のユーザー保存先へ GUID 付き検証スレッド2件を作成し、再起動・復元・削除を確認する明示的な検証
./tests/Verify-StorageDialogs.ps1 -ExecutablePath 'src/Ateliers.Ai.Assistant.Desktop/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/Ateliers.Ai.Assistant.Desktop.exe'
```

後者は作成した検証スレッド2件を最後に削除します。既存の会話は編集・削除しませんが、失敗で検証を中断した場合は GUID 付きの検証スレッドが残る場合があります。

実 WebView2 の表示検証は、前述の Debug / Unpackaged ビルド後に実行します。Ollama は操作しません。`--verify-markdown` は Debug にだけ含まれる検証用起動モードです。

```powershell
./tests/Verify-MarkdownDisplay.ps1
```

これは初期化前の更新、書式、C# ハイライト、未完了フェンス、部分応答、中止・失敗時の反映、会話リセット、長文、スクロール追従、HTML と外部通信の制限を検証します。外部リンクは実際の利用者入力相当のクリックを送り、検証済み URL がホストへ渡ることを確認します。既定ブラウザーの起動先だけをテスト用受け口に置き換えるため、外部ブラウザーは開きません。

画像貼り付けの実画面検証も Debug / Unpackaged ビルド後に実行します。**この検証だけはクリップボードを一時的に変更し、元の全形式をメモリへ退避して終了時に復元します。** 通常のアプリはクリップボードを読み取るだけです。

```powershell
./tests/Verify-ImagePaste.ps1
# 起動済み Ollama と導入済みの画像対応モデルで実機確認する場合（他のモデルは未ロードの状態で実行）
./tests/Verify-ImagePaste.ps1 -VisionModel gemma3:4b
```

検証ではメモリ内で合成画像を生成し、ネイティブの貼り付けイベント、テキスト貼り付け、縮小、プレビュー、削除、置換、画像のみ送信、会話内の画像表示と後続履歴、不正入力、会話リセットを確認します。`-VisionModel` を付けない場合は Ollama を呼び出しません。付けた場合は指定モデルで画像のみの質問と後続質問を行い、最後にモデルをアンロードします。`--verify-image-paste` は Debug 専用です。

実機のモデル一覧と2ターンの会話を確認する場合は、導入済み・未ロードのモデルを指定します。終了時に検証用モデルをアンロードします。

```powershell
dotnet run --project tests/Ateliers.Ai.Assistant.Desktop.Tests -- --ollama-model qwen2.5-coder:3b
```

実画面の自動検証は、前述の Unpackaged ビルド後に実行します。**Ollama にロード中モデルがない状態で実行してください。** 指定モデルのロード、会話、停止、モデル切替、通常終了時のアンロードまで検証し、検証用ウィンドウを閉じます。モデルをダウンロードする処理はありません。

```powershell
./tests/Verify-ChatUi.ps1 -Model qwen2.5-coder:3b
```

Markdown の DOM は専用検証で確認し、既存 UI テストはネイティブの入力・コマンド状態・モデル管理を確認します。任意の発行物を確認する場合は `-ExecutablePath` で EXE を指定できます。

## Markdown の実装と制約

- `Presentation/MarkdownMessageRenderer.cs`: 汎用の変換と HtmlSanitizer の許可リスト設定。生 HTML、画像、埋め込み、イベント属性・任意スタイルを許可せず、絶対 HTTP / HTTPS URL だけをリンクとして残します。
- `Presentation/TranscriptProtocol.cs`: UI 専用スナップショット、変換キャッシュ、差分、世代と更新番号、JSON ソース生成。HTML はメッセージモデルへ戻しません。
- `Controls/ChatTranscriptView.xaml(.cs)`: WebView2 の初期化とセキュリティ設定、コレクション監視、100ms の集約、背景変換、初期化前の更新保持、原文フォールバック、終了時の解放。
- `Assets/ChatTranscript`: HTML / CSS / JavaScript と highlight.js。専用仮想ホスト・CSP・リソース要求の許可リストで表示資産を限定します。CDN は利用しません。
- `MainWindow.xaml(.cs)`: 会話一覧だけを新コントロールへ置き換え、入力欄・モデル管理を維持します。
- `Diagnostics/MarkdownDisplayVerification.cs`: Debug 専用の実ブラウザー検証。通常起動と Release には検証シナリオを出しません。
- コマンド型と TwoWay バインディングの型は、Release trimming で WinRT マーシャリング・プロパティ情報を生成するための `partial` / メタデータを付けています。ViewModel に WebView2・HTML・JavaScript の処理は追加していません。

受信中のコードは等幅表示し、完了・中止・失敗時に言語指定付きのコードを着色します。未知の言語はプレーン表示です。Markdown の外部画像、数式、Mermaid、生 HTML、DOM 仮想化、コードコピー専用ボタンは未実装です。手動添付の画像は本文と別経路で表示します。通常のテキスト選択とキーボードによるコピーは利用できます。

表示更新は本文全体やページを再読み込みせず、変更メッセージに限定します。過去を読んでいるときのスクロール位置を維持します。ただし会話全体の DOM は保持するため、非常に長い会話や巨大なコードではメモリ・描画負荷が増えます。WebView2 障害後の自動再初期化は行わず、原文表示で継続するかアプリを再起動します。

## データ環境分離の検証記録（2026-10-29）

- Debug ビルド・Release 発行に成功。XML ドキュメント生成を有効にしたビルドも警告0・エラー0。
- Debug / Release とも全36シナリオ成功。同じ基準フォルダーに用意した両環境で、Debug の保存・削除後も Release の DB・設定・ログが同じ内容であることをハッシュで確認。
- 両構成の実画面で環境名と保存先4項目を確認。Debug の隔離保存先で、保存・再起動・画像・下書き復元と確認ダイアログの回帰検証も成功。
- 通常の自動テストと診断モードは一時保存先を使う。実画面の通常起動は Debug が `Ai-Assistant.Debug`、Release が従来の `Ai-Assistant` を使う。

## Phase 2 の検証記録（2026-10-29）

- x64 Debug の通常／Unpackaged ビルド成功。XML ドキュメント生成の確認も警告0・エラー0。
- Debug / Release の自動検証は全35シナリオ成功。実 SQLite の保存・再読込・Migration、画像 BLOB、競合、失敗・破損・ロックと、会話切替前の旧遅延保存の排除を確認。
- 実ウィンドウで未保存の終了キャンセル、非同期保存完了前の終了抑止、別プロセスでの原文・画像・下書き復元と追記の自動保存を確認。
- UI Automation で保存名・離脱・終了・削除の実 ContentDialog と、固定パス4項目を確認。Release 発行物でも保存・終了・再起動・下書き復元・削除を確認。
- x64 Release 発行成功（trimming 無効）。古い発行用 DLL の残留を解消したクリーン発行物で、起動・設定・Explorer の実起動・通常終了を確認。
- Windows 11 x64 の Debug で MSIX / Unpackaged のパスをファイルハンドルから比較し、Roaming / Local / data / logs が表示と一致。MSIX 検証は別 ID の一時開発パッケージを使い、終了後に削除。既存の開発用登録は変更しない。
- MSIX の生成は成功。署名なしの検証用パッケージで、環境に `mspdbcmf.exe` がないためシンボルパッケージを作れない警告が1件。実行用パッケージの生成には影響しない。
- 実 WebView2 の Markdown 表示と貼り付け104回、Ollama の実画面でロード・2ターン会話・中止・切替・通常終了時アンロードの回帰確認に成功。
- Release MSIX、Store 配布、別 OS・x86 / ARM64、ACL 自体を変更するアクセス拒否試験は未検証。Release の実画面検証は Unpackaged。アクセス拒否の通知は偽操作・書込不可パスで確認。

## Phase 1 の検証記録（2026-10-28）

- x64 / Debug のビルド成功（警告 0、エラー 0）。
- Ollama 不要の自動検証: 26 シナリオ成功。画像のみの送信、画像履歴、削除・置換、中止・失敗画像の除外、API の images 配列、Ollama エラー本文を追加検証。
- ローカル Ollama と `qwen2.5-coder:3b` を使い、実機のモデル一覧と履歴を参照する2ターンのストリーミング会話を確認。
- ランタイム非同梱の実画面で、モデル選択、入力バインディング、回答表示、会話継続、中止後の復帰、新しい会話による表示リセットを UI Automation で確認。
- 実画面でモデル起動と `/api/ps` の一致、管理画面からの停止・空状態、モデル切替時の旧モデル停止と新モデル未ロード、通常終了時のアンロードを確認。
- 実 WebView2 で Markdown 書式・C# ハイライト、初期化前の更新、中止・失敗の最終反映、外部リンクの検証とホストへの引き渡し、外部遷移・通信の遮断、会話リセット、長文・スクロール位置維持を確認。
- 実画像貼り付けイベントで、テキスト動作、3000px → 1536px の縮小、プレビュー・削除・置換、画像のみ送信、履歴内の画像描画、後続リクエストへの画像保持、会話リセット、過大サイズ・不正画像からの復帰を確認。クリップボード復元も確認。
- 実機 `gemma3:4b` に合成した赤い画像を送信し、画像のみの応答と後続の質問への回答を確認（回答「その画像は赤色です。」）。認識精度の一般的な保証を意味しません。非対応モデルのエラー表示は偽 HTTP 応答で検証しています。
- x64 / Release は既存の `PublishTrimmed=true` を維持して発行成功。発行物でモデル起動、送受信、中止、モデル管理、切替、終了時アンロードと、原文フォールバックへ落ちないことを確認。検証で見つかった WinRT コマンド・TwoWay バインディングの trimming 互換性は生成メタデータで対応。
- 実機の Ollama を停止する検証は行っていません。接続拒否・途中切断・待機のタイムアウト・モデル一覧の失敗は偽サービス／HTTP 応答で検証しています。
- x86 / ARM64、MSIX のインストール・配布、目視によるテーマ・サイズ別のレイアウトは未検証です。既定ブラウザーの実起動は自動テストで差し替えています。
- 長い会話のトークン管理は未実装です。必要に応じて「新しい会話」でリセットしてください。

## 画像入力の実装と制約

- `Core/Chat/ChatImage.cs` と `ChatMessage.cs`: UI 非依存・不変のメモリ内 PNG 添付。Base64 は一度だけ生成し、表示履歴と送信履歴では同じ画像オブジェクトを参照します。
- `Desktop/Images/ClipboardImageService.cs`: クリップボード Bitmap のデコード、長辺1536px以下への縮小、PNG 正規化とプレビュー生成。元は32 MiB・4000万画素以下、縮小後は12 MiB以下に制限します。ファイルは作成しません。
- `ClipboardImageController.cs` と `MainWindow`: Paste イベント・プレビュー・エラー・終了時キャンセルの接続。ViewModel は BitmapImage やクリップボード API を参照しません。
- `ClipboardImageController` は、WinUI の貼り付けイベント内でクリップボード COM API を呼ばず、必ず UI キューへ延期して直列に処理します。テキストも選択範囲に挿入し、Undo と改行を維持します。遅い提供元を待つ間に入力が変わった場合や終了した場合は、古い結果を反映しません。`Verify-ImagePaste.ps1` に連続貼り付け・遅延提供元・終了競合の回帰検証を含めています。
- `ChatViewModel` / `ChatMessageViewModel`: 添付の有無に応じた送信制御、送信前削除、送信後の履歴保持。完了した画像は後続リクエストにも送信し、中止・失敗したターンは画像も含め送信履歴から除外します。画面には部分応答と画像を残します。
- `OllamaChatService` / `OllamaJsonContext`: `messages[].images` へ PNG の Base64 を1件設定します。画像なしでは省略し、Data URL 接頭辞は API に送りません。非対応等の Ollama エラー本文を長さ制限付きで表示します。将来の画面監視も同じ UI 非依存のリクエストを利用できます。
- `TranscriptProtocol` / `ChatTranscriptView` / ローカル JS・CSS・CSP: Markdown の安全化済み HTML と分離して PNG 添付を表示します。CSP は `img-src data:` とし、アプリで用意した PNG Data URL だけを専用フィールドから画像要素へ設定します。Markdown の画像・生 HTML・外部画像・外部通信は引き続き禁止します。

添付は1メッセージ1枚です。ファイルのコピーからの添付、複数添付、ドラッグ＆ドロップ、編集・切り抜き、OCR、自動的なモデル能力判定は実装していません。Phase 2 の保存済み会話・下書きの画像は BLOB として永続化します。WebView2 が使えない場合の履歴フォールバックは画像サイズをテキスト表示します。

モデル変更では過去の画像も含め会話を維持します。非対応モデルに変えたときは「新しい会話」を使ってください。画像履歴は会話全体で自動削減しないため、長い会話ではメモリ・再送信量・DB サイズと解析負荷が増えます。画像の独立ファイルへの書き出しや未保存会話の自動永続化は行いません。
