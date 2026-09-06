# 同期・描画セッション整理 実装計画

**Goal:** 調査で合意した4項目を実装し、既存再生挙動と実機回帰検証を維持する。
**Architecture:** 時計の注入、共有LTC制御、状態を所有する描画セッション、例外を収集する順序付き終了処理。
**Tech Stack:** C# / .NET 8 WPF x64 / xUnit / FluentAssertions / FlaUI。
**Spec:** `docs/superpowers/specs/2026-09-07-session-lifecycle-design.md`

## Global Constraints

- 通常の判定条件、実行順序、早期return、ログテンプレート・レベル・プロパティ名を保持する。
- 終了の例外経路の改善だけをTask 4の明示的な挙動変更として扱う。
- ネイティブABI、描画callbackのフィールド保持、専用スレッドでのmpv render API実行を保持する。
- コミットメッセージは日本語。未追跡AGENTS.mdはステージしない。push/mergeは行わない。
- 実装者は子エージェントを起動しない。担当ファイルだけを編集し、自己レビューとテストを実施する。
- 各タスクのテスト: `dotnet test tests/TimecodeSyncPlayer.Tests/TimecodeSyncPlayer.Tests.csproj -c Debug --filter "FullyQualifiedName!~E2ETests" -v minimal`。
- テストは振る舞いを検証する。既存テストで守られた抽出は既存テストを維持し、新しい境界/不具合には失敗を確認するテストを先に追加する。

### Task 1: 時計と時間依存テストの分離

Files: `src/TimecodeSyncPlayer/TimecodeSyncService.cs`, `GapFreezeHandler.cs`, `App.xaml.cs`（必要なら）, 対応するTestsと`tests/TimecodeSyncPlayer.Tests/Helpers/ManualTimeProvider.cs`。

Interfaces: 既存constructorを保ち、`TimeProvider? timeProvider = null`を末尾に追加。内部では`_timeProvider.GetUtcNow().UtcDateTime`を既存DateTime.UtcNowの位置で読む。ManualTimeProviderはテスト専用で、SetUtcNow/Advanceに相当する制御を提供する。

- [ ] 時計差し替えが反映されるテストを先に作り、既定時計のままでは失敗することを確認。
- [ ] 250ms debounce、5秒load timeout、3秒freeze timeoutの直前・同時・直後を固定時計で検証。
- [ ] 時間操作のreflectionと実時間境界依存を除去。呼び出し側とDI登録の互換性を確認。
- [ ] 対象テスト、非E2E全件、自己レビュー、日本語コミット。

### Task 2: 本番と統合テストでLTC制御を共用

Files: `src/TimecodeSyncPlayer/LtcSyncController.cs`（新規）, `MainWindow.xaml.cs`, `tests/TimecodeSyncPlayer.Tests/LtcSyncControllerTests.cs`（新規）, `MainWindowLtcDisplayWiringTests.cs`, `Integration/SyncScenarioHarness.cs`と関連統合テスト。

Interfaces: `LtcSyncController`にLTCフレーム処理、信号断tick、表示状態更新、同期モード分岐と手動Gap退出を集約する。既存LtcFrameProcessor/SignalLossPolicy/SingleModeSyncCoordinator/ContinueOnTrackCoordinator/GapEnterCoordinatorを利用。境界のeffectsはUIとI/Oの意味のある操作単位にとどめる。詳細な署名は既存呼び出しの順序を読み、同一制御を本番/テスト双方が呼べるように定める。

- [ ] 同期抑止、信号断→復帰、手動操作、デバイス列挙失敗→繰り返しtickの振る舞いをテストで固定する。
- [ ] MainWindowのLTC状態/制御フローをcontrollerへ移し、UI DispatcherはWindow側で維持する。
- [ ] ハーネス内のSupplyLtc/ApplySync/信号断/手動Gap退出の重複判断を同じcontrollerへの呼び出しへ置換。
- [ ] 正規表現テストを本番controllerの表示維持テストへ置換。
- [ ] 対象テスト、非E2E全件、自己レビュー、日本語コミット。

### Task 3: 描画セッションと所有権の抽出

Files: `src/TimecodeSyncPlayer/RenderSession.cs`（新規）, `MainWindow.xaml.cs`, 必要な描画クラスとDI登録, `tests/TimecodeSyncPlayer.Tests/RenderSessionTests.cs`（新規）。

