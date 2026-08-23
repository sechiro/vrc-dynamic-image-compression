using UdonSharp;
using UnityEngine;
using VRC.SDK3.Image;
using VRC.SDKBase;

/// <summary>
/// Minimal call-site migration example for DRCompressedImageDownloader.
/// Assign the facade prefab instance, URL, and destination Material in Inspector,
/// then invoke BeginDownload from another Udon event or a UI button.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DRCompressedImageDownloaderExample : UdonSharpBehaviour
{
    [SerializeField] private DRCompressedImageDownloader downloader;
    [SerializeField] private VRCUrl imageUrl;
    [SerializeField] private Material destinationMaterial;
    [SerializeField] private string materialProperty = "_MainTex";

    // The facade writes this variable immediately before either callback event.
    [HideInInspector] public DRCompressedImageDownload DRImageDownloadResult;

    private DRCompressedImageDownload _currentRequest;
    private int _currentRequestId;

    public void BeginDownload()
    {
        if (downloader == null || imageUrl == null)
        {
            Debug.LogError("[DRCompressedImageDownloaderExample] Setup is incomplete.");
            return;
        }

        // This minimal sample owns one pooled handle at a time. Starting again
        // without releasing it would lose the old handle and eventually exhaust
        // the prefab's handle pool.
        if (_currentRequest != null && _currentRequest.IsAllocated)
        {
            Debug.LogWarning(
                "[DRCompressedImageDownloaderExample] Release the current request first.");
            return;
        }

        TextureInfo textureInfo = new TextureInfo();
        textureInfo.GenerateMipMaps = false;
        textureInfo.FilterMode = FilterMode.Bilinear;
        textureInfo.WrapModeU = TextureWrapMode.Clamp;
        textureInfo.WrapModeV = TextureWrapMode.Clamp;
        textureInfo.WrapModeW = TextureWrapMode.Clamp;
        textureInfo.AnisoLevel = 0;
        textureInfo.MaterialProperty = materialProperty;

        DRCompressedImageDownload startedRequest = downloader.DownloadImage(
            imageUrl,
            destinationMaterial,
            this,
            textureInfo);

        if (startedRequest == null)
        {
            Debug.LogError("[DRCompressedImageDownloaderExample] Request rejected: "
                + downloader.LastServiceError);
            return;
        }

        // An immediate failure callback is allowed to release this pooled
        // handle before DownloadImage returns.
        if (!startedRequest.IsAllocated)
        {
            return;
        }

        _currentRequest = startedRequest;
        _currentRequestId = startedRequest.RequestId;
    }

    public void OnCompressedImageLoadSuccess()
    {
        if (DRImageDownloadResult == null)
        {
            Debug.LogError("[DRCompressedImageDownloaderExample] Success result missing.");
            return;
        }

        _currentRequest = DRImageDownloadResult;
        _currentRequestId = _currentRequest.RequestId;
        Debug.Log("[DRCompressedImageDownloaderExample] Complete format="
            + _currentRequest.CompressionFormat
            + " compressed=" + _currentRequest.IsCompressed
            + " fallback=" + _currentRequest.UsedFallback
            + " edgePadding=" + _currentRequest.UsedEdgePadding
            + " contentUvScale=" + _currentRequest.ContentUvScale);
    }

    public void OnCompressedImageLoadError()
    {
        if (DRImageDownloadResult == null)
        {
            Debug.LogError("[DRCompressedImageDownloaderExample] Failure result missing.");
            return;
        }

        DRCompressedImageDownload failedRequest = DRImageDownloadResult;
        int failedRequestId = failedRequest.RequestId;
        Debug.LogError("[DRCompressedImageDownloaderExample] "
            + failedRequest.ErrorMessage
            + " compression=" + failedRequest.CompressionErrorCode);

        failedRequest.DisposeIfCurrent(failedRequestId);
        if (_currentRequest == failedRequest)
        {
            _currentRequest = null;
            _currentRequestId = 0;
        }
        DRImageDownloadResult = null;
    }

    public void DisposeCurrentRequest()
    {
        if (_currentRequest != null)
        {
            _currentRequest.DisposeIfCurrent(_currentRequestId);
            _currentRequest = null;
            _currentRequestId = 0;
        }
        DRImageDownloadResult = null;
    }

    private void OnDestroy()
    {
        DisposeCurrentRequest();
    }
}
