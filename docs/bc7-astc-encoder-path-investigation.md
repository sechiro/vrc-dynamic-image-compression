# VRCImageDownloader BC7 / ASTC encoder 経路調査

調査日: 2026-08-11

## 結論

- **BC7:** Udon から実行できる GPU encoder の技術経路は成立する。fragment shaderによる **BC7 Mode 6限定PoC** まで実装し、D3D11 Editorと実Udon VMでRInt / ARGB32の両経路を完走した。詳細は [Runtime BC7 Mode 6 encoder PoC 実装報告](bc7-mode6-poc-implementation.md) を参照。ただしVRChat Build & Test、実画像、連続差し替えは未完了で、製品版とみなせる段階ではない。
- **ASTC:** GPU readback から `Texture2D.LoadRawTextureData(byte[])` へ渡す搬送経路はBC7と共用できる。一方、実用的なASTC encoder本体はBC7 Mode 6より大幅に複雑であり、今回のMVPへ同時投入するのは非推奨。Android/Questは別マイルストーンとして扱う。
- 当初想定した `RenderTextureFormat.ARGBInt` 1ピクセルへの128bit出力は使えない。Windows/D3D11では生成できても、AsyncGPUReadbackが `RGBA32 SInt` を入力として拒否した。
- 代わりに、1圧縮ブロックを4個の32bitピクセルへ展開する。
  - 優先経路: `RenderTextureFormat.RInt`
  - フォールバック: `RenderTextureFormat.ARGB32` に4 byteをRGBAとして書く
- Windows/D3D11実測では、両経路とも期待した16 byteを完全一致で回収できた。

この経路は、前調査で判明した「`Texture2D.GetRawTextureData()` がUdon非公開」という障害を回避する。元Textureのraw dataを読むのではなく、shaderが圧縮ブロックを直接生成し、GPU readbackでそのブロック列だけをCPU側へ移す。

## 調査環境

- Unity 2022.3.22f1
- VRChat SDK Worlds / Base 3.10.4
- Active Build Target: StandaloneWindows64
- Graphics API: Direct3D 11
- GPU: NVIDIA GeForce RTX 3060
- Active scene: `Assets/Scenes/VRCDefaultWorldScene.unity`
- Android / Quest / Vulkan / GLES3: 未実測

## Udonから利用できるAPI

Class Exposure TreeとUdonSharp compilerの公開判定基盤で確認した。

| API | Udon公開 |
|---|---:|
| `VRCGraphics.Blit(Texture, RenderTexture, Material, int)` | Yes |
| `VRCAsyncGPUReadback.Request(Texture, int, IUdonEventReceiver)` | Yes |
| `VRCAsyncGPUReadbackRequest.TryGetData(byte[], int)` | Yes |
| requestの `done`, `hasError`, `layerDataSize`, `width`, `height` | Yes |
| `new RenderTexture(..., RenderTextureFormat, RenderTextureReadWrite)` | Yes |
| `RenderTexture.Create()` / `Release()` / `IsCreated()` | Yes |
| `Texture2D.LoadRawTextureData(byte[])` | Yes |
| BC7/ASTCを指定できる `Texture2D` constructor | Yes |
| `Texture.isDataSRGB` | Yes |
| `Material.SetInt/Float/Vector/Texture` | Yes |
| `SystemInfo.SupportsTextureFormat` | **No** |
| `SystemInfo.SupportsRenderTextureFormat` | **No** |
| `SystemInfo.supportsAsyncGPUReadback` | **No** |
| `Application.platform` | **No** |
| `RenderTexture.GetTemporary/ReleaseTemporary` | **No** |

サポート問い合わせAPIが使えないため、RenderTextureは `Create()` の戻り値と `IsCreated()`、readbackはcallbackの `hasError`、出力形式は起動時sentinel probeで判定する。BC7/ASTCの選択はruntimeの `Application.platform` では行えないため、プラットフォーム別ビルド設定として明示的に与える必要がある。

## 成立した搬送レイアウト

BC7とASTC 4x4はいずれも、1ブロックが16 byteで4x4 texelを表す。ブロック数を次のように置く。

```text
blockWidth  = ceil(outputWidth  / 4)
blockHeight = ceil(outputHeight / 4)
```

出力RenderTextureを次の寸法にする。

```text
RT.width  = blockWidth * 4
RT.height = blockHeight
```

fragment位置から次を求める。

```text
blockX = pixelX >> 2
lane   = pixelX & 3
blockY = pixelY
```

shaderは `(blockX, blockY)` の4x4入力を圧縮し、128bit blockの `word[lane]` を出力する。readback byte配列は行優先で次の並びになる。

```text
block0.word0, block0.word1, block0.word2, block0.word3,
block1.word0, block1.word1, block1.word2, block1.word3, ...
```

これは `LoadRawTextureData(byte[])` が要求するブロック列と一致させられる。

### D3D11 sentinel実測

shaderから次の4ワードを `RInt` 4x1へ出力した。

```text
0x11223344 0x55667788 0x99AABBCC 0xDDEEFF00
```

AsyncGPUReadback結果:

