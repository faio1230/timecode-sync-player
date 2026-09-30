（下書き。開発機の一式が済み、候補の SHA-256 が決まってから送る）

v0.6.0 の候補 1 の検証をお願いします（TSP-Opus）。
- 候補: ProductVersion `0.5.4+<SHA>`（版は据え置き）。setup.exe・zip・テストのソースの tar を Taildrop で送ります（SHA-256 は下）
- 順番: (0) 事前確認 → (1) 実インストール → (2) 一式 → (3) ProRes の追加の回

(0) 事前確認
- コミットの空き 4 GB 以上、C: の空き 20 GB 以上（ランナーの事前確認にも入った）
- `$env:TCS_FFMPEG` を明示してからランナーを起動し、レポートの `ffmpeg-version.txt` が 8.0.1 であることを確かめる
- ランナーは pwsh で起動する（`pwsh -NoProfile -File scripts\run-ltc-scenarios.ps1 ...`）。空振りで `-?` を使わない

(1) 実インストール（一式の前）
- (a) インストールの前に `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64` の Major・Minor・Bld を記録
- (b) setup.exe でインストール。インストーラーのログの `VC++ runtime x64 installed … install=` の行と、インストールの後の版を記録（14.50.35710 以上が入っていれば再頒布は走らないのが正しい）
- (c) インストールの後の最初の起動で、起動にかかった時間（プラグインの登録・レジストリの作り直しを含む）と、アプリのログの `ui.heartbeat` の最初の `lateMs`。2 回目の起動と比べる

(2) 固定の一式（v0.5.4 と同じ M1〜M7 の割り当て）
- 標準シナリオ 3 通り（pass-a M1,M4,M7 は本番構成: インストール版・トレース無し / pass-b M2,M5,M6 / pass-c M3,M1,M5）
- L-1 6 本（M1,M2,M3 と M4,M5,M7）、A 切替 2 本
- ProRes（M5）は、出力先を RTX に置いた構成で `prores-gpu` になるはず（tcs-gst のログの `load.summary … profile=prores-gpu`、`prores-gpu: adapter-luid set=… read=…` が RTX の LUID、`out-mem … same=1`）。結果 JSON の `prores` の内訳で `cpu` が 0
- 所見に書き添えるもの: 開始時の `commitFreeGbAtStart`・`cDriveFreeGbAtStart`、`appExit` の回数・中央・最大（15 秒超があればログの行）、`prores` の内訳と `adapterMismatch` の回数

(3) ProRes の追加（別の `-MediaDir` に ProRes のサブフォルダ、記号は PR と長さで）
- 出力先 RTX: PR1（206 秒）・PR2（218 秒）・PR3（198 秒）で標準シナリオ 1 通り。すべて `prores-gpu`
- 出力先なし（内蔵 AMD）: M5 を含む 1 通り。`prores-gpu: mode=auto … vendor=0x1002 -> disabled`、`load.skip … reason=prores-gpu-vendor`、`prores-cpu`
- 色: M5（HQ 4K60）と PR1（1080p60）の同じ位置の 1 フレームを、GPU（既定）と CPU（`TCS_PRORES_GPU=off`）で取り、BGRA の差の平均と p99 を出す（取り方は相談）
- PR4（4444 アルファ 4K30）: GPU と CPU でロードして止まらないこと、同じ位置の合成後の 1 フレームが一致すること（色のしきい値の分布には入れない）
- 追加でもう 1 回、切り替えの後の非E2E と E2E を 1 回ずつ（TCS_FFMPEG の切り替えで、既存の合否の基準がそのまま通るか）

結果は回ごとの結果 JSON と 3 行の所見を Taildrop で。ホスト名・利用者名・素材のファイル名は書かない。
