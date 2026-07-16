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
- false（既定）: `osd-level=1`とし、`osd-msg3`へのメタデータ/時間表示の書き込みを行わない。
  mpv標準のステータス行を抑止しつつ、シーク時のOSDバーは維持する
- true: `osd-level=3`とし、`osd-msg3`へ現行どおり表示
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

- `scripts/capture-setup.ps1`を実行すると、ffmpegでSMPTE HDカラーバーへタイムコードを焼き込んだ
  25秒・1280x720・25fps・H.264動画を一時ディレクトリへ生成する
- 同スクリプトは隔離したsettings.jsonでタイムライン表示をONにし、動画を開いてプレイリストへ
  登録したTimecodeSyncPlayerを起動する。ここまでをCodex側の自動化範囲とし、画面取得は行わない
- RDPセッション内のキャプチャAPIではWPF映像面を取得できないため、撮影はユーザーがRDPクライアント
  またはローカルOSのスクリーンショット機能で行う。S1実装後なのでデバッグOSDは既定で写らない
- ユーザー提供画像は`assets/screenshot.png`（PNG、幅1200px程度）として後からコミットする。
  README.md / README.en.mdには先に同パスへの参照を組み込み、一時的な画像リンク切れを許容する

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