```text
44 33 22 11 88 77 66 55 CC BB AA 99 00 FF EE DD
```

`ARGB32` 4x1へ各ワードをRGBA 4 byteとして出力した場合も、同じ16 byteになった。つまりD3D11では32bit wordのlittle-endian順とraw byte列を確認済み。

ただしこれはAndroid/Vulkan/GLES3を保証しない。各ビルドでsentinelを実行し、16 byte完全一致を確認してからencoderを有効化する。

## ARGBInt経路が不成立な理由

`RenderTextureFormat.ARGBInt` は32bit signed integerを4チャンネル持つため、理論上は1ピクセルで128bitを保持できる。現環境では `Create()` も成功した。しかしreadback時にUnityが次のエラーを出した。

```text
AsyncGPUReadback - RGBA32 SInt (44) graphics format is not supported as source for async read back
```

従って `Create()` 成功だけではreadback可能性を判定できない。`RInt` は同環境でreadback成功、`layerDataSize=4`、byte列も完全一致した。

## Texture2D形式・サイズ実測

### サポート状況（Windows/D3D11）

```text
TextureFormat.BC7       supported=True
TextureFormat.ASTC_4x4  supported=False
RenderTextureFormat.RInt supported=True
AsyncGPUReadback         supported=True
```

ASTC Texture2Dの生成とraw byte長取得はWindows Editorでも可能だったが、サンプリング可能な形式としては非対応である。constructorが成功してもプラットフォーム対応判定にはならない。

### raw byte長

| Format | Size | Mip | Raw bytes | 結果 |
|---|---:|---:|---:|---|
| BC7 | 2048x2048 | No | 4,194,304 | 成功 |
| BC7 | 2048x2048 | Yes | 5,592,432 | 成功 |
| BC7 | 1920x1080 | No | 2,073,600 | 成功 |
| BC7 | 1000x750 | No | - | constructor失敗 |
| BC7 | 1023x1023 | No | - | constructor失敗 |
| ASTC 4x4 | 2048x2048 | No | 4,194,304 | 生成成功（GPU形式は非対応） |
| ASTC 4x4 | 1000x750 | No | 752,000 | 生成成功（250x188 blocks） |
| ASTC 4x4 | 1023x1023 | No | 1,048,576 | 生成成功（256x256 blocks） |
| ASTC 4x4 | 1x1 | No | 16 | 生成成功 |

BC7のruntime constructorは両辺が4の倍数でないと失敗した。VRCImageDownloaderは最大2048x2048だが、任意の縦横比を取り得るため、この制約は実装上重要である。

BC7では以下のいずれかが必要になる。

1. 非4アライン画像ではencoderを無効化して元画像を維持する。
2. 出力を4の倍数へリサンプルする。通常のRendererでは影響は小さいが、`RawImage.preserveAspect`等では縦横比がわずかに変わる。
3. UV scaleを同時に管理してpadding部分を隠す。利用先すべてへの適用が必要なため汎用部品には不向き。

最初のMVPでは1を推奨する。

## BC7 encoder案

### 最初の品質レベル

BC7は4x4 texel・128bit固定で、8つのmodeを持つ。Mode 6は次の単純な構成で、RGBAを扱える。

- 1 subset
- 2 RGBA endpoints
- endpointごとに共有P-bitを1個
- 各texelに4bit index
- texel 0がfix-up index

最初はMode 6だけを実装する。1ブロック内で概ね次を行う。

1. 16 texelを取得する。
2. RGBAのendpoint候補を求める。実装PoCではchannel-wise min/maxと最遠実画素ペアを比較する。
3. 各endpointについて共有P-bit 0/1を試し、量子化誤差の小さい7bit値を選ぶ。
4. decoderと同じ補間値を使い、各texelの4bit indexを決める。
5. fix-up index 0のMSBが0になるようendpoint順とindexを反転する。
6. mode bits、endpoint、P-bit、63bitのindex列を4個のuintへpackする。
7. laneごとの32bit wordを `RInt` または `ARGB32` へ出力する。

高品質encoderのように複数mode・partitionを探索しないため、グラデーション、複数色クラスタ、独立したalpha変化では品質差が出る。品質が不足する場合は、次に不透明ブロック向けmodeやalpha分離modeを追加する。

### 色空間

shader samplingではsRGB sourceがlinearへdecodeされる。初版は圧縮先Texture2Dを `linear=true` で生成し、sample済みlinear値をそのままBC7 UNORMへ量子化する。これなら表示時の二重sRGB変換を避けられ、sourceがlinear dataでも同じ処理にできる。

標準的なsRGB BC7を生成したい場合は、RGBだけをshader内でlinear-to-sRGB変換し、圧縮先を `linear=false` にする。alphaにはsRGB変換を適用しない。

### fragment shaderコスト

1ブロックを4ピクセルへ出力するため、単純実装では同じブロック圧縮計算が4回走る。2048x2048では262,144 blocks、1,048,576 fragment executionsとなる。高品質探索を避け、endpoint/index計算を固定回数にする必要がある。

ComputeShader dispatchはUdon経路として使えないため、共有計算をgroup shared memoryへ移す設計は採用できない。