Interfaces: RenderSessionがnative contextと専用スレッド/params/callback/バッファ/世代/ゲート/進行中workerを所有する。Create/ProcessUpdate/Render/Invalidate/Disposeに相当する呼び出しを提供し、Windowは描画セッションを介して操作する。Task 2で作成したLTC制御とは意味のあるGap状態/表示操作で接続する。メソッド移動だけにせず、所有する状態をMainWindowから除去する。

- [ ] fake native APIと完了制御できるTaskで、旧世代フレーム破棄、処理中の世代変更、直列公開、callback保持、同一スレッドcreate/update/render/freeをテストする。
- [ ] 描画資源の生成・使用・停止をRenderSessionへ移し既存部品を再利用する。
- [ ] 通常/Black/Freezeのawait後の再確認とbuffer gateを保つ。UI/Spout公開はUIスレッド。
- [ ] Task 4が安全に停止/解放できる所有関係を文書化する。
- [ ] 対象テスト、非E2E全件、自己レビュー、日本語コミット。

### Task 4: 例外があっても安全に終了する

Files: `src/TimecodeSyncPlayer/MainWindowResourceDisposer.cs`, `MainWindow.xaml.cs`, `RenderSession.cs`, `App.xaml.cs`（必要なら）, 各対応テスト。

Interfaces: DisposeAllは順序を保ち各独立処理を試行し、最後にAggregateExceptionで例外を通知する。RenderSessionはnative worker完了前の解放を禁じ、context→thread/bufferなどの依存を明示的に扱う。

- [ ] 途中の解放例外でも後続の独立資源を試行し、複数例外を保持する失敗テストを先に追加。
- [ ] 初期化途中、二重Dispose、callback後着、進行中workerの終了とnative資源解放順を検証。
- [ ] Window/RenderSession/DIの解放責任を整理し、例外経路を改善。
- [ ] 対象テスト、非E2E全件、自己レビュー、日本語コミット。

### Final verification (controller)

- [ ] 全変更の独立レビューと必要な修正。
- [ ] Debugビルド警告0・エラー0、非E2E全件、E2E全件（テスト対象EXEを今回のworktreeに固定）。
- [ ] `docs/ARCHITECTURE.md`と本計画へ構成・検証結果を記録し、ユーザーへ作業ブランチと結果を報告。

## 実施記録

| 段階 | コミット | 検証・レビュー |
| --- | --- | --- |
| 変更前 | `ef67b68` | Debug build警告0、非E2E 1174/1174、実機E2E 50/50（Skip 0） |
| 時計の注入 | `6e3a420` | 非E2E 1177/1177、独立レビューで仕様・品質とも承認 |
| LTC制御の共用 | `5266760` | 非E2E 1184/1184、実機E2E 50/50（Skip 0）、独立レビュー承認 |
| 描画セッションの抽出 | `a0de055` | 非E2E 1194/1194、実機E2E 50/50（Skip 0）、独立レビュー承認 |
| 終了処理の改善 | `0149f1f` | 非E2E 1207/1207、Debug build警告0・エラー0 |

MainWindowは2163行から1719行へ、コンストラクタの依存は25個から19個へ減少した。
新しいクラスは単なる呼び出し転送ではなく、LTC状態と制御フロー、描画資源と公開の所有者になっている。

### 実装上の判断

- ユーザーの「1から4まで実施」を設計意図と実行方法の承認として扱い、詳細な接続方法は既存コードに合わせて決定した。
- 元のmainを保持するため、`.superpowers/worktrees/session-refactor` の `refactor/session-lifecycle` で作業した。
- 7月の完了済み抽出計画の「native所有構造を動かさない」は当時の抽出範囲に対する制約。今回は明示的な描画セッション抽出のため所有権を移すが、ABI・callback保持・専用スレッドの条件を維持する。
- 時計のUTC差分を維持したため、OS時刻調整への耐性を新たに追加する変更は含めない。
- 終了時にrender contextの解放に失敗した場合は依存資源を保持し、独立した後処理だけを続ける。使用中の資源を先に解放する危険を避けるためで、失敗時にはプロセス終了まで資源が残る。
- native workerが戻らない場合の待機時間上限は追加しない。タイムアウト後の強制解放は実行中のnative処理を壊すため、強制終了が必要な場合はプロセス単位で扱う。
- 一時的な実装・レビュー報告はgitignore済みのSDD領域に保存し、設計・構成・検証結果は追跡対象のdocsへ残す。未追跡AGENTS.mdはステージしない。
