# Spout調査とUIコピー削減（2026-09-09）

`refactor/session-lifecycle` の `2b64940` を起点に、未追跡のAGENTS.mdを保持して進めた。担当を分けて実装・独立レビューを行い、GPU・OBS・音声の実機試験は親担当が直列実行した。証跡の保存先はGit管理外の `TestResults/obs-clean/implementation-20260909-005400/`。ディレクトリ名は識別子であり、各試験の開始終了は原始結果のUTC／QPCを参照する。

## 1. GPU未完了の原因調査

製品の待機条件を変更せず、現行固定版でOBS接続・送信のみを各30秒×3回確認した。OBS条件では標準sourceがactive/showing、旧private-copy sourceはinactiveで、独立受信・PNG保存・音声入力・録画・配信は起動していない。

| 条件 | 送信回数 | 予定欠落 | 最大SendMs | slow Warning | 無効化 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 標準OBS、3回 | 5,389 | 11 | 133.7960 | 3 | 0 |
| 送信のみ、3回 | 5,394 | 6 | 95.9878 | 5 | 0 |

全6回が計測・Dispose完了、exit0で、終了後の対象プロセス一覧は空だった。`fixed-probe-audit.json` に原始JSONLのSHA256・送信会計・診断を保存した。既存runnerを使ったため、バッチ自体は旧証跡ディレクトリの `conditions-20260909-005156-obs-cold-receiver-none-shots-0/` と `conditions-20260909-005543-obs-none-receiver-none-shots-0/` にあり、個々のraw runを参照する。再現しなかった6回を、過去のGetData未完了100msや旧175405の原因解消とは扱わない。予定欠落とslow Warningがあり、性能合格とも判定しない。

[Capture-SpoutGpuTrace.ps1](../scripts/SpoutTransportProbe/Capture-SpoutGpuTrace.ps1) と専用WPR profileを追加した。1回の送信プローブ、CPUスケジュール／stack、DxgKrnl・D3D11・DXGIを固有instanceとQPCで関連付ける。既存出力を拒否し、所有probeだけを監視・終了する。開始成功したinstanceだけを保存終了し、元の失敗とcleanup失敗を分けて保持する。メモリリングの設定は計256MiBで、実ETLでは記録範囲とevent lossを別に確認する。

この環境のtokenには必要な管理者権限がなく、preflightの `canRecord=false` を確認した。権限・ポリシーを変更せず、実WPR start／ETL採取は行っていない。profileの解釈確認と、実WPR・実probeを呼ばないmock9ケース（開始後ログ失敗、終了失敗、有限待ち、既存raw保持等）は成功した。mock証跡は `wpr-mock-20260908-160045-3459/mock-summary.json`。**GPUスケジューラ上で未完了が長引く原因は未特定であり、タスク1の原因修正は未完了。**

## 2. timeout後の安全な回収

[Spout timeout解放境界](SPOUT-TIMEOUT-CLEANUP.md) に現DLLの逆アセンブル・API契約・必要な所有権を記録した。ReleaseSenderやCOM Releaseは完了通知ではなく、DLLのFlushWait／Waitは失敗HRESULTでも抜けない無期限待ちだった。

未完了unlock、無期限wait、資源永久保持のいずれも採用せずに有限回収を保証する最小変更は、現DLL契約では確認できていない。製品のtimeout後解放順序は変更していない。**タスク2の安全な異常回収は未実装・未解決。**

## 3. 標準OBSの版と受信所有権

[OBS受信の版・所有権](OBS-RECEIVER-OWNERSHIP.md) のとおり、実win-spout.dllと同梱Spout DLL群を公式v1.8 ZIPへハッシュ照合し、release tag sourceを特定した。そのソースはshared textureを直接描画しており、Spout mutex内のprivate copy／完了確認がない。配布物一致と再現ビルド一致は区別する。

正常時だけprivate copyを追加してもpending時の有限shutdownは解決しない。送受信の資源所有・世代・GPU完了または安全な取消しの契約が必要である。通常OBSや既存fixtureを変更せず、未検証pluginを本番修正として追加していない。**タスク3の画像混在修正は未実装・未解決。**

## 4. UI側の全面コピーを除去

専用nativeスレッドのbufferからimmutable snapshotを作る処理と、UIがそのsnapshotを保持する範囲は維持した。通常の公開で行っていたsnapshot→UI PixelBufferの全面コピー1回を除き、snapshotをWriteableBitmapへ直接コピーする。4K BGRAでは1枚33,177,600 bytes（約31.6MiB）のコピーを省く。Spoutを同期呼び出しする間だけ配列をpinし、finallyで解除する。Black用bufferは独立し、Freeze／明示captureにはsnapshotから専用所有bufferへ必要なコピーを残す。

