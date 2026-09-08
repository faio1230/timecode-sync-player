# 主画面プレビューと外部出力の分離

主画面の`VideoImage`へ渡す画像を最大960×540のプレビューへ分離する。更新上限は通常30Hz、全画面表示中10Hzとする。Spoutと全画面用の画像は、従来の解像度・公開要求ごとの更新を維持する。デコード既定は`hwdec=no`のまま。mpvの単一nativeスレッド、snapshot lease、Spout mutex／GPU完了待ちを変更せず、GPU描画への全面変更も行わない。

初版は外部公開ごとに同期縮小していた。初版の単発測定ではSDK併用59.727fps、全画面併用46.864fpsとなり、全画面条件の悪化から不採用とした。初版のバイナリ・source・原始証跡は`TestResults/obs-clean/preview-separation-20260909/`以下に保持し、担当Bのsource／tests／本文初版は`v1-synchronous-preview/integration-b/`へSHA付きで退避した。以下は、縮小処理をタイマー側へ移した改訂候補の設計であり、これだけで改善を証明するものではない。

## 公開の順序と画像の所有者

通常フレームはUIスレッドで、従来どおり全解像度WriteableBitmap更新→Spout同期送信→性能記録→Freeze用コピーを行い、その後にプレビュー用の最新画像を通知する。この経路では縮小コピーを行わない。外部出力先が閉じている場合も、この候補では全解像度bitmap更新を省略しない。

`FrameRenderer.CurrentBitmap`が全解像度画像を保持する。`QueuePreviewFromCurrentBitmap(kind)`はbitmapオブジェクトと種別だけをPresenterへ通知する。Presenterが保持する元画像は強参照1件で、次の通知で置き換える。snapshot leaseやCPU入力配列、BackBuffer pointerを保持しない。縮小元は変更可能なbitmapなので、通知時点の画像の凍結コピーではなく、タイマー時点の最新画素を読む。

`PreviewFramePresenter`は独立したBackground優先度のUIタイマーで、期日を迎えたときだけ縮小と公開を行う。その同期コピー中だけ最新元bitmapのBackBufferを読取り専用で参照し、コピー後にpointerを保持しない。元画像はUIでのコピー中に書き換えず、読取りのための再Lockも追加しない。部分的な入力更新でも表示面に残る末尾画素を読む。保留がある場合は、再生が一時停止して次の描画通知が来なくても最後の画像をタイマーから公開する。更新先の`TryLock(0)`が失敗した場合は待機せず、最新画像を次の機会へ残す。30Hz／10Hzは上限であり、UIが忙しいときの到達保証ではない。既存のIntPtr overloadは同期独立コピー契約の低レベル用途として残すが、製品のFrameRenderer経路では使用しない。

Black、Frozen、CachedGapFreeze、Bufferedも、従来の全解像度描画とSpout呼出しの後でプレビューを準備する。既存の種別は維持し、Frozen不足によるBlackフォールバックは`black`、CachedGapFreezeの既存Buffered経路は`buffered`として記録する。Spout無効時やBufferedの送信pointerがない場合も、表示に成功すればプレビューを更新する。

## 主画面と全画面

主画面は`RenderSession.PreviewBitmapChanged`だけを購読する。全画面は従来の`BitmapChanged`を購読し、開くときの初期画像には`CurrentExternalBitmap`を使用する。主画面の縮小画像を初期画像へ流用しないため、一時停止中やBlack／Freezeで新しい描画要求がない場合も、保持中の全解像度画像から開始できる。全画面Show成功時にプレビュー上限を10Hzへ、Closed／Show失敗時に30Hzへ戻す。上限変更は開閉時だけであり、毎フレーム行わない。変更中の保留画像は維持する。全画面のShow失敗時とClosed時の購読解除を維持する。

