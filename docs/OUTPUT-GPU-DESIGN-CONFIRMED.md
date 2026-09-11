# 出力側 GPU 設計の確定版（2026-09-11）

2026-09-09〜11 の設計議論と独立試作（`scripts/GpuOutputProbe`）で確定した、合成→全画面・Spout・プレビューの設計を1本にまとめる。個別の実証文書は根拠として末尾から参照する。合意済みの製品動作（キャンバス、テストカード、終了、フェード後回し等）は [OUTPUT-PIPELINE-DESIGN.md](OUTPUT-PIPELINE-DESIGN.md) が正で、本文書はその GPU 実装方針を確定させたもの。本体への統合は未着手。

## 構造

```text
ソース（mpv／GStreamer／HAP）── IVideoSource.TryAcquire(世代, 位置) ──▶ 合成層（GPU worker）
                                                                    │  固定キャンバスへ合成、テストカード上位レイヤー
                                                                    ▼
                                                     合成画像 pool（3枚、lease、共有フェンス）
                                    ┌──────────────────────┬────────────────────┐
                                    ▼                      ▼                    ▼
                          全画面（同一デバイス）      Spout worker（同一デバイス）   プレビュー（後段）
                          vblank 位相基準で Present    合成に対する位相 4ms で送信
```

- 操作 UI は WPF のまま。映像は D3D11 で合成・表示。
- 各出力は合成済み画像の寸法・番号・寿命だけを扱い、タイムラインの判断（クリップ、シーク、Freeze、Black）はソース選択と合成層に置く。

## 確定した規則

| 項目 | 規則 | 根拠 |
| --- | --- | --- |
| 合成画像の所有 | 3枚の pool。合成は最新でなく読者0の領域に書き、GPU 完了確認後に最新へ置換。読者は lease を取り、GPU 使用完了まで返さない | [初回実証](GPU-OUTPUT-PROBE-RESULTS-2026-09-09.md)、[フレーム基盤](OUTPUT-FRAME-PIPELINE.md) |
| 書き手→読者の順序 | NT ハンドル共有＋D3D11.4 共有フェンス（値＝画像番号）。読者同士は排他しない（keyed mutex を使わない） | [フェンス実証](GPU-FENCE-VSYNC-RESULTS-2026-09-10.md): 衝突0、Spout 年齢最大 89／122→23／22ms |
| 読者→書き手の安全 | CPU lease（読者0の領域にしか書かない） | 同上 |
| 表示の時計 | DXGI フレーム統計（`GetFrameStatistics`）で次の vblank を予測し、`vblank − margin(3ms)` に最新画像を Present。1 vblank 1表示、失敗は次の vblank。目標到達済みの表示は合成より優先 | [走査計測](GPU-SCANOUT-STATS-RESULTS-2026-09-10.md)、[vblank 実証](GPU-VBLANK-PACING-RESULTS-2026-09-10.md) |
| 合成の位相 | 主表示があれば `vblank − margin − lead(3ms)` に逐次補正（slew 0.5ms/tick）。周期は主表示の実周期に追従。表示がなければ自由走行 60Hz | [整列実証](GPU-COMPOSE-ALIGN-RESULTS-2026-09-10.md): 総遅延 5.6ms で起動非依存、表示落ち0 |
| Spout | 専用 worker・同一デバイス（段階 3 で別デバイスから変更）。合成 worker が compose 完了の測定後に同じ context で保持テクスチャへ GPU コピーし、送信 worker は専用フェンスでコピー完了を確認してからアクセス mutex（要求 8ms・期限＝次回予定）を取得し SendTexture。失敗は保持画像の再送、資源は無効化しない | [位相](GPU-SEND-PHASE-PROBE-RESULTS-2026-09-09.md)、[8ms](GPU-MUTEX-8MS-PROBE-RESULTS-2026-09-09.md) |

### 段階 3 の設計差異（2026-09-11）

Spout 送信は当初「別デバイス・送信 worker が GPU コピー」だった。4K 実測で送信 worker のコピーと合成が物理 GPU を奪い合い、合成完了待ちに毎秒 1〜2 回 8〜20ms の外れ値が出て lead が 8ms に張り付いたため、次のように変更した。

- コピーは合成 worker が `compose.complete` の測定後に同じ context へ発行する（Spout 無効時は発行しない）。
- 送信 worker は段階リング（保持テクスチャ 2 枚）の Ready を選び、専用の Event query（`GpuDevice.CreateFence`）でコピー完了を確認してから mutex 取得と SendTexture のみを行う。
- immediate context は free-threaded 前提でスレッド間共有する。完了待ち query はスレッドごとに専有し、保持テクスチャの書込み/読取りは段階リングで排他する。読者同士（全画面・Spout）は排他しないまま。
| 待ち | 高分解能 waitable timer（`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`）。起床遅れ p99 0.6ms | [整列実証](GPU-COMPOSE-ALIGN-RESULTS-2026-09-10.md) |
| 終了 | 新規処理停止→送信 worker join→送信側の共有資源解放→合成側の解放。待機中も UI 応答、強制終了は最初から選択可 | 設計文書、試作の UI |
| 異常 | GPU 完了の期限超過を資源解放の理由にしない。デバイス消失は一度だけ自動復旧、再発は手動 | 設計文書 |
| ソース契約 | 世代排除・最新優先・準備不可は「なし」・有限 lease・同一デバイス・非ブロッキング | [ソース契約](GPU-SOURCE-CONTRACT-SPEC.md) |

