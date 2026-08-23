# ポスター掲示ギミック（PosterDisplay）

作成日: 2026-08-22
状態: デモ動作確認済み（Windows Editor / ClientSim）。VRChat Build & Testは未実施。

`VRCImageDownloader`で取得したポスター画像を[VRC Dynamic Image Compression](../README.md)でGPU圧縮し、壁の掲示板（Quad）へ表示するギミック。Windowsではalphaなし画像がBC1（4 bpp）になり、RGB24比16.7%のVRAMで掲示できる。

## ポスターのパターン（縦長）

| パターン | 画像pixel | 既定の物理サイズ（幅 x 高さ） | 圧縮後（BC1） |
|---|---:|---:|---:|
| Large | 1448 x 2048 | 1.414 m x 2.0 m | 1,482,752 byte |
| Small | 724 x 1024 | 0.707 m x 1.0 m | 370,688 byte |

- どちらも縦横比 1:√2（A判縦）で、両辺とも4の倍数なのでedge paddingは不要。
- 画像は**pixel数を上記に合わせて配信する**。サイズが違っても表示はされる（掲示板へ引き伸ばし）が、Consoleにwarningが出る。
- VRChatの画像上限は2048 x 2048。超える画像は `MaximumDimensionExceeded` で読み込み自体が失敗する（2026-08-23、2048 x 2906のPNGでClientSim上でも確認）。
- 圧縮形式は**alphaチャンネルの有無（Texture形式）**で決まる。alphaチャンネルを持つPNGは中身が全画素不透明でも `RGBA32` として読み込まれBC7（8 bpp）になるので、ポスターはJPEGまたは**alphaチャンネルなしのPNG**で配信する。
- 元画像が上記と違う場合は、Unityで `LoadImage` → `Graphics.Blit` → `ReadPixels(RGB24)` → `EncodeToPNG` で縮小・alpha除去したPNGを作ると、そのままBC1対象になる（Editorスクリプトで実施可）。

## 構成

```text
Scripts/PosterDisplay/
  PosterDisplayController.cs / .asset   <- 掲示板1枚（download、Materialへ設定、サイズ検証、retry）
  PosterDisplayManager.cs / .asset      <- 複数枚を直列にロード（VRChatのrate limit対応）
  PosterDisplayDebugPanel.cs / .asset   <- 実機確認用のworld内ログパネル（legacy UI Text）
Prefabs/PosterDisplay/
  PF_PosterDisplay.prefab               <- Board(Quad, Unlit/Texture) + Frame(Cube) + Controller
Materials/PosterDisplay/
  M_PosterBoard.mat / M_PosterFrame.mat / M_PosterWall.mat
Scenes/
  PosterGimmickDemo.unity               <- デモシーン
```

デモシーン `PosterGimmickDemo.unity` は `Assets/Scenes/VRCDefaultWorldScene.unity` をUnity上で複製し（blueprint IDは空）、次を追加したもの。ClientSimと重複する `EventSystem` は除去してある。

```text
PosterWall                 (Cube 8 x 4 x 0.2, z = 3.15)
DRCompressedImageDownloader (Facade Prefab instance)
Poster_Large_1448x2048     (PF_PosterDisplay, x = -1.5, y = 1.9, z = 3.0, 幅1.414 m x 高さ2.0 m)
Poster_Small_724x1024      (PF_PosterDisplay, x = +0.68, y = 1.6, z = 3.0, 幅0.707 m x 高さ1.0 m)
PosterDisplayManager       (downloader + posters[2] + debugPanel)
PosterDebugPanel           (world-space Canvas 1.8 x 2.2 m, x = +2.6, y = 1.9, z = 3.0。status / log の2つのText)
```

配置は2026-08-24にUnity上で調整済み（Smallとパネルを中央寄せ）。公開版のデモSceneでは `VRCWorld` の blueprint ID を空にしてあるので、Build & Publishで新規worldとして作成する。

Quadの法線は-Zなので、掲示板はrotation 0でspawn（原点）側を向く。

## PosterDisplayController（掲示板1枚）

