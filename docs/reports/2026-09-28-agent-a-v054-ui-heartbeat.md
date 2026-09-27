# 起動直後の UI スレッドの生存記録（ui.heartbeat）

- 作成: 2026-09-28（サブエージェント、ブランチ `agent-a-v054`、HEAD `ffd0dce` の上）
- コミット: `68d7155`
- 背景: L-2 で 1 回、起動直後（4K の素材を読み込んだ直後、同期の前）に UI オートメーションの検索が `COMException: Operation timed out`（`E2EAppRunner.Combo` → `ConfigureLtc`）。
  その区間の app ログに UI スレッドの生存を示す行が無く、止まっていたかを切り分けられなかった
- 実機・E2E は回していない

## 1. 何を記録するか

- `MainWindow` のコンストラクタの先頭から 30 秒、100ms ごとに Debug で 1 行:
  - 始まり: `ui.heartbeat start intervalMs=100.0 windowMs=30000.0`
  - tick: `ui.heartbeat seq=<n> lateMs=<遅れ>`（小数 1 桁）
  - 終わり: `ui.heartbeat end reason=window|closing ticks=<n> maxLateMs=<最大の遅れ> elapsedMs=<始まりからの経過>`
- 遅れ（lateMs）= 直前の tick（最初は始まり）からの経過 − 100ms。WPF の `DispatcherTimer` は発火のたびに次の予定を「今 + 間隔」で取り直すので、
  固定の格子（始まり + n × 100ms）ではなく直前の tick を基準にした。固定の格子だと、正常でも発火の数 ms の遅れが積み上がって見える
- 毎 tick を出す（遅れが大きいときだけにはしない）。始まりの行の後に行が途切れていれば、その時刻から UI スレッドが回っていない。
  区間の途中でプロセスが終わったときは終わりの行が無い。止まったまま殺された場合と区別できるよう、最後の tick の時刻が残る
- 量: 1 回の起動で約 300 行 + 2 行

## 2. 区間の決め方（起動から一定時間にした理由）

- 「起動から最初の同期の適用まで」は採らなかった:
  - 最初の同期の適用（`sync.apply`）は `LtcSyncController.ApplySyncRequest` にあり、MainWindow へ知らせる口が無い。記録のために新しい通知を足すことになる
  - LTC を使わない起動では同期が一度も適用されないので、それだけでは区間が閉じない。上限が別に要り、終わりの条件が 2 つになる
- 起動から 30 秒の根拠: 2026-09-26〜27 の app ログ（試験の起動 約 2800 回分、重複を含む）で、起動（`=== TimecodeSyncPlayer … 起動 ===`）から最初の `sync.apply` までが 8.8〜18.8 秒。
  L-2 の時間切れは同期の前（`ConfigureLtc`）なので、この範囲に入る。30 秒はその最大に余裕を足した値。
  L-2 の起動から `ConfigureLtc` までの時刻は確かめていない（検証機の遅い機械では 30 秒を超える可能性は残る）
- 始まりを `MainWindow` のコンストラクタにしたのは、`Window_Loaded` の初期化（プレイヤーの生成など、UI スレッドを占める処理）も区間に含めるため。
  ディスパッチャの処理が始まる前に作ったタイマーなので、最初の tick の遅れには表示と `Loaded` までの時間が入る

## 3. 挙動を変えないこと

- 記録専用のクラス `UiHeartbeatRecorder`（状態は記録のためだけ）と、専用の `DispatcherTimer` 1 つ。製品の判断・状態・既存のタイマー（`OnTick`）には触れない
- トレースや環境変数では切り替えない（常に同じ経路）。ただし Debug の行なので、Release（最小レベル Information）のログには出ない。E2E・実機の試験の Debug ビルドには出る
- タイマーの優先度は `DispatcherPriority.Normal`。既存の `OnTick`（Background）や描画更新（Background）より上なので、それらの混雑ではなく UI スレッドが処理を回しているかを見る。
  この優先度のタイマーの処理は、ログの 1 行の書き込み（Serilog のファイルシンク、非同期のフラッシュ）だけ
