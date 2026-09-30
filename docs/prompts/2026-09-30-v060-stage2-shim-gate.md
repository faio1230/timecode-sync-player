# 指示書: v0.6.0 段 2（shim）ベンダーの門・モードの入口・不一致の失敗・v0.2.0・WARNING の記録

担当: サブエージェント A。作業ツリー `timecode-sync-player-v053`、ブランチ `agent-a-v060`（`v0.6.0` の `6cc4896` まで進めてある）。
設計書: `docs/design/v0.6.0-prores-gpu.md`（3-2〜3-4・5 節、段 1 の実機の結果）。先に全体を読むこと。段 1 の報告 `docs/reports/2026-09-30-agent-a-v060-stage1.md` も読むこと。
並行して担当 B が段 3（アプリの設定・P/Invoke・UI）を別のツリーで進める。**下の C ABI の形は B と共有の約束なので変えない。**

## C ABI（担当 B と共有の約束）

```c
/* v0.6.0: ProRes の GPU 復号のモード。最初のロードの前だけ有効（tcs_player_set_decode_mode と同じ制約）。 */
#define TCS_PRORES_GPU_AUTO 0
#define TCS_PRORES_GPU_ON   1
#define TCS_PRORES_GPU_OFF  2
TCS_API int tcs_player_set_prores_gpu (TcsPlayer* p, int mode);
/* 戻り値: TCS_OK、ロード済みなら TCS_ERR_GENERIC、mode が範囲外なら TCS_ERR_GENERIC（値は変えない） */
```

`include/tcs_gstreamer.h` の `tcs_player_set_decode_mode` の隣に置く。

## 範囲

1. **ベンダーの門**（設計書 3-3）
   - shim のデバイスを作るとき、そのアダプタの `DXGI_ADAPTER_DESC.VendorId` を `TcsPlayer` に保存する（`create_or_adopt_device` の中。いま捨てている値）
   - 純関数 `tcs_prores_gpu_allowed(int mode, unsigned vendor_id)` を `include/` の新しいヘッダ（`tcs_prores_gpu_policy.h`）に置き、`--policy-only` に真理値表の試験を足す（auto: 0x10DE だけ真、on: 常に真、off: 常に偽、vendor 0 も含む）
   - 試行ループで `prores-gpu` の番が来て門が偽なら、`load.skip path=… attempt=N profile=prores-gpu reason=prores-gpu-off|prores-gpu-vendor vendor=0x%04x` を出して次へ（D16 の skip と同じ形）
   - `on` で vendor が 0x10DE 以外のときは、その試行の前に 1 行 `prores-gpu: mode=on on an unverified adapter vendor=0x%04x`
2. **モードの入口**
   - `tcs_player_set_prores_gpu`（上の ABI）。`TcsPlayer` に mode を持つ（既定 auto）
   - 環境変数 `TCS_PRORES_GPU`（auto / on / off、大文字小文字を区別しない、試験用）: プレイヤーの作成時に 1 回読み、値があれば setter の値より優先する（setter が後から呼ばれても環境変数が勝つ）。不正な値は無視して 1 行警告
   - ProRes の素材のロードの最初に 1 行: `prores-gpu: mode=<auto|on|off> source=<setting|env|default> vendor=0x%04x -> <enabled|disabled>`（ProRes 以外の素材では出さない。判断は demux の caps が `video/x-prores` と分かった後でよい。難しければ `prores-gpu` の番が来た時点で出す）
3. **不一致を失敗にする**（TSP-Fable の判定 2026-09-30）
   - 段 1 のログはそのまま残し、`same=0`（出力バッファのデバイスが shim のデバイスでない、または D3D11 のメモリでない）か、最初のフレームの後の `adapter-luid` の読み返しが shim の LUID と違えば、その試行を失敗にして次の試行（`prores-cpu`）へ進む
   - `same` は最初のフレームのプローブ（ストリーミングのスレッド）で分かるので、プレイヤーに原子的なフラグを立て、最初のフレームの待ちの後の判定（D34 のゲートの手前）で読む。`load.attempt … result=decoder-adapter-mismatch` と `load.fail profile=prores-gpu reason=decoder-adapter-mismatch same=%d read=%016llx want=%016llx` を出す
   - `frame_lock` を持ったまま状態変更をしない（ロック規則）
