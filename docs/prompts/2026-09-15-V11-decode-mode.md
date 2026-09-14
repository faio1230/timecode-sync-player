# V11: デコード方式の切替（`decodeMode`）を実装する

main から新ブランチ `codex/v11-decode-mode-20260915` を切る。
**自分のワークツリー（`C:\Users\codea\Documents\timecode-sync-player-wt-output-engine-20260911-1247`）で作業すること。**
※ パスは全て**小文字ハイフン**が正しい綴り。大文字混じりだと毎回許可を聞かれる。

不変条件は `docs/OUTPUT-GPU-INVARIANTS.md`（I13 を含む）。実機の前に一声かけること。
**合否判定は書かないこと。**

---

## これは何か

**mpv 除去後の退避手段**。GPU ハードウェアデコードが使えない／不調な素材や環境のために、
**CPU ソフトウェアデコードへ全体を切り替える 1 つのスイッチ**を設ける。

仕様は `docs/V04-SCOPE-mpv-removal-decode-mode.md` の 3 節にある。**そちらが正**。以下は要点。

| モード | profile の探索順 |
| --- | --- |
| `hardware`（**既定**） | GPU profiles → CPU profiles → `decodebin(sysmem)`（**現行と同一**） |
| `software` | **CPU profiles → `decodebin(sysmem)` → GPU profiles（最後の手段）** |

- `AppSettings` に `decodeMode` を追加。**settings.json のみ、UI は作らない**（`backend` / `outputBackend` と同じ扱い）
- 不正値は `hardware` として扱い、**警告ログを 1 回**
- `software` で GPU に落ちたときは**必ず警告ログ**（「ソフトウェアデコーダが無いためハードウェアを使った」）。
  **黙って意図と違う経路を使わないこと**
- **既定 `hardware` の挙動は現行から一切変えない**

---

## 先に提案してほしいこと（実装前に親が承認する）

**shim への伝え方。** C ABI は不変条件の管轄なので、**実装前に提案し、親の承認を得てから着手すること。**

- 候補 1: `tcs_player_create` に引数を追加する
- 候補 2: `tcs_player_set_decode_mode(player, mode)` を新設する
- **環境変数で渡す方法は採らない**（製品の設定が環境変数に化けると追跡しにくい）

**どちらを選ぶか、理由と、既存の呼び出し側への影響**を短く書いて出すこと。
**承認を待たずに実装しないこと。**

---

## 実装後の検証（V11 の a〜d）

`docs/V04-SCOPE-mpv-removal-decode-mode.md` の 5 節の表がそのまま合格条件。

| | 内容 | 合格 |
| --- | --- | --- |
| V11-a | `hardware` 既定の非回帰 | V1 の 11 素材を既定で再実行し、**V1 の結果と同一**（デコーダ名も一致） |
| V11-b | `software` での再生能力 | 同じ 11 素材を `software` で実行し、**素材ごとに達成 fps と CPU 使用率を記録**。1080p は実フレーム＝素材 fps。**4K は達成値を記録する（未達でも可。限界を文書化する）** |
| V11-c | 退避の警告 | ソフトウェアデコーダが無いコーデックで `software` を指定 → **GPU に落ち、警告ログが出る** |
| V11-d | 出力側の不変 | `software` で共通条件（表示・合成 p99・Spout・error・exit）が `hardware` と同等 |

**V11-a の「デコーダ名も一致」が重要。** 既定を変えていないことの証拠になる。
`FetchMetadata` のログに出るデコーダ名（`d3d11h264dec` など）で確認すること。

**実機は親が回す。** ただし短い動作確認は自分で回してよい（1 本ずつ、自分の PID のみ終了）。

---

## 守ること

1. **既定（`hardware`）の挙動を変えない。** ここが崩れると V1 をやり直すことになる
2. **C ABI の変更は親の承認後**
3. 非E2E が全て通ること（現在 1754 件）
4. `python scripts/check-shim-lock-rule.py` が PASS すること
5. **合否判定は書かないこと**

## 提出時に書いてほしいこと

- C ABI の伝達方法（承認を得たもの）と、その実装箇所
- `software` の探索順が仕様どおりであることを、どう確認したか
- 警告ログが出る条件と、実際に出た例
- 非E2E と `check-shim-lock-rule.py` の結果