全画面を開くときだけ、外部用bitmapとプレビューbitmapの寸法をログへ残す。モニターの物理解像度・全画面の実提示頻度・Spout受信側の実到達数は、このbitmap寸法や更新数から保証しない。

## 寿命と失敗時の扱い

RenderSessionがPresenterを所有する。InvalidateとResetDisplayは保留画像を取り消し、旧フレームの遅延公開を防ぐ。Stopはプレビューを停止し、Disposeはプレビューの独立した資源を解放する。UI以外からの停止でも、Dispatcherへの同期呼出しやUI待機をnative barrierの前に追加しない。

プレビューの失敗はWarningへ記録し、外部出力へ例外を伝播しない。Presenterが内部で記録して処理を終えた例外を、呼出側が重複して記録しない。全解像度画像更新・Spout・Freezeの例外契約は従来どおり。native context解放に失敗した場合も、previewの独立資源は解放できるが、mpvのcallback・native buffer・threadを先に解放しない。

元bitmapが書込み途中で失敗すると、そのオブジェクトを指す以前の保留通知から未完成画素を読めてしまう。このため、bitmap書込みまたは後続外部公開の例外経路では保留プレビューを取り消してから元例外を再送出する。通常／Black／Frozen／BufferedとGapFreezeの各経路を対象にする。通常の成功フレームでは取消を呼ばず、タイマー期日を毎フレームリセットしない。

縮小CPU配列の保持は最大960×540×4＝2,073,600 bytes（約1.98MiB）で、latest-onlyとする。全解像度bitmap／既存snapshot poolは別に存続し、縮小WriteableBitmapやWPF内部資源・リサイズ時の一時的なGC待ちはこのCPU配列上限に含まれない。プロセス全体のメモリが2MiBで増加停止するという意味ではない。

## 診断と性能値

既存の`frame` traceは全解像度bitmap公開境界のままとする。全画面が閉じていれば保持画像の更新であり、物理画面への表示とは同一視しない。`preview-frame`は縮小プレビューの公開を別件として記録し、元の`kind`と画素probeを保持する。縮小によってmarkerが読めない場合もあり、markerなし画像から一意な動画フレーム数は保証しない。

`render-stage/preview-prepare`は通常公開後の最新画像通知callback区間を記録する。改訂候補ではここに縮小費を含まない。`call-returned`はcallbackから戻ったという意味で、タイマーによるプレビュー表示完了ではない。既存`bitmap`／`spout`区間や性能記録にこの通知費を混ぜない。外側の`publish`は後段callbackを含む親区間なので、preview-prepare等の子区間や別スレッド時間を加算しない。

互換性のため`displayedFps`／`DisplayedFps`名は維持するが、数える対象は全解像度bitmapの公開でありプレビューfpsではない。性能ログに`frameBoundary=full-resolution-bitmap`を付記する。preview-frameを既存frame／LTC精度の母数へ混ぜず、trace footerの記録件数には含める。

## 検証方針

管理テストでは、外部→Freeze→preview通知の順序と例外隔離、表示先bitmapの別参照とタイマー時点の最新元画素、部分更新、Black／Freeze／Bufferedの種別とpointer、書込み失敗時の保留取消、native解放失敗時のpreview独立解放、停止中の全画面初期参照と再作成を確認する。10Hz／30Hzの開閉変更が外部送信数や全解像度参照を減らさないことも確認する。Presenter自身のタイマー・latest-only・TryLock busy・停止競合は、fake timerと時刻で別途検証する。

ビルド・管理テスト結果、固定条件の実機測定結果と採否は以下に記録する。設計実装だけで4K60、画像整合、同期精度の合格とは判定しない。


## 2026-09-09 実機比較と採否

縮小をタイマー側へ移したv2のプレビュー分離を採用する。通常窓の公開頻度は基準55.591〜56.409fpsから59.773〜60.000fpsへ改善した。全画面併用は基準55.045〜55.955fpsに対して56.045〜57.409fpsで、差が小さく試験間のばらつきもある。全画面での持続的な改善は断定せず、安定60fpsは未達とする。毎公開で同期縮小するv1は全画面を46.864fpsへ悪化させたため不採用のまま保持する。

