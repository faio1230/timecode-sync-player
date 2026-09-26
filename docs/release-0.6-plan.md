# v0.6.0 計画: ProRes の GPU 復号（gst-prores-d3d11）

作成: 2026-09-25（利用者の決定。TSP-Fable 経由）。**着手は v0.5.2 → v0.5.3 の後。** いまは計画として置くだけ。

## 取り込むもの

利用者が作った GStreamer プラグイン gst-prores-d3d11（https://github.com/faio1230/gst-prores-d3d11）。

- 要素 `proresd3d11dec`。出力は D3D11Memory（I422_10LE / Y444_10LE / I422_12LE / Y444_12LE / AYUV64）
- GStreamer 1.28 以降、LGPL-2.1

## 取り込み方

1. **shim のプロファイル**: プロファイル表の `prores-cpu` の前に `prores-gpu`（`proresd3d11dec → d3d11colorconvert`）を足す。
   D34 のロード成功ゲートを通らなければ `prores-cpu` へ落ちる。HAP と同じく `TCS_PRORES_GPU=off` で切れるようにする。
   **検証機で通るまで既定はオフ。**
2. **配布**: プラグインのソースは持ち込まない。タグ付きリリースの DLL をバイナリの依存として扱い、
   `package-release.ps1` で `lib\gstreamer-1.0` に同梱する（LGPL の DLL 単体の同梱は可）。
   サードパーティ表記にライセンス文と入手元（URL とタグ）を足す。`docs/SETUP.md` の「素材の推奨」で ProRes を推奨側へ戻す。
3. **プラグイン側の前準備**（Gst-ProRes セッションに依頼済み）: タグ付きリリース、`adapter-luid` プロパティの追加、
   `d3d11colorconvert → BGRA` の交渉の確認。**TSP 側の設計はこの結果を待ってから始める。**

## プラグイン側の前準備の結果（2026-09-25、Gst-ProRes セッションから）

- **リリース**: タグ `v0.1.0`（commit `0a4f19b`）、https://github.com/faio1230/gst-prores-d3d11/releases/tag/v0.1.0
  - zip `gst-prores-d3d11-v0.1.0-win64-gst1.28.2.zip`（SHA-256 `2119a6ede678fe7ec4bfa4db3778b72a1ba57dd08c9d624e80a2cd03f7331e6b`）
  - 中身: `gstproresd3d11.dll`（SHA-256 `77b0776adbd62363e251623077546a2ea168c7dcb7f6cde72f04707433ff72d4`）、`prores_*.cso` 6 個、LICENSE、README.txt、SHA256SUMS.txt
  - ビルド: MSVC 19.50、Windows SDK 10.0.26100、Release x64、GStreamer 1.28.2 MSVC x64
  - 依存: GStreamer の 6 つ（gstd3d11 / gstvideo / gstbase / gstreamer / gobject / glib）と MSVC ランタイムだけ
- **同梱の注意**（設計で扱う）:
  - `.cso` は DLL と同じフォルダに置く
  - DLL の名前を変えない（GStreamer がファイル名から入口関数を探す）
  - **VC++ 再頒布パッケージ 14.50 以上が要る**。GStreamer の bin には入っていない → **TSP のインストーラーで用意する**（2026-09-25 利用者決定）。入っていない、または古いときだけ入れる形は設計で決める
- **`adapter-luid`**（gint64、読み書き可、既定 0）: 0 以外なら adapter より優先して LUID でデバイスを作る。作った後は実際の LUID を読み返せる。
  RTX 3070 で 0 → 読み値 71564、71564 → 71564、存在しない LUID → RESOURCE/NOT_FOUND で停止。
  **ハイブリッド GPU は未検証**（検証機は Optimus なので、TSP の出力と同じアダプタを LUID で渡すことを検証機で確かめる）
- **`d3d11colorconvert → video/x-raw(memory:D3D11Memory),format=BGRA → appsink`**: 交渉・全フレームの受け渡し・EOS まで成功
  （5 形式、インターレース TFF の alpha 付きを含む、色タグ BT.601 / 709 / 2020 / PQ / HLG となし、16 の倍数でない寸法と奇数寸法、実写 4K60 480/480）。
  `proresd3d11rgb` の経路は不要。先頭フレームの BGRA は FFmpeg の CPU 変換と平均値の差 0.3 以内
