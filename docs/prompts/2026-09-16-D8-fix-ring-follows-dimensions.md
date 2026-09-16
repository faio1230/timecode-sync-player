# D8 の原因の判定と修正方針（承認）: リングを寸法に追従させ、GPU 合成ではリング外のリースを使わない

作成: 2026-09-16 14:40、親。担当: `w5:p6`（作業ツリー `timecode-sync-player-wt-b`、ブランチ `agent-b`）。
入力: 実装側の 1 次報告（14:03）と 2 次報告（14:12、`TCS_LEASE_LOG=1` の change / same 測定、`57ff8ca` のハーネス）。
親は shim（`ensure_ring_locked`、`texture_of_lease`、`on_new_sample`）とアプリ（`GStreamerSource.TryAcquire`、`GstRingPolicy`、
`OutputEngine.AcquireGStreamer`、`ComposeLayer.KeepHeld`）を独立に読み、報告の筋と一致することを確認した。

**D8 は D6（2026-09-14、`docs/prompts/2026-09-14-D6-device-lost-ring-reopen.md`）と同じ欠陥。** D6 は 720p → 1080p、D8 は 1080p60 → 720p25。
D6 は「未修正・記録のみ」のまま残っていた。以後は D8 として扱い、記録は D6 の項に統合する。

---

## 1. 原因の判定（親）

### 一次原因: 解像度が変わるとリングが使えず、GPU 合成が別デバイスの非共有テクスチャを描いている

1. shim の共有リングは**最初の寸法で 1 回だけ作る**設計（`ensure_ring_locked` のコメント「Created once: dimension changes fall back to the legacy sample path」）。
   寸法が変わると `ensure_ring_locked` が false を返し、フレームは `ring_slot = -1`（旧サンプル経路）で積まれる。
   測定でも `ring: dimension change requested 1280x720 but ring is 1920x1080; using legacy sample lease` の直後に `slot=-1` のリースが出ている
2. 旧サンプル経路のリースの実体は `texture_of_lease` が返す **shim デバイス上のデコーダプールのテクスチャ**で、共有ハンドルを開いたものではない
3. GPU 合成は `GStreamerSource.TryAcquire` の `Slot < 0` 分岐で `TryGetLeasedTexture` の生ポインタを受け取り、
   `OutputEngine.AcquireGStreamer` が**合成デバイス側で `Surface`（SRV）を作って描く**。別デバイス・非共有資源の不正使用
4. GPU フォールト → アダプタ全体が `DXGI_ERROR_DEVICE_REMOVED`（`0x887A0005`、`GetDeviceRemovedReason` も同値。誤検知ではない）。
   **shim のデバイスも一緒に死ぬ**（別デバイスだが同じアダプタ）

### 二次: 復旧後に止まる・落ちるのは、shim のデバイスが死んだまま

- shim にはデバイス消失の検出も再作成も無い。パイプラインは進まなくなる（`playbackRate` 0.085〜0.18、ロード完了も出ない）
- アプリ側の復旧は成功して見える（共有ハンドルの割当は生きているので open は通る）が、死んだデバイスの資源に対する
  `sharedFence.Signal`（`OutputEngine.cs:1018`）でドライバ内アクセス違反 → プロセス停止。共有フェンス自体は作り直されているので「古いフェンスの使い回し」ではない
- D6 の「復旧後に旧寸法（1280x720）で開き直して黒のまま」も同じ筋。`EnsureRing` は `ring != null` なら寸法を見ない

### 確定していないが、修正方針に影響しない点

- フォールトを起こした直接の API（SRV 作成か Draw か）。どちらでも「別デバイスの非共有資源を使った」ことが原因なので追わない
- 同解像度の切替では 3 切替とも 0 件（測定済み）。解像度変更が引き金であることは確定

---

## 2. 修正方針（承認済み。3 段。順に統合する）

### 修正 1: 安全網。GPU 合成ではリング外（slot < 0）のリースを使わない（先に単独で統合）