| Inspector | 既定 | 説明 |
|---|---|---|
| Poster Url | - | ポスター画像のURL（`VRCUrl`）。redirectしない直URLにする |
| Expected Width / Height | 2048 / 1448（script既定）| 想定pixel数。Sceneでは縦長の1448 / 2048、724 / 1024を設定。結果が違えばwarning |
| Board Renderer | Board | 表示先Renderer |
| Material Property | `_MainTex` | textureを入れるproperty |
| Use Material Instance | ON | `renderer.material`（Rendererごとのinstance）を使う。Prefabを複製しても画像を共有しない |
| Board Width Meters | 2.0（script既定）| 掲示板の物理幅。高さは想定縦横比から算出。Sceneでは1.414 / 0.707（高さ2.0 / 1.0 m） |
| Board / Frame Transform | Board / Frame | `ApplyBoardSize()` がscaleを設定する対象 |
| Apply Board Size On Start | ON | Startでscaleを想定比率へ合わせる（Editor上の見た目も同じ値を入れてある） |
| Downloader | - | Managerを使う場合は未設定でよい（Managerが注入） |
| Load On Start | OFF | Manager無しで単体ロードする場合にON |
| Max Retries / Retry Delay Seconds | 2 / 6 | 失敗時の再試行。VRChatの5秒制限より長くする |
| Discard Alpha | ON | Boardのshaderはalphaを使わないので、alphaチャンネル付き画像でもWindowsではBC1（4 bpp）にする（Facadeの `SetForceBc1`） |
| Downscale To Expected Size | ON | downloadした画像がExpected Sizeのちょうど2倍 / 4倍なら、encoder shader内のbox平均で縮小してencodeする（Facadeの `SetTargetSize`）。LargeとSmallが同じURLを共有できる |

公開API: `BeginLoad()`, `Unload()`, `ApplyBoardSize()`, `SetDownloader()`, `SetManager()`。
公開状態: `IsLoading`, `IsLoaded`, `LastFormat`（`BC1` / `BC7` / `ASTC_4x4` / `Original`）, `LastUsedFallback`, `LastImageWidth/Height`, `LastSizeInMemoryBytes`, `LastError`。

callback名（`OnCompressedImageLoadSuccess` / `OnCompressedImageLoadError`）と結果変数（`DRImageDownloadResult`）はFacade Prefabの既定値と一致させてある。

## PosterDisplayManager（直列ロード）

Facadeは同時に1 requestしか受け付けず、VRChatは画像downloadを5秒に1回に制限しているため、`posters[]` の順に1枚ずつロードする。

| Inspector | 既定 | 説明 |
|---|---|---|
| Downloader | Facade Prefab instance | 共有downloader。各掲示板へ注入する |
| Posters | - | ロード順の掲示板配列 |
| Load On Start | ON | `Start Delay Seconds`（1秒）後に `LoadAll()` |
| Interval Seconds | 5.5 | 1枚完了から次の開始までの間隔。5秒未満にしない |

公開API: `LoadAll()`, `ReloadAll()`, `UnloadAll()`。状態: `IsLoadingAll`, `LoadedCount`, `FailedCount`。

## PosterDisplayDebugPanel（実機確認用ログ）

Quest / iOS / PC buildではUnity Consoleが見えないので、結果をworld内のパネルへ出す。legacy `UnityEngine.UI.Text`（組み込みフォント `LegacyRuntime`）を使い、TextMeshPro Essentialsを必要としない。`Udon` からは `SystemInfo` が参照できないため、GPU名やformat対応表は出せない。代わりにworkerの診断（ASTC transport probe / backend）が端末固有の判定材料になる。

