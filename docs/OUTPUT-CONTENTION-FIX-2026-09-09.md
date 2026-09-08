# 全画面・Spout併用時の公開順序の改善

全画面とSpout併用時、WPF Bitmapの即時Lock取得可否に応じて送信順序を選ぶ候補2を、短時間の固定条件で確認できた限定改善として採用する。今回の旧版再測定55.727fpsに対して59.455／59.591／58.273fpsだった。最後の反復では低下があり、常時60fpsは未達。S（Spout＋公式SDK受信）とF（全画面・Spout OFF）は各1回60.000fpsだった。

## 実装と例外処理

通常フレームで全画面とSpoutが両方有効な場合だけ、新しい経路を使う。

- WPF `TryLock(0)` 成功時：Bitmapへコピーし、Lock保持中にsnapshotをSpout送信してからUnlockする。
- busy時：snapshotを先にSpout送信し、その後でBitmap.Lock→コピー→Unlockを行う。

元解像度Bitmap更新を省略せず、Bitmapの画素書込みは必ずLock取得後に行う。busy時の送信元は所有期間内のsnapshotで、共有Bitmapの無保護な書換えではない。Spoutの実送信名に対するmutex、GPU完了待ちとその期限、snapshot lease／pin、`hwdec=no`は変更しない。S/F単独、Black／Frozen／Buffered等の直接描画経路は既存の順序を維持する。プレビューの分離と上限（通常30Hz、全画面中10Hz）も維持する。

獲得済みWPF LockはfinallyでUnlockを試行する。送信等の元例外を維持し、Unlockも失敗した場合は両方をAggregateExceptionへ含める。失敗時は保留プレビューを取り消す。同じFrameRendererへの更新再入を拒否し、BitmapChangedと初回画像ログの任意callbackはLock外で処理する。今回追加したrender-stage診断イベントのキュー投入もUnlock試行後。Spout内部の既存Warning／失敗ログは送信中にも記録される。Bitmap公開時刻は実際のUnlock後を維持する。取得成功分岐はSpout送信中にWPF Lockを保持するため、ネイティブ呼出しが戻らなければLock保持が長期化し得る。busy分岐では送信後のBitmap.Lock／コピーに失敗するとSpoutだけ成功し得る。二つの出力の原子性は保証しない。

候補1は単にUnlockをSpout送信後へ遅らせた版で、56.818fps、Lock平均5.320ms、bitmap-send-scope平均15.731msとなり、実質的な改善を得られず不採用。`candidate1-held-lock/` にそのruntime・source／tests・差分・SHAを退避した。

## 固定条件と結果

証跡は `TestResults/obs-clean/output-contention-fix-20260909/`。基点 `ccc938e` のruntimeを `app-baseline/` に退避し、候補2の後にその旧版を実行したのが `baseline-05-B`。前段のoutput-isolation試験とは別の測定である。順序は表のとおり、候補1を含め全7回。最終DLLは `7AA413956D7A106ABEAED86166DC7005E00ED158176CB22FE2ADBA682538B792`。

各回32秒、開始後5秒以上27秒未満の22秒を解析。既存 `synthetic-4k60-changing.mp4`（3840×2160、60fps、30秒、SHA256 `1F8D01C4B0801F0D57C90FA80940424F2FAB3D7AD537FBACA2931D6175557F2F`）、Debug/x64、LTC OFF、専用settings・消音、メイン窓1076×680／位置(156,156)、公式SDK受信を使用した。全画面はWinDisc 2080×1017・96dpiで、物理4Kモニターの試験ではない。実機は直列、OBS・音声入力・WPR・画像採取は併走させていない。測定中UIAなし、プロセスとネイティブ窓のみ1Hzで採取した。

| run | Bitmap公開 fps | Spout呼出し完了 fps | 1秒窓 最小／最大 | Bitmap間隔 p95／p99／最大 ms | native終了群の未公開件数 |
| --- | ---: | ---: | ---: | ---: | ---: |
| candidate-01-B | 56.818 | 56.818 | 54 / 59 | 22.521 / 28.034 / 36.793 | 71 |
| candidate2-01-B | 59.455 | 59.455 | 58 / 61 | 20.106 / 23.040 / 29.615 | 12 |
| candidate2-02-S | 60.000 | 60.000 | 60 / 60 | 19.322 / 20.780 / 23.280 | 0 |
| candidate2-03-F | 60.000 | 無効 | 60 / 60 | 19.265 / 20.579 / 22.342 | 0 |
| candidate2-04-B | 59.591 | 59.545 | 58 / 61 | 20.319 / 23.109 / 26.316 | 9 |
| baseline-05-B | 55.727 | 55.682 | 52 / 59 | 23.050 / 28.117 / 30.998 | 94 |
| candidate2-06-B | 58.273 | 58.273 | 54 / 61 | 21.279 / 24.527 / 31.032 | 38 |

