# Runtime BC7 Mode 6 encoder PoC 実装報告

実装・検証日: 2026-08-11

## 結論

Windows / D3D11向けのBC7 Mode 6限定encoderを実装し、次の経路がUnity Editor上の実Udon VMで成立した。

```text
Texture2D
  -> VRCGraphics.Blit
  -> RInt（失敗時ARGB32）RenderTexture
  -> VRCAsyncGPUReadback
  -> Mode 6 block列の検証
  -> Texture2D(BC7, mip=false, linear=true)
  -> LoadRawTextureData
  -> Apply(false, true)
```

Mode 6の既知block、byte順、block行順、Y方向、BC7 hardware decodeまで検証済み。RIntとARGB32の両方を実Udon VMから完走し、実画像download、Facade所有権、連続Material差し替え、Experimental edge paddingもEditor Play Modeで通過した。一方、Windows VRChat Build & Testと20回以上の連続memory plateauは未検証なので、まだPC向けproof-of-conceptとして扱う。

## 実装アセット

| Asset | 役割 |
|---|---|
| `Shaders/RuntimeImageCompression/Bc7Mode6Encoder.shader` | 4x4 texelをBC7 Mode 6の16 byteへencodeするfragment shader |
| `Materials/RuntimeImageCompression/M_Bc7Mode6Encoder.mat` | encoder shaderを参照する実行用Material |
| `Scripts/RuntimeImageCompression/RuntimeBc7EncoderController.cs` | UdonSharpの状態管理、readback、fallback、BC7 Texture生成 |
| `Scripts/RuntimeImageCompression/RuntimeBc7EncoderController.asset` | 上記C#へ対応するUdonSharpProgramAsset |

## encoderの内容

- 1 blockにつきMode 6だけを使用する。
- endpoint候補はchannel-wise min/maxと、4x4内で最も離れた実画素ペアの両方を評価する。
- endpointごとの共有P-bit 4組を評価する。
- indexは投影tableで初期化し、その前後をBC7整数palette上で再評価する。
- texel 0のfix-up bitを満たすため、必要ならendpointをswapして全indexを `15-index` へ反転する。
- 128bit blockは4個の32bit wordへpackする。
- pass 0はRIntへwordをそのまま出力し、pass 1はARGB32のRGBAへ4 byteを分割する。

RInt/ARGB32出力RTの寸法は次のとおり。非4整列時はsource Textureを増やさず、shaderが右端・上端のtexelを複製する。

```text
blockWidth   = max(1, ceil(source.width / 4))
blockHeight  = max(1, ceil(source.height / 4))
encodedWidth = blockWidth * 4
encodedHeight= blockHeight * 4
RT.width     = encodedWidth
RT.height    = encodedHeight / 4
raw bytes    = encodedWidth * encodedHeight
```

非4整列対応は**Experimental**で既定OFFである。原則として入力のwidth / heightは両方とも4pxの倍数にする。これはpower-of-two制約ではなくBC7の4x4 block境界制約であり、`12x20`のような非2冪画像は補正なしで処理できる。Facadeの自動UV補正を使う場合、同じMaterial/propertyは単一の共有`DRCompressedImageDownloader` instanceで管理する。

## controllerの契約

### 入力条件

- Windows standalone build向け
- `Texture2D`、mipmapなし（`mipmapCount == 1`）
- 推奨: width / heightが両方とも4pxの倍数
- Experimentalを有効にした場合: 1px以上の任意寸法を各軸4px単位、最小4pxへedge padding
- 同時実行は1件だけ

条件外ではBC7 Textureを作らず、`LastEncodeSucceeded=false` と `LastError` を設定する。

### 所有権

- `sourceTexture` はcaller所有。controllerは破棄しない。
- encode開始時に入力参照を固定し、処理中の `SetSourceTexture` は拒否する。
- controllerが破棄するのは、自身が生成したBC7 Textureと一時RenderTextureだけ。
- 新しいencode成功時は、前回controllerが生成したBC7 Textureを破棄する。
- `EncodedTexture` は参照公開用であり、破棄対象は別のprivate所有参照で管理する。
- Downloader統合時はsuccess/failure通知まで `IVRCImageDownload` を保持する。元downloadを先に `Dispose()` してはならない。
- PoCではreadback中のGameObject disable / destroyを行わない。