| 行 | 内容 |
|---|---|
| `Target` | compile symbolで決まるbuild target（Windows / Android / iOS）。codec routingと同じ判定 |
| `Frame max` | `download`（requestがDownloading中 = nativeのdecodeを含む）/ `encode`（Encoding中 = strip）/ `loading`（Manager動作中）/ `total` の最大frame timeと現在値。起動直後90フレームは除外。EditorではInspector操作などによる停止も拾う |
| `ASTC` / `BC7/BC1` の2行目 | 直近encodeのstrip統計: `strips=`（フレーム数）、`blk/frame=min..max`（適応制御の到達範囲）、`base=`（encode中の最短frame time）、`worstStrip=`（strip直後の最長frame time）。`worstStrip` が `base + 予算` を大きく超えるなら予算か初期値を下げる |
| `Facade` | active request の有無、`LastServiceError` |
| `Worker` | `DRCompressedImageDownloader.GetBackendDiagnostics()`。Android/iOSでは `ASTC probe= rowsReversed= probeErr= backend= busy= lastErr= lastMs=`、Windowsでは `BC7/BC1 backend= bc1= srgb= busy= lastErr= lastMs=` |
| `Manager` | `loading / loaded / failed` |
| 掲示板ごと | state、format（`BC1` / `BC7` / `ASTC_4x4` / `Original(fallback)`）、backend、encode寸法（縮小時は download寸法と divisor）、byte数、encode時間、error |
| ログ | `Manager` / `Controller` からの時刻付きイベント（sequence start / load / loaded / failed / retry / gave up / finished）。既定18行で古い行から消える |

Inspector: `Status Text` / `Log Text`（UI Text）、`Downloader`、`Manager`、`Posters`、`Max Log Lines`（18）、`Refresh Interval Frames`（30）、`Frame Stats Warmup Frames`（90）。ManagerのInspector `Debug Panel` に割り当てると、Managerが各掲示板へ注入する（未割り当てならログは出ないだけで動作は変わらない）。

### 実機（Android / iOS）での確認手順

1. Active Build TargetをAndroid（またはiOS）にしてBuild & Test / Upload。codec routingはcompile symbolで決まるので、PC buildではASTC経路は通らない。
2. 入室後、壁右のパネルで次を見る。
   - `Worker: ASTC probe=True probeErr=-`: 64-byte transport sentinelが実機GPUで通過（RInt / ARGB32のどちらかが `backend=` に出る）。`probe=False` かつ `probeErr` ありなら、その端末ではGPU readback経路が成立していない（結果は非圧縮fallback）。
   - `rowsReversed`: readback行順。`True` でも正常（shader側で反転）。
   - 各掲示板が `ASTC_4x4` で `fallback` 表記なし。byte数は `pixel数 x 1`（1448x2048で2,965,504、724x1024で741,376）。
   - `Frame max: loading` がtile-based GPUで許容範囲か（strip分割の効果）。大きければworkerの `Max Blocks Per Frame` を下げる。
   - ポスター自体の向き・上下・色（sRGB）が正しいか。ASTC hardware decodeの検証はここが初めてになる。
3. 結果はスクリーンショットで残す。`fallback` になった場合は掲示板行の `err=` と `ASTC` 行の `err=` がfallback理由。

パネルの `Facade` 行の読み方（2026-08-23追加）:

- `hb=`（heartbeat）が増えていない → Facadeが停止している（Udon例外）。
- `cb ok/err=0/0` のまま `Request: phase=Downloading progress=0.00` が続く → nativeのcallbackが来ていない。典型例は**許可ドメイン外のURL**（Allow Untrusted URLs無効のclientでは無言で止まる）。`Download Timeout Seconds` 経過後に `DownloadTimeout` として失敗し、Controllerがretryする。
- `bypass=` / `passMat=` はFacadeの診断スイッチの状態。どちらも既定OFF。`passMat`（`Pass Material To Native Downloader`）をONにするとnative clientがdownload直後に元画像をMaterialへ設定するため空白期間はなくなるが、encode中のcancelやfallback無効時のerrorでMaterialに破棄済みTextureの参照が残り得る（Facadeの所有権管理外）。診断が終わったら必ずOFFに戻す（2026-08-24にOFFへ戻した）。
- `ASTC` / `BC7/BC1` 行はworkerの公開fieldを直接読んでいるので、Facade停止時も最新値。