実SpoutDX.dllのSendImageはCPUポインタをUpdateSubresourceへ渡し、ポインタをfieldへ保持しないことを独立レビューで確認した。UpdateSubresourceは戻った後のCPU sourceの変更・解放を許すため、SendFrameが戻ってからpinを解除する範囲はこの契約に従う。このCPU寿命の確認を、未解決の共有GPU画像timeout後の安全性へ拡張しない。[UpdateSubresource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-updatesubresource)

診断の `ui-copy/copied` は `ui-source/borrowed` に変更し、コピーをしていない区間をコピー時間と偽らない。従来のspoutMsは送信delegate内の時間、新しいrender-stageのspoutはpinの生成・解除も含むため、比較時に境界を区別する。`hwdec=no`、mpv単一nativeスレッド、UIでの表示・Spout公開は維持し、デコードライブラリやGPU描画への全面変更は行っていない。

コピー削減後の非E2Eテストは1,417件成功、失敗0・skip0。GC／mailbox終了中のsnapshot lease、Spout例外、BitmapChanged・Black buffer操作とresize、snapshot返却後のFreeze保持を追加検証した。Python精度解析は31件成功し、ui-source診断を画像標本の代わりに数えないことを確認した。

### 性能計測の成立条件

最初の変更前4回と変更後1回は、毎秒のUI Automation問い合わせが数秒以上停止することを確認した。変更前公開は約35.8〜55.3fpsと大きく変動したため、これらからコピー削減の改善率は算出しない。`p4-before-on-*`、`p4-before-obs-*`、`p4-after-on-01-20260909` のrawは削除・変更していない。

比較を取り直すハーネスは、setupと終了処理を除きUI Automationを呼ばない。プロセス標本のCPU／メモリ読取前後QPCを記録し、native HWNDの位置・サイズ・最小化状態だけを確認する。Spout実状態と通常公開はtrace／perfログで判定する。変更前DLL `6AF83FAFA17287A7E4BE35C9B205A00702AE89B14F9A962F42F8F3BA911318A0` と変更後DLL `08CC7D05DC2DB762035BB6879C6F3077393E35C57981ED02B62024F232C5C2F9` を依存ファイルとともに別ディレクトリへ固定し、変更前後を交互に測定する。実測結果とその限界を以下に示す。

### UI Automationを除いた8回の実測

各32秒、解析は開始QPCから完全な `[5,27)` 秒とした。全8回で実HWNDはx156・y156・1076×680、元4K素材・参照プロジェクト・mpv／Spout DLLのハッシュは一致した。LTC同期はOFF、独立受信・PNG保存・音声負荷を併走していない。標準OBS条件は専用portable OBSを使用した。製品は全8回WM_CLOSE後exit0、強制終了なし、trace footerの欠落・書込みエラー0、記録件数も整合した。ただし、**正常終了したうち1回は途中でSpoutが無効になった。**

| 条件・回 | 版 | Bitmap公開fps | native完了→公開数 | 未公開数 | CPU平均cores | Spout ON成立 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| OBSなし・1 | 変更前 | 51.227 | 1,320→1,127 | 193 | 4.758 | 成立 |
| OBSなし・1 | 変更後 | 47.818 | 1,320→1,052 | 268 | 4.603 | 成立 |
| OBSなし・2 | 変更前 | 46.182 | 1,320→1,016 | 304 | 4.865 | 成立 |
| OBSなし・2 | 変更後 | 51.227 | 1,320→1,127 | 193 | 4.335 | 成立 |
| 標準OBS・1 | 変更前 | 43.000 | 1,320→947 | 373 | 4.929 | 成立 |
| 標準OBS・1 | 変更後 | 48.773 | 1,320→1,073 | 247 | 4.908 | 成立 |
| 標準OBS・2 | 変更前 | 58.500（比較除外） | 1,320→1,286 | 34 | 比較除外 | **途中無効化** |
| 標準OBS・2 | 変更後 | 43.545 | 1,320→958 | 362 | 4.547 | 成立 |

native完了→公開数は、窓内に終了したnative呼出し1,320件をsnapshotのsession／generation／sequenceで全trace中の公開へ関連付けた数である。窓境界の公開はfps集計と1件異なる場合がある。表の未公開はすべて同一identityの `mailbox-replaced` に一致した。CPU平均は各22標本の最初と最後、約5～26秒のprocess CPU累積差から求めた論理core相当値で、22秒全体のtrace窓やシステム全体のCPU使用率ではない。読取り区間による端点誤差幅も監査JSONに保存した。

