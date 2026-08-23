# VRC Dynamic Image Compression 設計（PoC）

更新日: 2026-08-22

利用者向けの導入・`VRCImageDownloader`置き換え手順は[README](../README.md)を参照する。

## 結論

`VRCImageDownloader`で取得した画像を、実行中のGPUでblock圧縮してからMaterialへ設定する共通Facadeを実装した。

| Build target | 圧縮形式 | GPU API想定 | 現在の状態 |
|---|---|---|---|
| Windows | BC1（alphaなし）/ BC7 Mode 6（alphaあり） | Direct3D 11 | Editor / Udon VM検証済み（2048x2048実URL） |
| Android | ASTC 4x4 | OpenGL ES 3 | Android target / Editor上のUdon VM検証済み、実機未検証 |
| iOS | ASTC 4x4 | Metal | コード分岐実装済み、iOS module / 実機未検証 |
| その他 | 元画像 | - | 非圧縮fallbackまたはerror |

VRChat自身も2026年5月のDeveloper Updateで、動的画像をPCではBC7、Android/iOSではASTCへ圧縮し、使用RAMを元の25%へ下げるクライアント実装を公表している。ただし、その時点ではUdonから読み込む画像は既存worldの互換性を守るため対象外である。

- [VRChat Developer Update - 7 May 2026](https://ask.vrchat.com/t/developer-update-7-may-2026/48395)
- [VRChat Image Loading](https://creators.vrchat.com/worlds/udon/image-loading/)
- [VRChat Build and Test for iOS](https://creators.vrchat.com/platforms/iOS/build-test-mobile/)

## 実装資産

- `Scripts/RuntimeImageCompression/DRCompressedImageDownloader.cs`
  - `VRCImageDownloader`の所有者
  - platform codec選択
  - native download、encode、Material差し替え、callbackの状態管理
- `Scripts/RuntimeImageCompression/DRCompressedImageDownload.cs`
  - `IVRCImageDownload`に近い名前を持つmanaged result handle
  - 圧縮Textureまたはfallback元Textureの所有者
- `Scripts/RuntimeImageCompression/RuntimeBc7EncoderController.cs`
  - Windows BC7 Mode 6 / BC1（DXT1）worker。`SetUseBc1` で切り替える
- `Scripts/RuntimeImageCompression/RuntimeAstc4x4EncoderController.cs`
  - Android/iOS共用ASTC 4x4 worker
- `Shaders/RuntimeImageCompression/Bc7Mode6Encoder.shader`
  - pass 0 / 1がBC7（RInt / ARGB32）、pass 2 / 3がBC1（RInt / ARGB32）
- `Shaders/RuntimeImageCompression/Astc4x4Encoder.shader`
- `Prefabs/RuntimeImageCompression/DRCompressedImageDownloader.prefab`
  - Facade、2 worker、4個の事前確保handleを配線済み
- `Scripts/RuntimeImageCompression/Samples/DRCompressedImageDownloaderExample.cs`
  - 呼び出し側の最小移行例

## なぜ完全なdrop-inではないか

UdonSharpは、ユーザー定義classによるinterface実装をサポートしない。したがって、ライブラリ側で独自の`IVRCImageDownload`を実装し、その`Result`を圧縮Textureへ置き換えることはできない。

また、native callbackは次の形であり、download完了時点ではまだ圧縮が終わっていない。

```csharp
public override void OnImageLoadSuccess(IVRCImageDownload result)
```

このため、Facadeがnative callbackを受け、圧縮完了後に利用者へcustom eventを送る方式を採用した。関数名と4引数の意味は維持するが、次の差分は避けられない。

1. 戻り型は`IVRCImageDownload`ではなく`DRCompressedImageDownload`。
2. callback receiver型は`IUdonEventReceiver`ではなく`UdonSharpBehaviour`。
3. callbackは`OnCompressedImageLoadSuccess` / `OnCompressedImageLoadError`。
4. callback引数の代わりに、receiverの`DRImageDownloadResult`変数へhandleを書き込む。

## 呼び出しの置き換え

Prefab instanceをInspectorから参照する。

```csharp
[SerializeField] private DRCompressedImageDownloader downloader;
[HideInInspector] public DRCompressedImageDownload DRImageDownloadResult;

private DRCompressedImageDownload _request;
private int _requestId;

public void BeginDownload()
{
    TextureInfo info = new TextureInfo();
    info.GenerateMipMaps = false;
    info.FilterMode = FilterMode.Bilinear;
    info.WrapModeU = TextureWrapMode.Clamp;
    info.WrapModeV = TextureWrapMode.Clamp;
    info.WrapModeW = TextureWrapMode.Clamp;
    info.AnisoLevel = 0;
    info.MaterialProperty = "_MainTex";

    _request = downloader.DownloadImage(url, material, this, info);
    if (_request != null)
    {
        _requestId = _request.RequestId;
    }
}

public void OnCompressedImageLoadSuccess()
{
    _request = DRImageDownloadResult;
    _requestId = _request.RequestId;
    Texture2D finalTexture = _request.Result;
}

public void OnCompressedImageLoadError()
{
    Debug.LogError(DRImageDownloadResult.ErrorMessage);
}

public void ReleaseImage()
{
    if (_request != null)
    {
        _request.DisposeIfCurrent(_requestId);
    }
}
```

完全なコードは`DRCompressedImageDownloaderExample.cs`を参照する。

## handle契約

nativeと同名の主な公開field:

```text
Result
State
Progress
Error
ErrorMessage
SizeInMemoryBytes
Material
TextureInfo
UdonBehaviour
Url
```

追加field:

```text
RequestId
IsCompressed
UsedFallback
CompressionFormat
CompressionBackend
CompressionErrorCode
Phase
IsDisposePending
OriginalWidth / OriginalHeight
ResultWidth / ResultHeight
UsedEdgePadding
RequiresContentUvCorrection
ContentUvScale / ContentUvOffset
MaterialUvCorrectionApplied
DisposeIfCurrent(savedRequestId)
```

`DRCompressedImageDownload`はPrefab内のcomponent poolから再利用される。古い参照が別requestへ再利用されたcomponentを指す可能性があるため、呼び出し側は`RequestId`も保存し、公開解放APIの`DisposeIfCurrent(savedRequestId)`を使用する。引数なし`Dispose()`はpool世代を識別できず、新しいrequestを古い参照から破棄し得るため、意図的に公開しない。

## Experimental: BC7 edge paddingと表示領域補正

**原則として、配信画像のwidth / heightは両方とも4pxの倍数にする。** BC7の非4整列対応は実験機能であり、Prefabの`Experimental: BC7 Non-aligned Dimensions > Enable Bc7 Edge Padding`は既定でOFFである。ここでいう「非整列」はpower-of-twoかどうかではなく、4x4 block境界へ揃っているかを意味する。たとえば`12x20`や`1000x768`は2冪ではないが補正不要である。

`SetBc7EdgePaddingEnabled(true)`をrequest開始前に呼ぶと、Windows BC7経路は物理Texture寸法を各軸4px単位へ切り上げ、足りない右端・上端をedge texelで複製する。中間RGBA padding Textureは作らない。設定がOFFのまま非4整列画像を受け取った場合は、`allowUncompressedFallback`の値に関係なく、利用者が選んだ方針として原寸の非圧縮Textureをsuccess結果にする。

例:

```text
original:        541x768
BC7 result:      544x768
ContentUvScale:  (541/544, 1)
ContentUvOffset: (0, 0)
error code when disabled: Bc7EdgePaddingDisabledByPolicy
```

Materialを渡した場合、Facadeは既存のtexture scale `S`とoffset `O`へ次の補正を合成する。

```text
appliedScale  = S * ContentUvScale
appliedOffset = O * ContentUvScale + ContentUvOffset
```

同じMaterial/propertyへの連続差し替えでは、旧handleの補正を新結果の適用直前に解除してから再計算するため倍率は累積しない。Dispose時は現在値がwrapperの適用値と一致する成分だけを元へ戻し、利用者が後から変更した値を上書きしない。

この自動所有権調停は、同じ`DRCompressedImageDownloader` instanceのhandle間で行う。同じMaterial/propertyを複数のFacade instanceから同時に管理するとST補正を識別できず、倍率が累積し得る。Experimental機能を使う場合は、対象Material/propertyごとに単一の共有Facadeを使用する。外部からTextureだけを差し替える場合は、先に旧handleを`DisposeIfCurrent`するか、差し替え後のscale / offsetも明示的に設定する。

自動補正の保証範囲は、対象shaderが`{MaterialProperty}_ST`をsamplingへ使用し、Clampかつ通常の0..1 UVで表示する場合である。STを使わないcustom shader、RawImage、Repeat/Mirrorや複数tilingでは、`OriginalWidth/Height`と`ContentUvScale/Offset`を使って利用側で表示領域を処理するか、実験機能をOFFにして非圧縮結果を使う。

Android/iOSのASTC Textureは論理寸法を原寸のまま保持できるため、このBC7固有の表示領域補正を必要とせず、同設定の対象外である。

## 所有権と差し替え順

```text
native download開始
  -> Materialへはまだ設定しない
  -> native callbackをFacadeが受信
  -> GPU block encode / readback
  -> 圧縮Texture生成
  -> Materialへ最終Textureを設定
  -> native handleをDispose
  -> 利用者へcustom callback
```

- Facadeが`VRCImageDownloader`を所有する。
- request handleが最終Textureを所有する。
- 圧縮成功まではnative `IVRCImageDownload`を保持する。
- 圧縮成功後は独立した圧縮Textureへ所有権を移し、native handleを解放する。
- fallback時はnative Textureが最終結果なので、request handleをDisposeするまでnative handleを保持する。
- request処理中の`DisposeIfCurrent(savedRequestId)`は、GPU処理を強制停止せずdrainする。結果を破棄してからsourceを解放し、slotを再利用可能にする。
- Materialをclearするのは、そのMaterialがまだ同じrequestのResultを参照している場合だけである。Disposeはwrapper適用前のTextureを復元せず、所有中のResultを`null`へ外す契約である。
- download progressの遅延pollは予約済みflagで常に1 chainへ制限し、cancel直後に次requestを開始してもstale eventから増殖しない。
- Facadeの`Dispose()`はterminalであり、その後の`DownloadImage()`は`ServiceDisposed`となる。

## mipmapと色空間

現在のencoderはtop mipだけを生成する。`TextureInfo.GenerateMipMaps == true`の要求は変更せずnative downloaderへ渡し、download成功後に圧縮をbypassして元画像をfallback結果とする。これによりmipmap付きfallbackの互換性を保つ。

shaderはsource Textureをsamplingしたlinear値を得る。sourceが`isDataSRGB`でworkerの`outputSrgb`（既定ON）が有効なら、RGBだけを正確なpiecewise sRGB transferで戻してから8-bitへ量子化し、最終BC7/ASTC Textureを`linear: false`（sRGB）で作る。alphaはlinearのまま。sourceがlinear Textureなら従来どおりlinear値を量子化し`linear: true`で作る。

linear値を8-bitで保存するとsRGB 0〜50の約50段階が約8段階に潰れ、暗部にbandingが出る（2026-08-22のコードレビューで指摘）。既存の64x64 linear fixtureによる画質baselineはlinear sourceで測ったものなので、sRGB経路の品質は実写真で別途確認する。known vector / golden blockの試験はlinear fixtureか`outputSrgb` OFFで行う。

## ASTC固定mode

今回のASTC PoCは、任意RGBA画像を処理できる次の1 modeだけを実装する。

```text
Block Mode:        0x042
Footprint:         4x4
Weight grid:       4x4
Weight quantize:   QUANT_4
Weights:           0, 21, 43, 64
Partition:         1
Plane:             1
Color endpoint:    CEM 12 RGBA-direct
Endpoint quantize: QUANT_256
```

各blockでRGBAのcomponent-wise min/maxと、実画素からtwo-sweepで得た遠点ペアの2候補を評価し、誤差が小さいendpoint lineを採用する。各候補について16画素それぞれの4 weightを全探索する。CEM 12のBlue Contraction条件へ入るendpoint順は事前にswapする。weight誤差はASTC仕様の16-bit補間で評価する。

これは有効なASTC encoderだが、1本のRGBA直線と4段階weightだけなのでproduction品質の汎用ASTC encoderではない。RGBとalphaが独立して変化する画像、checker、透明境界、noiseでは画質が落ちる。

## BC1（DXT1）固定構成

Windowsでalphaなしsource（`RGB24` / `RGB48` / `RGB565`）を受け取ったとき、Facadeの `preferBc1ForOpaqueSources`（既定ON）がBC1を選ぶ。alphaありsourceは常にBC7である。R8 / R16はBC1でも削減できるが、RGB565 endpointが無彩色に色を付けるので対象外のままにしている。

```text
byte 0-1: color0 (RGB565, little-endian)
byte 2-3: color1 (RGB565, little-endian)
byte 4-7: 16 x 2-bit index, texel 0 がbyte 4のbit 0-1（row-major、LSB first）
palette:  c0, c1, (2*c0 + c1 + 1) / 3, (c0 + 2*c1 + 1) / 3  （8-bit展開値で計算）
```

- RGB565量子化は `(v * 31 + 127) / 255`（Gは63）。誤差評価はdecoderと同じbit複製展開値（`r5 << 3 | r5 >> 2`）で行う。
- endpoint候補はchannel-wise min/max、その1/16 inset、最遠実画素ペアの3組。各候補で16 texelの4 palette全探索。
- encoderは常に `color0 > color1`（4色mode）になるよう量子化後にendpointを並べ替えてからindexを決める。`color0 == color1`（flat block）はindex全0。alphaは無視する。
- readback後の標本検証は、BC1では各標本blockの `color0 >= color1` と、等しい場合のindex全0を確認する。
- transportはBC7と共用。1 block = 2 word なのでRTの幅は `blockWidth * 2`、payloadは `width * height / 2` byte。`Texture2D(DXT1)` も両辺4の倍数が必要で、edge padding経路を共用する。

## alpha破棄と縮小（2026-08-23）

- `SetForceBc1(true)`（Inspector: `Force Bc1 Discard Alpha`）: Windowsでsource形式に関係なくBC1 passを使う。BC1 encoderは `SampleSourceTexel` のRGBしか読まないので、`RGBA32` sourceでも中間変換なしにalphaが落ちる。Facadeは受付時に値を取り込む（`_activeForceBc1`）ので、受付後のsetter呼び出しは進行中requestへ影響しない。
- `SetTargetSize(w, h)`（Inspector: `Target Width / Height`）: download寸法が両辺ともtargetのちょうど2倍または4倍なら、workerへ `SetDownscaleDivisor(d)` を渡す。shaderは `_SourceSize` を縮小後寸法として受け取り、`_SourceDownscale = d` のとき各encoded texelをd x dの元texelのbox平均（linear値で平均してからsRGB変換）で作る。中間RenderTextureは作らない。BC7 / BC1 / ASTCの3経路で共通。
- handleは `OriginalWidth/Height`（encoder入力 = 縮小後）と `DownloadedWidth/Height`、`DownscaleDivisor` を分けて持つ。UV補正の判定は `Original` と `Result` で行うので、縮小はpadding扱いにならない。
- 検証（D3D11 Editor、BC1 pass、linear fixture 16x16: 左半分が赤、右半分が1px白黒checker）: divisor 2で8x8 = 赤block / 灰128 block（`00 F8 00 F8 00..` / `10 84 10 84 00..`）が行ごとに繰り返し、RInt / ARGB32で同一。divisor 4で4x4 = 左2列赤・右2列灰128の1 block（`00 F8 10 84 50 50 50 50`）。実URL（1448x2048 RGB24 PNG）を `SetTargetSize(724, 1024)` で724x1024 BC1（370,688 byte）にし、`DownscaleDivisor = 2`、UV補正なしを確認。

## block transport

BC7とASTC 4x4は1 block 16 byte、BC1は8 byteである。

```text
blockWidth  = ceil(width / 4)
blockHeight = ceil(height / 4)
rawBytes    = blockWidth * blockHeight * 16   (BC7 / ASTC)
            = blockWidth * blockHeight * 8    (BC1)

transportRT.width  = blockWidth * 4           (BC7 / ASTC)
                   = blockWidth * 2           (BC1)
transportRT.height = blockHeight
```

各32-bit wordを次のいずれかで搬送する。

- `RInt`: packed `uint`を`asint`し、1 pixelへ出力。
- `ARGB32 Linear`: 4 byteをRGBA channelへ`byte / 255.0`で出力。

両RTはdepth 0、MSAA 1、mipmapなし、Blend Off、`RenderTextureReadWrite.Linear`とする。

ASTC workerは最初のencode前に4x4 RTへ64-byte sentinelを書き、raw readbackの全byteを照合する。通常行順または4行反転だけを許容する。反転時はencode passへ`_FlipOutputY`を渡し、shaderが物理出力行`y`へblock行`blockHeight - 1 - y`を書くことで、readbackを常に正順で受け取る。CPU側でのbyte並べ替えは行わない。channel swizzle、word byte swap、sRGB変換、値破損は許容せず、反対backendをsentinelから最大1回だけ試す。成功backendと行順はworker instance内にcacheし、実encode側で異常が出た場合はcacheを破棄する。

診断fieldは`TransportProbeCompleted`、`TransportRowsReversed`、`LastTransportProbeError`である。`_FlipSourceY`はsource sampling方向、`_FlipOutputY`はGPU readback方向であり、別々に扱う。

### Udon VM上の処理量

Udon VMは単純なループ1回あたり約1.6 µs（VRCLibraryの公開ベンチマーク: 5万回 ≈ 80 ms）かかるため、payloadをblock単位やbyte単位で走査してはならない。2048x2048は262,144 block / 4,194,304 byteあり、全数走査は秒単位のフレーム停止になる。

encode後の検証は、先頭と末尾を含む等間隔の最大64 blockだけを見る標本検証である。transport異常（swizzle、byte順、sRGB変換）は全blockへ一様に現れるため、標本で十分に検出できる。readback callback内でUdonが行う残りの処理は、`TryGetData`（native memcpy）、`LoadRawTextureData`、`Apply`（GPU upload）であり、いずれも反復を伴わない。

### GPU時間の分散

encode passは`_StripRange`で指定したblock行の範囲だけを描き、範囲外のfragmentは計算前に`discard`で抜ける。workerは`maxBlocksPerFrame`（既定16384 block）から1フレームあたりの行数を決め、`SendCustomEventDelayedFrames`で1フレームに1 stripずつ同じRTへ描画し、最後のstripの直後に1回だけ`VRCAsyncGPUReadback.Request`を出す。RTは描画間で内容を保持するので、Udon側でbyte配列を連結する必要はない。

| Size | blocks | 既定16384 block/frameでのフレーム数 |
|---:|---:|---:|
| 512x512 | 16,384 | 1 |
| 1024x1024 | 65,536 | 4 |
| 2048x2048 | 262,144 | 16 |

RTX 3060の参考値（2048x2048で14.76 ms）から1 stripは約0.9 msになる。下位GPUやQuestで1フレームに収まらない場合は`maxBlocksPerFrame`を下げる。`LastDurationMilliseconds`はstrip分散を含む所要時間になる。

### Udon VM上の2048x2048実測（2026-08-22、Windows Editor / ClientSim / D3D11 / RTX 3060）

`VRCImageDownloader`で2048x2048 JPEG（`RGB24`、sRGB、666 KB）を実downloadし、Facade経由でencodeした。frame timeはEditor側から毎フレーム`Time.unscaledDeltaTime`を記録したもので、同条件のidle時は約11 msに20〜30 msのEditor由来spikeが混じる。

| 形式 | bytes | Encoding中のフレーム数 | Encoding中の最大frame time | `LastDurationMilliseconds` | decode後PSNR（全体 / 最悪strip） |
|---|---:|---:|---:|---:|---:|
| BC7（`preferBc1ForOpaqueSources` OFF相当） | 4,194,304 | 17（16 strip + readback） | 40 ms（初回strip。RT生成を含む） | 235.9 ms | 40.53 dB / 37.89 dB |
| BC1（既定） | 2,097,152 | 16 + readback | 22.8 ms | 204.8 ms | 33.09 dB / 29.95 dB |

- 16 stripを別フレームで同じRTへ描いた結果を128行ごとに元JPEGと比較し、全stripが正常にdecodeされた（RT内容がフレームを跨いで保持される）。Questのtile-based GPUでは未確認。
- 2048x2048のUdon側処理（`TryGetData`、`LoadRawTextureData`、`Apply`）を含むreadbackフレームでもframe time spikeは出なかった。
- 最初の試行で観測した240 msのframeはdownload完了時のnative側JPEG decodeで、圧縮処理とは無関係（2回目以降は出ない）。
- ClientSimの`VRCImageDownloader`はHTTP redirectを追わない（`Redirect limit exceeded`）。試験URLは302を返さない最終URLを使う。

## 今回通過した検証

環境:

```text
Unity 2022.3.22f1
VRChat SDK Worlds 3.10.4
Editor host: Windows / D3D11 / RTX 3060
Android active target: OpenGLES3 / ARM64 / ASTC subtarget
```

- ASTC fixed-mode golden blockをRInt / ARGB32の両方で16 byte完全一致。
- golden vector:

```text
42 80 01 FE 01 FE 01 FE 01 FE 01 00 27 27 27 27
```

- 8x8非対称4象限をencodeし、ASTC `LoadRawTextureData`、decode後の64画素を完全一致。
- 64x64 linear fixtureの品質baseline:

```text
smooth RGBA gradient: RGB 38.64 dB / max error 9, alpha 40.28 dB / max error 5
red-cyan checker:     RGB exact, alpha exact
seeded RGBA noise:    RGB 11.95 dB, alpha 12.09 dB
```

- BC1（2026-08-22、D3D11 Editor、linear fixture、`_OutputSrgb = 0`）:
  - known vector（RInt pass 2 / ARGB32 pass 3とも完全一致）:

```text
flat (200,100,50):        26 C3 26 C3 00 00 00 00
checker black/white:      FF FF 00 00 44 11 44 11
ramp 0/85/170/255 (x):    FF FF 00 00 2D 2D 2D 2D
flat (0,0,0):             00 00 00 00 00 00 00 00
flat (255,255,255, a=0):  FF FF FF FF 00 00 00 00   (alphaは無視)
```

  - 8x8 4象限（赤 / 緑 / 青 / 灰128）: block順 `(0,0) (1,0) (0,1) (1,1)`、`Texture2D(DXT1)` のhardware decode後64画素がRGB565展開値と完全一致（灰128は `(132,130,132)`）。`_FlipSourceY = 0`。
  - 64x64 quality: smooth gradient 38.39 dB / max error 10、red-cyan checker exact、seeded noise 12.71 dB。
  - BC7回帰: 同じsampling helperを通したgrey RGBA 4象限（0 / 85 / 170 / 255）がpass 0 / 1とも64画素完全一致。

- `_FlipSourceY = 0`でEditor上の向き一致。
- 4x4 / 64-byte transport sentinelはD3D11 Editor上でRInt / ARGB32とも全byte一致、row反転なし。
- 実Udon VMでも初回self-probeから8x8 encode、ASTC decodeまでRInt / ARGB32とも64画素完全一致。ただしWindows / D3D11はASTCをGPUで扱えないため、このdecodeはUnityがupload時にCPUでRGBAへtranscodeしたものであり、Quest GPUのhardware decodeは未検証である。
- 実Udon VMで8x8をARGB32 / RIntの両backendから64 byte ASTCへ変換。
- 実Udon VMでNPOT 3x5を32 byte、1x1を16 byteへ変換。
- Android active targetでC# compile成功。
- UdonSharp 12 ProgramAssets compile成功、`AnyUdonSharpScriptHasError == false`。
- Android target import後、ASTC shader message 0。
- D3D11 / RTX 3060上のASTC encode + synchronous readback参考値:

```text
256x256:    0.95 ms /    65,536 bytes
512x512:    0.79 ms /   262,144 bytes
1024x1024:  2.79 ms / 1,048,576 bytes
2048x2048:  7.33 ms / 4,194,304 bytes
```

これはdesktop hostの参考値であり、Android/iOSの性能保証には使わない。

- Facade Prefabはservice 1、encoder 2、handle 4、backing UdonBehaviour 7を配線済み。
- Windows BC7の既知vector、decode、Udon VM、2048x2048 smokeは別文書の結果を維持。
- Windows実Udon VMで`3x4 -> 4x4`の右端と`4x3 -> 4x4`の上端が、sourceをRepeatにしてもedge複製されることをdecode後の完全一致で確認。
- `1x1 -> 4x4 / 16 byte`をforced ARGB32、`12x20 -> 12x20 / 240 byte`をpadding OFFのRIntで確認。
- 指定Discord画像`541x768 / RGB24 / 1,246,464 byte`を実downloadし、実験機能ONでは`544x768 / BC7 / 417,792 byte`、OFFでは原寸非圧縮successとなることを確認。
- 同じMaterialへBC7結果を2回連続適用しても`541/544`が二重適用されず、次の非圧縮結果で元のscale / offsetへ戻ることを確認。
- download待機中に利用側が変更したMaterial scale / offsetを、結果適用時の新しいbaselineとして保持することを確認。

## 現在の環境blocker

Android:

- UnityのAndroid Player moduleは認識される。
- SDK / NDK / OpenJDKの設定先が存在しない。
- `adb`が存在しない。
- したがってQuest / Android clientのBuild & Testは未実施。

iOS:

- `BuildPipeline.IsBuildTargetSupported(iOS) == false`。
- iOS Build Support moduleが未導入。
- Metal shader compileとVTP Build & Testは未実施。
- SDK側のiOS platform表示にはログイン済みaccountの`SupportsiOS`判定も必要。

これらのmodule導入やSDK更新は、このPoCでは実施していない。

## Android / iOS実機Go gate

2026-08-23時点の状況（ポスター掲示ギミックのworld内パネルで確認。詳細は `poster-display-gimmick.md`）: iOS（Metal）で gate 1 通過（RInt、行順正順）、gate 2 は4象限試験ではなく実画像の表示で確認、gate 5 は1448x2048 / 724x1024の所要時間のみ（encode 515 ms / 34 ms、ロード中の最大frame time 255 ms）。Quest（GLES3）も gate 1 通過（ARGB32、行順正順）、実画像の表示確認、1448x2048で encode 715 ms / 最悪frame time +9.5 ms（適応strip、mobile上限4,096 block）。3・4・6・7・8は未実施。
注意: 画像URLはVRChatの許可ドメイン（`*.github.io`、`i.imgur.com` など13件）に置くこと。Discord CDNは許可外で、mobile clientでは「Allow Untrusted URLs」を有効にしないとcallbackが来ないままdownloadが始まらない。

1. 組み込み64-byte self-probeがRIntまたはARGB32を選択し、`TransportProbeCompleted == true`になることを端末ログで確認。
2. ASTC known blockをhardware decodeし、4象限のRGBAとX/Y方向を完全一致。
3. source samplingのY方向とreadback block-row方向を別々に確定。
4. NPOT `1x1`, `3x5`, `1000x750`, `1023x1023`。
5. `256`, `512`, `1024`, `2048`でencode時間、callback latency、最大frame hitch、peak memoryを測定。
6. 20回以上のdownload / encode / swap / Disposeでmemory plateauを確認。
7. readback error、backend layout不一致、request中Dispose、timeoutで元画像維持。
8. 実際の`VRCImageDownloader` URLを使い、圧縮Texture成立後だけnative handleが解放されることを確認。

iOSではさらに、VTP port 9002、同一LAN、Local Network許可、Windows Firewall、VPN無効を確認する。

## library化までの残作業

優先度順:

1. Android / iOS実機で組み込み64-byte transport probeとhardware decode gateを通過。
2. source Y方向を端末別に確定し、graphics context再生成時のtransport再probeを追加。
3. Facade内部にbounded FIFOを追加。現在はnative / encodeを1件ずつ直列化し、処理中の追加requestを拒否する。
4. backend routerへoperation generationを追加し、stale resultを明示的にreject。
5. download timeoutとbackground復帰試験（encode側timeoutは`compressionTimeoutSeconds`で実装済み）。timeout後に遅れて届いたreadback callbackは`IsBusy`のidle guardで捨てるが、同寸法の次requestがbusyの間に届いた場合は区別できない。
6. mip chain全level encode、またはmipmap fallbackを正式仕様化。
7. ASTC / BC7品質mode追加とfixture別baseline。
8. VPM package化、SemVer、CHANGELOG、LICENSE、`README.md`のpackage root移設、`Samples~`、Tests。
9. VRChat公式SDKへUdon用動的圧縮APIが追加された場合、そのAPIを優先するadapterまたは移行経路を用意。
10. BC1の品質改善（endpoint最小二乗refine、Android側の対応物としてASTC 6x6）。現状のBC1は実写真で約33 dBであり、写真用途では`preferBc1ForOpaqueSources`をOFFにしてBC7を選べる。

想定package構成:

```text
com.digitalregion.vrc-image-compression/
├─ package.json
├─ Runtime/
│  ├─ Scripts/Public/
│  ├─ Scripts/Internal/
│  ├─ Shaders/
│  ├─ Materials/
│  └─ Prefabs/
├─ Samples~/
├─ Tests/
├─ Documentation~/
├─ README.md
├─ CHANGELOG.md
└─ LICENSE.md
```

現段階は、bitstream生成、GPU transport、Udon worker、所有権を含むFacadeの設計を検証するPoCである。Android/iOS実機gateとquality baselineを通すまではproduction release扱いにしない。
