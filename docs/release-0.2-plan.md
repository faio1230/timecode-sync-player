# リリース v0.2.0 準備プラン

作成: 2026-07-16。v0.1.0 公開直後のフィードバック反映。
**実行ルールは docs/test-coverage-roadmap.md の「一括実行ルール」に従う。** 上書き事項:
- ブランチ: release/0.2-prep。push しない。タグ作成・GitHub Release 公開もしない（人間側）
- E2E ゲートは全件（実機 LTC 11件含む・Skip なし）

## S1: 映像出力のデバッグ OSD トグル（機能・仕様確定済み）

**背景:** 現在、mpv の `osd-msg3`（MainWindow.xaml.cs:1596-1597 付近）に時間 + メタデータ行
（解像度/fps/V:コーデック/A:コーデック）が常時焼き込まれており、Spout 出力にも乗る。
これはデバッグ用表示であり、本番の出力に出すべきではない。

**仕様:**
- AppSettings に `ShowDebugOsd`（bool、**既定 false**）を追加。settings.json のみ・UI なし
  （デバッグ用のため。変更はアプリ再起動で反映、で可）
- false（既定）: `osd-msg3` へのメタデータ/時間表示の書き込みを行わない
  （空文字を設定するか書き込み自体をスキップ — mpv 側に残留表示が出ない方を選ぶ）
- true: 現行どおり表示
- **シーク時の OSD バー（osd-bar、suppressOsd 制御）は対象外** — あれは操作フィードバックで
  デバッグ表示ではない。挙動を変えないこと
- 既定 false は v0.1.0 からの見た目上の挙動変更（出力からコーデック表示が消える）。
  これは意図された変更であり、CHANGELOG の Changed に明記する

**テスト:** 判定を純ロジックに切り出してユニットテスト（既定 false / true 時の出力文字列 or
書き込み有無）。AppSettings の新キー（既定値・後方互換）テスト。docs/settings.md にキー追記。

## S2: README の日本語ファースト化

- `README.md` を**日本語のみ**に再構成（現在の日英併記をやめる）。冒頭に
  `[English](README.en.md)` リンクを置く
- `README.en.md`（新規）: 英語版。内容は日本語版と対称（バッジ・スクショ・リンク含む）。
  冒頭に `[日本語](README.md)` リンク
- **GitHub Releases へのリンク**を両方の README の目立つ位置（バッジ横 or ダウンロード節）に
  追加: https://github.com/faio1230/timecode-sync-player/releases — 「インストーラー/zip は
  こちら」の導線として
- 既存のドキュメントリンク・クレジット節（Studio Sandix ロゴ）・CI/MIT バッジは両言語版で維持

## S3: スクリーンショット撮影と掲載

- ffmpeg で SMPTE カラーバー動画を生成（例: `-f lavfi -i smptehdbars=size=1280x720:rate=25`
  に `drawtext` でタイムコード焼き込み、20〜30秒、H.264）
- アプリを起動し、その動画をプレイリストに投入・再生、**タイムラインビューを表示した状態**で
  ウィンドウ全体をキャプチャ（PowerShell PrintWindow 方式か FlaUI の CaptureImage。
  E2E 基盤の起動ヘルパーを流用してよい。ウィンドウサイズは既定、DPI スケールで
  ぼやけないよう等倍で取得）
- S1 実装後に撮影する（既定 OFF なのでデバッグ OSD が写らないクリーンな出力になる）
- `assets/screenshot.png` として保存（PNG、幅 1200px 程度に縮小・最適化）し、
  README.md / README.en.md の冒頭（タイトル直下）に掲載
- 撮影に使った一時動画・スクリプトはコミットしない（scripts/ に汎用化して残すのは任意）

## S4: バージョニングと配布物

- csproj `Version` を 0.2.0 へ。`ApplicationVersionTests` のピンも 0.2.0 に更新
  （リリースゲートとして意図的なピンである旨のコメントは維持）
- `CHANGELOG.md` に 0.2.0 節を追加: Added（OSD トグル設定）/ Changed（デバッグ OSD が
  既定非表示に、README 構成変更）
- `scripts/package-release.ps1` で zip + setup.exe を再生成し、SHA-256 を progress に記録
  （スクリプトがバージョンをハードコードしていないか確認。していたら csproj から
  動的取得に直してよい）
- Release 構成で非E2E + E2E 全件グリーンを確認

## S5: 外部モニターフルスクリーン出力

- UIに接続ディスプレイ選択ComboBox（プライマリ表記、AutomationId付き）とFULLSCREEN
  トグルボタンを追加。選択ディスプレイへ映像を表示し、表示中はラベルを変更して再押下で閉じる
- フルスクリーンウィンドウは`WindowStyle=None`、`ResizeMode=NoResize`、`Topmost`、
  `Cursor=None`とし、選択ディスプレイ全領域へ配置。黒背景のImageをアスペクト比維持で表示する
- 既存`FrameRenderer`の`WriteableBitmap`を共有し、`BitmapChanged`購読だけを追加する。
  mpv／FrameRendererの既存レンダーパスは変更しない
- ESCで閉じる。MouseEnter時にウィンドウをアクティブ化してキーボードフォーカスを取得し、
  メインウィンドウのFULLSCREEN再押下でも閉じられるようにする
- 表示中の対象ディスプレイ切断時は安全に閉じ、メインウィンドウ終了時にも破棄する。
  DPIが異なるモニターでも全領域へ鮮明に配置し、アスペクト比を維持して余白を黒にする
- 選択ディスプレイを`AppSettings`へ保存し、次回起動時に復元。見つからなければプライマリを選ぶ。
  `docs/settings.md`へ追記する
- ディスプレイ選択／表示判定は純クラスへ抽出してユニットテストする。E2Eを1件追加し、
  FULLSCREEN押下で新ウィンドウが出現し、ESCで閉じることをFlaUIで検証する
- `CHANGELOG.md`の0.2.0 Addedへ追記する

## 実行順とゲート

S1 → S5 → S3 → S2 → S4（S3のスクショはS1完了後。S5はS3の前後どちらでもよいが、
本一括実行ではS1直後に行う）。S1とS5はコード変更のため非E2E + E2E全件ゲート。
完了時にprogressへ記録。
タグ・GitHub Release 公開は人間側に残す。
