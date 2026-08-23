# VRC Dynamic Image Compression

Runtime GPU block compression (BC1 / BC7 / ASTC 4x4) for images loaded with `VRCImageDownloader` in VRChat worlds (UdonSharp). Downloads stay uncompressed only while encoding; the Material receives the compressed texture, cutting VRAM to 1/8 (BC1) or 1/4 (BC7 / ASTC) of the original. Verified on Windows (D3D11), Quest (GLES3) and iOS (Metal). Includes a poster display demo scene. License: MIT.

更新日: 2026-08-24
状態: **Proof of Concept（production release前）**
ライセンス: MIT（[LICENSE](LICENSE)）

`VRCImageDownloader`で取得した動的画像をGPUでblock圧縮し、完成したTextureだけをMaterialへ設定するUdonSharp向けWrapperである。

| Active Build Target | 圧縮形式 | 想定GPU API | 現在の検証状態 |
|---|---|---|---|
| Windows | BC1（alphaなしsource）/ BC7 Mode 6（alphaありsource） | Direct3D 11 | Windows Editor / D3D11 / 実Udon VMで検証済み（2048x2048実URLまで） |
| Android | ASTC 4x4 | OpenGL ES 3 | Windows Editor / D3D11 host上でAndroid targetを検証。Quest / GLES3実機は未検証 |
| iOS | ASTC 4x4 | Metal | 分岐実装済み。iOS module / Metal実機は未検証 |
| その他 | 元画像へfallback、またはerror | - | production対象外 |

codec routingは実行PCのOS判定ではなく、UnityのActive Build Targetに対応するcompile symbolで決まる。Editor検証前にもWindows / Android / iOSのtargetを意図どおり選ぶ。