事例（2026-08-23、iOS実機）: `Request: phase=Downloading state=Pending progress=0.00 elapsed=31s`、worker未起動、Windows実clientでは同じビルドが表示できた。原因はDiscord CDNが許可ドメイン外で、PC clientだけAllow Untrusted URLsが有効だったこと。`Pass Material To Native Downloader` ONでも変化なし（null Materialは原因ではない）。

### 実機結果（2026-08-23、Quest / iOS、Allow Untrusted URLs有効、Discord CDNの1448 x 2048 RGB PNG）

Quest / iOSとも2枚のポスターが表示された。iOSのパネル:

```text
ASTC probe=True rowsReversed=False probeErr=- backend=RInt lastErr=- lastMs=34
Poster_Large_1448x2048: loaded ASTC_4x4 RInt 1448x2048 2965504 B enc 515 ms
Poster_Small_724x1024:  loaded ASTC_4x4 RInt 724x1024 (dl 1448x2048 /2) 741376 B enc 34 ms
Frame max: loading 255.0 ms
Manager: loaded=2 failed=0
```

- Metal上でRInt transportが64-byte sentinel完全一致で成立し、readback行順は正順（`_FlipOutputY` 不要）。
- ASTC 4x4のhardware decodeで表示できた（PoC設計docのgate 1・2相当。4象限の画素一致試験は未実施）。
- shader内1/2縮小（`DownscaleDivisor=2`）はASTC経路でも動作。
- 所要: Large 515 ms（strip 12回 + readback）、Small 34 ms。`Frame max 255 ms` はnativeのPNG decode（PCでも約240 ms）かstripの初回かを切り分けていない。体感ではロード直後にかくつきがあった。
- 対処（2026-08-23）: workerのstrip幅を固定16,384 blockから**適応制御**（初期2,048 block、frame time予算3 ms、上限16,384）へ変更。パネルの `Frame max` を `download` / `encode` に分離し、strip統計（`strips= blk/frame= base= worstStrip=`）を追加。Windows Editorでは Small が `strips=8 blk/frame=2048..10368 base=9.5ms worstStrip=12.1ms` で予算内に収束。nativeのPNG decode（`download` 側の最大値）はVRChat側の処理で、ギミック側では減らせない（JPEG化・小さい画像で軽くなる）。

適応制御後の実機結果（2026-08-23〜24）。体感のかくつきは解消。

| 端末 | backend | Large enc | Small enc | strip統計（Small） | Frame max download / encode |
|---|---|---:|---:|---|---|
| iOS（120 Hz） | RInt | 144 ms（固定stripでは515 ms） | 72 ms | `strips=7 blk/frame=2048..16384 base=7.6ms worstStrip=8.4ms` | 31.8 ms / 8.4 ms |
| Quest（72 Hz） | ARGB32 | **6,038 ms** | 145 ms | `strips=7 blk/frame=2048..15552 base=12.7ms worstStrip=14.9ms` | 21.5 ms / 193.1 ms |

