# D4: GPU 合成にすると、ロード後 5 秒間 LTC 同期の補正が止まる

main から新ブランチ `codex/d4-sync-gate-20260914` を切る。
**自分のワークツリー（`C:\Users\codea\Documents\timecode-sync-player-wt-output-engine-20260911-1247`）で作業すること。**
`C:\Users\codea\Documents\timecode-sync-player` は親も使う共有の場所なので使わない。

不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I4・I13 を含む）。
実機の前に一声かけること。**合否判定は書かないこと。**

---

## 欠陥

`ContinueOnTrackCoordinator` は `Decide` に入る前に 3 つのゲートを通る。

```csharp
if (_effects.IsNativeSeeking?.Invoke() == true) return SyncRequestResult.Deferred;
(int timePosRc, double playbackSeconds) = _effects.GetTimePos();
if (timePosRc != 0) return SyncRequestResult.Deferred;
if (!_syncService.TryMarkFileLoaded(playbackSeconds, _effects.GetTotalRenderedFrames())) return SyncRequestResult.Deferred;
```

3 番目が `TimecodeSyncService.TryMarkFileLoaded`:

```csharp
private const long FileLoadRenderedFrameProgress = 2;
private static readonly TimeSpan FileLoadTimeout = TimeSpan.FromSeconds(5);
...
long renderedFrameProgress = renderedFrameCount - _fileLoadStartedRenderedFrames;
if (playbackProgress < FileLoadPlaybackProgressSeconds ||
    renderedFrameProgress < FileLoadRenderedFrameProgress)
    return false;
```

`renderedFrameCount` の実体は `MainWindow.xaml.cs` の
`GetTotalRenderedFrames: () => _playbackPerformanceStats.TotalRenderedFrames` であり、
**CPU の WriteableBitmap 経路で描画したフレーム数**である。

**GPU 合成ではビットマップを描かないので、この値は永久に 0。**
よって `renderedFrameProgress` は常に `0 < 2` で、**ゲートは 5 秒のタイムアウトでしか開かない。**

## 測った事実

`TestResults/v3b` の `sync.evaluate` 計測（親が実施）。

| フェーズ | 長さ | gst の評価回数 | 1 秒あたり |
| --- | ---: | ---: | ---: |
| black-sweep | 36.3s | 496 | 13.68 |
| freeze-sweep | 36.3s | 371 | 10.23 |
| seek-a / b / c / back | 各 6.2s | **各 1** | **0.16** |

**mpv でも同じ**（seek-a/c/back が各 1 回）。アプリログにも証拠がある:

```
Continue mode: waiting for file load stability playback=5.167 mediaPos=5.240 renderedFrames=0
```

**影響**: `OutputBackend=Gpu` では、ファイルロード・トラック切替のたびに
**LTC 同期の補正が 5 秒間止まる**。ライブショー用途では見過ごせない。

---

## 依頼: ゲートを出力経路に依らない指標にする

### 満たすこと

1. **CPU 合成でも GPU 合成でも動く。** かつ **mpv でも GStreamer でも動く**
   （`Arrivals` のような shim 固有の値は GStreamer 専用なので単独では不可）
2. **ゲートの意味を保つ。** これは「読み込んだファイルが実際に描画され始めたか」を見る門であって、
   デコーダの出力数に置き換えると意味が変わる（デコードできていても表示されていない状態を通してしまう）。
   **「表示経路に到達したフレーム」を数えること。**
3. **既定経路のコストを増やさない**（不変条件 I4）。計測用の分岐を常時走らせない
4. **CPU 合成の挙動を変えない。** 現在 CPU 合成では正しく動いているので、そこは同じ数字で通ること

### 進め方の目安（方針は任せる）

- `GetTotalRenderedFrames` の供給元を、**いま有効な出力経路の公開フレーム数**にする
  （CPU 合成なら現行の `PlaybackPerformanceStats`、GPU 合成なら出力エンジン側の公開数）
- どちらを使うかを `MainWindow` で切り替えるのが素直だが、
  **他により良い形があればそれでよい。選んだ理由をコミットメッセージに書くこと。**

### テスト

- **単体**: GPU 合成に相当する条件（ビットマップ描画が 0 のまま）で
  `TryMarkFileLoaded` が **5 秒待たずに** 開くことを確認するテストを足す。
  **いまの実装ではそのテストが落ちること**（＝負の対照）を確認してから直すこと
- 既存の CPU 経路のテストが同じ結果で通ること
- 非E2E が全て通ること（現在 1750 件）

### 提出時に書いてほしいこと

- どの指標に変えたか、なぜそれが 4 条件を満たすか
- 負の対照（修正前にそのテストが落ちること）を確認した結果
- 非E2E の結果と `python scripts/check-shim-lock-rule.py` の結果
- **実機は親が回す。** 測り方の希望があれば書いてよい

---

## 注意

この修正で **V3 の測定値が変わる**。親は D4 統合後に V3 を測り直す。
**いまの V3 の数字（seek-c の -274ms など）を根拠に何かを結論づけないこと。**