圧縮後のpayloadは元Textureの形式で決まる。VRChatはalphaなし画像をRGB24、グレースケールをR8として読み込む（[Image Loading](https://creators.vrchat.com/worlds/udon/image-loading/)）。WindowsではalphaなしsourceをBC1（DXT1、4 bit/pixel）、alphaありsourceをBC7（8 bit/pixel）へ圧縮する。ASTC 4x4は8 bit/pixelである。

| 元Texture形式 | Windows | Android / iOS |
|---|---:|---:|
| RGBA32（alphaあり） | BC7、25% | ASTC 4x4、25% |
| RGB24（JPEGなどalphaなし） | BC1、16.7% | ASTC 4x4、33% |
| R8（グレースケール） | 削減なし。`SourceFormatHasNoGain` として元画像のまま返す | 同左 |

BC1はRGB565 endpoint 2個と2-bit indexの4色paletteなので、BC7より画質は落ちる（実写真2048x2048でBC7 40.5 dB、BC1 33.1 dB）。alphaありsourceへBC1は選ばない。BC1を使わずalphaなしsourceもBC7にしたい場合はPrefab rootの `Prefer Bc1 For Opaque Sources` をOFFにする。

encode中は元画像と圧縮結果が一時的に共存するため、瞬間的なpeak memoryがこの比率になるわけではない。

> [!WARNING]
> 現在はPoCである。Windows VRChat Build & Test、Android/iOS実機、長時間のmemory plateau、汎用画質は最終確認前である。公開Worldへ導入する前に、対象端末と実画像で確認すること。

## 必要環境

- Unity 2022.3.22f1
- VRChat SDK Worlds 3.10.4
- UdonSharp
- mipmapを必要としない動的画像

VRChat側のURL制限、download size上限、rate limitなどは通常の`VRCImageDownloader`と同じである。Image Loading自体の条件は[VRChat公式ドキュメント](https://creators.vrchat.com/worlds/udon/image-loading/)を参照する。

## 導入方法

### 導入手順

このリポジトリのフォルダをそのままUnityプロジェクトの `Assets/` 配下へ置く（例: `Assets/vrc-dynamic-image-compression/`）。`.meta` を含めて配置し、Unityのcompile完了後にConsoleを絞り込まずに確認する。`Scripts/` 内の `.asset` はUdonSharp ProgramAssetであり必須である。

デモは `Scenes/PosterGimmickDemo.unity`（ポスター掲示ギミック。[docs/poster-display-gimmick.md](docs/poster-display-gimmick.md)）を参照する。デモSceneの `Poster Url` にはサンプル画像 `https://sechiro.github.io/vrc-posters/posters/hatago_1448x2048.png`（1448 x 2048、RGB PNG、GitHub Pages = VRChatの許可ドメイン）を設定してある。自分の画像に差し替える場合は許可ドメイン上のPNG / JPEGを指定する。blueprint IDは空なので、新規worldとしてBuild & Publishする。

1. 次のPrefabをSceneへ1つ配置する。

   ```text
   Prefabs/RuntimeImageCompression/DRCompressedImageDownloader.prefab
   ```

   ```text
   DRCompressedImageDownloader        <- 利用者が参照・設定するroot
   ├─ BC7 Encoder                     <- 内部worker
   ├─ ASTC Encoder                    <- 内部worker
   ├─ Request Handle 0                <- 内部pool
   ├─ Request Handle 1
   ├─ Request Handle 2
   └─ Request Handle 3
   ```

2. downloadを呼ぶ`UdonSharpBehaviour`へ、`DRCompressedImageDownloader`型のSerializeFieldを追加する。
3. Scene上のPrefab instanceをそのfieldへ割り当てる。
4. URL、表示先Material、Material property名を呼び出し側へ設定する。
5. 下記のcallback用変数とcustom eventを実装する。

同じMaterial/propertyは、Scene内の単一の共有Prefabから操作することを推奨する。特にExperimentalなBC7表示領域補正は、複数Prefab間の所有権調停を行わない。

利用者が操作するのはrootの`DRCompressedImageDownloader` componentだけである。子worker、encoder Material、UdonSharp ProgramAsset、4個のhandle配列は配線済みなので直接変更しない。

### 一部だけ使う

現時点ではVPM package化前のため、ライブラリ本体だけを使う場合は次の各フォルダ自身の`.meta`と、配下すべてのファイル・`.meta`をまとめてコピーする。

```text
Materials/RuntimeImageCompression/
Prefabs/RuntimeImageCompression/
Scripts/RuntimeImageCompression/
Shaders/RuntimeImageCompression/
```

`Scripts/RuntimeImageCompression/`内の`.asset`はUdonSharp ProgramAssetであり必須である。`.cs`だけをコピーしないこと。`Samples/`は任意だが、初回導入では動作確認用に含めることを推奨する。コピー後はUnityのcompile完了を待ち、Consoleを絞り込まずに確認する。

## 最小利用例

Prefabのcallback設定が既定値の場合、次のコードで利用できる。

```csharp
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Image;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CompressedImageExample : UdonSharpBehaviour
{
    [SerializeField] private DRCompressedImageDownloader downloader;
    [SerializeField] private VRCUrl imageUrl;
    [SerializeField] private Material destinationMaterial;
    [SerializeField] private string materialProperty = "_MainTex";

    // 名前はPrefabのCallback Result Variableと完全に一致させる。
    [HideInInspector] public DRCompressedImageDownload DRImageDownloadResult;

    private DRCompressedImageDownload _currentRequest;
    private int _currentRequestId;

    public void BeginDownload()
    {
        if (downloader == null || imageUrl == null)
        {
            Debug.LogError("[CompressedImageExample] Setup is incomplete.");
            return;
        }

        // この最小例が同時に所有するhandleは1つだけ。
        if (_currentRequest != null && _currentRequest.IsAllocated)
        {
            Debug.LogWarning("[CompressedImageExample] Release the current request first.");
            return;
        }

        TextureInfo info = new TextureInfo();
        info.GenerateMipMaps = false;
        info.FilterMode = FilterMode.Bilinear;
        info.WrapModeU = TextureWrapMode.Clamp;
        info.WrapModeV = TextureWrapMode.Clamp;
        info.WrapModeW = TextureWrapMode.Clamp;
        info.AnisoLevel = 0;
        info.MaterialProperty = materialProperty;

        DRCompressedImageDownload request = downloader.DownloadImage(
            imageUrl,
            destinationMaterial,
            this,
            info);

        if (request == null)
        {
            // 同期的に拒否されたrequestにはcallbackが来ない。
            Debug.LogError("[CompressedImageExample] Request rejected: "
                + downloader.LastServiceError);
            return;
        }

        // 即時failure callback内で解放済みの場合がある。
        if (!request.IsAllocated)
        {
            return;
        }

        _currentRequest = request;
        _currentRequestId = request.RequestId;
    }

    // overrideではなく、Wrapperから呼ばれるpublic custom event。
    public void OnCompressedImageLoadSuccess()
    {
        if (DRImageDownloadResult == null)
        {
            Debug.LogError("[CompressedImageExample] Success result missing.");
            return;
        }

        _currentRequest = DRImageDownloadResult;
        _currentRequestId = _currentRequest.RequestId;

        Debug.Log("[CompressedImageExample] format="
            + _currentRequest.CompressionFormat
            + " compressed=" + _currentRequest.IsCompressed
            + " fallback=" + _currentRequest.UsedFallback
            + " bytes=" + _currentRequest.SizeInMemoryBytes);
    }

    public void OnCompressedImageLoadError()
    {
        if (DRImageDownloadResult == null)
        {
            Debug.LogError("[CompressedImageExample] Failure result missing.");
            return;
        }

        DRCompressedImageDownload failed = DRImageDownloadResult;
        int failedId = failed.RequestId;

        Debug.LogError("[CompressedImageExample] "
            + failed.ErrorMessage
            + " compression=" + failed.CompressionErrorCode);

        failed.DisposeIfCurrent(failedId);
        if (_currentRequest == failed)
        {
            _currentRequest = null;
            _currentRequestId = 0;
        }
        DRImageDownloadResult = null;
    }

    public void ReleaseImage()
    {
        if (_currentRequest != null)
        {
            _currentRequest.DisposeIfCurrent(_currentRequestId);
        }

        _currentRequest = null;
        _currentRequestId = 0;
        DRImageDownloadResult = null;
    }

    private void OnDestroy()
    {
        ReleaseImage();
    }
}
```

同梱の実装例は`Scripts/RuntimeImageCompression/Samples/DRCompressedImageDownloaderExample.cs`にある。

## 既存のVRCImageDownloaderから置き換える

### 置き換えの要点

`DownloadImage`という関数名と4引数の意味は維持している。ただしUdonSharpのinterface制約と、native download完了後に圧縮工程が続くため、完全なdrop-in replacementではない。

| 既存の`VRCImageDownloader` | `DRCompressedImageDownloader` |
|---|---|
| 呼び出し側で`new VRCImageDownloader()` | Scene上の共有PrefabをSerializeFieldで参照 |
| 戻り型`IVRCImageDownload` | 戻り型`DRCompressedImageDownload` |
| receiver型`IUdonEventReceiver` | receiver型`UdonSharpBehaviour` |
| `override OnImageLoadSuccess(result)` | `public OnCompressedImageLoadSuccess()` |
| `override OnImageLoadError(result)` | `public OnCompressedImageLoadError()` |
| callback引数の`result`を読む | public変数`DRImageDownloadResult`を読む |
| `request.Dispose()` | `request.DisposeIfCurrent(savedRequestId)` |
| consumerがdownloaderを`Dispose()` | 共有FacadeはconsumerからDisposeしない |
| native download完了時にcallback | 圧縮またはfallback確定後にcallback |

### 置き換え前

一般的なnative実装は、呼び出し側がdownloaderとrequestを所有する。

```csharp
private VRCImageDownloader _downloader;
private IVRCImageDownload _request;

private void Start()
{
    _downloader = new VRCImageDownloader();
}

public void BeginDownload()
{
    TextureInfo info = new TextureInfo();
    info.GenerateMipMaps = false;
    info.MaterialProperty = "_MainTex";

    _request = _downloader.DownloadImage(
        imageUrl,
        destinationMaterial,
        this,
        info);
}

public override void OnImageLoadSuccess(IVRCImageDownload result)
{
    Debug.Log("Complete: " + result.Result);
}

public override void OnImageLoadError(IVRCImageDownload result)
{
    Debug.LogError(result.ErrorMessage);
}

private void OnDestroy()
{
    if (_request != null) _request.Dispose();
    if (_downloader != null) _downloader.Dispose();
}
```

### 置き換え後

変更点は次の6つである。

1. `VRCImageDownloader`の生成を削除し、Prefab参照へ置き換える。
2. request型を`DRCompressedImageDownload`へ変更する。
3. `DRImageDownloadResult`というpublic fieldをreceiverへ追加する。
4. nativeのoverride callbackを、引数なしのpublic custom eventへ変更する。
5. callback内では引数ではなく`DRImageDownloadResult`を読む。
6. handleと`RequestId`を一緒に保存し、`DisposeIfCurrent`で解放する。

```csharp
[SerializeField] private DRCompressedImageDownloader downloader;
[HideInInspector] public DRCompressedImageDownload DRImageDownloadResult;

private DRCompressedImageDownload _request;
private int _requestId;

public void BeginDownload()
{
    if (_request != null && _request.IsAllocated)
    {
        Debug.LogWarning("Release the current request first.");
        return;
    }

    TextureInfo info = new TextureInfo();
    info.GenerateMipMaps = false;
    info.MaterialProperty = "_MainTex";

    DRCompressedImageDownload started = downloader.DownloadImage(
        imageUrl,
        destinationMaterial,
        this,
        info);

    if (started == null)
    {
        Debug.LogError(downloader.LastServiceError);
        return;
    }

    // 即時failure callback内で解放済みの場合がある。
    if (!started.IsAllocated) return;

    _request = started;
    _requestId = started.RequestId;
}

public void OnCompressedImageLoadSuccess()
{
    DRCompressedImageDownload completed = DRImageDownloadResult;
    if (completed == null) return;

    _request = completed;
    _requestId = completed.RequestId;
    Debug.Log("Complete: " + completed.Result);
}

public void OnCompressedImageLoadError()
{
    DRCompressedImageDownload failed = DRImageDownloadResult;
    if (failed == null) return;

    int failedId = failed.RequestId;
    Debug.LogError(failed.ErrorMessage);
    failed.DisposeIfCurrent(failedId);

    if (_request == failed)
    {
        _request = null;
        _requestId = 0;
    }
    DRImageDownloadResult = null;
}

public void ReleaseImage()
{
    if (_request != null)
    {
        _request.DisposeIfCurrent(_requestId);
    }

    _request = null;
    _requestId = 0;
    DRImageDownloadResult = null;
}
```

`TextureInfo`を省略したい場合でも、Wrapperの現在のsignatureでは第4引数に`null`を渡す。`MaterialProperty`が空なら`_MainTex`を使う。Materialへ自動設定せず`Result`だけ受け取りたい場合は、第2引数へ`null`を渡せる。

### なぜcallbackをそのまま使えないか

nativeの`OnImageLoadSuccess(IVRCImageDownload result)`が呼ばれた時点では、まだGPU圧縮が終わっていない。Wrapper自身がnative callbackを受け、次の順序で処理する。

```text
native download
  -> GPU encode
  -> AsyncGPUReadback
  -> 圧縮Texture生成
  -> Materialへ最終結果を設定
  -> receiverのDRImageDownloadResultへhandleを書き込み
  -> OnCompressedImageLoadSuccess / Error
```

また、UdonSharpBehaviourは独自classで`IVRCImageDownload` interfaceを実装できないため、戻り値を独自handleへ置き換えている。

## callbackと結果の判定

Prefabの既定callback contractは次のとおりである。

| Inspector項目 | 既定値 | receiver側 |
|---|---|---|
| Callback Result Variable | `DRImageDownloadResult` | 同名のpublic field |
| Success Event Name | `OnCompressedImageLoadSuccess` | 引数なしpublic method |
| Failure Event Name | `OnCompressedImageLoadError` | 引数なしpublic method |

Wrapperは結果変数を書き込んだ直後にeventを送る。名前をInspectorで変更した場合はreceiver側も同じ名前へ変更する。

受付後にnative downloaderの開始自体が失敗した場合、failure eventが`DownloadImage()`のreturnより先に同期実行される可能性がある。callback内では呼び出し側fieldへの代入完了を前提にせず、必ず`DRImageDownloadResult`を読む。開始側は戻りhandleの`IsAllocated`も確認する。

success callbackは「圧縮成功」だけでなく「元画像へfallbackして表示成功」も表す。必ず次を確認する。

```csharp
bool compressed = DRImageDownloadResult.IsCompressed;
bool fallback = DRImageDownloadResult.UsedFallback;
string format = DRImageDownloadResult.CompressionFormat;       // BC1 / BC7 / ASTC_4x4 / Original
string reason = DRImageDownloadResult.CompressionErrorCode;    // fallback理由など
```

主な公開field:

| Field | 意味 |
|---|---|
| `Result` | 最終Texture。Materialを渡した場合はcallback前に設定済み |
| `State` | `Pending` / `Complete` / `Error` / `Unloaded` |
| `Progress` | native download進捗。Encoding中も1になるため完了判定には使わない |
| `Error`, `ErrorMessage` | native downloadまたは最終error |
| `SizeInMemoryBytes` | 最終Textureのpayload目安 |
| `IsCompressed` | BC1 / BC7 / ASTCのいずれかになったか |
| `UsedFallback` | 元Textureを結果として使ったか |
| `CompressionFormat` | `BC1` / `BC7` / `ASTC_4x4` / `Original` |
| `CompressionBackend` | readback backendまたは`UncompressedFallback` |
| `CompressionErrorCode` | fallback / compression failureの理由 |
| `Phase` | `Downloading` / `Encoding` / `Complete` / `Error`など |
| `RequestId` | pooled handleの世代識別子 |
| `OriginalWidth/Height` | encoderが入力として扱った論理寸法（縮小後） |
| `DownloadedWidth/Height` | downloadした画像の寸法。`DownscaleDivisor > 1` のときだけ`Original`と異なる |
| `DownscaleDivisor` | 1 / 2 / 4。shader内box平均による縮小率 |
| `CompressionDurationMilliseconds` | encode開始からreadback完了までの時間（strip分散を含む） |
| `ResultWidth/Height` | 圧縮Textureの物理寸法 |

`DownloadImage()`が`null`を返した場合は同期的な受付拒否であり、callbackは発生しない。`downloader.LastServiceError`を直ちに確認する。

主な受付拒否codeは`ServiceDisposed`、`RequestAlreadyInFlight`、`CompressionBackendBusy`、`UrlMissing`、`RequestHandlePoolExhausted`、`MaterialPropertyMissing`である。

## handleの所有権と解放

Prefabは4個のrequest handleを事前確保しているが、download / encodeのactive処理は同時に1件だけである。

- `DownloadImage()`が返したhandleは、successでもerrorでも解放するまでpool slotを占有する。
- handle componentはpoolで再利用されるため、参照だけでは世代を判定できない。
- 受付時の`RequestId`を保存し、`DisposeIfCurrent(savedRequestId)`を使う。
- `DisposeIfCurrent`後は`Result`を参照しない。
- `Result`を呼び出し側から直接`Destroy`しない。Textureを使う全期間、handleを保持する。
- Materialがまだそのhandleの`Result`を表示している場合、Dispose時にTextureを`null`へ外す。Wrapper適用前のTextureは復元しない。
- 個別画像を解放するためにFacadeの`Dispose()`を呼ばない。Facadeの`Dispose()`はterminalで、その後の全requestを拒否する。
- error callbackで返されたhandleも必ず解放する。
- encode中の`DisposeIfCurrent`はGPU処理を強制停止せず、drain完了後に解放する。その間は`IsDisposePending`が立つ。
- request処理中の`DisposeIfCurrent`はcancelとして扱い、success / failure callbackは送らない。callback待ちのflagやUIは呼び出し側で同時に解除する。

Udon間の内部連携上、`Prepare`、`CompleteCompressed`、`CompleteFallback`、`DisposeNativeDownload`などもpublicになっているが、利用者向けAPIではない。通常利用で直接呼ぶのはFacadeの`DownloadImage`、BC7 paddingのSet/Get、`SetForceBc1` / `SetTargetSize`（request前に呼ぶ。受付後の変更はそのrequestへ影響しない）、handleの`DisposeIfCurrent`だけである。`GetBackendDiagnostics()` は選択中workerの状態（ASTC transport probe、backend、最後のerror / 所要時間）を1行の文字列で返す診断用で、実機のworld内表示に使う。

最小例では、次のdownload前に`ReleaseImage()`を呼ぶ。旧画像を表示したまま次画像へ切り替えたい場合は、表示中handleとpending handleを別々に保持する。新しいsuccess callbackの後で旧handleを解放すれば、Materialは新結果のまま維持される。

## 圧縮・fallback方針

| 状況 | 既定動作 |
|---|---|
| Windows、両辺4px整列、mipmapなし、alphaなしsource（RGB24 / RGB48 / RGB565）、Prefer Bc1 For Opaque Sources ON | BC1へ圧縮 |
| Windows、両辺4px整列、mipmapなし、上記以外 | BC7へ圧縮 |
| Android/iOS、mipmapなし | ASTC 4x4へ圧縮 |
| compression失敗、Allow Uncompressed Fallback ON | 元画像をsuccess callbackで返す |
| compression失敗、Allow Uncompressed Fallback OFF | failure callback |
| `GenerateMipMaps == true` | 圧縮対象外。設定に応じてfallbackまたはerror |
| Windowsで非4整列、Experimental padding OFF | 方針として元画像をsuccess callbackで返す |
| 元TextureがR8 / Alpha8 / 既にblock圧縮済み | 圧縮しても縮まないため、方針として元画像をsuccess callbackで返す（`SourceFormatHasNoGain`） |
| encode + readbackが `Compression Timeout Seconds` を超過 | encoderを強制idleにし、設定に応じてfallbackまたはerror（`CompressionTimeout`） |
| native download失敗 | failure callback |

非圧縮fallbackでも表示自体は成功しているため、success callbackになる。`UsedFallback`と`CompressionErrorCode`をログへ残すと、気付かないまま非圧縮運用になることを防げる。

## Experimental: BC7 / BC1の任意寸法対応

> [!CAUTION]
> 原則として、配信画像のwidth / heightは両方とも4pxの倍数にする。

BC1もBC7と同じ4x4 block制約を持ち、同じ設定とedge padding経路を共用する。以下の「BC7」はBC1にも当てはまる。

ここでいう非整列はpower-of-twoではなく、BC7の4x4 block境界に対する非整列である。たとえば`12x20`や`1000x768`は2冪ではないが補正不要である。

`Enable Bc7 Edge Padding`は既定OFFである。

この設定はPrefab rootの`DRCompressedImageDownloader`で変更する。子`BC7 Encoder`の`Allow Edge Padding`はFacadeがrequest開始時に設定する内部値なので、直接編集しない。

- OFF: 非4整列画像を原寸の非圧縮Textureとしてsuccessにする。
- ON: 右端・上端のtexelを複製し、各辺を4px単位へ切り上げてBC7圧縮する。

コードから変更する場合はrequest開始前のidle時に呼ぶ。

```csharp
downloader.SetBc7EdgePaddingEnabled(true);
bool enabled = downloader.GetBc7EdgePaddingEnabled();
```

`541x768`は`544x768`へなり、次のmetadataを返す。

```text
OriginalWidth/Height: 541x768
ResultWidth/Height:   544x768
ContentUvScale:       (541/544, 1)
ContentUvOffset:      (0, 0)
```

Materialを渡した場合、Wrapperは既存のtexture scale / offsetへ補正を合成する。自動補正の保証条件は次のとおりである。

- shaderが`{MaterialProperty}_ST`をsamplingに使う。
- 通常の0..1 UVを使う。
- Wrap ModeがClampである。
- 同じMaterial/propertyを単一の共有Facadeで管理する。

RawImage、STを使わないcustom shader、Repeat/Mirror、複数tilingでは自動補正を前提にしない。`RequiresContentUvCorrection`、`ContentUvScale`、`ContentUvOffset`を使って利用側で補正するか、Experimental機能をOFFにする。

Android/iOSのASTC Textureは論理寸法を原寸のまま保持するため、このBC7固有設定の対象外である。

## Inspector設定

通常はPrefabの内部worker参照を変更しない。

| 設定 | 既定値 | 説明 |
|---|---|---|
| Callback Result Variable | `DRImageDownloadResult` | receiverへ結果を書き込む変数名 |
| Success Event Name | `OnCompressedImageLoadSuccess` | success / fallback時のevent |
| Failure Event Name | `OnCompressedImageLoadError` | download / compression error時のevent |
| Prefer Bc1 For Opaque Sources | ON | Windowsでalphaなしsource（RGB24 / RGB48 / RGB565）をBC1（4 bpp）にする。OFFでBC7（8 bpp） |
| Force Bc1 Discard Alpha | OFF | Windowsでsourceの形式に関係なくBC1にし、alphaを捨てる。表示shaderがalphaを使わないcontent向け。requestごとに `SetForceBc1(bool)` で指定できる |
| Target Width / Height | 0 / 0 | downloadした画像が両辺ともこの寸法のちょうど2倍または4倍なら、encoder shader内のbox平均でこの寸法に縮小してencodeする。0で無効。requestごとに `SetTargetSize(w, h)` で指定できる |
| Enable Bc7 Edge Padding | OFF | ExperimentalなWindows非4整列対応（BC1 / BC7共通） |
| Allow Uncompressed Fallback | ON | compression failure時に元画像を使う |
| Compression Timeout Seconds | 15 | encode + readbackの上限。超過時はencoderをidleへ戻し、以降のrequestを受け付けられる状態に復帰する。0で無効 |
| Verbose Logging | ON | service受付拒否などをWarningへ出す |

子workerのGPU encodeはblock行のstripに分割され、1フレームに1 stripだけ描画される。strip幅は既定で**適応制御**（`Adaptive Frame Budget Milliseconds` = 3、`Adaptive Initial Blocks Per Frame` = 2048、`Adaptive Min Blocks Per Frame` = 1024）: workerはアイドル中に `Update` でフレーム時間の移動平均（基準）を取り続け、encodeは2,048 blockから始めて、encode中フレーム時間の移動平均が「基準 + 予算」を超える間は×0.7、予算の半分未満なら×1.3でstripを増減する。上限は `Max Blocks Per Frame`（既定16384）。ただしAndroid / iOSではcompile時定数 `MobileBlocksPerFrameCap`（4096）でさらに頭打ちにする。tile-based GPUはstripのGPU時間をキューが飽和するまでフレーム時間に出さないため、フレーム時間のfeedbackだけでは1回大きなstripを通してしまう（Quest 2で16,384 blockが約28 ms）。「stripを出した次のフレームの基準超過分 ÷ そのstripのblock数」を1 blockあたりのコスト（`LastMicrosecondsPerBlock`、移動平均）として計測するが、これは**診断表示専用**で制御には使わない（制御に使う案は、アバター読込などstripと無関係なhitchを1回拾うだけで推定が跳ね上がりstripが最小に張り付くため却下した）。最小値に30フレーム張り付いた場合は基準を現在の平均へ再設定する（過負荷時に進まなくなるのを防ぐ）。予算を0にすると従来どおり `Max Blocks Per Frame` 固定になる。端末ごとの目標フレーム時間を設定する必要はなく、mobile GPUでも1 stripが1フレームに収まる大きさへ自動で落ち着く（代わりにencode完了までのフレーム数が増える）。単発の最短フレームを基準にする方式は、vsync後のcatch-upフレームやEditorの不規則なフレームで基準が汚染されてstripが最小に張り付く（Questで1448x2048が6秒かかった事例）ため採用しない。結果は `LastStripCount` / `LastMinBlocksPerFrame` / `LastMaxBlocksPerFrame` / `LastBaselineFrameMs` / `LastWorstStripFrameMs` で確認できる。

共有Prefabの設定変更はすべてのconsumerへ影響する。特にpadding方針をコードからrequestごとに切り替える場合、active request中の変更は拒否される。

## トラブルシューティング

| 症状 | 確認事項 |
|---|---|
| `DownloadImage()`が`null` | `LastServiceError`を確認。active request、backend busy、handle pool、Material property、Dispose済みserviceを確認 |
| callbackが来ない | 同期rejectではcallbackなし。変数名・event名がPrefab設定と完全一致するか確認 |
| successだが圧縮されていない | `UsedFallback`と`CompressionErrorCode`を確認。mipmap、BC7寸法、platform、encoder error、`SourceFormatHasNoGain`（R8など）、`CompressionTimeout`を確認 |
| 画像が暗部で縞になる | workerの`Output Srgb`がOFFになっていないか、sourceが`isDataSRGB`か確認 |
| alphaなし画像でblockノイズ・色の段差が目立つ | BC1（4 bpp）の限界。`Prefer Bc1 For Opaque Sources`をOFFにするとBC7（8 bpp）になる |
| 5回目付近から受付拒否 | 完了/error handleを解放せず4-slot poolを使い切っていないか確認 |
| `Progress == 1`なのに未完了 | Encoding中も1になる。custom callbackまたは`State`を使う |
| Dispose後に画像が消える | 所有中のMaterial Textureを外す仕様。表示期間中はhandleを保持する |
| padded画像が切れる・縮む | shaderの`_ST`利用、Clamp、UV、単一Facade条件を確認。非対応表示ならpaddingをOFF |
| Materialへ設定されない | `TextureInfo.MaterialProperty`がshaderに存在するか確認。Materialを`null`で渡していないか確認 |
| 画像更新で一瞬空になる | 新結果のcallbackまで旧handleを保持し、表示中/pendingの2 handleで管理する |

## 現在の制約

- active download / encodeは1件。同時request queueは未実装。
- handle poolは4個。完了/error結果を長期間4件保持すると新規受付できない。
- 全Behaviourは`SyncMode.None`であり、各clientが独立してdownload / encodeする。結果はnetwork同期されない。
- mip chain encodeは未実装。`GenerateMipMaps=true`はfallback対象。
- BC7はMode 6固定、BC1はendpoint候補3組（min/max、1/16 inset、最遠画素ペア）の簡易探索、ASTCは単一固定modeであり、いずれもproduction品質の汎用encoderではない。
- BC1はalphaを持たない。Facadeは既定でalphaなし形式（RGB24 / RGB48 / RGB565）にだけBC1を選び、R8 / R16などのグレースケールはBC1の対象外（RGB565 endpointで色が付くため）。alphaチャンネル付きでも表示側がalphaを使わない場合は `Force Bc1 Discard Alpha` / `SetForceBc1(true)` でBC1にできる（encoderはもともとRGBしか読まないので変換は発生しない）。
- 縮小は2ⁿ倍（2, 4）かつ割り切れる寸法のみ。download自体は元解像度で行われるため、通信量とdecode時のpeak memoryは減らない（常時VRAMだけが減る）。
- source TextureがsRGBの場合、RGBはsRGBへ戻してから8-bit量子化し、結果TextureもsRGBとして生成する（worker の `Output Srgb`、既定ON）。alphaはlinearのまま。sourceがlinear Textureの場合は従来どおりlinear値を保存する。
- encodeはblock行のstripに分割して複数フレームで実行するため、callbackまでの遅延はdownload完了後に最大で `ceil(blocks / Max Blocks Per Frame)` フレーム加わる。
- `FilterMode.Point`は維持するが、mipmapを持たないためBilinear / TrilinearはBilinearになる。Wrap Modeは引き継ぎ、圧縮結果の`anisoLevel`は0になる。
- 圧縮結果はtop mipだけ（`mipmapCount == 1`）でCPUからread不可であり、`GetPixels`や`EncodeToPNG`用途には使えない。
- 共有Materialを渡すと、そのMaterialを使う同一client内の全Rendererへ表示変更が反映される。
- download側のtimeoutは未実装（native downloaderのerrorに依存する）。encode側は `Compression Timeout Seconds` で復帰する。
- Android/iOS実機検証前。
- `IVRCImageDownload`互換interfaceの完全実装ではない。

## ファイル構成

```text
Prefabs/RuntimeImageCompression/
  DRCompressedImageDownloader.prefab
Materials/RuntimeImageCompression/
  M_Bc7Mode6Encoder.mat
  M_Astc4x4Encoder.mat
Scripts/RuntimeImageCompression/
  DRCompressedImageDownloader.cs
  DRCompressedImageDownload.cs
  RuntimeBc7EncoderController.cs
  RuntimeAstc4x4EncoderController.cs
  Samples/DRCompressedImageDownloaderExample.cs
Shaders/RuntimeImageCompression/
  Bc7Mode6Encoder.shader
  Astc4x4Encoder.shader
```

## 詳細資料

- [利用例: ポスター掲示ギミック（PosterDisplay）](docs/poster-display-gimmick.md)
- [Wrapperの設計・検証・所有権](docs/runtime-compressed-image-downloader-poc.md)
- [BC7 Mode 6実装報告](docs/bc7-mode6-poc-implementation.md)
- [BC7 / ASTC encoder経路調査](docs/bc7-astc-encoder-path-investigation.md)
- [初期調査](docs/runtime-image-compression-investigation.md)
