# 共有source保持区間の記録とコピー再取得の実証仕様

状態: 2026-09-10、実機比較を完了。結果は [実証結果](GPU-MUTEX-HOLD-RETRY-RESULTS-2026-09-10.md) を参照。本体へのGPU統合は開始しない。

## 目的

[表示準備通知の実証結果](GPU-READY-PUMP-RESULTS-2026-09-10.md) で、Spoutのコピー失敗（`copy.keyedMutexBusy`）が同じ画像の全画面描画区間に集中していた。次の最小単位として、次の2点を独立試作の範囲で確定する。

1. 共有sourceのkeyed mutexを両workerが実際に保持した区間を記録し、コピー失敗がその区間と一致するかを判定する。既存ログは `display.select.end`〜`present.start` などの上限推定でしか区間を知ることができなかった。
2. 保持の返却通知で起こされる同一出力枠内の1回だけの再取得（`--copy-retry signal`）が、送信位相に依存せず、保持画像の再送と送信画像年齢を減らすかを比較する。固定の待ち時間や位相の再調整は行わない。

## 既存ログの再解析（根拠）

別セッションの調査（`TestResults/gpu-out-inv-20260909T180637Z/findings.md`、2026-09-10 03:18 JST）を本セッションで再実行し、同じ値を得た（`TestResults/gpu-mutex-retry-session-20260910T0752Z/reproduce-hold-windows-existing-logs.txt`）。

| 4K run | GPU予定→select.end 平均ms | 保持上限幅 平均／p99／最大ms | 窓内copy busy | 保持区間内 |
| --- | ---: | ---: | ---: | ---: |
| tick 1 | 0.307 | 0.154／0.467／0.741 | 0 | — |
| ready 1 | 3.227 | 0.274／0.792／1.057 | 59 | 59（描画区間内50、返却までの尾部9） |
| ready 2 | 0.319 | 0.178／0.723／0.905 | 0 | — |
| tick 2 | 0.315 | 0.181／0.770／0.966 | 0 | — |

- Spoutの試行時刻は、対応するGPU合成tickの予定時刻＋4.000msちょうどに固定され、起床ジッタは平均0.047ms。
- ready 1だけ表示側の取得時刻が約3.2ms後ろへ動き、保持終端（約＋4.3ms）が固定位相4msのSpout試行と重なった。ready 2は通知が即時に返り、tickと同じ位置に戻った。
- 保持幅は1ms程度で、合成の60Hzは崩れていない。競合は合成負荷ではなく、読み取り側の排他の偶発的な重なりである。

仮説として、(H1) copy busyは全件が同一画像の表示側保持区間内で発生する、(H2) readyの結果の不一致は取得時刻の移動と固定位相の相対関係による、(H3) 位相変更は相対関係依存の対症にすぎない、(H5) 返却通知での同一枠内1回再取得は保持画像の再送と画像年齢を減らす、を置く。H1はまだ上限推定であり、新しい記録で確定させる。

## 実装

別セッションの調査で試作へ実装済み（03:24〜03:33 JST、findings.mdの進捗欄は未更新）。本セッションでソースをレビューし、ビルド（警告0・エラー0、生成物SHA256は当時のバイナリと一致）、`--self-test` 41件、解析器63件の成功を確認した。

- `display.mutex.acquire/release`（GPU worker）と `copy.mutex.acquire/release`（Spout worker）を、取得直後・返却直後のQPCで記録する。同じworker・scheduledQpc・画像stampで対になる。既定 `--copy-retry off` でも記録する。
- `--copy-retry signal`（split＋Spout限定）。初回 `copy.keyedMutexBusy` の後、画像lease・pool使用権・keyed mutexを持たずに、表示側の `display.mutex.release` 直後に立つ通知を最大 `min(4ms, 次回送信予定までの残り整数ms)` 待つ。復帰後に停止・期限を再確認し、最新画像を選び直して1回だけ再取得する。2回目の失敗は `copy.keyedMutexBusy.retry`、予算0・停止は `copy.retry.deadline`／`copy.retry.cancelled`。
- 通知の取り逃し（取得失敗と `Reset` の間に返却が入った場合）は予算満了まで待ってから同じ1回の再試行に進む。待ちは通知で起こされる機構であり、固定の待ち時間ではない。
- 送信 `SendTexture` の期限・mutex要求8ms・GPU完了確認・Present前の共有source返却・終了順序（送信スレッドjoin後に共有資源解放）は変更しない。通知handleは送信スレッド終了後に破棄する。

