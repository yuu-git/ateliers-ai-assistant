# Ateliers AI Assistant — Phase 1 + モデル管理 + Markdown + 画像貼り付け

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

- モデルは自動選択せず、利用者が選択します。「一覧を更新」は利用可能なら現在の選択を維持します。モデルの自動ダウンロードは行いません。
- 独自のシステム指示は送信しません。モデルの生成オプションを変更せず、`message.content` を回答として表示します。`thinking` は表示・履歴に含めません。
- 完了した送受信と今回のユーザー入力を毎回送信します。アプリ側では履歴を切り詰めません。Ollama・モデル側のコンテキスト上限の制約は残ります。
- 中止・失敗した送受信は、部分本文を含め画面に状態付きで残し、そのターンの入力と回答は両方とも次回の履歴から除外します。
- 一覧取得の期限は 10 秒、生成全体は既存の試験コードと同じ 10 分です。ストリームの読み取り待機にも期限を適用します。
- 生成中は中止だけを有効にし、終了時にもキャンセルを伝播します。通信失敗後は操作可能な状態へ戻し、エラーを表示します。自動再試行は行いません。
- 設定・モデル選択・会話はアプリ終了時に破棄します。Windows Service、SQLite、履歴保存は実装していません。

## 構成

- `Core`: UI や外部サービスに依存しない会話モデル。
- `Application`: `IChatService`（モデル一覧・回答ストリーム）と表示可能なサービス例外。
- `Application/Models`: `LoadedModel`、`IModelControlService`、`IModelLifecycleService`、`ChatModelSession`。UI・HTTP に依存せず、管理対象の追跡・排他・状態共有・期限付き終了を扱います。
- `Infrastructure`: `OllamaChatService`。`ateliers-learn-llm` の `HttpClient` / `/api/chat` 呼び出しを移植し、`GET /api/tags` と NDJSON の読み取りを追加しています。JSON のメタデータをソース生成し、既存の trimming 設定に対応します。`DummyChatService` はテスト用です。
- `Desktop`: View、ViewModel、表示用コンバーター。`App.OnLaunched` で HTTP の所有権・接続先とサービスを構成します。ViewModel は HTTP や Ollama の DTO を参照しません。
- `ModelManagementViewModel` / `LoadedModelViewModel`: ロード中モデル一覧と手動停止、サイズ・VRAM・量子化形式・ロード期限の表示。既存の `ChatViewModel` と同じ `ChatModelSession` を利用します。
- `tests/Ateliers.Ai.Assistant.Desktop.Tests`: UI 非依存の ViewModel と偽の HTTP 応答による自動検証。外部テストパッケージ不要の実行プログラムです。

依存方向は Core ← Application ← Infrastructure、Desktop → Application / Infrastructure を維持しています。Phase 1 で NuGet パッケージ・プロジェクトの追加は行っていません。例外詳細は `Trace` に出力し、ファイルログは後続で扱います。

## モデル管理と終了

- 起動は空の `messages` による `POST /api/chat` と `keep_alive: -1`、停止は `keep_alive: 0` を使います。通常のチャット送信も `keep_alive: -1` とし、保持方針を一致させます。CLI や Ollama サーバープロセスは操作しません。
- 起動・停止後は `/api/ps` を再取得します。ロード状態に応じてボタンを切り替え、状態未確認・取得失敗時は起動／停止を禁止して更新を促します。
- 「モデル管理」は Ollama 全体のロード一覧です。手動停止は任意モデルに適用でき、他アプリの利用にも影響します。
- 管理対象は、この起動中にロード要求またはチャット使用要求を送ったモデルだけです。一覧に表示されたという理由で管理対象にはしません。要求応答が失われた場合も終了時の確認対象にできるよう、要求直前に追跡します。
- モデル変更では管理対象の旧モデルだけをアンロードし、新モデルは自動ロードしません。切替中とモデル操作中は送信・他のモデル操作を禁止します。停止失敗を表示し、旧モデルは終了時の確認対象に残します。
- チャット中はチャット画面・モデル管理画面の両方で停止を禁止し、サービス層でも競合要求を拒否します。
- 通常終了は `AppWindow.Closing` で一度保留し、生成やモデル操作をキャンセルしてから管理対象のロード状態を確認・停止します。全体で 5 秒、Window 側でも 6 秒の上限を置き、失敗を理由に終了を妨げません。
- 強制終了やクラッシュ時、および通信失敗・終了期限超過時のアンロードは保証できません。
- **共有利用の制約:** 同じモデルを他アプリも利用しているかは Ollama API で判定できません。このアプリが使用したモデルの切替・終了時停止は他アプリにも影響し得るため、運用で調整してください。
- このライフサイクルは Phase 1 の単一チャット用仕様です。将来、情報収集・画面監視や Windows Service を導入する際には、複数機能の利用権と停止条件を見直します。SQLite、Windows Service、モデルのダウンロード・削除・更新は追加していません。

手動確認では、未ロードのモデルを選んで起動し、「モデル管理」の表示と停止、モデル切替後の旧モデル停止・新モデル未ロード、チャット中の停止禁止、モデルがロード済みの状態でウィンドウを閉じた後の `/api/ps` を確認してください。

## ビルドとテスト

画像貼り付けの追加で新しい NuGet パッケージやプロジェクトは追加していません。クリップボード・縮小・プレビューには Windows の標準 API を利用しています。

Markdown 対応で追加した依存は Desktop の `Markdig` 1.5.0、`HtmlSanitizer` 9.2.1039（推移依存 `AngleSharp` 1.7.2）です。WebView2 SDK は既存の Windows App SDK 経由の参照を利用します。highlight.js 11.11.1 は `Assets/ChatTranscript/vendor` にライセンス付きで同梱しています。新しいプロジェクトは追加していません。

ソリューションのビルドとテストは、同じ中間ファイルを利用するため順番に実行してください。

```powershell
dotnet build ateliers-ai-assistant.slnx -p:Platform=x64
dotnet run --project tests/Ateliers.Ai.Assistant.Desktop.Tests
```

テストは `dotnet test` ではなく `dotnet run` で実行します。失敗時は非 0 の終了コードになります。Ollama を起動せずに、選択・空白入力、逐次表示、会話継続、モデル変更、中止・失敗した履歴の除外、部分応答、一覧取得失敗、異常 JSON、UTF-8 分割、未完了ストリーム、接続拒否、タイムアウト、終了時キャンセルを検証できます。

Markdown の書式・HTML 安全化・URL 制限・変換キャッシュ・世代と更新順の検証もこのプログラムで実行します。

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

## 検証記録

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

添付は1メッセージ1枚です。ファイルのコピーからの添付、複数添付、ドラッグ＆ドロップ、編集・切り抜き、OCR、自動的なモデル能力判定、画像履歴の永続化は実装していません。WebView2 が使えない場合の履歴フォールバックは画像サイズをテキスト表示します。

モデル変更では過去の画像も含め会話を維持します。非対応モデルに変えたときは「新しい会話」を使ってください。画像履歴は会話全体で自動削減しないため、長い会話ではメモリ・再送信量と解析負荷が増えます。アプリから画像をファイル・DB に保存する処理はありません。
