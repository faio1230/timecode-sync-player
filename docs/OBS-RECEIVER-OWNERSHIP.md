# 標準OBS Spout受信の版と所有権境界

2026-09-09、隔離したOBS 29の実DLLを公式配布物へ照合した。受信側の画像混在は未解決であり、この調査では通常OBS・plugin・既存private-copy fixtureを変更していない。

## 実DLLに対応する配布物

`TestResults/obs-clean/obs/obs-plugins/64bit/win-spout.dll` は、公式 [v1.8 release](https://github.com/Off-World-Live/obs-spout2-plugin/releases/tag/v1.8) の `OBS_Spout2_Plugin_ManualInstall_v1.8.zip` 内DLLとバイト単位のSHA256が一致した。同梱のSpout.dll、SpoutDX.dll、SpoutLibrary.dllも、隔離OBS内の実ファイルとすべて一致した。

| 対象 | SHA256 |
|---|---|
| 公式v1.8 ZIP | `BF21CDD0CA7E427454C8DD625BDDAB2B081C2F2398E06FBA42CAE69D0D287AD4` |
| win-spout.dll | `18DA15E559B06DE8BCC2F719E5857EAB486835A98565B95C73CF3BB0E0B788D8` |
| OBS側SpoutDX.dll | `7584F98B51CC80D50D7255EAB51DDEF4B954AB4BB28C3E5B6F43CB680F82B3BC` |

v1.8 tagのcommitは `3eb5e3d1e39363f40389f3697dc5605c7059f971`、Spout2 submoduleは `a26f74a11d43ef74778d73b6741d6805462702c0`。win-spout.dllのPE timestampは2023-02-08 11:54:50 UTC、バージョン文字列とRSDS/PDB識別情報はなかった。手元のv1.9.0 ZIP内win-spout.dllは別SHAだった。

これは**実DLLと公式release配布物の一致、およびそのrelease tag sourceの特定**である。同じソースから再ビルドしてDLLを再現したという証明ではない。以前参照した最新masterを実DLLと同じ版として扱わない。

ZIP、release/tag/dependency API応答、対象ソースとlicense、全DLL照合の `audit.json` は、Git管理外の `TestResults/obs-clean/implementation-20260909-005400/receiver-source-audit/` に保存した。対象ソースのSHA256は `EDBA37948D25496C37470A59D2BC340008E83B894B49851654E866EF267F29AE`。

## 対応ソースに存在する受信経路

[v1.8 win-spout-source.cpp](https://github.com/Off-World-Live/obs-spout2-plugin/blob/3eb5e3d1e39363f40389f3697dc5605c7059f971/source/win-spout-source.cpp) の63行で送信名に対応する共有handle等を取得し、158行で `gs_texture_open_shared`、323行でその共有textureを `obs_source_draw` に直接渡す。受信source内にSpoutの名前付きmutex取得、private textureへのコピー、GPU完了確認の境界はない。OBS graphics lockはOBS内のcontext使用を直列化するが、別プロセスの送信画像更新を排他しない。

この経路は共有画像が読み出し中に更新され得る構造を示す。既存のPNG混在反例と整合するが、単発の送信無効化やすべての混在の原因を、このソース照合だけで断定しない。

## 安全な変更に必要な条件

正常受信の最小境界は「実送信名のmutex取得 → sharedからprivateへコピー → そのコピーのGPU完了確認 → 同じ所有スレッドでmutex解放 → 完成したprivate画像を描画」である。送信名・共有handle・寸法・formatの変更時も、古いコピーの完了と資源寿命を維持する必要がある。初回コピー完了前のprivate画像は表示成功として扱わない。

この正常経路だけを実装しても、pending時の有限時間終了は解決しない。

| pending時の処理 | 残る問題 |
|---|---|
| 100ms後にmutexを解放 | 未完了の共有画像読み出しを送信側が上書きできる |
| 次回OBS callbackへmutexを持ち越す | callbackの実行スレッドが同じとは限らず、Windows mutexの所有権を移譲できない |
| 完了までcallback内でpoll | OBSの描画・終了処理が無期限に止まり得る |
| worker/processへ移す、timeout後に破棄・kill | GPU処理を安全にcancelした証明にならず、共有画像とmutexの所有権問題が残る |
| pending資源を永久隔離 | 安全な回収・有限shutdownを実現したことにならない |

旧private-copy fixtureは無期限pollとWait0による更新停滞が未解決で、本番向けではない。今回、timeout延長や未完了unlockでこれを隠す修正は追加しない。

有限時間で安全に停止・再接続するには、送受信双方が合意する資源所有・世代切替・GPU完了または安全なcancelのprotocolが必要になる。専用workerや別processの採用だけではこの契約を代替できない。現SDKの共有画像契約を保った小変更として保証できる範囲を超えるため、未検証pluginを製品修正として導入しない。次の受信実装では、正常画像整合とpending時の終了・回収の両方を別々に検証する。

本調査は配布物・PE・ソースの照合のみで、ビルドや新たなGPU実機試験は行っていない。