- 区間の後: 終わりの行を出し、タイマーを止めて `Tick` を外し、参照を捨てる
- 終了手順: `MainWindow.Dispose()` の先頭（資源の破棄より前）で、区間の途中なら `end reason=closing` を出してタイマーを捨てる。区間の後なら何もしない。
  UI スレッドの上で止めるので、破棄中のタイマーの発火とはぶつからない。アプリのログの閉じ（`App` の終了）より前に行う

## 4. テスト（`UiHeartbeatRecorderTests`、6 件、偽の時刻で固定）

- 始まる前は何も出さない（Tick・Stop とも）
- 始まりの行が出る
- 遅れの計算: 始まり 0、tick 100 → 0.0、tick 350 → 150.0、tick 452.5 → 2.5（直前の tick が基準）
- 30 秒の区間を過ぎた tick で終わりの行（`end reason=window ticks=3 maxLateMs=28600.0 elapsedMs=30000.0`）を出し、false を返す（呼び出し側がタイマーを捨てる）
- 終わった後は Tick・Stop・Start のどれも何も出さない（区間の外では出ない、始め直さない）
- 区間の途中の Stop は `end reason=closing` を 1 回だけ出す
- 非E2E: 2747 件合格、失敗 0、スキップ 0（`68d7155`。このうち追加 6 件）。ビルド 0 エラー
- MainWindow の配線（タイマーの生成・破棄）は単体の試験が無い。実機のログで start と end の行を確かめる必要がある

## 5. 未解決の疑問

1. L-2 の起動から `ConfigureLtc` までの時刻（30 秒に入るか）は、その回のログで確かめていない
2. Release のログには出ない。検証機の試験が Release で走るなら、この記録は得られない
3. UIA の検索は UI スレッドのメッセージ処理で答える。tick が遅れずに出ていて UIA だけが時間切れになった場合は、UI スレッドではなく UIA の側（試験のプロセス・UIA のキャッシュ）を疑う材料になるが、この記録だけでは UIA の要求の処理時間は分からない

## 追補（2026-09-28、終わりの行が実機の終了で出なかった件）

- 事実（親の実機の確認）: 終了したとき `end reason=closing` が出ず、生存記録は資源解放の途中（seq=245）まで出続けてプロセスの終わりで途切れた。
  終了の手順（`ExitCoordinator` の段 → 資源解放）は `MainWindow.Dispose()` を通らないので、`StopUiHeartbeat("closing")` が呼ばれていなかった
- 変更（`ExitCoordinator` と `MainWindow`）:
  - `ExitCoordinator` のコンストラクタに `shutdownStarting`（省略可）を足した。通常終了は `NormalExitRequested` で段の実行を始める直前（最初の段の前）に 1 回、強制終了は `ForceRequested` で `forceExit` の前に 1 回呼ぶ。例外は `終了の開始の通知に失敗` の Error で記録し、手順は続ける
  - `MainWindow` はここで `StopUiHeartbeat("closing")` を呼ぶ。`Dispose()` の呼び出しは残した（区間が閉じた後なので 2 回目は何も出ない）
  - 入口を `OnClosingRequested` ではなく `NormalExitRequested` にした理由: `OnClosingRequested` は確認のダイアログを出すだけで、利用者が取り消すと再生に戻る（終了が決まっていない）。最初の段の前は終了が決まった時点で、ここから先は段の行（`shutdown stage.begin …`）が続く
- テスト: `ExitCoordinatorTests.NormalExit_NotifiesShutdownStartingBeforeTheFirstStage_ClosingTheHeartbeat`。確認の段階では end が出ず、通常終了で end が最初の段（新規受付停止）より前に 1 回だけ出る。Dispose 相当の 2 回目の Stop では出ない
- 非E2E: 2758 件合格、失敗 0、スキップ 0（v0.5.4 の `45005ed` を取り込んだ後）
- 未確認: 実機の終了で `end reason=closing` が段の行の前に出ること
