# Spout continuity probe

長時間耐久試験用の独立 Spout 受信監査器。製品プロセスとは別の D3D11/Spout
receiver として接続し、送信フレーム番号と受信時刻を QPC で記録する。

記録対象:

- 受信フレーム間隔と 100/250/500ms 以上の停止
- 送信フレーム番号の飛び、巻き戻り、再接続、メタデータ変更
- `ReceiveTexture` の所要時間
- 指定間隔での画面横断 pixel fingerprint と GPU readback 所要時間
- fingerprint が変化しない連続時間（実映像停止の補助指標）
- receiver 自身の CPU 時間

9行の全幅 readback は画像内容の存在と GPU copy 完了を補助確認するためのもので、同一 hash
だけでは映像フリーズと静止画を区別できない。物理ディスプレイや受信アプリの Present
完了も保証しない。

## ビルド

Spout2 2.007.017 の固定ソースを先に取得する。

```powershell
& .\native\gst-shim\get-spout.ps1
& .\scripts\SpoutContinuityProbe\build.ps1
```

出力は `scripts/SpoutContinuityProbe/bin/Debug/x64/SpoutContinuityProbe.exe`。

## 単体実行

```powershell
& .\scripts\SpoutContinuityProbe\bin\Debug\x64\SpoutContinuityProbe.exe `
  --sender TimecodeSyncPlayer `
  --output D:\outside-git\spout-receiver.jsonl `
  --stop-file D:\outside-git\spout-receiver.stop `
  --duration 120 --poll-ms 1 --sample-ms 1000
```

出力先は新規ファイルに限る。終了コード0は、指定送信元へ接続し、有効なフレーム番号を
2件以上受信して正常終了したことを示す。停止ファイルが作成されるか、durationへ到達すると
summaryを書いて終了する。