- iOS: 固定strip時の255 msは初回strip（16,384 block）だったことが確定（`download` 側は31.8 ms）。
- Quest: stripは予算内（14.9 ms）だが、Largeは6秒かかった。入室1秒後のロードで、アバター/ワールド読込中の長いフレームに適応制御が反応してstripが最小値（256 block）に張り付いたと推定（Smallはその後に15,552まで伸びている）。`encode 193 ms` の単発hitchはstripではなく（`worstStrip` は14.9 ms）、readback完了フレーム（3 MBの `LoadRawTextureData` / `Apply`）か入室直後の他処理。切り分けのため、パネルはポスター完了ごとに「完了フレームのframe time + そのencodeのstrip統計」をログへ残すようにした（Largeの統計がSmallで上書きされないようにする）。対策候補: Managerの `Start Delay Seconds` を入室直後の負荷が落ち着く3〜5秒にする、`Adaptive Min Blocks Per Frame` を上げる（256 → 1024）。
- 対処（2026-08-24）: Windows Editorで同じ症状を再現（`base=2.4ms` / `6.6ms` の異常に短いフレームが基準になり、`strips=299 blk/frame=256..3072`、Largeに2.5秒）。基準を「encode中の最短フレーム」から**アイドル中に `Update` で取る移動平均**へ変更し、判定もencode中フレームの移動平均（単発ではない）にした。増減は×1.3 / ×0.7、最小値を1024へ。同じEditorで `strips=20 blk/frame=1863..16384 base=11.4ms enc=234ms` に収束。Managerの `Start Delay Seconds` は3秒に変更。パネルはポスター完了ごとに `finish frame <ms> | strips=.. blk/frame=.. base=.. worst=.. enc=..` をログへ残す（Largeの統計がSmallで上書きされる問題の対策）。
- Quest再計測（2026-08-24、アイドル平均基準）: Large `enc 548 ms`（6,038 msから短縮）、`strips=28 blk/frame=1348..16384 base=13.9ms worst=42.4ms`、finish frame 14.4 ms。Small `enc 151 ms strips=8 blk/frame=2048..12845 worst=15.6ms`。`base=13.9ms` は72 Hzのフレーム時間どおり。残る `worst=42.4ms`（`Frame max encode 87.3ms`）は、移動平均が反応する前にstripが上限16,384まで伸びた後の1回で、Quest GPUでは16,384 blockが約28 msかかる。
- 対処: 観測した1 blockあたりコスト（`us/blk`、移動平均）から `予算 ÷ コスト` を次のstripの上限にするモデル制約を追加。Questの実測（約1.7 µs/block）では予算3 msで約1,700 block/frame → Largeは約110フレーム（72 Hzで1.5秒）になる見込み。表示までの時間とhitchのトレードオフは `Adaptive Frame Budget Milliseconds` で調整する（例: 5 msなら約0.9秒）。
- Quest再計測（モデル制約・移動平均版）: Large `enc 490 ms strips=26 blk/frame=2048..16384 worst=34.8ms us/blk=0.47`、encode max 52.6 ms。改善はしたが上限まで伸びてから1回hitchが出る。原因は**tile-based GPUの深いパイプライン**で、stripのGPU時間はキューが飽和するまでフレーム時間に現れず、伸びている最中のコスト計測（0.15〜0.47 µs/block）が実際（約1.3〜1.7 µs/block）より小さく出るため。
- 対処（2026-08-24）: コスト推定を「観測した最悪値を即採用、超過のないフレームで5%ずつ減衰」に変更し、**encodeをまたいで保持**（2枚目以降・再ロードは最初から適正サイズ）。初期推定値をplatform別に固定（Android/iOS 1.0 µs/block → 初回から上限約3,000 block、PC 0.05）。最後のstripの次フレームも計測に含めた（従来は未計測で、`Frame max encode` と `worst` の差がここだった）。
- 「最悪値即採用」はEditorで逆効果だった（Editorの停止フレームを拾って `us/blk=16.76`、252 strip / 3.2秒）。単発フレームではstrip起因のstallと無関係なhitch（アバター読込など）を区別できないため、推定は移動平均（立ち上がり0.3、静穏時5%減衰、上限4 µs/block）に戻し、代わりに**mobile（Android/iOS）のstrip上限を4,096 blockに固定**（`MobileBlocksPerFrameCap`、compile symbol）。Questで約6 msのGPU時間なので1フレームに収まり、Largeは約46フレーム（72 Hzで約0.6秒、iOS 120 Hzで約0.4秒）。PCは `Max Blocks Per Frame`（16,384）のまま。
- 移動平均＋クランプでもEditorでは `us/blk=4.00`（上限張り付き）で206 strip / 2.6秒になったため、コスト推定は**制御から外し診断表示のみ**にした（単発hitchと区別できない以上、制御に使うと数秒の停滞を招く）。最終的な制御は「アイドル平均基準 + encode中フレームの移動平均判定（×1.3 / ×0.7）+ 固定上限（PC 16,384 / mobile 4,096）+ 最小値張り付き時の基準再設定」。Editor確認: Large `strips=18 blk/frame=2048..16384 base=11.3 worst=18.9 enc=206ms`。
- **Quest最終計測（2026-08-24）**: Large `enc 715 ms strips=48 blk/frame=2048..4096 base=14.4 worst=23.9 us/blk=1.26`、Small `enc 191 ms strips=13 worst=20.5`、`Frame max: download 25.5 / encode 23.9 ms`（encode maxがworstと一致 = 全stripを計測）、完了フレーム 17.6 / 16.0 ms。72 Hzに対し最悪+9.5 ms（ロード中に1フレーム落ちが数回）で、体感のかくつきはなし。この構成（mobile上限4,096）を既定として採用。フレーム落ちゼロを優先するなら `MobileBlocksPerFrameCap` を2,048（Large約1.3秒、worst ≈ base + 5 ms）、表示速度を優先するなら6,144（約0.5秒、worst ≈ +14 ms）。
- 固定strip（16,384 block、初回実機）からの推移: Large 6,038 ms → 548（アイドル平均基準）→ 490（モデル制約）→ 715 ms（mobile上限4,096）、worst strip 42.4 → 34.8 → 23.9 ms。encode時間は伸びたがhitchは消えた。
- Questの数値は未記録（表示のみ確認）。

