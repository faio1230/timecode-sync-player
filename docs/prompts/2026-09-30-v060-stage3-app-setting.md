# 指示書: v0.6.0 段 3（アプリ）設定 `proResGpu`・P/Invoke・UI・CPU で開いたときの記録

担当: サブエージェント B。作業ツリー `timecode-sync-player-v054b`、ブランチ `agent-b-v060`（`v0.6.0` の `6cc4896` から）。
設計書: `docs/design/v0.6.0-prores-gpu.md`（4・5 節、3-3 の真理値表、3-4 の last-good の判定）。先に全体を読むこと。
並行して担当 A が段 2（shim）を別のツリーで進める。**下の C ABI は A と共有の約束。shim の C++ は触らない。**

## C ABI（担当 A が shim に実装する。B は宣言と呼び出しだけ）

```c
#define TCS_PRORES_GPU_AUTO 0
#define TCS_PRORES_GPU_ON   1
#define TCS_PRORES_GPU_OFF  2
TCS_API int tcs_player_set_prores_gpu (TcsPlayer* p, int mode);
/* 最初のロードの前だけ有効。戻り値: TCS_OK、ロード済みか mode が範囲外なら TCS_ERR_GENERIC */
```

このブランチの shim にはまだこの関数が無い。**実機でアプリを起動しないこと**（呼べば入口が見つからず例外になる）。試験は `FakeGstNative` で行う。

## 範囲

1. **設定**: `AppSettings.ProResGpu`（JSON は `"proResGpu"`、既定 `"auto"`）。`ProResGpuPolicy.Resolve(string?, warnUnknown)` → enum（Auto / On / Off）。空・auto は Auto、on・off（大文字小文字を区別しない）、それ以外は警告 1 行で Auto（`DecodeModePolicy` と同じ形。`DecodeModePolicy.cs` と試験を手本に）。旧い設定ファイル（キーなし）は Auto
2. **P/Invoke**: `GstNative`（定数と `tcs_player_set_prores_gpu`）、`IGstNativeApi`、`GstNativeApi`、`FakeGstNative`（呼ばれた値を記録）
3. **適用**: `GstBackendState.EnsurePlayer` の直後、`ApplyDecodeMode` の隣で `ApplyProResGpu`。**Auto でも必ず呼ぶ**（shim の既定と同じ値でも、設定の値を明示して渡す。`ApplyDecodeMode` が hardware のとき呼ばないのとは違う。理由: shim のログの `source=setting` で、設定が届いたことを確かめるため）。戻り値が失敗なら警告 1 行（起動は止めない）。GPU の復旧の作り直し（`RecreatePlayer`）でも同じ経路で掛かることを試験で固定
4. **UI**: `MainWindow.xaml` の既存の設定の並び（LTC fps の `LtcFpsModeCombo`、補正のモードの `SyncCorrectionModeCombo` の付近）に「ProRes の GPU 復号」の `ComboBox` を 1 つ。項目は「自動」「有効」「無効」（Tag は auto / on / off）
   - 選んだら設定を保存し（既存の設定の保存と同じ経路、record の `with`）、横か下に「再起動の後に反映」を表示する（起動時の値と違うときだけ）
   - 起動時は設定の値を選択状態にする。ツールチップ: 「自動: NVIDIA の GPU でだけ使う。有効: どの GPU でも使う（NVIDIA 以外は未検証）。無効: CPU で復号。GPU の復号に 1 回失敗すると、その起動の間は CPU で復号する」
   - 既存の UI の見た目・並び・既存のコントロールの名前を変えない。UI の自動試験（FlaUI）が既存のコントロールを名前で探しているので、`AutomationProperties.AutomationId` を新しいコントロールに付ける（`ProResGpuCombo`）
5. **CPU で開いたときの記録**（設計書 5 節、アプリ側）: ロードの後のメタデータ（`FetchMetadata` のデコーダ名）で、ProRes の素材（コーデック名に `prores` を含む）が `avdec_prores` で開いたら、アプリのログに `ProRes: CPU で復号（proResGpu=<auto|on|off>、理由は tcs-gst のログ）` を 1 行。**同じ内容は起動の間に 1 回だけ**。GPU（`proresd3d11dec`）で開いたときは出さない。判定は純関数にして試験で固定（1 回だけ・GPU では出さない・ProRes 以外では出さない）
6. **既存の試験**: `CodecAdviceTests` に `proresd3d11dec` を「可」とする行を足す（振る舞いは変えない。推奨に上げるかは利用者の判断待ち）

## してはいけないこと

- push、`main`・`v0.6.0` への書き込み、stash・reset・clean
- **実機の試験**（アプリの起動、E2E、LTC シナリオ）。ビルドと非E2E まで
- shim（`native/`）の変更、`CodecAdvice` の判定の変更
- 素材の作品名・ローカルの絶対パスをコミットするファイルに書くこと

## 完了の条件

- `dotnet build`（警告を増やさない）と非E2E（`--filter "FullyQualifiedName!~E2ETests"`）が全件合格
- 足した試験: `ProResGpuPolicyTests`、`GstBackendStateTests` の転送（Auto・On・Off、作り直しでも掛かる、失敗の戻り値で起動が止まらない）、CPU で開いた記録の純関数、設定の往復（保存して読み直すと同じ値、キーなしは Auto）
- 日本語のコミット（1 項目 1 コミットを目安）
- **終わったら、そのツリーでビルドやファイルの書き換えをしない**

## 報告

`docs/reports/2026-09-30-agent-b-v060-stage3.md` に事実だけ（合否は書かない）。コミットの一覧、項目ごとの変更と要点、試験の件数、UI の追加の位置（xaml の行）、親が実機で確かめる手順の案（段 2 の shim と合わせたとき、`source=setting` のログと UI の選択の往復を見る手順）。