本セッションでの追加（サブエージェント実装、親レビュー）:

- 再試行時の `send.select.start/end` を `value=1` で区別する（初回は従来どおり0）。解析器は試行ごとに対を組み、再試行対には先行する初回対・`copy.keyedMutexBusy`・`copy.retry` の順序と期限を要求する。これがないと解析器は再試行runを「対の重複」として無効にする。
- `Analyze-MutexHolds.py` を `analyze_mutex_holds.py` に改名し、合成ログの単体テストを追加する。

## 比較条件

全run同一の新バイナリ。split／both／60Hz／mutex要求8ms／present-wait 0ms／fixed／display pacing tick／公式WinSpoutDXreceiver／monitor index 1（DISPLAY2 1920×1080/60Hz）。既存runnerで直列実行し、自分が起動したプロセスだけを扱う。

| 段階 | 条件 | 目的 |
| --- | --- | --- |
| 1080p確認 | windowed、12秒、warmup 2、位相0.5ms、`signal` | 新経路の正常終了・ログ整合・解析有効・再試行イベントの出現確認 |
| 4K負荷条件 | 生成キャンバス3840×2160、全画面1面、32秒、位相0.5ms、`off→signal→signal→off` | 保持区間（GPU予定＋約0.3〜0.5ms）へ意図的に試行を重ね、H1を能動的に確認し、signalの効果を測る |
| 4K通常条件 | 同上、位相4ms、`off→signal→signal→off` | 従来の基準条件で退行がないことを確認する |
| 補助（任意） | 位相4ms、`--display-pacing ready`、`off`／`signal` | 競合が実際に観測されたready条件での効果。起動依存のため参考扱い |

位相0.5msは競合を再現するための負荷条件であり、候補設定ではない。

## 合格条件

- 全runが `completedNormally`、`validPerformanceResult=true`、error 0、イベント欠落0、プロセス残存なし。
- H1: 全runで `unexplained_busy=0`（窓内の初回copy busyがすべて同一画像の表示側実保持区間内）。1件でも区間外なら仮説を修正し、signalの根拠を再検討する。
- 負荷条件（位相0.5ms）: `off` でcopy busyが再現すること。`signal` では再試行の取得成功が再試行数の90%以上、Spoutの同一ID再公開数と送信画像年齢の最大値が `off` より減少、Spout公開の最大間隔が悪化しない、合成・表示が60Hzを維持、`copy.retry.deadline` が0。
- 通常条件（位相4ms）: `signal` と `off` で、Spout公開Hz・最大間隔・画像年齢平均、表示Hz、合成開始遅れ、アプリCPU秒に系統的な差がない（ABBAの両ペアで同方向の差が出ないこと）。
- 判断: 上記を満たす場合、試作の既定を `signal` に変えるかは結果文書で判断する。満たしても本体採用・GPU統合の許可ではない。

## 評価指標

公開頻度に加えて、Spoutの異なる画像ID数、同一ID再公開数、送信画像年齢（平均／p99／最大）、公開最大間隔、表示の未準備・busy件数、両workerの実保持幅（平均／p99／最大）、初回busyと再試行busy、再試行の成功数と新しいIDを得た数、合成開始遅れ、pool占有、アプリCPU秒、終了挙動を記録する。受信画像の一意性、物理表示、実受信時刻は測定範囲外とする。

## 環境と制約

- 本セッション開始時はRDP接続中で、物理表示が列挙されなかった。利用者のRDP切断後にコンソールへ移行し、DISPLAY1／DISPLAY2とも1920×1080/60Hzが列挙された。過去runではDISPLAY1が3840×2160だったため、表示構成の差として記録する。表示設定は変更しない。
- 性能試験中にビルドや別の重い試験を並行しない。異常・終了失敗があれば以後の実機試験を止め、状態を保存する。WPR／UAC記録、GPUリセット、ドライバー変更、OS再起動は行わない。
- 本体（`src/`・`tests/`）は変更しない。hwdec、mpv経路、フェード、Spout無効化問題、OBS問題は範囲外。