- **その他**: gst-launch で試すときは appsink に `wait-on-eos=false`。色タグの無い素材は `colorimetry=2:0:0:0` で出るので、色を決め打ちするなら TSP 側で補う

## 検証

- 固定の一式（標準シナリオ 3 通り＋L-1 6 本＋A 切替、RTX）に **ProRes 4K60 を RTX で**足す
- AMD のアダプタが選ばれたときに `caps-missing → prores-cpu` へきれいに落ちることを 1 本
- プラグインの既知の制限（AMD / Intel は未検証、エラーの報告が最大 3 フレーム遅れる）を TSP の既知の制限に書き写す

## 版

- **Pre-release のまま**（2026-09-26 の利用者の方針）。ProRes の GPU 復号・プラグイン同梱・PowerShell 7 化の検証が済むまで Latest にしない。v0.6.0 を公開しても v0.5.4 の Latest は外さない。**前提: v0.5.4 が Latest として公開済み**

v0.6.0（新しい復号経路 = 機能の追加）。v0.5.2（状態の整理）と v0.5.3（寿命の修正）を先に終える。

## 決定（2026-09-25、利用者。TSP-Fable 経由）

- **サポート範囲**: ProRes の GPU 復号は NVIDIA（RTX 級）で検証し、それをサポート対象とする。Radeon・Intel は未検証のため**サポート対象外**（既知の制限に記載）。
- **既定の振る舞い**: `prores-gpu` は検証済みベンダー（NVIDIA、DXGI VendorId 0x10DE）のアダプタでだけ既定で有効。他のベンダーは既定で `prores-cpu`。理由: フォールバック（D34 のロード成功ゲート）が拾えるのは「ロードが失敗する」型だけで、シェーダーの実装差による「絵が違う」型は通ってしまうため、未検証のベンダーでは既定で走らせない。
- **フォールバック**: 全ベンダーで有効。ゲート不成立なら `prores-cpu` へ落ちる（NVIDIA でもドライバ差の保険）。
- **切り替え**: 設定キー `proResGpu` = `auto`（既定）/ `on` / `off` を `settings.json` に置き、アプリが shim にロード時のオプションとして渡す（`decodeMode` と同じ経路）。UI に「ProRes の GPU 復号: 自動 / 有効 / 無効」の 3 択を 1 つ（適用は再起動）。環境変数 `TCS_PRORES_GPU` は試験用として残し、設定より優先。`on` で未検証のアダプタを使うときはログに 1 行出す。
- **確認**: メタデータ行のデコーダ名（`V:proresd3d11dec` など）で GPU 復号が効いたかを画面で確認できる。
- UI の 3 択は UI 刷新計画（Codex）と重なるため、刷新側の「復号」区画に `decodeMode` と並べる。

## 決定（2026-09-26、利用者。TSP-Fable 経由）: スクリプトを PowerShell 7 に寄せる

- **v0.6.0 で TSP のスクリプトを PowerShell 7 に寄せる。** v0.5.3 までは 5.1 互換を維持する（検証機の再確認を増やさない）。
- 内容: `scripts/`・`native/gst-shim/*.ps1`・`packaging` のスクリプトに `#requires -Version 7` を付け、5.1 向けの回避
  （BOM の注意書き、`$ErrorActionPreference` と NativeCommandError の回避、`-File` 起動時の `$PSScriptRoot` 既定値の手当て、
  `Get-Content -Encoding` の指定）を外して簡潔にする。文書の `powershell -File` は `pwsh -File` にする。
  `docs/SETUP.md` に PowerShell 7 の導入（`winget install Microsoft.PowerShell`）を要件として書く。
  検証機にも 7 を導入し、ランナーを 7 で 1 回通してから候補に使う。
- 背景: 開発機は 5.1 のみ（7 は未導入）。gst-prores-d3d11 のビルドは 7 が要るので、v0.6.0 で両方の前提が揃う。
  開発機への 7 の導入は利用者が行う。
- 合格の条件への追加: v0.6.0 の固定の一式と重い素材セットを **pwsh で起動したランナー**で通す。