原始データは `TestResults/obs-clean/preview-separation-20260909/`。基点は `8b45ff0`、退避版は `app-baseline/`、不採用v1は `app-v1-synchronous-preview/`。今回の実機は10回で、基準の通常窓・全画面を早期と後期に各2回、v2の通常窓・全画面を各2回、不採用v1を各1回測定した。長時間連続試験や統計的な効果保証ではない。

各回32秒、開始後5秒以上27秒未満の22秒を解析した。素材は既存の `synthetic-4k60-changing.mp4`（3840×2160、60fps、30秒、SHA256 `1F8D01C4B0801F0D57C90FA80940424F2FAB3D7AD537FBACA2931D6175557F2F`）。Debug/x64、`hwdec=no`、LTC OFF、専用settingsで消音、メインウィンドウ1076×680・位置(156,156)、公式SDK `WinSpoutDXreceiver.exe` を使用した。OBS、音声入力、WPR、画像採取を併走させず、全試験を直列実行した。測定中はUIAを使わず、1Hzでプロセスとネイティブウィンドウを採取した。

全画面の実測表示範囲は `WinDisc` の2080×1017、96dpi。入力と外部用Bitmapは4Kだが、物理4Kモニターでの表示試験ではない。全画面は他の窓を遮蔽するため、通常窓との違いを単独の効果として比較しない。

| 条件（証跡ディレクトリ） | 全解像度Bitmap fps | Spout呼出し完了 fps | Bitmapの1秒窓 最小／最大 | Bitmap間隔 p95／p99／最大 ms |
| --- | ---: | ---: | ---: | ---: |
| baseline-sdk | 55.591 | 55.545 | 48 / 60 | 23.233 / 27.835 / 40.239 |
| baseline-sdk-late | 56.409 | 56.409 | 54 / 59 | 22.071 / 26.330 / 29.180 |
| candidate-v2-sdk | 60.000 | 60.000 | 60 / 60 | 18.977 / 20.445 / 26.722 |
| candidate-v2-sdk-repeat | 59.773 | 59.773 | 57 / 60 | 20.134 / 22.634 / 34.155 |
| baseline-fullscreen | 55.045 | 55.000 | 49 / 59 | 23.658 / 28.313 / 30.654 |
| baseline-fullscreen-late | 55.955 | 55.909 | 54 / 59 | 22.426 / 26.815 / 33.572 |
| candidate-v2-fullscreen | 57.409 | 57.364 | 53 / 59 | 21.257 / 26.390 / 28.758 |
| candidate-v2-fullscreen-repeat | 56.045 | 56.091 | 50 / 58 | 23.046 / 27.051 / 34.176 |

Bitmapは `frame/kind=normal` の公開時刻、Spoutは `render-stage/spout/outcome=call-returned` の終了時刻で数えた。境界が異なるため同じ22秒窓でも1件程度の差があり、その差を送信欠落とはしない。間隔・段階時間の分位値はnearest-rank。段階時間は開始・終了の両方が解析窓内にある記録を採用する。元の親区間と子区間を重複加算しない。

v2のプレビューは通常窓2回とも21.227fps、全画面併用8.636／8.591fps、寸法は全て960×540だった。30Hz／10Hzは更新上限であり、到達保証ではない。プレビューを減らした回数を外部出力の達成値として数えない。全解像度BitmapとSpoutの寸法は全て3840×2160を維持した。

## 測定で確認できた処理時間と資源

通常窓のBitmap.Lock平均は、基準6.191／6.426msからv2の0.012／0.010msへ減少した。公開全体の平均は基準16.601／16.643msに対してv2は10.196／10.676ms。通常公開後の `preview-prepare` はv1で平均2.209msの同期縮小を含んでいたが、v2では画像通知だけとなり0.007ms程度だった。縮小処理自体はタイマーに移って存続する。