## 掲示板を増やす

1. `PF_PosterDisplay` をSceneへ置き、`Poster Url` と `Expected Width / Height`、`Board Width Meters` を設定する。
2. Editor上の見た目を合わせたい場合は `Board` のscaleを `(幅, 幅 x 高さpx / 幅px, 1)`、`Frame` を `(幅 + 0.1, 高さ + 0.1, 0.03)` にする（Startで同じ値が再適用される）。
3. `PosterDisplayManager` の `Posters` へ追加する。Facade Prefabは1つを共有する。

## 制約・注意

- 同期なし（`SyncMode.None`）。各clientが自分でdownload / 圧縮する。
- download中・圧縮中は掲示板が空（Material既定の白）。placeholderは未実装。
- ポスターの差し替えはURLを変えて `BeginLoad()`（1枚）または `ReloadAll()`（全枚）。
- mipmapは生成しない（Facadeの制約）。遠距離ではエイリアシングが出る。
- **配信ホストはVRChatの画像読み込み許可ドメインにする。** 公式doc（[Image Loading](https://creators.vrchat.com/worlds/udon/image-loading/)）の許可リストは `*.disbridge.com`、`dl.dropbox.com` / `dl.dropboxusercontent.com`、`*.github.io`、`images4.imagebam.com`、`i.ibb.co`、`images2.imgbox.com`、`i.imgur.com`、`i.postimg.cc`、`i.redd.it`、`pbs.twimg.com`、`*.vrcdn.cloud`、`assets.vrchat.com`、`i.ytimg.com` の13件（2026-08-23時点）。**Discord CDN（`cdn.discordapp.com` / `media.discordapp.net`）と `raw.githubusercontent.com` は含まれない。** リスト外はclientの「Allow Untrusted URLs」が有効なときだけ読み込まれ、無効なclientではerror callbackも来ずにdownloadが始まらない（Facadeは `Download Timeout Seconds`（既定45秒）で `DownloadTimeout` にする）。
- ClientSimの `VRCImageDownloader` はHTTP redirectを追わないため、redirectしない直URLを使う。デモSceneの `Poster Url` にはサンプル画像 `https://sechiro.github.io/vrc-posters/posters/hatago_1448x2048.png`（GitHub Pages、1448 x 2048 RGB PNG）を設定してある。Large / Smallとも同じURLで、Smallはギミック側で1/2に縮小される。差し替える場合は許可ドメイン上の画像を指定する。
- Facadeのhandle poolは4個。掲示板は成功後もhandleを保持する（Textureの所有者）ので、同時に掲示できるのは4枚まで。5枚以上は `DRCompressedImageDownloader.prefab` のRequest Handleを増やすか、Managerを分ける。

## 動作確認

2026-08-22（横長パターン 2048 x 1448 / 1024 x 724、Windows Editor / ClientSim / D3D11）:

- Play開始後、Large → 5.5秒 → Smallの順にロード。両方 `format=BC1 fallback=False`、byte数は上表どおり（pixel数は横長・縦長で同じ）。
- 2枚のBoardは別々のMaterial instance（`M_PosterBoard (Instance)`）にそれぞれのDXT1 textureが入る。
- Main Cameraからのスクリーンショットで向き・縦横比・枠を目視確認。Consoleにerror / warningなし。
- Manager完了時 `LoadedCount=2 FailedCount=0`。

2026-08-23（縦長へ変更）:

- Sceneを縦長パターンへ変更。指定されたDiscord CDNの元画像（2048 x 2906、RGBA PNG、alphaは全画素255）は `MaximumDimensionExceeded` で読み込み失敗し、Controllerのretry（2回、6秒間隔）の後にfailureとなることを確認。
- 上記画像を1448 x 2048へ縮小しalphaを除いたPNG（RGB）をDiscordへ再アップロードしたURLで再検証。Large は `cdn.discordapp.com/.../nigaoe_1448x2048.png?ex=..&is=..&hm=..`（1448 x 2048）、Small は同じ添付をDiscordのメディアプロキシで縮小した `media.discordapp.net/...?ex=..&is=..&hm=..&width=724&height=1024`（724 x 1024、PNGのまま）。どちらも `RGB24` として読み込まれ `format=BC1 fallback=False`、byte数は上表どおり。Manager `LoadedCount=2 FailedCount=0`、スクリーンショットで縦長の向き・比率を確認。
- 注意: Discordが表示用に生成する `format=webp` 付きURLはVRChatが読めない（PNG / JPEGのみ）。`format` / `quality` を外すか `cdn.discordapp.com` を使う。Discord CDNの添付URLは `ex=` の期限付き（今回のURLは2026-08-24 00:10 JSTまで）なので、常設ポスターには期限のないURLを使う。

2026-08-23（alpha破棄・ギミック側縮小）:

- `PosterDisplayDebugPanel` を追加し、Windows EditorでLarge / Smallの結果行とイベントログが表示されることを確認（スクリーンショット）。
- `Discard Alpha`（既定ON）と `Downscale To Expected Size`（既定ON）を追加。SmallのURLをLargeと同じ `cdn.discordapp.com` の1448 x 2048 PNGに変更し、Discordメディアプロキシを使わずに `downloaded=1448x2048 divisor=2 -> 724x1024 BC1 370,688 byte` になることを確認（UV補正なし、Manager `loaded=2 failed=0`、Consoleにerrorなし）。
- 縮小で減るのは常時VRAMだけで、downloadと`VRCImageDownloader`のdecodeは元解像度（1448 x 2048、RGBA32換算で11.9 MB）のまま行われる。通信量とpeakも減らしたい場合は配信側で縮小する。

### VRAM削減幅（D3D11。RGB24はGPU上RGBA32として保持される）

| 元画像 | 圧縮形式 | 1 px当たり | Large 1448x2048 | Small 724x1024 | 元画像比 | 備考 |
|---|---|---:|---:|---:|---:|---|
| alphaなし（RGB24、GPU実体4 B/px） | BC1（Windows既定） | 0.5 B | 11.9 MB -> 1.48 MB | 2.97 MB -> 0.37 MB | -87.5%（論理値3 B/px比では -83.3%） | |
| alphaあり（RGBA32） | BC7（Windows既定） | 1 B | 11.9 MB -> 2.97 MB | 2.97 MB -> 0.74 MB | -75% | alphaを保持 |
| alphaあり -> alphaなしと判断してBC1（`Discard Alpha` / `SetForceBc1`） | BC1 | 0.5 B | 11.9 MB -> 1.48 MB | 2.97 MB -> 0.37 MB | -87.5% | BC7比でさらに半分（-50%）。alphaは失われる |
| Android / iOS（ASTC 4x4、alphaの有無によらず） | ASTC 4x4 | 1 B | 11.9 MB -> 2.97 MB | 2.97 MB -> 0.74 MB | -75% | |