### 完了通知

- `completionReceiver` と `successEventName` / `failureEventName` を設定できる。
- 成功後は `EncodedTexture`、`LastBackend`、`LastEncodedByteCount`、`LastDurationMilliseconds` を参照できる。
- `LastSourceWidth/Height`、`LastEncodedWidth/Height`、`LastUsedEdgePadding`で物理寸法を診断できる。
- `outputMaterial` とproperty名を設定した場合は成功時にBC7 Textureへ差し替えるが、standalone workerはUV補正を行わない。非4整列画像の自動UV補正には`DRCompressedImageDownloader` Facadeを使う。
- `ClearEncodedTexture()` はcontroller所有Textureだけを解除・破棄する。

## 検証結果

### compile

- Unity通常C# compile: errorなし
- shader: supported、compiler message 0
- UdonSharp explicit compile: 8 programs、errorなし
- `RuntimeBc7EncoderController.asset` から対象classを解決できることを確認

### Mode 6 known vector

入力をBC7 4-bit weightのdecode値へ合わせた4x4 grayscale blockとし、次の16 byteへ完全一致した。

```text
40 C0 1F F0 07 FC 01 7F 11 32 54 76 98 BA DC FE
```

RInt/pass 0とARGB32/pass 1の両方で完全一致した。このvectorはmode prefix、endpoint field順、P0/P1、anchor 3bit、残り15個の4bit index、little-endian word順を同時に検証する。

### 8x8 block順・Y方向・decode

4象限を次のRGBA値へしたlinear / Point入力を使用した。

```text
top:    170 | 255
bottom:   0 |  85
```

- RInt raw 64 byte: expectedと完全一致
- ARGB32 raw 64 byte: expectedと完全一致
- RInt列を `Texture2D(8, 8, BC7)` へLoadRawした結果: 64 pixel完全一致
- `_FlipSourceY=0` が正しい

### 実Udon VM smoke

ClientSim Play Modeで、helperからcallbackを直接呼ばずに次を実行した。

```text
Start
 -> VRCGraphics.Blit
 -> VRCAsyncGPUReadback.Request
 -> OnAsyncGpuReadbackComplete
 -> TryGetData
 -> Texture2D(BC7)
 -> LoadRawTextureData
 -> Apply(false, true)
```

| Backend | Result | Bytes | Texture | 参考elapsed |
|---|---|---:|---|---:|
| RInt | success | 64 | BC7 8x8、mip 1 | 98.62 ms |
| ARGB32 forced | success | 64 | BC7 8x8、mip 1 | 88.15 ms |

elapsedはClientSim上の8x8 1回だけの値で、性能指標には使用しない。

Play Modeでは既存シーンとClientSimのEventSystemが重複する既存エラーが1件出た。encoder callbackはその後に成功し、上表の状態値もUdon heapから回収できた。

### Experimental edge padding

実Udon VMのshader encode、AsyncGPUReadback、BC7 hardware decodeまで通した。

| Source | Backend | Result | Bytes | Gate |
|---:|---|---:|---:|---|
| 3x4 Repeat | RInt | 4x4 | 16 | decode後のx=3がx=2と全row完全一致 |
| 4x3 Repeat | RInt | 4x4 | 16 | decode後のy=3がy=2と全column完全一致 |
| 1x1 | ARGB32 forced | 4x4 | 16 | 16画素が入力RGBAと完全一致 |
| 12x20 | RInt、padding OFF | 12x20 | 240 | 非2冪でも4整列なら補正不要 |

実際の`VRCImageDownloader` URLでは`541x768`を`544x768 / 417,792 byte`へ変換し、Facadeが`ContentUvScale=(541/544, 1)`をMaterialの既存STへ合成した。2回連続差し替え、非圧縮方針への切替、Dispose復元、download中の外部ST変更も通過した。