無効化した原始runは `TestResults/obs-clean/runs/p4p-before-obs-02-20260909-011241/`。開始約7.177秒の `app-timecodesyncplayer-20260909.log` 28行目に送信count396の例外があり、GetDataの `hr=00000001`・`completed=0`・poll546,482回・GPU完了待ち100ms、mutex待ち0.005ms、SendImage 2.508ms、cleanup前102.268msを記録した。deviceRemovedReasonは0だった。これはGPU完了を100ms以内に観測できなかった証拠であり、その原因や安全な回収が分かったことを意味しない。完全perf窓10件はすべて `spoutEnabled=false` だったため、58.500fpsと低いCPU値をSpout ONの性能に採用しない。この再現は変更前DLLであり、コピー削減による新規障害とは扱わない。旧175405の例外不明事象と同一原因とも断定しない。

変更後4回では `ui-copy` がなく、`ui-source/borrowed` の平均は約0.00021～0.00029msだった。一方、bitmap区間の平均は変更前の有効3回で6.690～7.633ms、変更後で10.062～13.475msとなった。コピーを1回省いたことは確認できたが、その旧所要時間をそのまま公開fpsの改善へ換算できない。OBSなしの対ではfps差の方向が反転し、標準OBSは変更前2回目が無効化している。**持続的なfps改善、60fps達成、未公開snapshotの解消は確認できていない。** CPUは表の観測値として保持し、因果的な改善率は算出しない。

各版・条件2回の交互測定で、試験専有環境ではない。前半には直前runのオフライン監査が数秒のCPU処理を要し、次runと重なった可能性がある。監査のidentity照合を辞書で行う形へ修正し、残りは全実機終了後に処理した。この背景負荷も含め、厳密に隔離した性能比較ではない。また、Bitmap公開数は一意な実動画画像の更新やOBS受信画像の整合を保証せず、この8回ではLTC同期精度を測定していない。作業領域の増減からリーク有無も判定しない。タスク1～3の未解決判定と4K60本番品質未達は維持する。

集計は証跡rootの `stage4-process-only-comparison.json`、各 `audit-p4p-*.json`、解析コード `audit_stage4_process.py`／`compare_stage4.py` に保存した。原始runは `TestResults/obs-clean/runs/` の `p4p-before-on-01`／`02`、`p4p-after-on-01`／`02`、`p4p-before-obs-01-20260909-011105`、`p4p-after-obs-01-20260909-011153`、上記無効化run、`p4p-after-obs-02-20260909-011323`。OBSの設定確認・所有プロセス終了記録は証跡rootの対応する `stage4-obs-*` に保持した。

### Black／Freezeの回帰確認と最終ビルド

VB-Cableの25fps LTCと元の1080pマーカー素材3本を使用し、標準受信pluginの専用OBSでBlack／Freeze各35秒とシーク4条件を直列実行した。正式runは証跡rootの `marker-regression-20260909-012411/`。E2Eは1件成功、失敗・skip0、workerとsupervisorはexit0。素材・プロジェクト・製品／native DLL・ハーネスを含む入力ハッシュは開始終了で一致し、所有WPF／OBSは正常終了、終了後の対象プロセス一覧は空だった。

独立したPNG解析の `png-audit.json` は成功。Blackの3枚は全画素のRGBが0、Freezeの3枚はclip1／2／3の末尾marker 239／299／599で、header・XORと48bitの読取り条件も一致した。これは採取した6枚の判定であり、Freeze画像全体の混在なし、全フレーム、同期精度、4K60性能の保証ではない。traceはendあり、記録31,238件、欠落・書込みエラー0。Spoutの無効化・GetData失敗はログになかった。LTCのJump Warningは別に保持し、Warning全体が0とはしない。

先行run `marker-regression-20260909-011455/` は、実行中にこちらがハーネスの監査対象リストを追記し、ハーネス自身のハッシュ不変条件を破った。素材・製品DLLは不変でPNG6枚の個別判定は成功したが、run全体は無効のまま保存した。実行前版をSHA一致で復元保存し、変更の内容を `wrapper-reconstruction.json` に記録したうえで、ハーネスを固定して上記正式runを取り直した。元結果を成功へ書き換えていない。

全実機終了後にSpoutTransportProbeをDebugでビルドし、警告0・エラー0。配置された製品DLLとアプリ側DLLはともに上記変更後の `08CC7D...` に一致した。非E2E1,417件、Python31件、WPR mock9件と合わせた検証範囲を保持する。`final-verification.json` に最終DLLハッシュ・非E2E件数・正式E2E採取結果を保存した。実WPR採取、timeout安全回収、OBS混在修正、持続的な性能改善は未完了である。
