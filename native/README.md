# Native Dependencies

このフォルダはネイティブDLLの置き場です。ライセンスの都合上、リポジトリにはDLL本体を含めません（`native/*.dll` は `.gitignore` 対象です）。

`TimecodeSyncPlayer` のビルドでは、このフォルダに存在するDLLだけが `src/TimecodeSyncPlayer/bin/<Configuration>/net8.0-windows/` に自動コピーされます。DLLが無くてもビルド自体は成功しますが、`mpv-2.dll` が無いと動画再生はできません。

---

## 必須 DLL

| ファイル | 用途 | 入手方法 |
|---------|------|---------|
| `mpv-2.dll` | libmpv（動画再生・ソフトウェアレンダー） | https://mpv.io/installation/ から Windows 向けビルドをダウンロードし、`mpv-2.dll` を取り出してこのフォルダに配置。**x64版**であることを確認してください。 |

## オプション DLL

| ファイル | 用途 | 入手方法 |
|---------|------|---------|
| `SpoutDX.dll` | Spout2送信（VJツール連携、任意機能） | https://github.com/leadedge/Spout2 — SDK内の SpoutDX プロジェクトをビルドして生成 |

`SpoutDX.dll` が無い場合、アプリ内のSpout出力ボタンが無効化されるだけで、それ以外の機能は正常に動作します。

---

## 配置例

```
timecode-sync-player/
└── native/
    ├── mpv-2.dll       # 必須
    └── SpoutDX.dll     # 任意
```

配置後、`docs/SETUP.md` の手順に従ってビルドしてください。
