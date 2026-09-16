# 映像ソース契約（合成層から見た GPU 画像の供給規則）

状態: 2026-09-11、試作に実装済み（`VideoSource.cs`、`SourceImageRing.cs`、`FakeVideoSource.cs`、管理テスト 9 件、解析器 `source` セクション）。実機確認は末尾の「実装結果」を参照。本体の変更は含まない。

## 目的

出力側の設計（[契約の突き合わせ](OUTPUT-GPU-CONTRACT-MAPPING-2026-09-10.md)、[整列実証](GPU-COMPOSE-ALIGN-RESULTS-2026-09-10.md)）は「合成層が、再生位置に対する最適な画像を選び、固定キャンバスへ合成し、各出力へ最新優先で渡す」と定めた。ソース（v0.4 は GStreamer。将来の HAP など）はその合成層に GPU 画像を供給するだけで、出力やタイムラインの判断を持たない。この境界を、コードで検証できる契約として先に置く。

## 用語

- **素材世代（generation）**: 素材の読み込み・切替・シークのたびに増える整数。合成層が現在値を持ち、ソースへ通知する。
- **時間位置（position）**: 再生ヘッダの時刻（素材内の秒、または再生エンジンの再生時計）。ソースはこの位置に対する「利用可能な最適画像」を返す。
- **画像 lease**: GPU テクスチャ（NV12 または BGRA）への読み取り権。合成層が使い終わるまでソースはそのテクスチャを再利用しない。
- **供給結果**: `Ready(lease)`、`NotReady`（準備中・読込み失敗・世代不一致）、`Ended`（素材末尾）。黒やエラー画像は作らない。

## インターフェース（試作の C#）

```csharp
readonly record struct SourceImageStamp(int Generation, long Sequence, double PositionSeconds, long DecodedQpc);
enum SourceStatus { Ready, NotReady, Ended }
interface ISourceImageLease : IDisposable          // 合成層が所有。Dispose で返却
{
    SourceImageStamp Stamp { get; }
    ID3D11Texture2D Texture { get; }               // 読み取り専用。NV12 か BGRA
    int Width { get; } int Height { get; }
    bool IsNv12 { get; }
    void BeginGpuUse(); void CompleteGpuUse();      // 既存 LatestPool.Lease と同じ規則
}
interface IVideoSource : IDisposable
{
    void SetGeneration(int generation);            // 以後、古い世代の画像は返さない
    SourceStatus TryAcquire(int generation, double positionSeconds, out ISourceImageLease? lease);
    SourceDiagnostics Diagnostics { get; }         // デコーダー名、GPU、色形式、待ち・失敗件数
}
```

## 規則（管理テストで固定）

1. **世代の排除**: `SetGeneration(n)` 後、`TryAcquire(n, …)` は世代 n の画像だけを返す。世代 n−1 の画像がデコード済みでも返さず `NotReady`。`TryAcquire(m≠現在世代)` は `NotReady`。
2. **最新優先**: 同じ世代で複数の画像が準備済みなら、`position` 以下で最大の位置の画像を返す。`position` より先の画像しかない場合はその最初の1枚（次に来る画像）を返し、Stamp で判別できるようにする。過去画像の待ち行列は持たない（合成層が取らなかった画像は捨ててよい）。
3. **準備できないときは なし**: デコード待ち・読込み失敗・EOS 直後は `NotReady`／`Ended` を返し、黒・前画像・エラー画像を返さない。保持（最後に確定した画像の継続表示）は合成層の責務。
4. **lease の寿命**: 返却前に同じテクスチャへ書き込まない。有限個のプール（既定 3 枚以上）で、全 lease が使用中なら新しいデコード結果は最も古い未 lease の画像を置き換える。`Dispose` は1回だけ有効、`BeginGpuUse` 中の `Dispose` は例外。`IVideoSource.Dispose` は全 lease の返却を待ってからテクスチャを解放する（強制解放しない）。
5. **デバイス**: ソースは合成層から渡された `ID3D11Device` 上にテクスチャを作る（同一デバイス）。別デバイスを使う実装は、共有フェンスで書き終わり順序を保証する責任をソース側に持つ。
6. **スレッド**: `TryAcquire` と `SetGeneration` は合成スレッドから呼ばれる。ソースのデコードスレッドは合成スレッドを待たない。`TryAcquire` はブロックしない（デコードを待たない）。
7. **診断**: 実際に選ばれたデコーダー名・GPU・色形式、世代排除件数、NotReady 件数、置き換え件数、lease の最大同時数を `Diagnostics` で返す。

## 試作での実装

- `FakeVideoSource`: 現在の GPU 生成パターン（`ShaderPipeline.Compose` の元画像）をソースとして包む。デコードスレッドの代わりにタイマー（素材 fps、既定 30fps で 60Hz 合成との差を再現）で画像を生成し、`position` は生成時刻から計算する。
- 合成層側の `--source fake` は既存動作と同じ（既定）。`--source contract-fake` で `IVideoSource` 経由に切り替え、同じ出力になることを既存の解析で確認する（合成・表示・Spout の 60Hz、画像 ID の単調増加）。
- 管理テスト（GPU なし、フェイクテクスチャ）: 上記規則 1〜4・6 を、世代切替・位置の前後・全 lease 使用中・EOS・Dispose 順序で検証する。

## GStreamer／HAP 実装が満たす条件

- `IVideoSource` を実装し、上記の管理テスト（テクスチャをフェイクに差し替え可能な形）に合格する。
- D3D11VA 経路は NV12 テクスチャ、HAP 経路は BC テクスチャを返す（BC の場合は `IsNv12=false` と形式の追加が必要になるため、契約に `Format` を持たせる拡張を許容する）。
- 時計: `positionSeconds` は合成層が渡す。ソース内部の時計（GStreamer の running time）を合成層へ露出しない。LTC ジャンプは `SetGeneration` で表現する。

## 範囲外

本体への接続、色変換シェーダー。

## 実装結果（2026-09-11）

- 試作 `--source contract-fake`: GPU 生成パターンを 30fps のフェイクソースとして `IVideoSource` 経由で合成層へ供給。リング容量 3（＋描画用 1 面）、lease 返却は合成の GPU 完了確認後。
- 管理テスト: 世代排除、最新優先（位置以下の最大、なければ次）、Ended、全 lease 使用中の drop 計数、古い未 lease の置換、lease の Dispose 一回・GPU 使用中の Dispose 拒否、TryDispose、別スレッドからの Offer、フェイクの周期。試作 81 件、解析器 118 件成功。
- 実機（1080p 全画面 12 秒、fence＋vblank＋align lead 3、公式受信機、`TestResults/gpu-source-20260911/20260910T191835902Z-…2d90a367`）: 合成・表示・Spout 60.000Hz、Spout 異なる ID 480/480、生成→走査 5.57ms、表示落ち 0、error 0。ソース側は 12 秒で 360 枚生成、取得 480 回すべて Ready、置換 357、drop 0、lease 最大同時 1。
- 注意: 30fps ソースを 60Hz で合成するため、各ソース画像は 2 回合成される（合成番号は増える）。ソース画像の年齢（デコード→走査）は最大約 33ms＋5.6ms で、これは素材 fps による設計どおりの値。解析器の生成→走査は合成基準であり、ソース基準の年齢は `source.acquire` の generatedQpc から別途出せる。
- 未実施: 世代切替（シーク）と Ended の実機経路。管理テストのみ。GStreamer 側の shim v3 は同じ規則（acquire(gen) のリース API、黒を作らない、外部デバイス採用）で実装中と隣のペインで確認した。

