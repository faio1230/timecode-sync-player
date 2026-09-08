# Spout GPU未完了timeout後の解放境界

2026-09-09、`refactor/session-lifecycle` の `2b64940` を起点に再確認。

**現DLLの契約では、GPU完了を確認できないまま100ms期限に達した送信を、有限時間・通常mutex解放なし・資源の永久保持なしで安全に回収し、同じプロセスを継続する実装は確認できていない。** cleanupの順番だけを変更して解決済みとは扱わない。製品の期限・解放処理は今回変更していない。

## 現在の実行順と証拠

`SpoutFrameTransfer.SendImage` は実送信名のmutexを取得し、`SendImage → End(EVENT query) → Flush → GetData` を実行する。`GetData` が `S_OK` かつBOOL=trueなら完了として受理する。未完了のまま100msに達すると例外になり、現在のfinallyはmutexを解放する。その後 `SpoutOutput` がqueryをDisposeし、`ReleaseSender → Destroy` を実行する。

`20260908-234220-stage1-20260908-234216-01/sender.jsonl.failure.json` は `S_FALSE / completed=0 / gpuElapsedMs=100 / deviceRemovedReason=S_OK` を保存している。これは完了確認が得られなかった証拠であり、GPU上の画像更新が100msずっと実行中だったとまでは示さない。元の `175405` の失敗と同じ原因かも未確定。

対象DLLは `scripts/SpoutTransportProbe/bin/Debug/net8.0-windows/SpoutDX.dll`、SHA256:

```text
BBCEE6F0031F6BD1A6461585C53F3F8F3CECBF006B486661BCDE6413E2ADDEF5
```

実DLLのexportsと逆アセンブル、および照合対象のvendor sourceを確認した。
逆アセンブル原文は `TestResults/obs-clean/implementation-20260909-005400/timeout-cleanup-dll-disassembly.txt` に保持する。

| API / DLL RVA | 確認した処理 | timeout後の保証として足りない点 |
|---|---|---|
| `ReleaseSender / 0x4460` | shared textureのIUnknown.Release、ポインタ・handleをnull化、sender名登録解除 | query完了・GPU取消しの確認がない |
| `CloseDirectX11 / 0x2670` | texture/context/deviceの解放へ進む | 有限時間で完了確認・取消しを返すインターフェースではない |
| `FlushWait / 0xCC20` | context.Flush後に`Wait / 0xE670`を呼ぶ | 次行の無期限待ちを含む |
| `Wait / 0xE670` | EVENT queryをEndし、GetDataがS_OKになるまでSleep(0)で反復 | 期限も失敗HRESULTでの脱出もない。製品では採用しない |

`SpoutGpuCompletion.Dispose` が所有するのはquery参照だけで、device/contextはSpoutから借りている。queryを先にReleaseしても、queued GPU処理を取り消したことにはならない。

## 単純な変更で閉じない理由

- **Flush追加、ClearState、COM Releaseは完了通知の代わりにならない。** Flushは非同期で、D3D11はオブジェクト破棄を遅延させる。ClearStateはcontextの設定を初期化するAPIで、既に投入した更新を期限付きで取り消す契約ではない。[Flush](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-flush)、[ClearState](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-clearstate)
- **sender登録解除やsender側texture解放は、既存receiverの参照を取り消さない。** shared resourceの寿命は、他deviceで開いたresource参照にも依存する。登録名を消すだけでは、既に開かれた画像へのアクセス終了を確認できない。[GetSharedHandleの寿命](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiresource-getsharedhandle)
- **待ちを別スレッドへ移してもGPU停止の契約は増えない。** mutexを解放できるのはその所有threadである。追加の有限待ちが尽きたときに、未完了解放・永久保持・停止不能のどれかへ戻る設計では要求を満たさない。[ReleaseMutex](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-releasemutex)
- **GetDeviceRemovedReasonのS_OKは完了を意味しない。** 失敗診断として維持するが、安全な解放の許可条件にしない。

正常なCOM参照解放と、共有画像を受信側へ安全に渡せることは別問題である。ここで証拠が不足しているのは後者と、異常時cleanupの所要時間上限。COM参照のRelease自体を一律にuse-after-freeと断定するものではない。

## 必要な設計境界

1. **完了または取消し完了を確認できるGPU所有境界。** その確認前は正常unlock・正常画像公開に進めない。現SendImageは公開済みshared textureを直接更新するため、完成した画像だけを公開する方式へ変更するならnative送信API側の変更が必要。単なるprivate staging追加でも最後のshared copyの問題は残る。
2. **receiverの失敗判定と世代切替の契約。** timeout、abandoned、sender世代変更では旧画像を正常フレームとして採用しない。sender登録解除を全receiverのGPU読出し完了通知とみなさない。標準OBSを含めて、この契約を確認・実装する必要がある。
3. **停止不能なnative呼出しとWPFを分離する所有境界。** 独立sender processと外部watchdogはUI応答性を守る候補になる。ただしprocess終了を検出しただけで共有画像のGPU更新取消し完了まで証明できるわけではなく、上のGPU・receiver契約と組み合わせて検証する必要がある。

これらは安全な実装に必要な条件の整理であり、process分離や共有画像protocolの改修を実装済みとはしていない。まず固定条件とGPU/CPUの実行証跡から未完了の原因を絞る。期限延長、無期限Wait採用、未完了でも成功扱いする変更、資源を保持し続けるだけの変更は行わない。
