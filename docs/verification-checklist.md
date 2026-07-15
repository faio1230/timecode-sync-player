# 実機確認チェックリスト

このチェックリストは、リリース前にLTC同期・ギャップ挙動・Spout出力などの主要機能を
実機環境（実際のLTC音源・実際の動画ファイル）で確認するための手順書。
自動テスト（非E2E・E2E）でカバーしきれない、実際の音声デバイスや長時間再生を伴う挙動を
対象とする。

---

## 事前準備

- [ ] Debugビルド最新化: `dotnet build src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj`
- [ ] `scripts/get-mpv.ps1`で`native/libmpv-2.dll`を配置済みか確認（Spout確認も行う場合は`SpoutDX.dll`も）
- [ ] LTC音源の準備。**重要: ループしないLTCソースを使うこと**（ループすると最終EOFを踏めない）。
  - 推奨: LTC音声ファイル（WAV）を用意し、プレイリスト全体の TimelineOut を**十分に越える長さ**まで再生できるようにする
  - LTCジェネレータを使う場合は、最終トラック終端を越えても巻き戻らずに進み続ける設定にする
- [ ] テスト用プレイリスト: 短い動画2本（例: 各20〜30秒）。Track2 の TimelineOffset を Track1 の直後または少しGapを空けて設定
- [ ] アプリ起動 → LTCデバイス選択 → START → LTCタイムコード表示が進むことを確認
- [ ] Mode: **Continue**、Sync: **ON**

ログの場所: `src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\logs\timecodesyncplayer-YYYYMMDD.log`

---

## ケース1: GapBehavior = Freeze で最終トラックEOF

- [ ] Gap: **Freeze** に設定
- [ ] LTCを流し、Track1 → Track2 と同期再生されることを確認
- [ ] LTCが Track2（最終トラック）の終端を越えて進み続ける
- [ ] **期待挙動:** 最終フレームが保持表示され続ける（暗転停止しない、映像が固まったまま表示継続）
- [ ] **期待ログ:**
  - `Continue mode: reached final track end, entering no-tracks gap state`
  - `Continue mode: no tracks, entering gap freeze target=...`（duration取得不可の場合は `gap freeze activated, holding current frame`）
- [ ] 終端越えの状態で1〜2分放置し、表示が乱れない・警告が連発しないことを確認

## ケース2: GapBehavior = Black で最終トラックEOF

- [ ] アプリ再起動（またはLTC停止→プレイリスト再ロード）後、Gap: **Black** に設定
- [ ] 同様にLTCを最終トラック終端越えまで進める
- [ ] **期待挙動:** 黒フレーム表示に遷移する
- [ ] **期待ログ:**
  - `Continue mode: reached final track end, entering no-tracks gap state`
  - `Continue mode: entered gap, rendering black frame`（または `gap, forcing black frame`）

## ケース3: EOF後のLTC復帰（ループ相当）

- [ ] ケース1またはケース2の終端状態から、LTCをプレイリスト有効レンジ内（例: Track1 の中間）へ戻す
- [ ] **期待挙動:** 該当トラックが再ロードされ同期再生が復帰する
- [ ] **期待ログ:**
  - `Continue mode: switching to track ... at media position ...` または
    `Continue mode: exiting gap, resuming playback at ...`
- [ ] 復帰後のシークが安定していること（`Continue mode: sync seek ... success=True`）

## ケース4（ついで確認・任意）: 既存機能の簡易回帰

機材セットアップ済みのついでに確認しておくと安心な項目:

- [ ] トラック間Gap（Freeze）: Track1→Gap→Track2 で前トラック最終フレームが保持される
- [ ] 同期seek: LTCを数回ジャンプさせ、`Timecode sync seek ... success=True` が出て追従する
- [ ] Spout出力（SpoutDX.dll がある場合）: 受信側（Resolume等）でフレームが届く

---

## 事後確認

- [ ] ログ全体に `ERR` / `FTL` / `Exception` / `success=false` が**0件**であること
- [ ] 診断レポート生成: `scripts\run-timecodesyncplayer-diagnostics.ps1`
      → `artifacts\diagnostics\timecodesyncplayer-diagnostics-*.md` を確認
- [ ] `Playback perf warning` が出ている場合、文脈分類（Track switch aftermath / Gap exit aftermath / Normal playback）を確認し、Normal playback 中の警告がないこと

## 結果記録

| 項目 | 結果 | メモ |
|---|---|---|
| ケース1 (Freeze EOF) | ⬜ OK / ⬜ NG | |
| ケース2 (Black EOF) | ⬜ OK / ⬜ NG | |
| ケース3 (LTC復帰) | ⬜ OK / ⬜ NG | |
| ケース4 (簡易回帰) | ⬜ OK / ⬜ NG / ⬜ 未実施 | |
| 事後ログ確認 | ⬜ OK / ⬜ NG | |

**実施日:** ____年__月__日