全画面併用ではv2でもBitmap.Lock平均5.553／5.571msが残り、公開全体は15.296／15.805msだった。v2の4回全体でpreview-prepare平均は0.0056〜0.0074ms。外部公開ごとの縮小を除いたことは確認できるが、全画面の待ちまで解消したわけではない。各回native-render終了は解析窓内1,320件であり、その区間にはmpv描画待ち・変換等も含む。デコード処理単独の不足やGPU内部の根本原因をこの数値から断定しない。

以下はplayer単体の約5〜26秒、22標本の値。CPUはプロセス採取の開始・終了QPCの中点差で算出し、SDK受信プロセスやシステム全体を含めない。

| v2条件 | CPU 論理コア相当 | Working Set 開始→終了 MiB | 増分 MiB |
| --- | ---: | ---: | ---: |
| candidate-v2-sdk | 3.228 | 894.75 → 902.73 | +7.99 |
| candidate-v2-sdk-repeat | 3.402 | 897.92 → 935.50 | +37.58 |
| candidate-v2-fullscreen | 3.937 | 939.46 → 932.24 | -7.21 |
| candidate-v2-fullscreen-repeat | 3.761 | 939.31 → 932.64 | -6.67 |

基準のCPUは通常窓3.544〜3.622、全画面3.768〜3.810コア相当。通常窓では軽減したが、全画面のv2は3.761〜3.937で低減を断言しない。通常窓v2反復のWorking Setは37.58MiB増えた。短時間の増減だけでリークの有無や長時間の保持量は判断しない。

## 終了・警告・検証範囲

10回ともplayer／所有SDK受信／ハーネスはexit0、強制終了なし、trace記録欠落・エラー0。原動画・参照プロジェクト・既存ログを保持し、専用settingsを用いた。終了後の読取り確認では関連プロセスは残っていなかった。ビルドはDebugで警告・エラー0、最終の非E2Eテストは1,474件成功、失敗・skipとも0（`tests/preview-separation-v2.trx`）。開発中の失敗を含む以前のTRXも上書きせず保持した。

警告0だけを成功条件にはしていない。基準通常窓の初回はSpout slow send完了1件と公開fps警告1件、後期の基準全画面はslow send完了1件。不採用v1全画面の警告19件は公開fps低下14件＋slow send完了5件であり、Spout無効化の19回発生ではない。v2通常窓初回はslow send完了1件、反復は測定開始6.863秒前の `Failed to save settings` 警告1件を残した。これは検証用settings保存時のIOExceptionを伴う起動準備時警告1件として区別し、原因は未調査。正常終了を理由に削除せず、全処理で例外がなかったとは報告しない。v2全画面2回はWarningなし。分類したslow sendはいずれも `Stage=Completed`、`SendSucceeded=true`。今回の保存ログにSpout例外・無効化を示す記録はなかったが、保留中の単発無効化問題を解決済みとはしない。

全画面のハーネスはseekして一時停止した状態で開く。v2の2回とも開始前ログは `externalBitmap=3840x2160 previewBitmap=960x540` だった。管理テストでも停止中の初期Sourceと再作成時に全解像度Bitmapを使うことを確認した。Black／Freeze・失敗時取消・開閉上限は管理テストとコードで検証し、今回の性能試験ではBlack／Freezeの実機画像採取を追加していない。

一意な受信画像の連続更新、画像混在、物理4K60表示、LTC同期精度は未判定。正常終了、Bitmap公開頻度、Spout呼出し完了、画像整合、同期精度は別の判定である。`final-offline-audit-summary.json` に新旧traceの独立再計算、警告分類、CPU／メモリ、寸法とハッシュを保存し、`final-source-binary-manifest.json` に最終DLLと変更source／tests／本文のハッシュを保存する。