4. **プラグイン v0.2.0**
   - `scripts/get-prores-plugin.ps1` の値を v0.2.0 に: タグ `v0.2.0`、zip `gst-prores-d3d11-v0.2.0-win64-gst1.28.2.zip` の SHA-256 `b49fdd041021e1d7a9bd1ed3548f7609ff8d901ad3f56d5bf3c8fa4f8efd8ff8`、`gstproresd3d11.dll` の SHA-256 `ee8dc3f7631ff077e9acd6c12c9584cdd05e91a954582da911020736abba34f3`。「暫定値」のコメントを「v0.6.0 が同梱する版」に直す。`.cso` は 6 個のまま
   - 実行して `native\gst-prores\` を v0.2.0 に入れ替える（DLL の SHA の照合が通ること）
5. **壊れたフレームの WARNING**（設計書 3-4、TSP-Fable の決定）
   - バスの WARNING（`GST_MESSAGE_WARNING`）で、ドメインが STREAM・コードが DECODE のもの（どの要素からでも）を数える。1 ロードあたり最初の 1 件だけ行で出す: `decode.warning profile=<名前> src=<要素名> msg=<メッセージ> debug=<先頭 200 文字>`。以後は件数だけ数え、パイプラインを解体するとき（次のロード・プレイヤーの破棄）に件数が 1 以上なら `decode.warnings profile=<名前> count=N` を 1 行
   - 画面・アプリへの通知・フォールバックはしない。`max-errors` は既定（-1）のまま触らない
   - いまのバスの WARNING の扱いを調べ、既存の経路があればそこに揃える（報告に書く）
6. **GLib・GStreamer の致命的なメッセージをログへ**（段 1 の落ちの切り分け）
   - `g_log_set_default_handler` か `g_log_set_writer_func` で、GLib の WARNING・CRITICAL・ERROR（全ドメイン）を shim のログ（`LOG`、書いた後にフラッシュ）へも出す。元の既定の処理（`g_log_default_handler`）へ必ず渡す（**挙動を変えない**。致命的なものはそのまま止まる）
   - `gst_debug_add_log_function` で GStreamer のデバッグの ERROR 以上だけを shim のログへ（`GST_DEBUG` を設定しない既定の状態で ERROR が出るかも確かめて報告）。量が多い経路を作らないこと
   - どちらも起動時に 1 回だけ登録（`gst_init` の後）。Debug だけでなく Release にも入れる。`G_DEBUG=fatal-criticals` は付けない（平常時の CRITICAL で落ちるようになる）
7. **解体の読み**（TSP-Fable の (d)）: `set-state-fail` の後の `teardown_pipeline` の経路で、`frame_lock`・共有リング・プレイヤーの欄（`vdec` ほか）が壊れる経路が無いかをコードで読み、報告に書く（直すのは明らかな欠陥だけ。直すなら赤いテストかログの根拠を添える）

8. **GOP scan の読み**（TSP-Fable、落ちの手掛かり: アプリのログは 13:46:05.750 の `GOP scan` の行で途切れ、落ちは 13:46:06、スタックは `avcodec → gstlibav → gstreamer → glib → ucrtbase abort`）。**事実を先に、推測と分けて**報告に書く:
   (a) GOP scan（アプリの `GOP scan:` の行、`TCS_GOP_SCAN`）は ProRes の素材でも走るか。走るなら何で開くか（別のパイプラインか、shim の中か。decodebin の自動選択なら、`proresd3d11dec` の rank は none なので選ばれないはず、を確かめる）。復号まで行うのか、demux とパーサーだけか
   (b) ProRes は全フレームがイントラ。scan の結果（キーフレームの位置・`maxGapMs`）を使っている箇所（シークの予算・キーフレーム間隔の警告ほか）を追い、ProRes で何が変わるかを書く。「イントラのみのコーデックは scan を飛ばす」が正しい直しかどうかの材料にする（直すのは親の判断の後。この段では読むだけ）
   (c) scan の経路のエラー処理で、libav のスレッドから呼ばれて `g_error`・`g_assert`・`g_return_if_fail` の致命的な型に当たりうる箇所
   （参考: `v0.6.0` の親の scratchpad に落ちた回のダンプとログがあるが、担当は読まなくてよい。スタックの粗い読みは上のとおり）

## してはいけないこと

- push、`main`・`v0.6.0` への書き込み、stash・reset・clean
- **実機の試験**（アプリの起動、E2E、LTC シナリオ）。ビルドと非E2E と `--policy-only` まで
- アプリ（C#）の設定・UI・P/Invoke（担当 B の範囲）。C# を触るのは、shim の ABI の追加で既存の試験が壊れたときだけ
- 素材の作品名・ローカルの絶対パスをコミットするファイルに書くこと
- ロック規則を破ること（`python scripts/check-shim-lock-rule.py`）

## 完了の条件

- shim の Debug・Release のビルド、Debug の DLL を `native\tcs_gstreamer.dll` へ
- `tcs-shim-test --policy-only` が Debug・Release とも `failures=0`（門の真理値表を含む）
- `check-shim-lock-rule.py` が PASS
- `dotnet build` と非E2E（`--filter "FullyQualifiedName!~E2ETests"`）が全件合格
- `get-prores-plugin.ps1` が v0.2.0 で OK（.ps1 は 5.1 で動く・ASCII・CRLF・構文エラー 0）
- 日本語のコミット（1 項目 1 コミットを目安）
- **終わったら、そのツリーでビルドやファイルの書き換えをしない**（親が同じツリーで実機を回すため）

## 報告

`docs/reports/2026-09-30-agent-a-v060-stage2.md` に事実だけ（合否は書かない）。コミットの一覧、項目ごとの変更と要点、試験の件数、5 の既存の WARNING の経路、6 の既定の状態での ERROR の出方、7 の読み、親が実機で確かめる手順の案（門の 3 モード × 素材、不一致の失敗を人工的に起こす方法があればその案、壊れたフレームの素材の作り方）。