## 実測（4K 生成画像、fence＋vblank＋align、60Hz 表示先、公式受信機あり）

| 指標 | 値 |
| --- | --- |
| 合成／表示／Spout 公開 | 60.000／60.000／60.000Hz |
| 生成→走査（表示の総遅延） | 平均 5.6〜5.7ms、p99 6.2ms、起動間差 0.1ms |
| Present→走査 | 平均 2.4ms |
| 表示落ち・重複・画像飛び | 0 |
| Spout 送信画像年齢 | 平均 5〜6ms、最大 9〜13ms（受信機の mutex 保持時を除く） |
| アプリ CPU | 2.4〜4.9秒／32秒（0.1〜0.15コア相当） |

物理 4K60 表示、複数画面、120Hz、mpv／実素材は未測定。GPU 生成画像の結果であり、素材側（デコード→GPU）は別途。

## 候補から外したもの

- keyed mutex＋返却通知後の再取得（`--copy-retry signal`）: 有効だが、フェンス方式で原因ごと消えたため代替案として記録。
- 表示通知直後の Present（`vsync`）: 走査基準で約1フレーム遅い。
- 表示準備の固定待ち（0／1ms）、送信位相の掃引: 起動依存の相対位相に対する対症で、根本解決にならない。
- 全画面ごとの専用スレッド／デバイス: 必要になっていない。

## 未解決・次の項目

1. 素材側: CPU 画像アップロードの上限、`hwdec=d3d11va-copy`、GStreamer D3D11VA（別 worktree で進行中）、HAP（[調査](HAP-GSTREAMER-INVESTIGATION-2026-09-11.md)、GStreamer 移行後に `video/x-hap` 分岐として追加）。
2. 120Hz 表示先、3面、非整列画面の年齢。
3. 表示準備未完了（`present.notReady`）が起動によって多発する事象は tick 方式に固有で、vblank 方式では発生しない。原因は未特定のまま。
4. 本体統合の順序: ソース契約→合成層→全画面→Spout→プレビュー。既存の `OutputFrame` 経路は切替可能な形で残す。ソース契約（[仕様](GPU-SOURCE-CONTRACT-SPEC.md)）と固定キャンバスの配置計算（[仕様](CANVAS-PLACEMENT-SPEC.md)）は試作に実装・実機確認済み（2026-09-11）。

## 本体統合

段階導入の計画は [本体統合の設計](OUTPUT-GPU-INTEGRATION-PLAN.md) を参照（バックエンド切替、OutputEngine、MpvSnapshotSource、GStreamer 接続点、段階ごとの合格条件）。

## 試作の比較基準と手順

`--source-sync fence --display-pacing vblank --present-margin-ms 3 --compose-align vblank --compose-lead-ms 3 --copy-retry off --send-phase-ms 4 --mutex-wait-ms 8`、split／both／60Hz、公式 WinSpoutDXreceiver、monitor index 1（DISPLAY2 1080p/60Hz）。実行は `TestResults/gpu-mutex-retry-session-20260910T0752Z/Invoke-Trial.ps1`（Windows PowerShell 5.1）、集計は同 `evaluate_runs.py`。試作 CLI の既定値は従来のまま（tick／keyed／align off）。実機前に `query session` で console であること、他 worktree の重い処理がないことを確認する。

## 実証文書の一覧（時系列）

初回 → [mutex 待ち](GPU-MUTEX-WAIT-PROBE-RESULTS-2026-09-09.md) → [送信位相](GPU-SEND-PHASE-PROBE-RESULTS-2026-09-09.md) → [受信機対照](GPU-RECEIVER-CONTROL-PROBE-RESULTS-2026-09-09.md) → [8ms](GPU-MUTEX-8MS-PROBE-RESULTS-2026-09-09.md) → [準備待ち](GPU-PRESENT-WAIT-PROBE-RESULTS-2026-09-09.md) → [区間切替](GPU-PRESENT-SEGMENT-PROBE-RESULTS-2026-09-09.md) → [通知待ち](GPU-READY-PUMP-RESULTS-2026-09-10.md) → [保持区間・再取得](GPU-MUTEX-HOLD-RETRY-RESULTS-2026-09-10.md) → [フェンス・vsync](GPU-FENCE-VSYNC-RESULTS-2026-09-10.md) → [走査計測](GPU-SCANOUT-STATS-RESULTS-2026-09-10.md) → [vblank](GPU-VBLANK-PACING-RESULTS-2026-09-10.md) → [整列](GPU-COMPOSE-ALIGN-RESULTS-2026-09-10.md)。本体側の契約は [契約の突き合わせ](OUTPUT-GPU-CONTRACT-MAPPING-2026-09-10.md)。
