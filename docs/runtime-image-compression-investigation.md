# VRCImageDownloader 動的画像圧縮 調査・API Probe記録

調査日: 2026-08-11

## 結論

計画書のMVP経路は、現行SDKでは実装できない。

`Texture2D.Compress(bool)` 自体はUdonへ公開されているが、圧縮済みデータを
独立Textureへ移すために必須の `Texture2D.GetRawTextureData()` (`byte[]`版) が
Udonへ公開されていない。Class Exposure Treeと同じ判定基盤、および実際の
UdonSharpコンパイルの両方で不成立を確認した。

Downloader所有Textureを圧縮したまま保持する実装は
`IVRCImageDownload.Dispose()` を実行できず、計画書の最重要要件と禁止事項に反する。
そのため、フォールバック専用の不完全な製品コードは追加していない。

## 調査環境

- Unity: 2022.3.22f1 (887be4894c44)
- VRChat SDK Worlds: 3.10.4
- VRChat SDK Base: 3.10.4
- UdonSharp: Worlds 3.10.4へ同梱された版（独立したsemantic versionはパッケージに記録なし）
- VPM manifest: `com.vrchat.worlds` 3.10.4、`com.vrchat.base` 3.10.4
- Active Build Target: StandaloneWindows64
- Graphics API: Direct3D 11
- Active scene: `Assets/Scenes/VRCDefaultWorldScene.unity`
- Editor状態: 非Play Mode、非コンパイル
- Build Settings登録シーン: なし
- Android/iOS: 現在のBuild Targetではなく、本調査では未検証

## 既存資産の調査

作業領域には、調査開始時点で
プロジェクト固有のScript、Material、Shader、Prefab、Sceneが存在しなかった。

アクティブシーンはVRChat SDKの既定シーンで、ルートは `VRCWorld`、
`Main Camera`、`Directional Light`、`Floor`、`EventSystem` の5個だった。
既存の `VRCImageDownloader` 利用箇所、対象Material/Renderer/RawImage、
カスタムShader、`TextureInfo` 設定、`GenerateMipMaps` 設定は存在しない。

したがって、既存Image Loaderへ統合できる対象も現時点ではない。

## API Probe結果

Class Exposure Treeが使用する `CompilerUdonInterface.IsExposedToUdon` で確認した。

| API | Udon公開 |
|---|---:|
| `Texture2D.Compress(bool)` | Yes |
| `Texture2D.GetRawTextureData()` (`byte[]`) | **No** |
| `Texture2D.GetRawTextureData<byte>()` | No |
| `Texture2D.GetPixelData<byte>(int)` | No |
| `NativeArray<byte>.ToArray()` / `CopyTo(byte[])` | No |
| `Texture2D.LoadRawTextureData(byte[])` | Yes |
| `Texture2D.Apply(bool, bool)` | Yes |
| `new Texture2D(int, int, TextureFormat, bool)` | Yes |
| `new Texture2D(int, int, TextureFormat, bool, bool)` | Yes |
| `new Texture2D(int, int, TextureFormat, int, bool)` | Yes |
| `Texture2D.Reinitialize(int, int, TextureFormat, bool)` | Yes |
| `UnityEngine.Object.Destroy(Object)` | Yes |
| `Texture2D.isReadable` | Yes |
| `Texture2D.format` | Yes |
| `Texture.mipmapCount` | Yes |
| `Texture.width` / `height` | Yes |
| sampler属性のgetter | Yes |
| `Graphics.CopyTexture(Texture, Texture)` | No |
| `Graphics.ConvertTexture(Texture, Texture)` | No |

一時的な `RuntimeImageCompressionApiProbe` UdonSharpプログラムを作成し、
UdonSharp compilerを明示実行した結果は次のとおり。

```text
RuntimeImageCompressionApiProbe.cs(10,34):
Method is not exposed to Udon: 'source.GetRawTextureData()'
```

Probe用 `.cs`、UdonSharpProgramAsset、生成フォルダは確認後に削除した。

## Windows EditorでのCompress実測