分位値はnearest-rank。Bitmapの時刻窓内件数と、native終了時刻で選んだ群のその後の公開数は境界が異なる。両者の単純な差を未公開数にしない。外部Bitmap／Spout送信は3840×2160、プレビューは960×540を維持した。

## ID結合と段階時間の解釈

native→snapshotはsession／generation／attempt、snapshot→publish／discardはsession／generation／sequenceで結合し、解析窓外まで含む全traceを追った。全7回とも解析窓内native終了1,320件がsnapshot-readyへ到達、対応欠落・重複・曖昧な終端なし。候補2 Bの公開／mailbox差替えは1,308／12、1,311／9、1,282／38。今回の旧版は1,226／94、S/Fは1,320／0だった。

候補2 Bの公開成功群ではTryLockのbusy／acquiredが1,265／43、1,296／15、1,272／10。大半は先にSpoutを送る分岐だった。候補2の `bitmap-lock` はbusy分岐だけに現れる送信後のLock時間であり、全フレームの待ちやGPU待ち全体を意味しない。平均は約0.028msへ減少した一方、Spout区間平均は9.505／9.831／9.812msだった。

`bitmap-send-scope` はtry-lock、必要時のblocking lock、copy、Spout、Unlockを含む親で、`publish` はさらに外側の親。これらを子の時間と加算しない。旧版の `bitmap` と候補2の `bitmap-send-scope` は範囲が異なるため、全体比較はpublish親で行う。開始・終了がともに解析窓内のpublish平均は旧版15.739ms、候補2 Bは13.993／14.580／15.001ms。境界を跨ぐtry-lock／bitmap-send-scope等も集計JSONに保持した。

最終BはLock平均0.028msのまま、copy平均が最初の4.339ms、次の4.602msから5.031msへ増え、publish平均も15.001msとなった。送信区間は直前Bと同程度であり、低下をLock待ちの再増や警告2件だけに帰属させない。snapshot-ready→publish開始の平均も旧版5.575msに対し候補2 Bは3.899／4.851／4.409msで待ちが残る。この区間はmailbox、UI callback、gate等を含む経過時間で、Dispatcherだけの待ちではない。

player単体の約5〜26秒のCPUは旧版3.794コア相当、候補2 Bは4.047／4.088／3.948だった。公開数増加とともにCPU消費も増えており、CPU低減とは報告しない。短区間のWorking Set増減からリークの有無を判定しない。

## 検証・警告・残る課題

root実施のDebugビルドは警告・エラー0、最終非E2Eテストは1,489件成功、失敗・skipとも0（`tests/busy-bitmap-send.trx`）。取得成功／busy、入力・TryLock・送信・Unlock失敗、二重例外、再入拒否、単独出力の経路維持を管理テストで確認した。7回ともplayer／ハーネス／所有SDK受信は正常終了、強制終了なし、trace欠落・エラー0、素材hash不変。rootの終了後確認で関連プロセスは残っていなかった。ETWセッション状態までの確認とはしない。

候補2初回のWarning1件は測定開始9.000秒前の検証用settings保存IOException（AtomicFileWriter.ReplaceFile系）で、Spout timeoutではない。manifestの専用settings、UI設定、perfのSpout状態照合を保持した。保存失敗の原因は今回未調査で、修正範囲を広げない。旧版のslow send2件と公開fps警告1件も解析窓前だった。

最終Bのslow send2件は送信終了16.145／19.618秒で解析窓内。所要16.888／17.780ms、両方 `Stage=Completed`、`SendSucceeded=true` だった。警告を隠さず残し、この2件を38件全てのmailbox差替えの原因とはしない。他のrunはWarning0。単発Spout無効化の課題を、この正常終了で解決済みとは扱わない。

この改善は短時間・同一素材・同一マシン条件の結果であり、常時60fpsは未達。native-renderはmpv描画API全体でデコード単独時間ではなく、回数は異なる画像のデコード数を証明しない。Bitmap公開やSpout呼出し完了も一意画像の到達、画像混在なし、物理4K60全画面、LTC同期精度を保証しない。GPU内部の根本原因も確定していない。

原始データ・候補1・今回の旧版再試験を保持し、`audit_contention_fix.py` と `final-audit-summary.json` にcompactなID結合、段階時間、警告の窓内外、CPU／メモリを保存した。`final-snapshot/` に最終runtime・変更source／tests／本文、`final-run-source-manifest.json` にSHAと実行参照を保存する。
