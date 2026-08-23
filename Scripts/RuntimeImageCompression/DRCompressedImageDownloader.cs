using UdonSharp;
using UnityEngine;
using VRC.SDK3.Image;
using VRC.SDKBase;

/// <summary>
/// Library-shaped facade around VRCImageDownloader. It downloads without
/// touching the target Material, performs platform block compression (Windows:
/// BC1 for opaque sources / BC7 otherwise, Android / iOS: ASTC 4x4), then
/// atomically installs either the compressed result or the retained original.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DRCompressedImageDownloader : UdonSharpBehaviour
{
    [Header("Platform Encoders")]
    [SerializeField] private RuntimeBc7EncoderController bc7Encoder;
    [SerializeField] private RuntimeAstc4x4EncoderController astcEncoder;

    [Header("Preallocated Request Handles")]
    [SerializeField] private DRCompressedImageDownload[] requestHandles;

    [Header("Callback Contract")]
    [SerializeField] private string callbackResultVariable = "DRImageDownloadResult";
    [SerializeField] private string successEventName = "OnCompressedImageLoadSuccess";
    [SerializeField] private string failureEventName = "OnCompressedImageLoadError";

    [Header("Windows Format Selection")]
    [Tooltip("Encode alpha-less sources (RGB24 / RGB48 / RGB565) as BC1 (DXT1, 4 bits per pixel) instead of BC7 (8 bits per pixel). Sources with alpha always use BC7.")]
    [SerializeField] private bool preferBc1ForOpaqueSources = true;
    [Tooltip("Encode every Windows source as BC1, discarding alpha even when the source has an alpha channel. Use for content whose shader ignores alpha. SetForceBc1 overrides this per request.")]
    [SerializeField] private bool forceBc1DiscardAlpha;

    [Header("Downscale")]
    [Tooltip("Target size for the encoded texture. When the downloaded image is exactly 2x or 4x this size in both dimensions, the encoder box-averages it down to the target. 0 disables. SetTargetSize overrides this per request.")]
    [SerializeField] private int targetWidth;
    [SerializeField] private int targetHeight;

    [Header("Experimental: BC7 / BC1 Non-aligned Dimensions")]
    [Tooltip("Experimental. Edge-pad BC7 / BC1 inputs whose width or height is not a multiple of 4. Keep disabled to return those downloads uncompressed.")]
    [SerializeField] private bool enableBc7EdgePadding;

    [Header("Fallback")]
    [Tooltip("Fallback after a compression failure. The experimental BC7 padding-disabled policy still returns the original as a successful result when this is false.")]
    [SerializeField] private bool allowUncompressedFallback = true;
    [Tooltip("Seconds allowed for GPU encode plus readback before the request is failed and the worker is reset. Without this, one lost readback callback would reject every later request.")]
    [SerializeField] private float compressionTimeoutSeconds = 15f;
    [Tooltip("Seconds allowed for the native download (no success / error callback) before the request is failed with DownloadTimeout. 0 disables.")]
    [SerializeField] private float downloadTimeoutSeconds = 45f;
    [Tooltip("Diagnostics only. Skip GPU compression and return every download as an uncompressed success (CompressionErrorCode = CompressionBypassed). Use on a device build to confirm that download and display work before debugging the encoder.")]
    [SerializeField] private bool bypassCompressionForDiagnostics;
    [Tooltip("Diagnostics only. Hand the destination Material to VRCImageDownloader instead of null, so the native client installs the uncompressed image itself before compression runs. Use to test whether a client rejects material-less downloads.")]
    [SerializeField] private bool passMaterialToNativeDownloader;
    [SerializeField] private bool verboseLogging = true;

    [HideInInspector] public DRCompressedImageDownload ActiveRequest;
    [HideInInspector] public string LastServiceError;

    // Diagnostics readable directly from the heap (valid even if this
    // behaviour has halted on an exception): poll ticks and the number of
    // native callbacks that reached the entry of each handler.
    [HideInInspector] public int HeartbeatTick;
    [HideInInspector] public int NativeSuccessCallbacks;
    [HideInInspector] public int NativeErrorCallbacks;

    // Download polling only refreshes Progress; it does not need every frame.
    private const int DownloadProgressPollFrames = 5;

    private VRCImageDownloader _nativeDownloader;
    private bool _isEncoding;
    private bool _disposeRequestedDuringEncode;
    private bool _downloadPollScheduled;
    private bool _isDisposed;
    private int _nextRequestId;
    private float _encodeStartedAt;
    private bool _activeForceBc1;
    private int _activeTargetWidth;
    private int _activeTargetHeight;
    private int _activeDownloadedWidth;
    private int _activeDownloadedHeight;
    private int _activeDownscaleDivisor = 1;

    private void Start()
    {
        EnsureDownloader();
    }

    /// <summary>
    /// Experimental. When enabled, non-4-aligned BC7 inputs are edge-padded and
    /// receive content-UV correction. The safe default returns them uncompressed.
    /// </summary>
    public void SetBc7EdgePaddingEnabled(bool value)
    {
        if (ActiveRequest != null || IsSelectedEncoderBusy())
        {
            LogWarning("Cannot change BC7 edge-padding policy while a request is active.");
            return;
        }

        enableBc7EdgePadding = value;
    }

    public bool GetBc7EdgePaddingEnabled()
    {
        return enableBc7EdgePadding;
    }

    /// <summary>
    /// When enabled, Windows encodes alpha-less sources as BC1 (4 bpp) and
    /// keeps BC7 (8 bpp) for sources that carry alpha.
    /// </summary>
    public void SetPreferBc1ForOpaqueSources(bool value)
    {
        if (ActiveRequest != null || IsSelectedEncoderBusy())
        {
            LogWarning("Cannot change the BC1 policy while a request is active.");
            return;
        }

        preferBc1ForOpaqueSources = value;
    }

    public bool GetPreferBc1ForOpaqueSources()
    {
        return preferBc1ForOpaqueSources;
    }

    /// <summary>
    /// Windows: encode the next requests as BC1 regardless of the source
    /// format, discarding alpha. The BC1 encoder reads RGB only, so no
    /// intermediate conversion happens. Rejected while a request is active.
    /// </summary>
    public void SetForceBc1(bool value)
    {
        if (ActiveRequest != null || IsSelectedEncoderBusy())
        {
            LogWarning("Cannot change the BC1 policy while a request is active.");
            return;
        }

        forceBc1DiscardAlpha = value;
    }

    public bool GetForceBc1()
    {
        return forceBc1DiscardAlpha;
    }

    public bool GetBypassCompressionForDiagnostics()
    {
        return bypassCompressionForDiagnostics;
    }

    public bool GetPassMaterialToNativeDownloader()
    {
        return passMaterialToNativeDownloader;
    }

    public RuntimeAstc4x4EncoderController GetAstcEncoder()
    {
        return astcEncoder;
    }

    public RuntimeBc7EncoderController GetBc7Encoder()
    {
        return bc7Encoder;
    }

    /// <summary>
    /// Requests that the encoded texture be width x height when the download
    /// is exactly 2x or 4x that size in both dimensions (box average in the
    /// encoder shader). Other sizes are encoded unchanged. 0 disables.
    /// Rejected while a request is active.
    /// </summary>
    public void SetTargetSize(int width, int height)
    {
        if (ActiveRequest != null || IsSelectedEncoderBusy())
        {
            LogWarning("Cannot change the target size while a request is active.");
            return;
        }

        targetWidth = width;
        targetHeight = height;
    }

    /// <summary>
    /// Near-replacement for VRCImageDownloader.DownloadImage. The four argument
    /// meanings are preserved; only the return type and callback event differ.
    /// This PoC serializes requests and returns null while another is pending.
    /// </summary>
    public DRCompressedImageDownload DownloadImage(
        VRCUrl url,
        Material material,
        UdonSharpBehaviour udonBehaviour,
        TextureInfo textureInfo)
    {
        LastServiceError = "";

        if (_isDisposed)
        {
            LastServiceError = "ServiceDisposed";
            LogWarning(LastServiceError);
            return null;
        }
        if (ActiveRequest != null)
        {
            LastServiceError = "RequestAlreadyInFlight";
            LogWarning(LastServiceError);
            return null;
        }
        if (IsSelectedEncoderBusy())
        {
            LastServiceError = "CompressionBackendBusy";
            LogWarning(LastServiceError);
            return null;
        }
        if (url == null)
        {
            LastServiceError = "UrlMissing";
            LogWarning(LastServiceError);
            return null;
        }

        DRCompressedImageDownload request = FindAvailableRequestHandle();
        if (request == null)
        {
            LastServiceError = "RequestHandlePoolExhausted";
            LogWarning(LastServiceError);
            return null;
        }

        TextureInfo effectiveTextureInfo = CopyTextureInfo(textureInfo);
        string materialProperty = effectiveTextureInfo.MaterialProperty;
        if (materialProperty == null || materialProperty == "")
        {
            materialProperty = "_MainTex";
        }
        if (material != null && !material.HasProperty(materialProperty))
        {
            LastServiceError = "MaterialPropertyMissing";
            LogWarning(LastServiceError);
            return null;
        }

        request.Prepare(
            url,
            material,
            udonBehaviour,
            effectiveTextureInfo,
            materialProperty,
            this,
            GetNextRequestId());
        ActiveRequest = request;
        _isEncoding = false;
        _disposeRequestedDuringEncode = false;
        // Policy values are captured per request so later setter calls cannot
        // change a request that is already in flight.
        _activeForceBc1 = forceBc1DiscardAlpha;
        _activeTargetWidth = targetWidth;
        _activeTargetHeight = targetHeight;
        _activeDownloadedWidth = 0;
        _activeDownloadedHeight = 0;
        _activeDownscaleDivisor = 1;

        EnsureDownloader();
        IVRCImageDownload nativeDownload = _nativeDownloader.DownloadImage(
            url,
            passMaterialToNativeDownloader ? material : null,
            this,
            effectiveTextureInfo);
        request.AttachNativeDownload(nativeDownload);

        if (nativeDownload == null)
        {
            CompleteActiveError(
                VRCImageDownloadError.Unknown,
                "NativeDownloadStartFailed",
                "NativeDownloadStartFailed");
            return request;
        }

        ScheduleDownloadPoll();
        return request;
    }

    public override void OnImageLoadSuccess(IVRCImageDownload result)
    {
        NativeSuccessCallbacks++;
        if (ActiveRequest == null)
        {
            LogWarning("Ignoring an unexpected native success callback.");
            if (result != null)
            {
                result.Dispose();
            }
            return;
        }
        if (result == null)
        {
            CompleteActiveError(
                VRCImageDownloadError.Unknown,
                "NativeSuccessResultMissing",
                "NativeSuccessResultMissing");
            return;
        }
        if (result != ActiveRequest.GetNativeDownload())
        {
            LogWarning("Ignoring a stale native success callback.");
            result.Dispose();
            return;
        }
        if (_isEncoding)
        {
            LogWarning("Ignoring a duplicate native success callback.");
            return;
        }

        Texture2D sourceTexture = result == null ? null : result.Result;
        if (sourceTexture == null)
        {
            HandleCompressionFailure("DownloadedTextureMissing");
            return;
        }

        if (ActiveRequest.TextureInfo != null && ActiveRequest.TextureInfo.GenerateMipMaps)
        {
            HandleCompressionFailure("MipmapsNotSupported");
            return;
        }

        if (bypassCompressionForDiagnostics)
        {
            CompleteActiveFallback("CompressionBypassed");
            return;
        }

        if (IsSourceFormatWithoutGain(sourceTexture.format))
        {
            CompleteActiveFallback("SourceFormatHasNoGain");
            return;
        }

        _activeDownloadedWidth = sourceTexture.width;
        _activeDownloadedHeight = sourceTexture.height;
        _activeDownscaleDivisor = GetDownscaleDivisor(sourceTexture.width, sourceTexture.height);
        int encodeWidth = sourceTexture.width / _activeDownscaleDivisor;
        int encodeHeight = sourceTexture.height / _activeDownscaleDivisor;

        ActiveRequest.Phase = "Encoding";
        ActiveRequest.Progress = 1f;
        _isEncoding = true;
        _encodeStartedAt = Time.realtimeSinceStartup;

        if (IsMobileAstcBuildTarget())
        {
            if (astcEncoder == null)
            {
                HandleCompressionFailure("AstcEncoderMissing");
                return;
            }

            if (astcEncoder.IsBusy)
            {
                HandleCompressionFailure("AstcEncoderBusy");
                return;
            }

            astcEncoder.ClearEncodedTexture();
            astcEncoder.SetDownscaleDivisor(_activeDownscaleDivisor);
            astcEncoder.SetSourceTexture(sourceTexture);
            astcEncoder.BeginEncode();
        }
        else if (IsWindowsBuildTarget())
        {
            if (bc7Encoder == null)
            {
                HandleCompressionFailure("Bc7EncoderMissing");
                return;
            }

            if (bc7Encoder.IsBusy)
            {
                HandleCompressionFailure("Bc7EncoderBusy");
                return;
            }

            if (RequiresBlockEdgePadding(encodeWidth, encodeHeight) && !enableBc7EdgePadding)
            {
                CompleteActiveFallback("Bc7EdgePaddingDisabledByPolicy");
                return;
            }

            bc7Encoder.ClearEncodedTexture();
            bc7Encoder.SetAllowEdgePadding(enableBc7EdgePadding);
            bc7Encoder.SetUseBc1(
                _activeForceBc1
                || (preferBc1ForOpaqueSources && IsOpaqueSourceFormat(sourceTexture.format)));
            bc7Encoder.SetDownscaleDivisor(_activeDownscaleDivisor);
            bc7Encoder.SetSourceTexture(sourceTexture);
            bc7Encoder.BeginEncode();
        }
        else
        {
            HandleCompressionFailure("UnsupportedBuildTarget");
            return;
        }

        SendCustomEventDelayedFrames(nameof(PollCompression), 1);
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
        NativeErrorCallbacks++;
        if (ActiveRequest == null)
        {
            LogWarning("Ignoring an unexpected native failure callback.");
            if (result != null)
            {
                result.Dispose();
            }
            return;
        }
        if (result != null && result != ActiveRequest.GetNativeDownload())
        {
            LogWarning("Ignoring a stale native failure callback.");
            result.Dispose();
            return;
        }
        if (_isEncoding)
        {
            LogWarning("Ignoring a duplicate native failure callback during encoding.");
            return;
        }

        VRCImageDownloadError error = result == null
            ? VRCImageDownloadError.Unknown
            : result.Error;
        string message = result == null
            ? "NativeDownloadFailed"
            : result.ErrorMessage;
        CompleteActiveError(error, message, "NativeDownloadFailed");
    }

    public void PollActiveRequest()
    {
        _downloadPollScheduled = false;
        HeartbeatTick++;
        if (ActiveRequest == null || _isEncoding)
        {
            return;
        }

        ActiveRequest.RefreshNativeProgress();

        if (downloadTimeoutSeconds > 0f
            && Time.realtimeSinceStartup - ActiveRequest.RequestStartedAt > downloadTimeoutSeconds)
        {
            LogWarning("Native download produced no callback within " + downloadTimeoutSeconds + "s.");
            CompleteActiveError(
                VRCImageDownloadError.Unknown,
                "DownloadTimeout",
                "DownloadTimeout");
            return;
        }

        ScheduleDownloadPoll();
    }

    private void ScheduleDownloadPoll()
    {
        if (_downloadPollScheduled)
        {
            return;
        }

        _downloadPollScheduled = true;
        SendCustomEventDelayedFrames(nameof(PollActiveRequest), DownloadProgressPollFrames);
    }

    public void PollCompression()
    {
        HeartbeatTick++;
        if (ActiveRequest == null || !_isEncoding)
        {
            return;
        }

        if (IsMobileAstcBuildTarget())
        {
            if (astcEncoder != null && astcEncoder.IsBusy)
            {
                if (HasCompressionTimedOut())
                {
                    astcEncoder.AbortEncode("CompressionTimeout");
                    if (_disposeRequestedDuringEncode)
                    {
                        FinishDeferredDispose();
                        return;
                    }
                    HandleCompressionFailure("CompressionTimeout");
                    return;
                }

                SendCustomEventDelayedFrames(nameof(PollCompression), 1);
                return;
            }
            if (_disposeRequestedDuringEncode)
            {
                FinishDeferredDispose();
                return;
            }
            if (astcEncoder == null || !astcEncoder.LastEncodeSucceeded)
            {
                HandleCompressionFailure(astcEncoder == null
                    ? "AstcEncoderMissing"
                    : astcEncoder.LastError);
                return;
            }

            Texture2D encodedTexture = astcEncoder.TakeEncodedTextureOwnership();
            if (encodedTexture == null)
            {
                HandleCompressionFailure("EncodedTextureMissing");
                return;
            }
            CompleteActiveCompressed(
                encodedTexture,
                "ASTC_4x4",
                astcEncoder.LastBackend,
                astcEncoder.LastEncodedByteCount,
                encodedTexture.width,
                encodedTexture.height,
                astcEncoder.LastDurationMilliseconds);
            return;
        }

        if (IsWindowsBuildTarget())
        {
            if (bc7Encoder != null && bc7Encoder.IsBusy)
            {
                if (HasCompressionTimedOut())
                {
                    bc7Encoder.AbortEncode("CompressionTimeout");
                    if (_disposeRequestedDuringEncode)
                    {
                        FinishDeferredDispose();
                        return;
                    }
                    HandleCompressionFailure("CompressionTimeout");
                    return;
                }

                SendCustomEventDelayedFrames(nameof(PollCompression), 1);
                return;
            }
            if (_disposeRequestedDuringEncode)
            {
                FinishDeferredDispose();
                return;
            }
            if (bc7Encoder == null || !bc7Encoder.LastEncodeSucceeded)
            {
                HandleCompressionFailure(bc7Encoder == null
                    ? "Bc7EncoderMissing"
                    : bc7Encoder.LastError);
                return;
            }

            Texture2D encodedTexture = bc7Encoder.TakeEncodedTextureOwnership();
            CompleteActiveCompressed(
                encodedTexture,
                bc7Encoder.LastUsedBc1 ? "BC1" : "BC7",
                bc7Encoder.LastBackend,
                bc7Encoder.LastEncodedByteCount,
                bc7Encoder.LastSourceWidth,
                bc7Encoder.LastSourceHeight,
                bc7Encoder.LastDurationMilliseconds);
            return;
        }

        HandleCompressionFailure("UnsupportedBuildTarget");
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;

        if (ActiveRequest != null && _isEncoding)
        {
            _disposeRequestedDuringEncode = true;
            ActiveRequest.State = VRCImageDownloadState.Unloaded;
            ActiveRequest.Phase = "DisposePending";
            ActiveRequest.IsDisposePending = true;
            return;
        }

        DisposeResourcesNow();
    }

    public bool RequestHandleDispose(DRCompressedImageDownload request)
    {
        if (request == null || ActiveRequest != request)
        {
            return false;
        }

        if (_isEncoding)
        {
            _disposeRequestedDuringEncode = true;
            return true;
        }

        ActiveRequest = null;
        _isEncoding = false;
        _disposeRequestedDuringEncode = false;
        return false;
    }

    private void DisposeResourcesNow()
    {
        ActiveRequest = null;
        _isEncoding = false;
        _disposeRequestedDuringEncode = false;

        if (requestHandles != null)
        {
            for (int i = 0; i < requestHandles.Length; i++)
            {
                if (requestHandles[i] != null)
                {
                    requestHandles[i].DisposeFromService();
                }
            }
        }

        if (_nativeDownloader != null)
        {
            _nativeDownloader.Dispose();
            _nativeDownloader = null;
        }
    }

    private void OnDestroy()
    {
        _isDisposed = true;
        DisposeResourcesNow();
    }

    private void EnsureDownloader()
    {
        if (!_isDisposed && _nativeDownloader == null)
        {
            _nativeDownloader = new VRCImageDownloader();
        }
    }

    private DRCompressedImageDownload FindAvailableRequestHandle()
    {
        if (requestHandles == null)
        {
            return null;
        }

        for (int i = 0; i < requestHandles.Length; i++)
        {
            DRCompressedImageDownload request = requestHandles[i];
            if (request != null && !request.IsAllocated)
            {
                return request;
            }
        }

        return null;
    }

    public void ReleasePriorMaterialAssignments(
        DRCompressedImageDownload incomingRequest)
    {
        if (incomingRequest == null
            || incomingRequest.Material == null
            || requestHandles == null)
        {
            return;
        }

        for (int i = 0; i < requestHandles.Length; i++)
        {
            DRCompressedImageDownload request = requestHandles[i];
            if (request != null
                && request != incomingRequest
                && request.IsAllocated
                && request.TargetsMaterial(
                    incomingRequest.Material,
                    incomingRequest.MaterialProperty))
            {
                request.ReleaseMaterialAssignmentForReplacement();
            }
        }
    }

    private int GetNextRequestId()
    {
        _nextRequestId++;
        if (_nextRequestId <= 0)
        {
            _nextRequestId = 1;
        }
        return _nextRequestId;
    }

    private bool IsSelectedEncoderBusy()
    {
        if (IsMobileAstcBuildTarget())
        {
            return astcEncoder != null && astcEncoder.IsBusy;
        }
        if (IsWindowsBuildTarget())
        {
            return bc7Encoder != null && bc7Encoder.IsBusy;
        }
        return false;
    }

    private TextureInfo CopyTextureInfo(TextureInfo source)
    {
        TextureInfo result = new TextureInfo();
        if (source != null)
        {
            result.FilterMode = source.FilterMode;
            result.WrapModeU = source.WrapModeU;
            result.WrapModeV = source.WrapModeV;
            result.WrapModeW = source.WrapModeW;
            result.AnisoLevel = source.AnisoLevel;
            result.MaterialProperty = source.MaterialProperty;
            result.GenerateMipMaps = source.GenerateMipMaps;
        }
        return result;
    }

    private void CompleteActiveCompressed(
        Texture2D encodedTexture,
        string format,
        string backend,
        int byteCount,
        int sourceWidth,
        int sourceHeight,
        float durationMilliseconds)
    {
        if (encodedTexture == null)
        {
            HandleCompressionFailure("EncodedTextureMissing");
            return;
        }

        DRCompressedImageDownload completedRequest = ActiveRequest;
        completedRequest.CompleteCompressed(
            encodedTexture,
            format,
            backend,
            byteCount,
            sourceWidth,
            sourceHeight,
            _activeDownloadedWidth,
            _activeDownloadedHeight,
            _activeDownscaleDivisor,
            durationMilliseconds);
        ActiveRequest = null;
        _isEncoding = false;
        _disposeRequestedDuringEncode = false;
        Notify(completedRequest, true);
    }

    private void HandleCompressionFailure(string errorCode)
    {
        if (ActiveRequest == null)
        {
            return;
        }

        if (allowUncompressedFallback)
        {
            CompleteActiveFallback(errorCode);
            return;
        }

        CompleteActiveError(
            VRCImageDownloadError.Unknown,
            "CompressionFailed: " + errorCode,
            errorCode);
    }

    private void CompleteActiveFallback(string errorCode)
    {
        DRCompressedImageDownload completedRequest = ActiveRequest;
        if (completedRequest == null)
        {
            return;
        }

        completedRequest.CompleteFallback(errorCode);
        bool fallbackSucceeded = completedRequest.State == VRCImageDownloadState.Complete;
        ActiveRequest = null;
        _isEncoding = false;
        _disposeRequestedDuringEncode = false;
        Notify(completedRequest, fallbackSucceeded);
    }

    private void CompleteActiveError(
        VRCImageDownloadError error,
        string message,
        string compressionErrorCode)
    {
        DRCompressedImageDownload completedRequest = ActiveRequest;
        if (completedRequest == null)
        {
            return;
        }

        completedRequest.CompleteError(error, message, compressionErrorCode);
        ActiveRequest = null;
        _isEncoding = false;
        _disposeRequestedDuringEncode = false;
        Notify(completedRequest, false);
    }

    private void FinishDeferredDispose()
    {
        Texture2D discardedTexture = null;
        if (IsMobileAstcBuildTarget()
            && astcEncoder != null
            && astcEncoder.LastEncodeSucceeded)
        {
            discardedTexture = astcEncoder.TakeEncodedTextureOwnership();
        }
        else if (IsWindowsBuildTarget()
            && bc7Encoder != null
            && bc7Encoder.LastEncodeSucceeded)
        {
            discardedTexture = bc7Encoder.TakeEncodedTextureOwnership();
        }

        if (discardedTexture != null)
        {
            UnityEngine.Object.Destroy(discardedTexture);
        }

        DRCompressedImageDownload disposedRequest = ActiveRequest;
        ActiveRequest = null;
        _isEncoding = false;
        _disposeRequestedDuringEncode = false;
        if (disposedRequest != null)
        {
            disposedRequest.CompleteDeferredDispose();
        }

        if (_isDisposed)
        {
            DisposeResourcesNow();
        }
    }

    private void Notify(DRCompressedImageDownload request, bool succeeded)
    {
        if (request == null)
        {
            return;
        }

        if (request.UdonBehaviour == null)
        {
            // Nobody can release a handle whose receiver is gone, so the
            // service must, or the pool slot and its texture leak for good.
            LogWarning("Receiver was destroyed before completion; releasing the handle.");
            request.DisposeFromService();
            return;
        }

        if (callbackResultVariable != null && callbackResultVariable != "")
        {
            request.UdonBehaviour.SetProgramVariable(callbackResultVariable, (object)request);
        }

        string eventName = succeeded ? successEventName : failureEventName;
        if (eventName != null && eventName != "")
        {
            request.UdonBehaviour.SendCustomEvent(eventName);
        }
    }

    /// <summary>
    /// One-line state of the worker selected for this build target, for
    /// in-world diagnostics on devices without a Console.
    /// </summary>
    public string GetBackendDiagnostics()
    {
        if (IsMobileAstcBuildTarget())
        {
            if (astcEncoder == null)
            {
                return "ASTC worker missing";
            }
            return "ASTC probe=" + astcEncoder.TransportProbeCompleted
                + " rowsReversed=" + astcEncoder.TransportRowsReversed
                + " probeErr=" + (astcEncoder.LastTransportProbeError == null || astcEncoder.LastTransportProbeError == "" ? "-" : astcEncoder.LastTransportProbeError)
                + " backend=" + (astcEncoder.LastBackend == "" ? "-" : astcEncoder.LastBackend)
                + " busy=" + astcEncoder.IsBusy
                + " lastErr=" + (astcEncoder.LastError == null || astcEncoder.LastError == "" ? "-" : astcEncoder.LastError)
                + " lastMs=" + astcEncoder.LastDurationMilliseconds.ToString("F0");
        }
        if (IsWindowsBuildTarget())
        {
            if (bc7Encoder == null)
            {
                return "BC7/BC1 worker missing";
            }
            return "BC7/BC1 backend=" + (bc7Encoder.LastBackend == "" ? "-" : bc7Encoder.LastBackend)
                + " bc1=" + bc7Encoder.LastUsedBc1
                + " srgb=" + bc7Encoder.LastOutputSrgb
                + " busy=" + bc7Encoder.IsBusy
                + " lastErr=" + (bc7Encoder.LastError == null || bc7Encoder.LastError == "" ? "-" : bc7Encoder.LastError)
                + " lastMs=" + bc7Encoder.LastDurationMilliseconds.ToString("F0");
        }
        return "unsupported build target";
    }

    private bool IsWindowsBuildTarget()
    {
#if UNITY_STANDALONE_WIN
        return true;
#else
        return false;
#endif
    }

    private bool HasCompressionTimedOut()
    {
        return compressionTimeoutSeconds > 0f
            && Time.realtimeSinceStartup - _encodeStartedAt > compressionTimeoutSeconds;
    }

    /// <summary>
    /// VRChat loads alpha-less images as RGB24 (RGB48 for 16-bit sources).
    /// BC1 stores 4 bits per pixel and no alpha, so it is only chosen for
    /// these formats. Greyscale R8 / R16 stay out: BC1 would reduce them,
    /// but its RGB565 endpoints tint neutral greys.
    /// </summary>
    private bool IsOpaqueSourceFormat(TextureFormat format)
    {
        return format == TextureFormat.RGB24
            || format == TextureFormat.RGB48
            || format == TextureFormat.RGB565;
    }

    /// <summary>
    /// BC7 and ASTC 4x4 both store 8 bits per pixel. VRChat loads greyscale
    /// images as R8 and may hand back already block-compressed textures, and
    /// re-encoding those costs quality without saving memory. R8 is also kept
    /// out of the BC1 path on purpose (see IsOpaqueSourceFormat).
    /// </summary>
    private bool IsSourceFormatWithoutGain(TextureFormat format)
    {
        return format == TextureFormat.R8
            || format == TextureFormat.Alpha8
            || format == TextureFormat.DXT1
            || format == TextureFormat.DXT5
            || format == TextureFormat.BC4
            || format == TextureFormat.BC5
            || format == TextureFormat.BC6H
            || format == TextureFormat.BC7
            || format == TextureFormat.ETC_RGB4
            || format == TextureFormat.ETC2_RGB
            || format == TextureFormat.ETC2_RGBA8
            || format == TextureFormat.ASTC_4x4
            || format == TextureFormat.ASTC_5x5
            || format == TextureFormat.ASTC_6x6
            || format == TextureFormat.ASTC_8x8
            || format == TextureFormat.ASTC_10x10
            || format == TextureFormat.ASTC_12x12;
    }

    private bool RequiresBlockEdgePadding(int width, int height)
    {
        return width < 4
            || height < 4
            || (width & 3) != 0
            || (height & 3) != 0;
    }

    /// <summary>
    /// 2 or 4 when the download is exactly that multiple of the requested
    /// target size in both dimensions; otherwise 1 (no downscale).
    /// </summary>
    private int GetDownscaleDivisor(int width, int height)
    {
        if (_activeTargetWidth <= 0
            || _activeTargetHeight <= 0
            || width <= _activeTargetWidth
            || height <= _activeTargetHeight
            || width % _activeTargetWidth != 0
            || height % _activeTargetHeight != 0)
        {
            return 1;
        }

        int divisor = width / _activeTargetWidth;
        if (divisor != height / _activeTargetHeight)
        {
            return 1;
        }
        return divisor == 2 || divisor == 4 ? divisor : 1;
    }

    private bool IsMobileAstcBuildTarget()
    {
#if UNITY_ANDROID || UNITY_IOS
        return true;
#else
        return false;
#endif
    }

    private void LogWarning(string message)
    {
        if (verboseLogging)
        {
            Debug.LogWarning("[DRCompressedImageDownloader] " + message);
        }
    }
}
