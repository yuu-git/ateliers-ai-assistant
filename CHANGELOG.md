# リリースノート

## 0.7.0 — 2026-10-31（MVP 仕上げ）

Phase 7 MVP と最大4件の独立画面監視を維持し、第二段階の一般アイコンと製品情報を仕上げた版です。更新前に旧 Worker を明示停止し、Desktop と `Worker/` を同じ版で更新してください。

版番号は利用者の指定により **0.7.0** としました。過去文書の7.xはフェーズ由来の導入時記録として保持し、今回の配布版番号・タグとは区別します。

### 変更

- 共通の `assets/branding/app.ico` を Desktop / Worker の EXE、Desktop ウィンドウ、監視対象選択ウィンドウ、監視トレイへ適用。
- 既存パッケージの正方形・横長ロゴとスプラッシュ画像を第二段階素材に更新。パッケージ ID、署名用 Publisher、起動登録、権限は維持。
- 製品名を Ateliers AI Assistant に統一。製品版を 0.7.0、Assembly / File / Win32 マニフェスト / 既存 MSIX 版を 0.7.0.0 に整合。Worker の実装版を共通の Assembly 版から取得し、既存の同版確認を維持。
- Debug のウィンドウ・パッケージ表示名を識別可能にした。Company は既存 Publisher の yuuko に合わせ、新しい著作権名義は追加していない。
- README、[素材差し替え手順](docs/features/branding-and-release.md)、検証スクリプトを追加・更新。

新しい NuGet 依存・プロジェクト・DB Migration・設定項目はありません。第三段階のキャラクター制作と MSIX の新規対応は対象外です。

### 検証結果

Windows x64 の開発環境で確認しました。

| 確認 | 結果 |
|---|---|
| Desktop / Worker を含む Debug / Release の Unpackaged ビルド | 両方成功、警告0・エラー0 |
| 既存コンソールテスト | Debug / Release 各99件成功 |
| `tests/Verify-Branding.ps1` | Debug / Release / Release 発行物で成功 |
| EXE 内の大小アイコン | 原本 ICO とピクセル一致 |
| Desktop / Worker の EXE・DLL | 製品名・Company・版情報が一致 |
| 実 Desktop | 隔離データとダミー応答で、ウィンドウのアイコン・Debug 表示・通常終了を確認 |
| ネイティブ所有者ウィンドウ・トレイ | 検証専用プローブで大小アイコンの一致、通知領域への登録・解除・再登録、終了時のウィンドウ破棄を5回反復して確認 |
| `tests/Verify-WorkerProcess.ps1` | 隔離 Worker で API・認証・一時停止・復旧・重複防止・通常停止の全項目成功 |
| Release 非パッケージ発行 | 成功。Worker と両ホストの ICO 同梱を確認 |
| 既存 Debug MSIX の署名なし生成 | 成功。パッケージ内の Worker・ICO・ロゴを確認。インストールは未実施 |

Debug MSIX 生成時に `mspdbcmf.exe` 不在による警告が1件あります。シンボル用パッケージは生成されませんが、アプリ本体の MSIX は生成できています。

### 再確認コマンド

```powershell
dotnet build src/Ateliers.Ai.Assistant.Desktop/Ateliers.Ai.Assistant.Desktop.csproj -c Debug -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=false
dotnet build src/Ateliers.Ai.Assistant.Desktop/Ateliers.Ai.Assistant.Desktop.csproj -c Release -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=false
dotnet run --project tests/Ateliers.Ai.Assistant.Desktop.Tests -c Debug
dotnet run --project tests/Ateliers.Ai.Assistant.Desktop.Tests -c Release
pwsh -File tests/Verify-Branding.ps1 -Configuration Debug
pwsh -File tests/Verify-Branding.ps1 -Configuration Release
pwsh -File tests/Verify-Branding.ps1 -Configuration Release -DesktopDirectory artifacts/mvp-0.7.0
pwsh -File tests/Verify-WorkerProcess.ps1
```

### 配布と残る確認

Release の確認用発行先は `artifacts/mvp-0.7.0/`。既存の x64 発行設定を維持しています。Windows App Runtime 2.6.4、Worker の ASP.NET Core Runtime 8（x64）、Markdown 表示の WebView2 Runtime は既存手順に従って用意してください。署名済みインストーラーや Store 配布物ではありません。

Release 発行物の通常利用データでの起動、トレイの全停止メニューを実際に選択する最終操作、Explorer 再起動による再登録、ピン留め済みタスクバーの実表示、各 DPI・テーマ、x86 / ARM64、Release MSIX の生成と MSIX の署名・インストールは今回未確認です。既存の停止・Explorer 再登録コードは維持しています。今回、実画像取得・実 Ollama 推論は再実行していません。

本変更は利用者の承認に基づき、文書リポジトリを先にコミット・pushし、その参照を含む本体をコミット・pushする対象です。タグは形式の検討を含めて保留し、今回は作成しません。日付形式・パッケージ版に合わせた形式のいずれも未決定です。
