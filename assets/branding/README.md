# Ateliers AI Assistant — 第二段階の一般アイコン

承認済みの第二段階素材から取り込んだアプリ用資産です。家・ロボット・歯車・緑の回路を簡略化した独立 SVG マスターが基準で、第三段階のキャラクター素材は含みません。

- `app.ico`: 両 EXE と実行時のウィンドウ・トレイに共用する 16 / 24 / 32 / 48 / 64 / 128 / 256px の ICO。
- `icon-1024.png`: 既存パッケージ用 PNG の再生成元。ほかの `icon-*.png` はサイズ別の第二段階素材。
- `app-icon-master.svg`, `app-icon-monochrome.svg`, `palette.json`: 編集用マスターと色の参照。
- `readme-header.png`: README 用画像。

参照先を変えずに同名の画像を差し替え、必要に応じて `tools/Update-BrandingAssets.ps1` を実行してから再ビルドします。ICO の EXE 埋め込みには再ビルドが必要です。詳しい手順は [アイコン・製品情報](../../docs/features/branding-and-release.md) を参照してください。

一般アイコンは暫定採用です。第三段階の完成や高解像度キャラクターの完成を意味しません。