## ASTC encoder判断

ASTC 4x4も1ブロック128bit・8bppなので、readbackと`LoadRawTextureData`の部分はBC7と同じでよい。NPOTもUnity側がブロック境界へ切り上げるためBC7より扱いやすい。

しかしcodecは、block mode、weight grid、weight quantization、partition、color endpoint mode、整数列encodingなどを組み合わせる。Armの公式encoderも複数の速度・品質presetと全block mode探索を持つ大規模codecである。BC7 Mode 6相当の小さな固定経路を作る場合でも、仕様準拠decoderとのbit単位検証、edge block、sRGB rounding、端末差の検証が必要になる。

従って優先順位は次とする。

1. PC/D3D11でBC7 Mode 6 proof-of-conceptを完成させる。
2. sentinel、画質、所要時間、連続差し替え時のメモリをBuild & Testで測る。
3. 成果が有効な場合だけ、ASTC 4x4の制限encoderを別設計・別検証として開始する。

ASTCの単色void-extent blockだけでは一般画像を表現できないため、実用encoderの代替にはならない。

## 非同期処理と寿命管理

推奨状態遷移:

```text
Downloaded
  -> RenderTexture.Create
  -> VRCGraphics.Blit
  -> VRCAsyncGPUReadback.Request
  -> OnAsyncGpuReadbackComplete
  -> sentinel/size/error検証
  -> Release + Destroy RenderTexture
  -> new Texture2D(BC7/ASTC, mipChain=false, linear=true)
  -> LoadRawTextureData(byte[])
  -> Apply(false, true)
  -> 表示先を圧縮Textureへ差し替え
  -> IVRCImageDownload.Dispose
```

安全性を優先する場合は、圧縮Textureの生成・差し替え成功まで元downloadをDisposeしない。ピークメモリを優先する場合はreadback成功後にRenderTextureを解放し、元downloadをDisposeしてから圧縮Textureを作るが、後段失敗時に元画像へ戻れない。

2048x2048 RGBA32、mipmapなしの概算ピークは次の通り。

| Resource | Bytes |
|---|---:|
| Source RGBA32 | 16,777,216 |
| RInt/ARGB32 output RT | 4,194,304 |
| Readback `byte[]` | 4,194,304 |
| BC7/ASTC destination | 4,194,304 |

すべて同時に存在すると約28 MiBに加え、driver・一時resource・managed overheadが乗る。ジョブは1件ずつ処理し、RenderTextureはcallback完了まで保持する。

## mipmap方針

`LoadRawTextureData`でmipmap付きTexture2Dへ入れる場合、全mipのblock列を大きいmipから順に正確なbyte長で連結する必要がある。Udon側で複数の大配列を連結するのはCPU時間とピークメモリの両面で不利。

VRCImageDownloaderの`GenerateMipMaps`既定値はfalseなので、最初のMVPもmipmapなしに限定する。mipmap対応は、全mipを1回のlinear output atlasへpackして1回readbackする方式を別途設計する。

## 実装Go / No-Go条件

BC7 proof-of-conceptへ進む条件:

- Windows Build & TestでRInt sentinelが完全一致する。失敗時はARGB32 sentinelへfallbackできる。
- 4アライン・mipmapなし・RGBA32/RGB24の入力範囲を受け入れる。
- 2048x2048で許容時間内に完了し、フレーム落ちが許容範囲である。
- 20回以上の差し替えでmanaged/GPU memoryが増え続けない。

ASTC実装へ進む条件:

- Android Build & TestでRIntまたはARGB32 readbackが完全一致する。
- `TextureFormat.ASTC_4x4` destinationが端末上で生成・表示できる。
- BC7 proof-of-conceptで搬送・寿命管理・キュー設計が安定している。
- ASTC codecを独立した長期実装として扱う合意がある。

## 参照資料

- VRChat AsyncGPUReadback: https://creators.vrchat.com/worlds/udon/vrc-graphics/asyncgpureadback/
- VRChat VRCGraphics: https://creators.vrchat.com/worlds/udon/vrc-graphics/
- VRChat Image Loading: https://creators.vrchat.com/worlds/udon/image-loading/
- Microsoft BC7 format: https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc7-format
- Microsoft BC7 mode reference: https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc7-format-mode-reference
- Khronos Data Format Specification, ASTC: https://registry.khronos.org/DataFormat/specs/1.4/dataformat.1.4.html
- Arm ASTC Encoder: https://github.com/ARM-software/astc-encoder

## 次段階の実装状況

BC7 Mode 6 shader、UdonSharp controller、Material、UdonSharpProgramAssetを実装した。known vector、8x8 orientation / decode、RInt / ARGB32実Udon smoke、64x64画質baseline、2048x2048参考時間まで確認済み。詳細は [Runtime BC7 Mode 6 encoder PoC 実装報告](bc7-mode6-poc-implementation.md) を参照。

引き続き未実施なのは、ASTC encoder、Android / Quest、Vulkan / GLES3、Windows VRChat Build & Test、VRCImageDownloader所有権統合、20回連続差し替え、World upload / publishである。
