using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// PC proof-of-concept that encodes a Texture2D into BC7 Mode 6, or into
/// BC1 (DXT1) for alpha-less sources when UseBc1 is set.
/// Non-block-aligned dimensions can be edge-padded to a complete 4x4 block.
/// The source remains owned by the caller; this component owns only EncodedTexture.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class RuntimeBc7EncoderController : UdonSharpBehaviour
{
    private const int MaximumEncodedDimension = 16384;

    // Every loop here runs on the Udon VM at roughly microseconds per
    // iteration, so the payload must never be walked block by block.
    private const int BlockValidationSampleCount = 64;

    // Shader pass layout: 0 = BC7 RInt, 1 = BC7 ARGB32, 2 = BC1 RInt, 3 = BC1 ARGB32.
    private const int Bc1PassOffset = 2;

    [Header("Required")]
    [SerializeField] private Material encoderMaterial;
    [SerializeField] private Texture2D sourceTexture;
    [Tooltip("1 = encode at source resolution. 2 or 4 = box-average that many source texels per encoded texel in the shader (no intermediate texture). Applied only when both source dimensions divide evenly.")]
    [SerializeField] private int downscaleDivisor = 1;

    [Header("Optional Output")]
    [SerializeField] private Material outputMaterial;
    [SerializeField] private string outputTextureProperty = "_MainTex";
    [SerializeField] private UdonBehaviour completionReceiver;
    [SerializeField] private string successEventName = "OnRuntimeBc7EncodeSuccess";
    [SerializeField] private string failureEventName = "OnRuntimeBc7EncodeFailure";

    [Header("Encoding")]
    [SerializeField] private bool encodeOnStart;
    [SerializeField] private bool preferArgb32Backend;
    [SerializeField] private bool enableArgb32Fallback = true;
    [Tooltip("Encode as BC1 (DXT1, 4 bits per pixel, no alpha) instead of BC7. The Facade sets this per request for opaque sources; alpha in the source is discarded.")]
    [SerializeField] private bool useBc1;
    [Tooltip("GPU work is spread over frames in strips of block rows so that one frame never encodes more than this many 4x4 blocks. 0 encodes everything in one frame.")]
    [SerializeField] private int maxBlocksPerFrame = 16384;
    [Tooltip("Adaptive strips. When > 0, encoding starts at Adaptive Initial Blocks Per Frame; the strip shrinks (x0.7) while the running average frame time during the encode exceeds (idle frame average before the encode + this budget) and grows (x1.3) while it stays under half the budget. Max Blocks Per Frame is the ceiling. 0 = fixed strips of Max Blocks Per Frame.")]
    [SerializeField] private float adaptiveFrameBudgetMilliseconds = 3f;
    [SerializeField] private int adaptiveInitialBlocksPerFrame = 2048;
    [SerializeField] private int adaptiveMinBlocksPerFrame = 1024;
    [Header("Experimental Dimensions")]
    [Tooltip("Experimental. Prefer source dimensions that are both multiples of 4.")]
    [SerializeField] private bool allowEdgePadding;
    [Header("Encoding Behavior")]
    [Tooltip("Store RGB as sRGB when the source is an sRGB texture. Storing linear values in 8 bits collapses the dark range (sRGB 0-50 becomes about 8 steps).")]
    [SerializeField] private bool outputSrgb = true;
    [SerializeField] private bool flipSourceVertically;
    [SerializeField] private float alphaErrorWeight = 1f;
    [SerializeField] private bool verboseLogging = true;

    [HideInInspector] public Texture2D EncodedTexture;
    [HideInInspector] public bool IsBusy;
    [HideInInspector] public bool LastEncodeSucceeded;
    [HideInInspector] public string LastError;
    [HideInInspector] public string LastBackend;
    [HideInInspector] public int LastEncodedByteCount;
    [HideInInspector] public float LastDurationMilliseconds;
    [HideInInspector] public int LastSourceWidth;
    [HideInInspector] public int LastSourceHeight;
    [HideInInspector] public int LastEncodedWidth;
    [HideInInspector] public int LastEncodedHeight;
    [HideInInspector] public bool LastUsedEdgePadding;
    [HideInInspector] public bool LastOutputSrgb;
    [HideInInspector] public int LastDownscaleDivisor;
    [HideInInspector] public int LastStripCount;
    [HideInInspector] public int LastMinBlocksPerFrame;
    [HideInInspector] public int LastMaxBlocksPerFrame;
    [HideInInspector] public float LastBaselineFrameMs;
    [HideInInspector] public float LastWorstStripFrameMs;
    [HideInInspector] public float LastMicrosecondsPerBlock;
    [HideInInspector] public bool LastUsedBc1;

    private RenderTexture _blockRenderTexture;
    private bool _outputSrgbActive;
    private int _downscaleActive = 1;
    private bool _bc1Active;
    private Texture2D _activeSourceTexture;
    private Texture2D _ownedEncodedTexture;
    private byte[] _encodedBytes;
    private bool _usingArgb32Backend;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _encodedWidth;
    private int _encodedHeight;
    private int _renderTextureWidth;
    private int _expectedByteCount;
    private float _encodeStartedAt;
    private int _totalBlockRows;
    private int _stripRowsPerFrame;
    private int _currentBlocksPerFrame;
    private int _stripBlockWidth = 1;
    private int _stripTotalRows;
    private float _baselineFrameMs;
    private float _frameMsAverage;
    private int _framesAtFloor;
    private float _idleFrameMsAverage;
    private int _lastIssuedBlocks;
    private float _msPerBlock;
    private int _nextStripStartRow;
    private bool _stripEventScheduled;

    private void Start()
    {
        // Per-block cost estimate persists across encodes so the second
        // board (and every reload) starts at a strip size that already fits.
        _msPerBlock = GetInitialMicrosecondsPerBlock() / 1000f;
        LastMicrosecondsPerBlock = GetInitialMicrosecondsPerBlock();

        if (encodeOnStart)
        {
            BeginEncode();
        }
    }

    // Mobile tile-based GPUs hide strip cost behind deep pipelining until the
    // queue saturates, so they start from a conservative assumption and let
    // the measurements relax it; desktop starts near its measured value.
    private float GetInitialMicrosecondsPerBlock()
    {
#if UNITY_ANDROID || UNITY_IOS
        return 1.0f;
#else
        return 0.05f;
#endif
    }

    // Tracks the normal frame time while idle so adaptive strips have a
    // baseline that strips themselves cannot distort.
    private void Update()
    {
        if (IsBusy)
        {
            return;
        }

        float frameMs = Time.unscaledDeltaTime * 1000f;
        if (_idleFrameMsAverage <= 0f)
        {
            _idleFrameMsAverage = frameMs;
        }
        else
        {
            _idleFrameMsAverage += (frameMs - _idleFrameMsAverage) * 0.05f;
        }
    }

    public void SetSourceTexture(Texture2D value)
    {
        if (IsBusy)
        {
            LogWarning("Cannot replace the source texture while a request is in flight.");
            return;
        }

        sourceTexture = value;
    }

    /// <summary>
    /// 1, 2 or 4. The shader averages divisor x divisor source texels per
    /// encoded texel, so the result is source / divisor in each dimension.
    /// Ignored (treated as 1) when the source does not divide evenly.
    /// </summary>
    public void SetDownscaleDivisor(int value)
    {
        if (IsBusy)
        {
            LogWarning("Cannot change the downscale divisor while a request is in flight.");
            return;
        }

        downscaleDivisor = value;
    }

    /// <summary>
    /// Experimental. Direct callers should prefer source dimensions that are
    /// both multiples of four; the Facade owns display-UV correction.
    /// </summary>
    public void SetAllowEdgePadding(bool value)
    {
        if (IsBusy)
        {
            LogWarning("Cannot change edge-padding policy while a request is in flight.");
            return;
        }

        allowEdgePadding = value;
    }

    /// <summary>
    /// Selects BC1 (DXT1, 4 bpp) instead of BC7 for the next encode. BC1
    /// has no alpha channel, so callers must only enable it for opaque
    /// sources. The dimension rules are the same as for BC7.
    /// </summary>
    public void SetUseBc1(bool value)
    {
        if (IsBusy)
        {
            LogWarning("Cannot change the target format while a request is in flight.");
            return;
        }

        useBc1 = value;
    }

    public bool GetUseBc1()
    {
        return useBc1;
    }

    public void BeginEncode()
    {
        if (IsBusy)
        {
            LogWarning("An encode request is already in flight.");
            return;
        }

        LastEncodeSucceeded = false;
        LastError = "";
        LastBackend = "";
        LastEncodedByteCount = 0;
        LastDurationMilliseconds = 0f;
        LastSourceWidth = 0;
        LastSourceHeight = 0;
        LastEncodedWidth = 0;
        LastEncodedHeight = 0;
        LastUsedEdgePadding = false;
        LastOutputSrgb = false;
        LastUsedBc1 = false;
        _encodeStartedAt = 0f;

        if (!IsSupportedBuildTarget())
        {
            Fail("UnsupportedBuildTarget");
            return;
        }

        if (encoderMaterial == null)
        {
            Fail("EncoderMaterialMissing");
            return;
        }
        if (sourceTexture == null)
        {
            Fail("SourceTextureMissing");
            return;
        }
        if (outputMaterial != null
            && outputTextureProperty != null
            && outputTextureProperty != ""
            && !outputMaterial.HasProperty(outputTextureProperty))
        {
            Fail("OutputTexturePropertyMissing");
            return;
        }

        _activeSourceTexture = sourceTexture;
        _sourceWidth = _activeSourceTexture.width;
        _sourceHeight = _activeSourceTexture.height;
        _downscaleActive = 1;
        if ((downscaleDivisor == 2 || downscaleDivisor == 4)
            && _sourceWidth % downscaleDivisor == 0
            && _sourceHeight % downscaleDivisor == 0)
        {
            _downscaleActive = downscaleDivisor;
            _sourceWidth /= _downscaleActive;
            _sourceHeight /= _downscaleActive;
        }
        LastDownscaleDivisor = _downscaleActive;
        LastSourceWidth = _sourceWidth;
        LastSourceHeight = _sourceHeight;
        _outputSrgbActive = outputSrgb && _activeSourceTexture.isDataSRGB;
        LastOutputSrgb = _outputSrgbActive;
        _bc1Active = useBc1;
        LastUsedBc1 = _bc1Active;

        // Unity cannot create a top-level BC7 / DXT1 resource unless both
        // dimensions are at least one full block and divisible by four. The
        // shader can clamp out-of-range texels to the source edge when
        // padding is enabled.
        bool requiresEdgePadding = _sourceWidth < 4
            || _sourceHeight < 4
            || (_sourceWidth & 3) != 0
            || (_sourceHeight & 3) != 0;
        if (requiresEdgePadding && !allowEdgePadding)
        {
            Fail("UnsupportedDimensions");
            return;
        }
        if (_activeSourceTexture.mipmapCount != 1)
        {
            Fail("MipmapsNotSupportedByProofOfConcept");
            return;
        }

        int encodedBlockWidth = _sourceWidth / 4;
        int encodedBlockHeight = _sourceHeight / 4;
        if ((_sourceWidth & 3) != 0)
        {
            encodedBlockWidth++;
        }
        if ((_sourceHeight & 3) != 0)
        {
            encodedBlockHeight++;
        }
        if (encodedBlockWidth < 1)
        {
            encodedBlockWidth = 1;
        }
        if (encodedBlockHeight < 1)
        {
            encodedBlockHeight = 1;
        }

        if (encodedBlockWidth > 536870911
            || encodedBlockHeight > 536870911
            || encodedBlockWidth > MaximumEncodedDimension / 4
            || encodedBlockHeight > MaximumEncodedDimension / 4)
        {
            Fail("PaddedDimensionsTooLarge");
            return;
        }

        _encodedWidth = encodedBlockWidth * 4;
        _encodedHeight = encodedBlockHeight * 4;
        if (_encodedHeight > 2147483647 / _encodedWidth)
        {
            Fail("EncodedByteCountTooLarge");
            return;
        }

        LastEncodedWidth = _encodedWidth;
        LastEncodedHeight = _encodedHeight;
        LastUsedEdgePadding = _encodedWidth != _sourceWidth || _encodedHeight != _sourceHeight;

        // BC7 stores 16 bytes per block (four 32-bit words, one per render
        // target pixel); BC1 stores 8 bytes per block (two words).
        if (_bc1Active)
        {
            _expectedByteCount = _encodedWidth * _encodedHeight / 2;
            _renderTextureWidth = encodedBlockWidth * 2;
        }
        else
        {
            _expectedByteCount = _encodedWidth * _encodedHeight;
            _renderTextureWidth = encodedBlockWidth * 4;
        }

        _encodeStartedAt = Time.realtimeSinceStartup;
        IsBusy = true;

        if (!TryStartBackend(preferArgb32Backend))
        {
            if (!preferArgb32Backend && enableArgb32Fallback && TryStartBackend(true))
            {
                return;
            }

            Fail("RenderTextureCreateFailed");
        }
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        if (!IsBusy)
        {
            LogWarning("Ignoring a readback callback while idle.");
            return;
        }

        if (!request.done
            || request.hasError
            || request.width != _renderTextureWidth
            || request.height != _encodedHeight / 4
            || request.layerDataSize != _expectedByteCount)
        {
            bool canRetry = !_usingArgb32Backend && enableArgb32Fallback;
            ReleaseBlockRenderTexture();
            _encodedBytes = null;
            if (canRetry)
            {
                LogWarning("RInt readback failed; retrying with ARGB32.");
                if (TryStartBackend(true))
                {
                    return;
                }
                Fail("RenderTextureCreateFailed");
                return;
            }

            Fail(request.hasError ? "AsyncGpuReadbackFailed" : "UnexpectedReadbackShape");
            return;
        }

        _encodedBytes = new byte[request.layerDataSize];
        if (!request.TryGetData(_encodedBytes, 0))
        {
            bool canRetry = !_usingArgb32Backend && enableArgb32Fallback;
            ReleaseBlockRenderTexture();
            _encodedBytes = null;
            if (canRetry)
            {
                LogWarning("RInt data copy failed; retrying with ARGB32.");
                if (TryStartBackend(true))
                {
                    return;
                }
                Fail("RenderTextureCreateFailed");
                return;
            }

            Fail("TryGetDataFailed");
            return;
        }

        // Every BC7 block starts with the Mode 6 prefix and every BC1 block
        // is written with color0 > color1 (or a flat block). This inexpensive
        // check catches common channel/order/conversion failures.
        if (!HasValidBlockLayout())
        {
            bool canRetry = !_usingArgb32Backend && enableArgb32Fallback;
            ReleaseBlockRenderTexture();
            _encodedBytes = null;
            if (canRetry)
            {
                LogWarning("RInt byte layout check failed; retrying with ARGB32.");
                if (TryStartBackend(true))
                {
                    return;
                }
                Fail("RenderTextureCreateFailed");
                return;
            }

            Fail("EncodedBlockLayoutInvalid");
            return;
        }

        ReleaseBlockRenderTexture();

        Texture2D newTexture = new Texture2D(
            _encodedWidth,
            _encodedHeight,
            _bc1Active ? TextureFormat.DXT1 : TextureFormat.BC7,
            false,
            !_outputSrgbActive);

        newTexture.filterMode = _activeSourceTexture.filterMode == FilterMode.Point
            ? FilterMode.Point
            : FilterMode.Bilinear;
        newTexture.wrapModeU = _activeSourceTexture.wrapModeU;
        newTexture.wrapModeV = _activeSourceTexture.wrapModeV;
        newTexture.wrapModeW = _activeSourceTexture.wrapModeW;
        newTexture.anisoLevel = 0;
        newTexture.LoadRawTextureData(_encodedBytes);
        newTexture.Apply(false, true);

        Texture2D previousTexture = _ownedEncodedTexture;
        _ownedEncodedTexture = newTexture;
        EncodedTexture = newTexture;

        if (outputMaterial != null && outputTextureProperty != null && outputTextureProperty != "")
        {
            outputMaterial.SetTexture(outputTextureProperty, EncodedTexture);
        }

        if (previousTexture != null)
        {
            UnityEngine.Object.Destroy(previousTexture);
        }

        LastEncodedByteCount = _encodedBytes.Length;
        _encodedBytes = null;
        LastDurationMilliseconds = (Time.realtimeSinceStartup - _encodeStartedAt) * 1000f;
        LastBackend = _usingArgb32Backend ? "ARGB32" : "RInt";
        LastEncodeSucceeded = true;
        IsBusy = false;
        _activeSourceTexture = null;

        if (verboseLogging)
        {
            Debug.Log("[RuntimeBc7Encoder] Success "
                + "format=" + (_bc1Active ? "BC1" : "BC7")
                + " source=" + _sourceWidth + "x" + _sourceHeight
                + " encoded=" + _encodedWidth + "x" + _encodedHeight
                + " padded=" + LastUsedEdgePadding
                + " bytes=" + LastEncodedByteCount
                + " backend=" + LastBackend
                + " elapsedMs=" + LastDurationMilliseconds);
        }

        NotifyCompletion(successEventName);
    }

    /// <summary>
    /// Drops the in-flight request so the worker can accept a new one. A
    /// readback already issued for the dropped request may still complete
    /// later; the idle guard in OnAsyncGpuReadbackComplete discards it.
    /// </summary>
    public void AbortEncode(string reason)
    {
        if (!IsBusy)
        {
            return;
        }

        Fail(reason == null || reason == "" ? "Aborted" : reason);
    }

    public void ClearEncodedTexture()
    {
        if (IsBusy)
        {
            LogWarning("Cannot clear the encoded texture while a request is in flight.");
            return;
        }

        DetachOwnedOutputTexture();
        if (_ownedEncodedTexture != null)
        {
            UnityEngine.Object.Destroy(_ownedEncodedTexture);
            _ownedEncodedTexture = null;
        }
        EncodedTexture = null;
    }

    /// <summary>
    /// Transfers the latest compressed texture to a higher-level owner.
    /// The caller becomes responsible for destroying the returned texture.
    /// </summary>
    public Texture2D TakeEncodedTextureOwnership()
    {
        if (IsBusy || _ownedEncodedTexture == null)
        {
            return null;
        }

        DetachOwnedOutputTexture();
        Texture2D result = _ownedEncodedTexture;
        _ownedEncodedTexture = null;
        EncodedTexture = null;
        return result;
    }

    private void OnDestroy()
    {
        ReleaseBlockRenderTexture();
        DetachOwnedOutputTexture();
        if (_ownedEncodedTexture != null)
        {
            UnityEngine.Object.Destroy(_ownedEncodedTexture);
            _ownedEncodedTexture = null;
        }
        EncodedTexture = null;
        _encodedBytes = null;
        _activeSourceTexture = null;
    }

    private bool TryStartBackend(bool useArgb32)
    {
        _usingArgb32Backend = useArgb32;
        RenderTextureFormat renderTextureFormat = useArgb32
            ? RenderTextureFormat.ARGB32
            : RenderTextureFormat.RInt;

        _blockRenderTexture = new RenderTexture(
            _renderTextureWidth,
            _encodedHeight / 4,
            0,
            renderTextureFormat,
            RenderTextureReadWrite.Linear);

        if (!_blockRenderTexture.Create() || !_blockRenderTexture.IsCreated())
        {
            ReleaseBlockRenderTexture();
            return false;
        }

        encoderMaterial.SetVector(
            "_SourceSize",
            new Vector4(
                _sourceWidth,
                _sourceHeight,
                1f / _sourceWidth,
                1f / _sourceHeight));
        encoderMaterial.SetFloat("_AlphaErrorWeight", alphaErrorWeight);
        encoderMaterial.SetFloat("_FlipSourceY", flipSourceVertically ? 1f : 0f);
        encoderMaterial.SetFloat("_OutputSrgb", _outputSrgbActive ? 1f : 0f);
        encoderMaterial.SetFloat("_SourceDownscale", _downscaleActive);

        _totalBlockRows = _encodedHeight / 4;
        BeginStripSchedule(_encodedWidth / 4, _totalBlockRows);
        _nextStripStartRow = 0;
        BlitNextStrip();
        return true;
    }

    public void ContinueEncodeStrips()
    {
        _stripEventScheduled = false;
        if (!IsBusy || _blockRenderTexture == null)
        {
            return;
        }

        // After the last strip this tick only feeds that strip's frame time
        // into the cost model; the readback is already in flight.
        if (_nextStripStartRow >= _totalBlockRows)
        {
            AdaptStripSize();
            return;
        }

        AdaptStripSize();
        BlitNextStrip();
    }

    private void BlitNextStrip()
    {
        LastStripCount++;
        int stripEndRow = _nextStripStartRow + _stripRowsPerFrame;
        int issuedRows = stripEndRow > _stripTotalRows ? _stripTotalRows - _nextStripStartRow : _stripRowsPerFrame;
        _lastIssuedBlocks = issuedRows * _stripBlockWidth;
        if (stripEndRow > _totalBlockRows)
        {
            stripEndRow = _totalBlockRows;
        }

        encoderMaterial.SetVector(
            "_StripRange",
            new Vector4(_nextStripStartRow, stripEndRow, 0f, 0f));
        VRCGraphics.Blit(
            _activeSourceTexture,
            _blockRenderTexture,
            encoderMaterial,
            GetEncoderPass());
        _nextStripStartRow = stripEndRow;

        if (_nextStripStartRow >= _totalBlockRows)
        {
            VRCAsyncGPUReadback.Request(_blockRenderTexture, 0, this);
            if (!_stripEventScheduled)
            {
                _stripEventScheduled = true;
                SendCustomEventDelayedFrames(nameof(ContinueEncodeStrips), 1);
            }
            return;
        }

        if (!_stripEventScheduled)
        {
            _stripEventScheduled = true;
            SendCustomEventDelayedFrames(nameof(ContinueEncodeStrips), 1);
        }
    }

    private int GetEncoderPass()
    {
        int pass = _usingArgb32Backend ? 1 : 0;
        if (_bc1Active)
        {
            pass += Bc1PassOffset;
        }
        return pass;
    }

    private void BeginStripSchedule(int blockWidth, int totalBlockRows)
    {
        _stripBlockWidth = blockWidth < 1 ? 1 : blockWidth;
        _stripTotalRows = totalBlockRows;
        int cap = GetBlockCap();
        if (adaptiveFrameBudgetMilliseconds > 0f)
        {
            int initial = adaptiveInitialBlocksPerFrame < 1 ? 1 : adaptiveInitialBlocksPerFrame;
            _currentBlocksPerFrame = initial > cap ? cap : initial;
        }
        else
        {
            _currentBlocksPerFrame = cap;
        }

        _baselineFrameMs = _idleFrameMsAverage;
        _frameMsAverage = _idleFrameMsAverage;
        _framesAtFloor = 0;
        _lastIssuedBlocks = 0;
        LastMicrosecondsPerBlock = _msPerBlock * 1000f;
        LastStripCount = 0;
        LastMinBlocksPerFrame = _currentBlocksPerFrame;
        LastMaxBlocksPerFrame = _currentBlocksPerFrame;
        LastBaselineFrameMs = 0f;
        LastWorstStripFrameMs = 0f;
        _stripRowsPerFrame = GetStripRowsPerFrame(_currentBlocksPerFrame);
    }

    // Mobile tile-based GPUs are several times slower per block and hide
    // strip cost behind deep pipelining, so frame-time feedback alone lets
    // one oversized strip through before it reacts. A hard ceiling keeps
    // the worst strip inside one frame (about 6 ms on Quest 2 for ASTC).
    private const int MobileBlocksPerFrameCap = 4096;

    private int GetBlockCap()
    {
        int cap = maxBlocksPerFrame > 0 ? maxBlocksPerFrame : 1073741824;
#if UNITY_ANDROID || UNITY_IOS
        if (cap > MobileBlocksPerFrameCap)
        {
            cap = MobileBlocksPerFrameCap;
        }
#endif
        return cap;
    }

    private int GetStripRowsPerFrame(int blocksPerFrame)
    {
        if (maxBlocksPerFrame <= 0 && adaptiveFrameBudgetMilliseconds <= 0f)
        {
            return _stripTotalRows;
        }

        int rows = blocksPerFrame / _stripBlockWidth;
        return rows < 1 ? 1 : rows;
    }

    // Runs on the frame after a strip was issued. With vsync, a strip that
    // does not fit the GPU frame delays the next present, which shows up as
    // a longer Time.unscaledDeltaTime; that is the feedback signal. The
    // shortest frame seen during the encode stands in for the device's
    // normal frame time, so no per-platform target needs configuring.
    private void AdaptStripSize()
    {
        if (adaptiveFrameBudgetMilliseconds <= 0f)
        {
            return;
        }

        float frameMs = Time.unscaledDeltaTime * 1000f;

        // Baseline: the idle-frame average measured by Update() before this
        // encode started (the device's normal frame time under its current
        // load, untouched by strips). Signal: a fast-moving average of the
        // frames during the encode, so one noisy frame does not swing the
        // strip size.
        if (_baselineFrameMs <= 0f)
        {
            _baselineFrameMs = frameMs;
        }
        if (_frameMsAverage <= 0f)
        {
            _frameMsAverage = frameMs;
        }
        else
        {
            _frameMsAverage += (frameMs - _frameMsAverage) * 0.3f;
        }
        LastBaselineFrameMs = _baselineFrameMs;
        if (frameMs > LastWorstStripFrameMs)
        {
            LastWorstStripFrameMs = frameMs;
        }

        // Cost model: the part of this frame above the baseline is attributed
        // to the strip issued last frame. A frame with no measurable excess
        // still pulls the estimate down, so a one-off hitch does not pin the
        // strip size. The model bounds growth so the average-based rule
        // cannot overshoot the budget before it reacts.
        if (_lastIssuedBlocks > 0)
        {
            // Rises quickly (0.3) on measured excess, relaxes slowly (5% per
            // quiet frame). A single frame cannot tell a strip stall from an
            // unrelated hitch (avatar load, editor stall), so the estimate is
            // clamped to a plausible range instead of trusting the worst
            // frame; the platform block cap bounds the damage either way.
            float extraMs = frameMs - _baselineFrameMs;
            if (extraMs > 0.5f)
            {
                float perBlock = extraMs / _lastIssuedBlocks;
                _msPerBlock += (perBlock - _msPerBlock) * 0.3f;
            }
            else
            {
                _msPerBlock *= 0.95f;
            }
            if (_msPerBlock < 0.00001f)
            {
                _msPerBlock = 0.00001f;
            }
            if (_msPerBlock > 0.004f)
            {
                _msPerBlock = 0.004f;
            }
            LastMicrosecondsPerBlock = _msPerBlock * 1000f;
            _lastIssuedBlocks = 0;
        }

        int next = _currentBlocksPerFrame;
        if (_frameMsAverage > _baselineFrameMs + adaptiveFrameBudgetMilliseconds)
        {
            next = _currentBlocksPerFrame * 7 / 10;
        }
        else if (_frameMsAverage < _baselineFrameMs + adaptiveFrameBudgetMilliseconds * 0.5f)
        {
            next = _currentBlocksPerFrame * 13 / 10;
        }
        // The per-block cost estimate is diagnostics only
        // (LastMicrosecondsPerBlock). Using it as a limit was tried and
        // dropped: one unrelated hitch (avatar load, editor stall) inflates
        // it and pins the strip at the floor for seconds. The platform cap
        // bounds the worst strip instead.

        int cap = GetBlockCap();
        int floor = adaptiveMinBlocksPerFrame < 1 ? 1 : adaptiveMinBlocksPerFrame;
        if (next > cap)
        {
            next = cap;
        }
        if (next < floor)
        {
            next = floor;
        }

        // Sitting at the floor for a long stretch means the baseline no
        // longer matches the device (it was captured from an unusually good
        // frame). Re-anchor it to the running average and probe again.
        if (next <= floor)
        {
            _framesAtFloor++;
            if (_framesAtFloor >= 30)
            {
                _baselineFrameMs = _frameMsAverage;
                _framesAtFloor = 0;
            }
        }
        else
        {
            _framesAtFloor = 0;
        }

        _currentBlocksPerFrame = next;
        _stripRowsPerFrame = GetStripRowsPerFrame(next);
        if (next < LastMinBlocksPerFrame)
        {
            LastMinBlocksPerFrame = next;
        }
        if (next > LastMaxBlocksPerFrame)
        {
            LastMaxBlocksPerFrame = next;
        }
    }

    private bool HasValidBlockLayout()
    {
        if (_encodedBytes == null || _encodedBytes.Length != _expectedByteCount)
        {
            return false;
        }

        // Transport faults (channel swizzle, word byte order, sRGB conversion)
        // corrupt every block the same way, so samples spread across the payload
        // including the first and last block are as conclusive as a full sweep.
        int blockBytes = _bc1Active ? 8 : 16;
        int blockCount = _expectedByteCount / blockBytes;
        int sampleCount = blockCount < BlockValidationSampleCount
            ? blockCount
            : BlockValidationSampleCount;
        int lastBlockIndex = blockCount - 1;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            int blockIndex = sampleCount < 2
                ? 0
                : sample * lastBlockIndex / (sampleCount - 1);
            int offset = blockIndex * blockBytes;

            if (_bc1Active)
            {
                // The encoder always orders endpoints as color0 > color1
                // (four-color mode). A flat block has equal endpoints and
                // all-zero indices. Anything else means the bytes were
                // swizzled, byte-swapped, or converted in transit.
                int color0 = _encodedBytes[offset] | (_encodedBytes[offset + 1] << 8);
                int color1 = _encodedBytes[offset + 2] | (_encodedBytes[offset + 3] << 8);
                if (color0 < color1)
                {
                    return false;
                }
                if (color0 == color1
                    && (_encodedBytes[offset + 4]
                        | _encodedBytes[offset + 5]
                        | _encodedBytes[offset + 6]
                        | _encodedBytes[offset + 7]) != 0)
                {
                    return false;
                }
            }
            else if ((_encodedBytes[offset] & 0x7F) != 0x40)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsSupportedBuildTarget()
    {
#if UNITY_STANDALONE_WIN
        return true;
#else
        return false;
#endif
    }

    private void DetachOwnedOutputTexture()
    {
        if (outputMaterial == null
            || outputTextureProperty == null
            || outputTextureProperty == ""
            || _ownedEncodedTexture == null)
        {
            return;
        }

        if (outputMaterial.GetTexture(outputTextureProperty) == _ownedEncodedTexture)
        {
            outputMaterial.SetTexture(outputTextureProperty, null);
        }
    }

    private void ReleaseBlockRenderTexture()
    {
        if (_blockRenderTexture == null)
        {
            return;
        }

        if (_blockRenderTexture.IsCreated())
        {
            _blockRenderTexture.Release();
        }
        UnityEngine.Object.Destroy(_blockRenderTexture);
        _blockRenderTexture = null;
    }

    private void Fail(string reason)
    {
        ReleaseBlockRenderTexture();
        _encodedBytes = null;
        _activeSourceTexture = null;
        LastEncodeSucceeded = false;
        LastError = reason;
        LastDurationMilliseconds = _encodeStartedAt > 0f
            ? (Time.realtimeSinceStartup - _encodeStartedAt) * 1000f
            : 0f;
        IsBusy = false;

        Debug.LogError("[RuntimeBc7Encoder] " + reason);
        NotifyCompletion(failureEventName);
    }

    private void NotifyCompletion(string eventName)
    {
        if (completionReceiver != null && eventName != null && eventName != "")
        {
            completionReceiver.SendCustomEvent(eventName);
        }
    }

    private void LogWarning(string message)
    {
        if (verboseLogging)
        {
            Debug.LogWarning("[RuntimeBc7Encoder] " + message);
        }
    }
}