### 画質baseline

64x64、linear RGBA、RInt経路、Unity BC7 decode後に比較した。

| Pattern | RGB PSNR | Alpha PSNR | max RGB error | max A error |
|---|---:|---:|---:|---:|
| smooth gradient + independent alpha | 38.74 dB | 45.00 dB | 14 | 8 |
| inverse-channel two-color checker | 55.91 dB | exact | 1 | 0 |
| deterministic noise | 16.01 dB | 44.97 dB | 141 | 6 |

最遠実画素ペアを追加する前はcheckerが7.86 dB / max error 165だった。改善後はこの破綻を解消した。ただし、単一subset・2 endpointで表現しにくいnoiseは低品質であり、Mode 6限定encoderを任意画像の最終品質とみなしてはいけない。

### D3D11 Editor参考時間

滑らかな合成入力をRIntへencodeし、同期的にreadback完了まで待った単発値。GPUはNVIDIA GeForce RTX 3060。

| Size | Raw bytes | encode + readback |
|---:|---:|---:|
| 256x256 | 65,536 | 1.02 ms |
| 512x512 | 262,144 | 1.53 ms |
| 1024x1024 | 1,048,576 | 3.88 ms |
| 2048x2048 | 4,194,304 | 14.76 ms |

Editor、1 GPU、合成画像、単発測定のため、VRChat clientのframe time保証には使えない。実際の採用判定はBuild & TestのProfilerと20回連続試験で行う。

2026-08-22以降、controllerはencodeを`maxBlocksPerFrame`（既定16384 block）ごとのstripに分けて複数フレームで描画するため、上表の値は「全stripのGPU時間の合計」に相当する。1フレームに乗るGPU時間はその `16384 / blocks` 倍（2048x2048なら1/16）になる。また、readback後のblock prefix検証は最大64 blockの標本検証に変更した。

実Udon VM上の2048x2048実URL計測（BC7 235.9 ms / 17フレーム、最大frame time 40 ms）は[PoC設計doc](runtime-compressed-image-downloader-poc.md)の「Udon VM上の2048x2048実測」を参照。

## BC1（DXT1）経路の同居（2026-08-22）

同じshader / controller / MaterialでBC1も生成できる。shaderのpass 2（RInt）/ pass 3（ARGB32）がBC1で、controllerの `SetUseBc1(true)` で切り替える。1 block = 8 byte = 2 wordなので、RT幅は `blockWidth * 2`、payloadは `encodedWidth * encodedHeight / 2`、`Texture2D(DXT1)` で生成する。標本検証は `color0 >= color1`（等しい場合はindex全0）。bitstream、endpoint探索、known vector、decode検証、実URL計測はPoC設計docにまとめた。Facadeはalphaなしsource（RGB24 / RGB48 / RGB565）にだけBC1を選ぶ。

## 現時点の未完了項目

- Windows VRChat Build & Test
- RInt sentinelを故意に不一致へしてARGB32へ自動fallbackする統合試験
- 両backend失敗時に元画像を維持する統合試験
- 20回以上の連続差し替えとmanaged / GPU memory測定
- alpha画像を使った実URL品質baseline
- sRGB appearanceの比較
- Experimental edge paddingのWindows Build & Testと20回連続memory plateau
- mipmap生成
- Android / iOS実機のASTC検証
- World upload / publish

## 次段階

1. RInt / ARGB32の2行sentinelを起動時に実行し、全byte一致したbackendだけを選ぶ。
2. Windows Build & Testで8x8 roundtrip、2048x2048、非4整列の両policyを実行する。
3. 20回連続差し替えでcallback重複、managed heap、GPU resourceの増加を確認する。
4. 実画像の品質が不足する場合だけ、endpoint再推定または追加BC7 modeを検討する。
5. Android / iOS実機でASTC transportとhardware decodeを確認する。
