# 文書案内

開発や現場準備では、まず現行の手順を参照してください。版ごとの設計書と検証記録には、当時の判断や結果を残しています。

## 現行の手順と仕様

| 用途 | 文書 |
|---|---|
| 現場準備、素材とLTCの条件 | [現場準備ガイド](USER-MANUAL.md) |
| 開発環境、ビルドとテスト | [セットアップ](SETUP.md) |
| ネイティブDLLの入手とビルド | [ネイティブ依存](../native/README.md) |
| 再生・同期・出力の内部構造 | [アーキテクチャ](ARCHITECTURE.md) |
| 設定項目 | [設定リファレンス](settings.md) |
| 実機での確認と公開条件 | [検証チェックリスト](verification-checklist.md) |
| 公開済みの版と今後の候補 | [開発ロードマップ](ROADMAP.md) |
| 次の開発の基準となる確認結果 | [2026-10-14の検証記録](reports/2026-10-14-development-baseline.md) |

## 版ごとの記録と過去の計画

公開内容は[CHANGELOG](../CHANGELOG.md)と[リリースノート](release-notes/)に記録しています。現行安定版は[v0.6.6](release-notes/v0.6.6.md)で、前の版は[v0.6.5](release-notes/v0.6.5.md)です。

設計の経過は[design/](design/)にあります。[v0.6.6の現場修正](design/v0.6.6-field-fixes.md)にはその版の決定と持ち越した報告、[2026年10月の試験基盤](design/test-infra-2026-10.md)には試験の改善と実施結果を残しています。実施済みの項目と未決定の候補は、各文書の記録で区別してください。

[テストカバレッジ拡充ロードマップ](test-coverage-roadmap.md)、[拡充進捗](test-coverage-progress.md)、[テスト強化プラン](test-hardening-plan.md)は2026年7月の計画・経過です。当時のルールと件数は本文に保存しています。現在の開発手順と最新の検証結果は、上の現行文書を参照してください。
