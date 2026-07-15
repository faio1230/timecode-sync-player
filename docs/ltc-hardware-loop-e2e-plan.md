# 実機 LTC ループ E2E テスト計画

作成: 2026-07-15。目的: これまで「DAW + VB-Audio Matrix で LTC を仮想入力に流し込んで手動確認」していた実機系テストを、VB-CABLE 経由で完全自動化する。

## 構成（データフロー）

```
LtcTestSignalGenerator（tests/Helpers/ 既存・テスト済みの LTC エンコーダ）
  ↓ float 波形（モノラル・指定fps/サンプルレート）
NAudio WasapiOut → 再生デバイス「CABLE Input (VB-Audio Virtual Cable)」   ← DAW の代替
  ↓ VB-CABLE 内部ルーティング                                              ← Matrix の代替
録音デバイス「CABLE Output (VB-Audio Virtual Cable)」
  ↓ アプリ本体の LtcAudioMonitor（WASAPI キャプチャ・本物のコードパス）
FlaUI E2E がアプリ UI を検証（タイムコード表示・同期シーク）
```

これにより LtcAudioMonitor のデバイスキャプチャ〜デコード〜同期までの実機パスが初めて自動テストされる（現状 LtcAudioMonitor のデバイス系は 0% カバレッジで、意図的にユニットテスト対象外）。

## 前提条件と Skip 規約

- **VB-CABLE（無印）** がインストールされていること。ドライバのみで動作し、常駐アプリ不要。
  ダウンロード: https://vb-audio.com/Cable/ （無料・ドネーションウェア）
- テストは `SkippableFact` とし、**録音デバイス名に "CABLE Output" を含むデバイスが
  存在しない場合はスキップ**する（既存の ffmpeg 用 `TestVideoFactory.FfmpegAvailable()` と
  同じ流儀。判定ヘルパーは `LtcAudioMonitor.GetCaptureDeviceNames()` か NAudio の
  MMDeviceEnumerator を使用）
- CI（GitHub Actions windows-latest）にはオーディオデバイスがなく、かつ E2E は
  `--filter "FullyQualifiedName!~E2ETests"` で除外済みのため **CI 変更は不要**
- 同期シーク検証は ffmpeg も必要（テスト動画生成）。VB-CABLE と ffmpeg の**二重 Skip 条件**

## 実装タスク

### H1: LTC 再生ヘルパー（テスト用信号送出）

`tests/TimecodeSyncPlayer.Tests/Helpers/LtcSignalPlayer.cs`（新規）:
- `LtcTestSignalGenerator` で生成した float 波形を、フレンドリ名に "CABLE Input" を含む
  WASAPI 再生デバイスへ NAudio `WasapiOut`（Shared モード）で再生する IDisposable ヘルパー
- モノラル波形→ケーブルへの供給はステレオ複製 or NAudio の変換に任せる（実装判断）
- 指定タイムコード開始位置から**連続フレーム列をストリーミング再生**できること
  （テスト中に数十秒分。事前生成バッファで可。ループ不要）
- 再生開始・停止・破棄が例外安全であること

### H2: 表示追従 E2E

`tests/TimecodeSyncPlayer.Tests/E2E/LtcHardwareLoopE2ETests.cs`（新規・既存 E2E コレクションに参加）:
1. アプリ起動（既存 `TimecodeSyncPlayerFixture` / 既存 E2E の起動パターンを踏襲）
2. LTC デバイス一覧を更新し、"CABLE Output" を含むデバイスを選択
   （既存 `E2E/LtcControlsE2ETests.cs` の UI 操作パターンを必ず参照）
3. H1 で LTC（例: 01:00:00:00 開始、25fps、48kHz）を再生 → LTC 開始ボタン押下
4. アサート: タイムコード表示が `01:00:0X` 台に到達し、かつ**単調に進行**する
   （ポーリングは既存 E2E のリトライ/待機ヘルパーを使用。固定 Sleep の乱発禁止）
5. LTC 停止 → 最後のタイムコード表示が保持され、数秒待っても進行しない
6. 信号断も1ケース: 再生を止めて数秒後、表示が進行を止めること（判定できる範囲で）

### H3: 同期シーク E2E（VB-CABLE + ffmpeg の二重 Skip）

同ファイル内:
1. `TestVideoFactory.GetOrCreate()` のテスト動画（20秒）をプレイリストに投入
2. Sync を有効化し、動画中盤に相当する LTC（タイムラインオフセット考慮。
   既存 `E2E/SyncModeE2ETests.cs` の設定パターンを参照）を再生
3. アサート: 再生位置が LTC 相当位置へシークされる（シークバー/時間表示で検証）

### H4: README 追記

`README.md` に「実機 LTC ループテスト」セクションを追加:
- 目的（DAW + Matrix の手動検証を置き換える自動テストであること）
- 必要なもの: VB-CABLE（ダウンロード先 https://vb-audio.com/Cable/ を明記、
  インストール後に再起動が必要な場合がある旨も）
- 実行方法: `dotnet test ... --filter "FullyQualifiedName~LtcHardwareLoop"` の実例
- VB-CABLE 未導入環境・CI では自動スキップされること
- 既存の README の文体・構成に合わせる（日本語）

## 制約（docs/test-coverage-roadmap.md の共通前提を継承）

- プロダクションコード（src/）は一切変更禁止。テスト・README のみ
- 検証: 非E2E 全グリーン（861件・警告0）+ E2E 全グリーン
  （新規含む。VB-CABLE がある本機では Skip されず実行されること —
  テスト出力の Skip 数で確認する）
- タイムコード進行の判定に実時間依存の脆いアサートを書かない
  （「N秒後にちょうど X フレーム」ではなく「進行している・範囲内」で判定）
- 音量・他アプリの干渉: 再生は VB-CABLE デバイスへ直接出力し、既定の再生デバイスを
  変更しない。テスト後にデバイス設定を残さない
- コミットメッセージは日本語。ブランチ test/ltc-hardware-loop で作業し push しない
- 追跡外ファイル AGENTS.md をステージしない（git add -A 禁止）
- 完了・停止時に docs/test-coverage-progress.md へ結果を追記

## 既知のリスク（実装者への注意）

- WASAPI Shared の既定フォーマットは 44.1k/48k 環境差がある。WasapiOut への供給
  フォーマットは NAudio に変換させるか、キャプチャ側の実サンプルレート
  （LtcAudioMonitor.SampleRate）に合わせて生成する
- 仮想ケーブルのレイテンシ（数十ms〜）があるため、表示アサートは余裕を持った
  タイムアウト（例: 5秒）でポーリングする
- E2E は実ウィンドウを開く。既存 25 件と同一コレクションで直列実行されること