- `GstRingPolicy.Decide`: `slot < 0` は **`Reject`**（`UseLegacy` を廃止）。`GStreamerSource.TryAcquire` の旧サンプル経路の分岐を削除
  （`player.Release()` して `NotReady`。I7 により合成は Held を描く）。**`TryGetLeasedTexture` 自体は CPU 合成の互換アダプタが使うので残す**
- `OutputEngine.AcquireGStreamer` の per-lease `Surface` を作る分岐（`OutputEngine.cs:1263-1269` 付近）は到達不能になるので削除
- 1 回だけ警告ログ: 「GStreamerSource: リング外のフレームを受け取りました WxH（リングは WxH）。GPU 合成では使いません」。以後は 2 秒ごとの統計に件数だけ
- テスト: `GstRingPolicyTests`（slot<0 → Reject）、`GStreamerSourceTests`（slot<0 のリースは返却されて NotReady）。旧サンプル経路のテストは削除
- **検証（実機、D8 ハーネス change モード）**: デバイス消失 0 件、プロセス生存、`playbackRate` 1.0 のまま、exit 0。
  **この段では 720p クリップは Held（切替前の絵）のまま**で正しい。ここで止めず、修正 2 へ

### 修正 2: 本修正。リングを寸法に追従させる（作り直し＋世代番号 epoch）

**shim（`native/gst-shim`）**

- `ensure_ring_locked` は寸法不一致なら **`destroy_ring` → 作り直す**。`ring_epoch`（`uint32_t`、作るたびに +1、0 は「無し」）を持つ
- `FrameSlot` と `lease_info` に epoch を持たせる。作り直し時に、FIFO 内の**旧 epoch のリング参照項目は破棄**（`gst_sample_unref`、`delivery_replaced` に計上）。
  リース中の `leased_slot` が旧 epoch なら、返却時に新リングのスロット占有を触らない（epoch で判別）
- 旧 epoch のテクスチャとフェンスの COM 参照は shim 側で Release してよい（合成側が開いたハンドルが割当を保持する）
- 作り直しは `frame_lock` の中で D3D の `CreateTexture2D` / `CreateFence` を呼ぶ。GStreamer の状態変更・シークではないので **I13 には触れない**。
  1 回あたり数 ms で、解像度変更のときだけ。`ring: recreated WxH epoch=n（was WxH）` をログ
- ABI: `TcsFrameInfo` の**末尾**に `uint32_t ring_epoch` を追加（C# の `GstNative.TcsFrameInfo` も同じ順序で追加）。
  リング情報は既存の `tcs_player_ring_info` の引数を変えず、新関数 `tcs_player_ring_epoch(player, uint32_t* out)` を足す。ヘッダのコメントと README の「Created once」を書き換える
- shim テスト（`native/gst-shim/test/shim_test.cpp`）: `--policy-only` に「epoch を跨ぐ slot 割当・evict」のケースを足す。実素材のテストに「1080p → 720p → 1080p のロードで `ring: recreated` が 2 回、lease_outstanding=0 で終了」を足す

**アプリ（`src/TimecodeSyncPlayer/Output/GStreamerSource.cs` ほか）**

- `RingResources` に `Epoch` を持つ。`TryAcquire` で、リースの epoch が現行リングの epoch と違えば **新しいリングを開き直す**（`TryGetRingInfo` + `tcs_player_ring_epoch`）
- **旧 `RingResources` は、それを参照するリースが全部返るまで破棄しない。** `ComposeLayer.KeepHeld` は Held としてリースを保持し続ける
  （`ReferenceEquals(held.Value.Lease, acquired.Value.Lease)`）ので、Held が旧リングを指したまま次の epoch のフレームが来る。
  実装: `SharedLease` が自分の `RingResources` を参照し、`RingResources` は参照数（未返却リース数 + 現行なら 1）で管理。
  現行を降ろした時点で 0 なら即 Dispose、そうでなければ最後のリース返却時に Dispose