以下はダウンロード画像ではなく、Editor内で生成した合成TextureによるAPI単体測定。
時間は内容、CPU、実行状態に依存するため参考値とする。

| 入力 | 入力bytes | 圧縮後 | 圧縮後bytes | 比率 | 時間 |
|---|---:|---|---:|---:|---:|
| RGBA32 2048x2048、mipmapなし | 16,777,216 | DXT5 | 4,194,304 | 25% | 19.075 ms |
| RGB24 2048x2048、mipmapなし | 12,582,912 | DXT1 | 2,097,152 | 16.67% | 7.175 ms |
| R8 1024x1024 | 1,048,576 | BC4 | 524,288 | 50% | 3.954 ms |
| RG16 1024x1024 | 2,097,152 | BC5 | 1,048,576 | 50% | 6.610 ms |
| RGB48 1024x1024 | 6,291,456 | DXT1 | 524,288 | 8.33% | 5.514 ms |
| RGBA64 1024x1024 | 8,388,608 | DXT5 | 1,048,576 | 12.5% | 7.836 ms |
| RGBA32 1024x1024、11 mips | 5,592,404 | DXT5、11 mips | 1,398,128 | 25% | 5.282 ms |

RGBA32 2048x2048を同一条件で比較した参考値は、`highQuality=false` が
15.650 ms、`highQuality=true` が15.817 msだった。合成された単色相当データのため、
実画像の画質・停止時間を代表する測定ではない。

## NPOT実測

Windows / D3D11では、各辺が4の倍数ならNPOTでも圧縮できた。
BCブロック境界に合わない画像は例外ではなくno-opになった。

| 入力 | 結果 |
|---|---|
| RGBA32 1920x1080 | DXT5、8,294,400 -> 2,073,600 bytes |
| RGBA32 1000x750 | no-op、RGBA32のまま |
| RGBA32 1023x1023 | no-op、RGBA32のまま |
| RGBA32 1024x1023 | no-op、RGBA32のまま |

従って、将来実装できる場合もformatとbytesの両方による成功判定が必要。

## 設計への影響

Strategy A (`Destroy`) とStrategy B (`Reinitialize`) の寿命管理APIは公開されている。
しかし、どちらもDownloader所有Textureから独立Textureへ圧縮データを移す手段がなく、
寿命管理へ到達する前段で停止する。

`Graphics.CopyTexture` によるGPU上の直接コピーもUdon非公開だった。
そのため、次の状態を同時に満たすMVPは現行SDKでは構成できない。

1. 最終表示Textureが圧縮済み
2. 最終表示TextureがDownloaderから独立
3. `IVRCImageDownload.Dispose()` 後も表示を維持
4. 差し替え時に独立Textureを確実に解放または再利用

## 未実施項目

- VRCImageDownloaderによる実画像ダウンロード
- `IVRCImageDownload.Dispose()` 後の表示維持
- 10～20回の連続差し替え・リーク試験
- VRChat Build & Test
- Android / Quest
- iOS

実行可能なUdonプログラムが成立していないため、これらを実施してもMVPの検証には
ならない。WorldのUpload/Publish、SDK・Unity・VPMパッケージ更新は行っていない。

## 次の判断候補

1. VRChat SDK側で `Texture2D.GetRawTextureData()` の `byte[]` 版、または
   `Graphics.CopyTexture` がUdon公開された版へ更新できる時点で再Probeする。
   更新はこのプロジェクトの変更管理に従い、明示承認後に行う。
2. SDK更新を待てない場合は、計画書Phase 2の
   `VRCGraphics.Blit` + encoder shader + `VRCAsyncGPUReadback` を別タスクとして
   技術検証する。BC7/ASTCエンコーダ実装、プラットフォーム差、Udon公開API、
   GPU readbackのraw layoutを改めてProbeする必要がある。
3. Downloader所有Textureを単に `Compress()` して保持する案は採用しない。
   `IVRCImageDownload.Dispose()` 不能となり、今回の主目的を満たさない。

