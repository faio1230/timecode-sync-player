# 固定キャンバスと配置計算の仕様

状態: 2026-09-11、試作に実装済み（`CanvasPlacement.cs`、管理テスト 9 件、`--source-size`）。実機確認は末尾。本体への接続は後回し。合意済みの製品動作は [OUTPUT-PIPELINE-DESIGN.md](OUTPUT-PIPELINE-DESIGN.md)「プロジェクトの固定キャンバス」に従う。

## データモデル（本体 `ProjectData`／`TrackData` の拡張案、今回は型だけ試作に置く）

- プロジェクトの映像設定 `CanvasSettings { int Width; int Height; FitMode DefaultFit; }`。新規プロジェクトは 1920×1080、`FitHeight`。
- クリップの配置設定 `ClipPlacement { FitMode? Fit; }`。null はプロジェクト既定を継承。
- `FitMode` は識別子（enum ではなく文字列 ID `"fit-height"`、`"fit-width"`）と計算処理を分け、後から `"fill"`、`"fit-inside"`、`"stretch"` 等を追加できる登録式にする。未知の ID はプロジェクト既定へフォールバックし警告を返す。
- 保存形式: `ProjectData` に `Canvas`（null は「未設定」＝既存プロジェクト）、`TrackData` に `Fit`（null は継承）。`Version` は据え置き、追加フィールドは省略可能として旧ファイルを読める。キャンバス未設定の既存プロジェクトは、初回にサイズ選択を促し、次の保存で記録する（UI は範囲外）。
- GPU 資源・実行時の画像は保存しない。

## 配置計算

入力: 素材の幅・高さ（回転は事前に反映済みとする）、キャンバスの幅・高さ、`FitMode`。
出力: `Placement { Rect Destination（キャンバス座標、実数）; Rect SourceCrop（素材座標）; }`。

- `fit-height`: 素材の高さをキャンバス高さに合わせる倍率 s = canvasH / srcH。幅 srcW·s がキャンバス幅を超える分は左右均等に切り落とす（`SourceCrop` を狭める）。足りない分は左右に黒余白（`Destination` を中央に置き、キャンバスの外は描かない）。
- `fit-width`: 上下で同じ。
- 中央揃え固定。余白は不透明な黒（クリア色）。
- 倍率は縦横同一（縦横比維持）。サブピクセルの端数は実数で持ち、整数化は描画側（ビューポート）で行う。
- 無効な入力（0 以下の寸法）は例外。

各 `FitMode` の計算は独立したクラス（`IFitCalculator`）にし、レジストリで ID から引く。テストは ID ごとに期待 Rect を固定値で検証する（16:9→16:9 等倍、4:3→16:9 の高さ合わせで左右黒、21:9→16:9 の高さ合わせで左右切り落とし、縦動画、1px 素材、キャンバスより大きい素材）。

## 反映のタイミング（規則のみ、実装は本体側）

- キャンバスサイズの変更はタイムラインの再生・LTC 追従を止めた状態だけで受け付ける。Freeze／準備待ちで画が止まっているだけの状態は対象外。
- 編集途中のサイズを逐次出力せず、新しい合成画像が確定した時点で反映する（合成層は `CanvasSettings` の不変スナップショットを tick ごとに読む）。
- テストカードはキャンバス寸法で生成し、クリップの配置設定を適用しない。

## 試作での実装

- 新規ファイル `CanvasPlacement.cs`（型・レジストリ・計算）と `CanvasPlacementSelfTests.cs`（GPU なし）。
- 試作の合成は現在キャンバス全体にパターンを描いているため、`--source contract-fake` でソース画像を合成するときに `fit-height` で配置する（ソース寸法をキャンバスと変えるオプション `--source-size WxH` を追加し、黒余白と切り落としを目視・ログで確認できるようにする）。
- 本体の `ProjectData`／`TrackData` は変更しない。

## 範囲外

UI（サイズ選択ダイアログ、クリップごとの設定画面）、保存形式の実装、テストカードの図柄、フェード。

## 実装結果（2026-09-11）

- `CanvasPlacement.cs`: `CanvasSettings`（既定 1920×1080・`fit-height`）、`ClipPlacement`、`FitRegistry`（ID 登録、null は継承、未知は既定へフォールバックし警告）、`FitHeight`／`FitWidth`。規約: Destination は常にキャンバス内、はみ出しは SourceCrop（素材座標）で表す。合わせた辺はキャンバス寸法に厳密に一致させ、丸め残差の隙間を出さない。管理テスト 9 件（16:9 同一、4:3・21:9 の高さ／幅合わせ、縦動画、1×1、巨大素材、レジストリのフォールバック、無効寸法）。
- 試作の合成: `--source contract-fake` で黒クリア→Destination をビューポート→SourceCrop を UV 矩形でサンプリング。`Display` シェーダーに UV オフセット／スケール（b1）を追加。manifest に `placement` を記録。
- 実機（1080p 全画面 12 秒、fence＋vblank＋align、公式受信機、`TestResults/gpu-placement-20260911/`）:
  - 1440×1080（4:3）→ Destination x=240 幅 1440（左右 240px の黒）、Crop 全体。合成・表示・Spout 60.000Hz、表示落ち 0、生成→走査 5.55ms。
  - 2560×1080（21:9）→ Destination 全面、Crop x=320 幅 1920（左右 320px 切り落とし）。同じく 60Hz、表示落ち 0、5.56ms。
  - 数値は仕様どおり。画面の目視は未実施（manifest と管理テストによる確認）。
- 残る注意: クロップ端の半テクセルの線形補間（必要なら point sampling か半テクセル内側寄せ）、奇数幅での半ピクセルの左右非対称（ビューポートの丸めはラスタライザ任せ）。NV12／BC でも UV 矩形はそのまま使える。