- `TryGetRingSurface(slot)`、`IsRingFenceComplete`、`WaitRingFence` は**そのリースが指す epoch のリング**を使う（現行リングではない）
- 復旧経路（`TryReopenOn` / `DropRingResourcesForRecovery`）は現行リングを捨てて `EnsureRing` するので、epoch を持てば同じ道で動く。D6 の「旧寸法で開き直す」も消える
- ログ: 「GStreamerSource: 共有リングを開き直しました WxH epoch=n」
- テスト（`GStreamerSourceTests`）: (a) epoch が変わったリースでリングを開き直す、(b) 旧リングは Held（未返却リース）がある間は生きていて、返却で破棄される、
  (c) 開き直しに失敗したら Reject（NotReady）で落ちない
- `docs/OUTPUT-GPU-INVARIANTS.md` I9 の「デバイス消失だけが再作成の根拠」は**親が**「デバイス消失と寸法変更だけ」に改める。実装側は触らない

**検証（修正 2）**

| 項目 | 合格の目安（判定は親） |
| --- | --- |
| ビルド | shim Debug、アプリ、テスト。警告 0・エラー 0 |
| shim テスト | failures=0（`--policy-only` と実素材） |
| ロック規則 | `python scripts/check-shim-lock-rule.py` PASS |
| 非E2E | 全件 |
| D8 ハーネス change | デバイス消失 0、`ring: recreated 1280x720`、`共有リングを開き直しました 1280x720`、切替後も `gpuPublishedFrames` が増える、`playbackRate` 1.0、exit 0 |
| D8 ハーネス same | 従来どおり（`ring: recreated` 0 件） |
| D6 の向き | 720p → 1080p も 1 本（`D5BlackAfterSwitchReproE2ETests` の素材差し替えか、ハーネスに mode を足す）。Spout 受信の黒比率 0 |
| 元のテスト | `GStreamerBackend_SurvivesRepeatedTrackSwitches` 合格（4 素材、解像度と fps が混在） |
| E2E 全件 | 出荷構成で。R1 の 58/62 から失敗が減っていること（D8 の 1 件と (b) の 2 件） |

### 修正 3（**保留。実装しない**。D9 として記録する）

本物の TDR（ドライバ更新など）でも shim のデバイスは死ぬが、shim に検出・再作成が無い。アプリの復旧はハンドルを開けてしまい、
死んだ資源への `Signal` で落ちる。案: shim に `tcs_player_device_removed_reason(player)` を足し、復旧時に非 0 なら player 再生成へ。
強制 TDR は規則で禁止なので検証できない。**利用者の判断待ち**（親が `docs/release-0.4-plan.md` の判断待ちに載せる）。

---

## 3. 進め方

1. まず main `d54f357`（文書のみ）を `agent-b` へ通常マージ
2. 修正 1 をコミット → 実機（D8 ハーネス change）→ **報告**（数字と証跡）。親が確認してから修正 2 へ
3. 修正 2 は設計に疑問があれば**先に質問**（選択肢＋根拠＋計測値）。特に「旧リングの寿命」「epoch の ABI」は勝手に変えない
4. 修正 2 をコミット → 上の検証表 → 報告
5. 報告は、コミット／変更ファイル／非E2E 件数／shim テスト／ロック規則／D8 ハーネスの数字（デバイス消失回数、rate、published frames、ring ログ）／
   E2E 全件の件数（実行・合格・失敗、失敗の名前）／設計差異／未検証。**合否は書かない**

## 4. 守ること

- 不変条件 `docs/OUTPUT-GPU-INVARIANTS.md`（I1〜I13）。特に I6（コールバックでブロックしない）、I7（NotReady で黒を出さない）、I8（停止順序）、I13
- 実機は 1 本ずつ。使う前に親へ一報。同期担当（`w5:p3`）も T2 で実機を使う予定なので、親が順番を決める
- main への書き込みはしない。コミットは `agent-b`、メッセージは日本語
- `Output/` の他のファイル（`ComposeLayer`、`GpuDevice`、`SpoutSender` など）は、この修正に必要な範囲を超えて触らない
