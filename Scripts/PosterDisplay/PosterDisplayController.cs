using UdonSharp;
using UnityEngine;
using VRC.SDK3.Image;
using VRC.SDKBase;
using HatagoWorks.DynamicImageCompression;

/// <summary>
/// Shows one poster image on a board Renderer. The image is downloaded and
/// block-compressed through DRCompressedImageDownloader, so only the final
/// texture (BC1 / BC7 / ASTC, or the original on fallback) is installed.
/// Supported poster patterns: 2048x1448 and 1024x724 (both 4px aligned).
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PosterDisplayController : UdonSharpBehaviour
{
    [Header("Poster")]
    [SerializeField] private VRCUrl posterUrl;
    [Tooltip("Expected pixel size of the poster image. Patterns: 2048x1448 or 1024x724. A mismatch is only warned about; the image is still shown stretched to the board.")]
    [SerializeField] private int expectedWidth = 2048;
    [SerializeField] private int expectedHeight = 1448;

    [Header("Board")]
    [SerializeField] private Renderer boardRenderer;
    [SerializeField] private string materialProperty = "_MainTex";
    [Tooltip("Use boardRenderer.material (a per-renderer instance) so duplicated boards never share one image. Off uses sharedMaterial.")]
    [SerializeField] private bool useMaterialInstance = true;
    [Tooltip("Physical width of the board in meters. The height follows the expected aspect ratio.")]
    [SerializeField] private float boardWidthMeters = 2f;
    [SerializeField] private Transform boardTransform;
    [SerializeField] private Transform frameTransform;
    [SerializeField] private float frameMarginMeters = 0.05f;
    [SerializeField] private bool applyBoardSizeOnStart = true;

    [Header("Encoding Policy")]
    [Tooltip("The board shader ignores alpha, so Windows encodes the poster as BC1 (4 bpp) even when the image has an alpha channel.")]
    [SerializeField] private bool discardAlpha = true;
    [Tooltip("When the downloaded image is exactly 2x or 4x the expected size, the encoder box-averages it down to the expected size. One source URL can then serve both poster patterns.")]
    [SerializeField] private bool downscaleToExpectedSize = true;

    [Header("Loading")]
    [Tooltip("Optional when a PosterDisplayManager drives this board; the manager injects its downloader.")]
    [SerializeField] private DRCompressedImageDownloader downloader;
    [Tooltip("Load from Start without a manager. Leave off when a PosterDisplayManager sequences several boards.")]
    [SerializeField] private bool loadOnStart;
    [SerializeField] private int maxRetries = 2;
    [Tooltip("VRChat allows one image download every 5 seconds, so retries must wait at least that long.")]
    [SerializeField] private float retryDelaySeconds = 6f;
    [SerializeField] private bool verboseLogging = true;

    // The facade writes the result handle here immediately before each callback.
    [HideInInspector] public DRCompressedImageDownload DRImageDownloadResult;
    [HideInInspector] public bool IsLoading;
    [HideInInspector] public bool IsLoaded;
    [HideInInspector] public string LastError;
    [HideInInspector] public string LastFormat;
    [HideInInspector] public bool LastUsedFallback;
    [HideInInspector] public int LastImageWidth;
    [HideInInspector] public int LastImageHeight;
    [HideInInspector] public int LastSizeInMemoryBytes;
    [HideInInspector] public string LastBackend;
    [HideInInspector] public int LastDownloadedWidth;
    [HideInInspector] public int LastDownloadedHeight;
    [HideInInspector] public int LastDownscaleDivisor = 1;
    [HideInInspector] public float LastEncodeMilliseconds;

    private PosterDisplayManager _manager;
    private PosterDisplayDebugPanel _debugPanel;
    private DRCompressedImageDownload _request;
    private int _requestId;
    private int _retryCount;
    private bool _retryScheduled;
    private Material _targetMaterial;

    private void Start()
    {
        if (applyBoardSizeOnStart)
        {
            ApplyBoardSize();
        }
        if (loadOnStart)
        {
            BeginLoad();
        }
    }

    public void SetDownloader(DRCompressedImageDownloader value)
    {
        downloader = value;
    }

    public void SetManager(PosterDisplayManager value)
    {
        _manager = value;
    }

    public void SetDebugPanel(PosterDisplayDebugPanel value)
    {
        _debugPanel = value;
    }

    public VRCUrl GetPosterUrl()
    {
        return posterUrl;
    }

    /// <summary>
    /// Replaces the URL used by the next BeginLoad. Does not start a load.
    /// </summary>
    public void SetPosterUrl(VRCUrl value)
    {
        posterUrl = value;
    }

    public int GetExpectedWidth()
    {
        return expectedWidth;
    }

    public int GetExpectedHeight()
    {
        return expectedHeight;
    }

    /// <summary>
    /// Scales the board quad to boardWidthMeters x (width / aspect) and the
    /// frame to the board plus margin. Called from Start when enabled; safe
    /// to call again after changing the size fields.
    /// </summary>
    public void ApplyBoardSize()
    {
        if (expectedWidth <= 0 || expectedHeight <= 0 || boardWidthMeters <= 0f)
        {
            return;
        }

        float boardHeight = boardWidthMeters * expectedHeight / expectedWidth;
        if (boardTransform != null)
        {
            boardTransform.localScale = new Vector3(boardWidthMeters, boardHeight, 1f);
        }
        if (frameTransform != null)
        {
            Vector3 frameScale = frameTransform.localScale;
            frameTransform.localScale = new Vector3(
                boardWidthMeters + frameMarginMeters * 2f,
                boardHeight + frameMarginMeters * 2f,
                frameScale.z);
        }
    }

    /// <summary>
    /// Starts (or restarts) the download. Any previous result is released
    /// first, so the board shows nothing until the new image is ready.
    /// </summary>
    public void BeginLoad()
    {
        if (IsLoading)
        {
            LogWarning("Already loading.");
            return;
        }

        ReleaseRequest();
        IsLoaded = false;
        LastError = "";
        LastFormat = "";
        LastUsedFallback = false;
        _retryCount = 0;

        if (downloader == null || posterUrl == null || posterUrl.Get() == "" || boardRenderer == null)
        {
            LastError = "SetupIncomplete";
            LogError("Setup is incomplete (downloader / posterUrl / boardRenderer).");
            NotifyManager(false);
            return;
        }

        IsLoading = true;
        StartRequest();
    }

    /// <summary>
    /// Releases the current image. The board material loses its texture.
    /// </summary>
    public void Unload()
    {
        ReleaseRequest();
        IsLoading = false;
        IsLoaded = false;
    }

    private void StartRequest()
    {
        Material material = GetTargetMaterial();
        if (material == null)
        {
            ScheduleRetryOrFail("BoardMaterialMissing");
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

        // Per-request policy. The facade captures these when DownloadImage
        // is accepted, so another board changing them later cannot affect
        // this request.
        downloader.SetForceBc1(discardAlpha);
        downloader.SetTargetSize(
            downscaleToExpectedSize ? expectedWidth : 0,
            downscaleToExpectedSize ? expectedHeight : 0);

        DRCompressedImageDownload request = downloader.DownloadImage(
            posterUrl,
            material,
            this,
            textureInfo);

        if (request == null)
        {
            // Synchronous rejection: no callback will follow.
            ScheduleRetryOrFail(downloader.LastServiceError);
            return;
        }

        // A failure callback may already have released the handle before
        // DownloadImage returned; that path went through OnCompressedImageLoadError.
        if (!request.IsAllocated)
        {
            return;
        }

        _request = request;
        _requestId = request.RequestId;
    }

    public void OnCompressedImageLoadSuccess()
    {
        DRCompressedImageDownload result = DRImageDownloadResult;
        if (result == null)
        {
            ScheduleRetryOrFail("SuccessResultMissing");
            return;
        }

        _request = result;
        _requestId = result.RequestId;
        LastFormat = result.CompressionFormat;
        LastUsedFallback = result.UsedFallback;
        LastImageWidth = result.OriginalWidth;
        LastImageHeight = result.OriginalHeight;
        LastSizeInMemoryBytes = result.SizeInMemoryBytes;
        LastBackend = result.CompressionBackend;
        LastDownloadedWidth = result.DownloadedWidth;
        LastDownloadedHeight = result.DownloadedHeight;
        LastDownscaleDivisor = result.DownscaleDivisor;
        LastEncodeMilliseconds = result.CompressionDurationMilliseconds;
        IsLoading = false;
        IsLoaded = true;
        _retryCount = 0;
        PanelLog("loaded " + LastFormat + (LastUsedFallback ? "(fallback " + result.CompressionErrorCode + ")" : "")
            + " " + LastImageWidth + "x" + LastImageHeight
            + (LastDownscaleDivisor > 1 ? " /" + LastDownscaleDivisor : "")
            + " " + LastSizeInMemoryBytes + "B " + LastEncodeMilliseconds.ToString("F0") + "ms");

        if (LastImageWidth != expectedWidth || LastImageHeight != expectedHeight)
        {
            LogWarning("Poster size mismatch: got " + LastImageWidth + "x" + LastImageHeight
                + ", expected " + expectedWidth + "x" + expectedHeight + ". Shown stretched to the board.");
        }
        if (verboseLogging)
        {
            Debug.Log("[PosterDisplay] " + gameObject.name + " loaded "
                + LastImageWidth + "x" + LastImageHeight
                + (result.DownscaleDivisor > 1
                    ? " (downloaded " + result.DownloadedWidth + "x" + result.DownloadedHeight
                        + ", 1/" + result.DownscaleDivisor + ")"
                    : "")
                + " format=" + LastFormat
                + " fallback=" + LastUsedFallback
                + (LastUsedFallback ? " reason=" + result.CompressionErrorCode : "")
                + " bytes=" + LastSizeInMemoryBytes);
        }

        NotifyManager(true);
    }

    public void OnCompressedImageLoadError()
    {
        DRCompressedImageDownload result = DRImageDownloadResult;
        string error = "Unknown";
        if (result != null)
        {
            error = result.ErrorMessage;
            if (result.CompressionErrorCode != null && result.CompressionErrorCode != "")
            {
                error = error + " (" + result.CompressionErrorCode + ")";
            }
            // Error handles still occupy a pool slot until released.
            result.DisposeIfCurrent(result.RequestId);
        }
        DRImageDownloadResult = null;
        _request = null;
        _requestId = 0;

        ScheduleRetryOrFail(error);
    }

    public void RetryLoad()
    {
        _retryScheduled = false;
        if (!IsLoading)
        {
            return;
        }

        StartRequest();
    }

    private void ScheduleRetryOrFail(string error)
    {
        LastError = error == null ? "" : error;
        if (_retryCount < maxRetries)
        {
            _retryCount++;
            PanelLog("failed: " + LastError + " -> retry " + _retryCount + "/" + maxRetries);
            LogWarning("Load failed (" + LastError + "); retry " + _retryCount + "/" + maxRetries
                + " in " + retryDelaySeconds + "s.");
            if (!_retryScheduled)
            {
                _retryScheduled = true;
                SendCustomEventDelayedSeconds(nameof(RetryLoad), retryDelaySeconds);
            }
            return;
        }

        IsLoading = false;
        IsLoaded = false;
        PanelLog("gave up: " + LastError);
        LogError("Load failed after " + _retryCount + " retries: " + LastError);
        NotifyManager(false);
    }

    private void ReleaseRequest()
    {
        if (_request != null)
        {
            _request.DisposeIfCurrent(_requestId);
            _request = null;
            _requestId = 0;
        }
        DRImageDownloadResult = null;
    }

    private void PanelLog(string message)
    {
        if (_debugPanel != null)
        {
            _debugPanel.Log(gameObject.name + ": " + message);
        }
    }

    private void NotifyManager(bool succeeded)
    {
        if (_manager != null)
        {
            _manager.OnPosterLoadFinished(this, succeeded);
        }
    }

    private Material GetTargetMaterial()
    {
        if (_targetMaterial == null && boardRenderer != null)
        {
            _targetMaterial = useMaterialInstance
                ? boardRenderer.material
                : boardRenderer.sharedMaterial;
        }
        return _targetMaterial;
    }

    private void OnDestroy()
    {
        ReleaseRequest();
    }

    private void LogWarning(string message)
    {
        if (verboseLogging)
        {
            Debug.LogWarning("[PosterDisplay] " + gameObject.name + ": " + message);
        }
    }

    private void LogError(string message)
    {
        Debug.LogError("[PosterDisplay] " + gameObject.name + ": " + message);
    }
}
