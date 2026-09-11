# 合成位相の vblank 整列（`--compose-align vblank`）の実証仕様

状態: 2026-09-11、実装・実機比較を完了。lead は実測に基づき 3ms を比較基準とした。結果は [実証結果](GPU-COMPOSE-ALIGN-RESULTS-2026-09-10.md) を参照。

## 目的

[vblank 実証](GPU-VBLANK-PACING-RESULTS-2026-09-10.md) で、表示は vblank 時計に従い Present→走査が約2.3msで一定になったが、合成 tick が自由走行のため生成→走査（総遅延）が合成と vblank の位相差で 10〜18ms と起動依存だった。合成 tick の位相を主表示の vblank に整列させ、総遅延を起動非依存の数 ms に収められるかを実証する。

## 動作

- `--compose-align off|vblank`（既定 off）。`vblank` は `--display-pacing vblank` かつ全画面出力を含む場合のみ許可。`--compose-lead-ms`（double、既定 1.5、0.5〜8.0、`vblank` 以外で非既定値は拒否）。根拠: 4K の合成開始→公開の実測 p99 約0.9ms と起床遅れ p99 約0.6ms。
- 目標: 合成 tick の予定時刻を `predictedVblank − margin − lead` に合わせる（表示目標は従来どおり `predictedVblank − margin`）。表示の予測（統計 `SyncQPCTime` と median 周期）を再利用する。
- 方法（位相の逐次補正、周期は変えない）: 既存 `TickSchedule` の起点に共有オフセット（`ScheduleOffset`、Interlocked で読み書き）を加える。GPU worker は各 `present.scanout` 観測後に、次の合成予定時刻 D と望ましい時刻 W（現在時刻より後の最初の `vblank − margin − lead`）の位相誤差 e = wrap(D − W, ±period/2) を求め、起点を `−clamp(e, ±slew)` だけ動かす。slew は 1 tick あたり 0.5ms（既定、固定、振らない）。収束後は e がほぼ 0 に保たれ、表示周期と合成周期の差は slew の範囲で追従する。
- Spout worker の `TickSchedule` も同じ `ScheduleOffset` を読むため、合成に対する送信位相（4ms）は維持される。**設計上の注記: 整列中は合成・Spout の周期が主表示の実周期（例 59.94Hz）に追従する。** 設計文書の「Spout を表示先の更新周期へ暗黙に従属させない」との関係は結果文書で明示し、実周期（合成公開の平均間隔）を記録する。
- 起点の変更は次の tick 以降にだけ効く。既に取得済みの scheduledQpc は変えない。tick の飛ばし・追いつきの既存規則（`schedule.late`）は維持し、起点を大きく動かしても過去の tick を貯めない（`TickSchedule.Take` の `Math.Max(nextIndex, floor(...))` により自動的に飛ぶ）。
- 統計を得る前（bootstrap 中）は補正しない。統計が止まった場合は最後の予測で外挿し、補正も止める。
- 記録: `compose.align`（GPU worker、qpc=観測時刻、value=位相誤差 e µs（符号付き）、deadlineQpc=W、detail=適用した補正量 µs（符号付き））を補正判断ごとに1件。manifest に `composeAlign`、`composeLeadMs`、`alignSlewMs`。
- 待機中に lease・keyed mutex・フェンス待ちを持たない規則、停止優先、終了順序は不変。

## 解析

`analyze_probe.py` に `composeAlign` セクション: 観測件数、位相誤差 e（平均／p95／p99／最大、絶対値）を「収束前（最初の1秒）」と「解析窓」で分けて集計、補正量の合計、合成公開の平均間隔（実周期）と表示の refresh 周期（統計の median）の差。既存 `scanout` セクション（生成→走査）が主指標。古いログは `available=false`。整列イベントは `composeAlign=vblank` のときだけ許可し、`ScheduleOffset` を反映した scheduledQpc が単調増加であることを検証する。

## 管理テスト

位相誤差の wrap と clamp、収束（誤差が slew 以下になる）、起点変更後の tick が過去を貯めない、bootstrap 中は補正しない、Spout 側の予定時刻が同じオフセットを反映して位相 4ms を保つ、オプションの組み合わせ検証。フェイク時計で行う。

## 実機と合格条件

同一バイナリ、fence・位相4ms・retry off・margin 3ms・monitor index 1（60Hz）。全画面 1080p 12秒（align vblank）→ 4K `align off`（vblank のみ）→ `align vblank` → `align vblank` → `align off` 各32秒。

- 全 run 有効・error 0、表示 60Hz、refresh 連続差すべて1、表示 ID 飛び 0、合成・Spout 60Hz、Spout 異なる ID 1320。
- align vblank: 生成→走査の平均が両 run で 1ms 以内に一致し、`margin + lead + 1ms`（約 5.5ms）以下。位相誤差の p99（窓内）が 0.5ms 以下。合成開始遅れと CPU が align off と同等。
- 満たせば後続基準を fence＋vblank＋align とし、合成が主表示の実周期に追従する扱いを設計文書へ反映する（Spout の周期をどう扱うかの合意が必要）。

## 範囲外

複数画面、mpv・CPU アップロード、本体変更、slew・lead の掃引。
