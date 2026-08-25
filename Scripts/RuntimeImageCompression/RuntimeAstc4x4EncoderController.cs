using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
using VRC.Udon;


namespace HatagoWorks.DynamicImageCompression
{
    /// <summary>
    /// Mobile proof-of-concept that encodes a Texture2D into ASTC 4x4 using one
    /// fixed legal block mode on Android or iOS. The source remains owned by the
    /// caller; this component owns only EncodedTexture.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class RuntimeAstc4x4EncoderController : UdonSharpBehaviour
    {
        [Header("Required")]
        [SerializeField] private Material encoderMaterial;
        [Tooltip("Disabled MeshRenderer holding encoderMaterial. Its per-renderer material instance receives the per-run shader parameters, so an Editor run never dirties the .mat asset. When empty, parameters are written to the .mat directly (old behavior).")]
        [SerializeField] private Renderer encoderMaterialRenderer;
        private Material _runtimeEncoderMaterial;
        [SerializeField] private Texture2D sourceTexture;
        [Tooltip("1 = encode at source resolution. 2 or 4 = box-average that many source texels per encoded texel in the shader (no intermediate texture). Applied only when both source dimensions divide evenly.")]
        [SerializeField] private int downscaleDivisor = 1;

        [Header("Optional Output")]
        [SerializeField] private Material outputMaterial;
        [SerializeField] private string outputTextureProperty = "_MainTex";
        [SerializeField] private UdonBehaviour completionReceiver;
        [SerializeField] private string successEventName = "OnRuntimeAstcEncodeSuccess";
        [SerializeField] private string failureEventName = "OnRuntimeAstcEncodeFailure";

        [Header("Encoding")]
        [SerializeField] private bool encodeOnStart;
        [SerializeField] private bool preferArgb32Backend = true;
        [SerializeField] private bool preferArgb32BackendOnIos;
        [SerializeField] private bool enableAlternateBackendFallback = true;
        [Tooltip("GPU work is spread over frames in strips of block rows so that one frame never encodes more than this many 4x4 blocks. 0 encodes everything in one frame.")]
        [SerializeField] private int maxBlocksPerFrame = 16384;
        [Tooltip("Adaptive strips. When > 0, encoding starts at Adaptive Initial Blocks Per Frame; the strip shrinks (x0.7) while the running average frame time during the encode exceeds (idle frame average before the encode + this budget) and grows (x1.3) while it stays under half the budget. Max Blocks Per Frame is the ceiling. 0 = fixed strips of Max Blocks Per Frame.")]
        [SerializeField] private float adaptiveFrameBudgetMilliseconds = 3f;
        [SerializeField] private int adaptiveInitialBlocksPerFrame = 2048;
        [SerializeField] private int adaptiveMinBlocksPerFrame = 1024;
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
        [HideInInspector] public bool TransportProbeCompleted;
        [HideInInspector] public bool TransportRowsReversed;
        [HideInInspector] public string LastTransportProbeError;
        [HideInInspector] public bool LastOutputSrgb;
        [HideInInspector] public int LastDownscaleDivisor;
        [HideInInspector] public int LastStripCount;
        [HideInInspector] public int LastMinBlocksPerFrame;
        [HideInInspector] public int LastMaxBlocksPerFrame;
        [HideInInspector] public float LastBaselineFrameMs;
        [HideInInspector] public float LastWorstStripFrameMs;
        [HideInInspector] public float LastMicrosecondsPerBlock;

        private const int OperationIdle = 0;
        private const int OperationProbe = 1;
        private const int OperationEncode = 2;

        private const int BackendUnknown = 0;
        private const int BackendRInt = 1;
        private const int BackendArgb32 = 2;

        // Every loop here runs on the Udon VM at roughly microseconds per
        // iteration, so the payload must never be walked block by block.
        private const int BlockValidationSampleCount = 64;

        private RenderTexture _blockRenderTexture;
        private Texture2D _activeSourceTexture;
        private RenderTexture _activeSourceRenderTexture;
        private RenderTexture _pendingSourceRenderTexture;
        private bool _pendingSourceIsSrgb;
        private Texture2D _ownedEncodedTexture;
        private byte[] _encodedBytes;
        private bool _usingArgb32Backend;
        private bool _outputSrgbActive;
        private int _downscaleActive = 1;
        private bool _triedRIntThisRequest;
        private bool _triedArgb32ThisRequest;
        private bool _cachedReadbackRowsReversed;
        private int _operation;
        private int _cachedBackend;
        private int _probingBackend;
        private int _sourceWidth;
        private int _sourceHeight;
        private int _blockWidth;
        private int _blockHeight;
        private int _expectedByteCount;
        private float _encodeStartedAt;
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
            _pendingSourceRenderTexture = null;
        }

        /// <summary>
        /// One-shot RenderTexture source for the next BeginEncode only (takes
        /// precedence over SetSourceTexture). Used by the Facade for sources it
        /// pre-resized on the GPU; the caller keeps ownership of the texture and
        /// must keep it alive until the encode completes. RenderTextures carry no
        /// usable sRGB metadata through Udon, so the caller states it explicitly.
        /// </summary>
        public void SetSourceRenderTexture(RenderTexture value, bool treatAsSrgb)
        {
            if (IsBusy)
            {
                LogWarning("Cannot replace the source texture while a request is in flight.");
                return;
            }

            _pendingSourceRenderTexture = value;
            _pendingSourceIsSrgb = treatAsSrgb;
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
            LastOutputSrgb = false;
            _encodeStartedAt = 0f;
            _triedRIntThisRequest = false;
            _triedArgb32ThisRequest = false;

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
            if (sourceTexture == null && _pendingSourceRenderTexture == null)
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

            bool sourceIsSrgb;
            _activeSourceRenderTexture = _pendingSourceRenderTexture;
            _pendingSourceRenderTexture = null;
            if (_activeSourceRenderTexture != null)
            {
                // Facade-resized source: already at its final size.
                _activeSourceTexture = null;
                _sourceWidth = _activeSourceRenderTexture.width;
                _sourceHeight = _activeSourceRenderTexture.height;
                _downscaleActive = 1;
                sourceIsSrgb = _pendingSourceIsSrgb;
            }
            else
            {
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
                sourceIsSrgb = _activeSourceTexture.isDataSRGB;
            }
            LastDownscaleDivisor = _downscaleActive;
            _outputSrgbActive = outputSrgb && sourceIsSrgb;
            LastOutputSrgb = _outputSrgbActive;

            if (_sourceWidth < 1 || _sourceHeight < 1)
            {
                Fail("UnsupportedDimensions");
                return;
            }

            _blockWidth = (_sourceWidth + 3) / 4;
            _blockHeight = (_sourceHeight + 3) / 4;
            _expectedByteCount = _blockWidth * _blockHeight * 16;
            _encodeStartedAt = Time.realtimeSinceStartup;
            IsBusy = true;

            if (_cachedBackend != BackendUnknown)
            {
                int cachedBackend = _cachedBackend;
                MarkBackendTried(cachedBackend);
                if (TryStartEncodeBackend(cachedBackend))
                {
                    return;
                }

                InvalidateTransportCache(
                    GetBackendName(cachedBackend) + ":RenderTextureCreateFailed");
                if (TryStartAlternateProbe(cachedBackend))
                {
                    return;
                }

                Fail("ByteTransportUnsupported");
                return;
            }

            int preferredBackend = GetPreferredArgb32Backend()
                ? BackendArgb32
                : BackendRInt;
            LastTransportProbeError = "";
            if (!TryStartProbeOrAlternate(preferredBackend))
            {
                Fail("ByteTransportUnsupported");
            }
        }

        public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
        {
            if (!IsBusy)
            {
                LogWarning("Ignoring a readback callback while idle.");
                return;
            }

            if (_operation == OperationProbe)
            {
                HandleTransportProbeReadback(request);
                return;
            }

            if (_operation != OperationEncode)
            {
                Fail("UnexpectedReadbackCallback");
                return;
            }

            HandleEncodeReadback(request);
        }

        private void HandleTransportProbeReadback(VRCAsyncGPUReadbackRequest request)
        {
            int completedBackend = _probingBackend;
            string probeFailure = "";
            byte[] probeBytes = null;

            if (!request.done || request.hasError)
            {
                probeFailure = "AsyncGpuReadbackFailed";
            }
            else if (request.width != 4
                || request.height != 4
                || request.layerDataSize != 64)
            {
                probeFailure = "UnexpectedReadbackShape";
            }
            else
            {
                probeBytes = new byte[64];
                if (!request.TryGetData(probeBytes, 0))
                {
                    probeFailure = "TryGetDataFailed";
                }
            }

            int rowOrder = 0;
            if (probeFailure == "")
            {
                rowOrder = GetTransportProbeRowOrder(probeBytes);
                if (rowOrder == 0)
                {
                    probeFailure = "SentinelMismatch";
                }
            }

            ReleaseBlockRenderTexture();

            if (probeFailure != "")
            {
                LastTransportProbeError = GetBackendName(completedBackend) + ":" + probeFailure;
                LogWarning(LastTransportProbeError + "; trying alternate backend if available.");
                if (TryStartAlternateProbe(completedBackend))
                {
                    return;
                }

                Fail("ByteTransportUnsupported");
                return;
            }

            _cachedBackend = completedBackend;
            _cachedReadbackRowsReversed = rowOrder == 2;
            TransportProbeCompleted = true;
            TransportRowsReversed = _cachedReadbackRowsReversed;

            if (!TryStartEncodeBackend(completedBackend))
            {
                InvalidateTransportCache(
                    GetBackendName(completedBackend) + ":RenderTextureCreateFailed");
                if (TryStartAlternateProbe(completedBackend))
                {
                    return;
                }

                Fail("ByteTransportUnsupported");
            }
        }

        private void HandleEncodeReadback(VRCAsyncGPUReadbackRequest request)
        {
            if (!request.done || request.hasError)
            {
                HandleEncodeTransportFailure("AsyncGpuReadbackFailed");
                return;
            }
            if (request.width != _blockWidth * 4
                || request.height != _blockHeight
                || request.layerDataSize != _expectedByteCount)
            {
                HandleEncodeTransportFailure("UnexpectedReadbackShape");
                return;
            }

            _encodedBytes = new byte[request.layerDataSize];
            if (!request.TryGetData(_encodedBytes, 0))
            {
                HandleEncodeTransportFailure("TryGetDataFailed");
                return;
            }

            if (!HasValidAstcBlocks())
            {
                HandleEncodeTransportFailure("EncodedBlockLayoutInvalid");
                return;
            }

            ReleaseBlockRenderTexture();

            Texture2D newTexture = new Texture2D(
                _sourceWidth,
                _sourceHeight,
                TextureFormat.ASTC_4x4,
                false,
                !_outputSrgbActive);

            if (_activeSourceTexture != null)
            {
                newTexture.filterMode = _activeSourceTexture.filterMode == FilterMode.Point
                    ? FilterMode.Point
                    : FilterMode.Bilinear;
                newTexture.wrapModeU = _activeSourceTexture.wrapModeU;
                newTexture.wrapModeV = _activeSourceTexture.wrapModeV;
                newTexture.wrapModeW = _activeSourceTexture.wrapModeW;
            }
            else
            {
                // RenderTexture sources come from the Facade's resize pass, which
                // always samples bilinear / clamp.
                newTexture.filterMode = FilterMode.Bilinear;
                newTexture.wrapModeU = TextureWrapMode.Clamp;
                newTexture.wrapModeV = TextureWrapMode.Clamp;
                newTexture.wrapModeW = TextureWrapMode.Clamp;
            }
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
            _operation = OperationIdle;
            _activeSourceTexture = null;
            _activeSourceRenderTexture = null;

            if (verboseLogging)
            {
                Debug.Log("[RuntimeAstc4x4Encoder] Success "
                    + _sourceWidth + "x" + _sourceHeight
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

        /// <summary>
        /// Per-run shader parameters go to the renderer's material instance so an
        /// Editor run never dirties the .mat asset (asset Material writes persist
        /// across Play Mode and show up as spurious VCS diffs). Udon exposes no
        /// Material constructor, so the instance comes from Renderer.material on a
        /// disabled MeshRenderer instead. Without that renderer this falls back to
        /// writing the asset directly, which only matters inside the Editor.
        /// </summary>
        private Texture GetActiveSourceForBlit()
        {
            return _activeSourceRenderTexture != null
                ? (Texture)_activeSourceRenderTexture
                : (Texture)_activeSourceTexture;
        }

        private Material GetRuntimeEncoderMaterial()
        {
            if (_runtimeEncoderMaterial == null)
            {
                _runtimeEncoderMaterial = encoderMaterialRenderer != null
                    ? encoderMaterialRenderer.material
                    : encoderMaterial;
            }
            return _runtimeEncoderMaterial;
        }

        private void OnDestroy()
        {
            ReleaseBlockRenderTexture();
            DetachOwnedOutputTexture();
            if (_runtimeEncoderMaterial != null)
            {
                // Only a renderer-owned instance is destroyed; the fallback path
                // aliases the shared asset, which must survive.
                if (_runtimeEncoderMaterial != encoderMaterial)
                {
                    UnityEngine.Object.Destroy(_runtimeEncoderMaterial);
                }
                _runtimeEncoderMaterial = null;
            }
            if (_ownedEncodedTexture != null)
            {
                UnityEngine.Object.Destroy(_ownedEncodedTexture);
                _ownedEncodedTexture = null;
            }
            EncodedTexture = null;
            _encodedBytes = null;
            _activeSourceTexture = null;
            _activeSourceRenderTexture = null;
            _operation = OperationIdle;
        }

        private bool TryStartProbeOrAlternate(int preferredBackend)
        {
            if (TryStartProbeBackend(preferredBackend))
            {
                return true;
            }

            return TryStartAlternateProbe(preferredBackend);
        }

        private bool TryStartAlternateProbe(int failedBackend)
        {
            if (!enableAlternateBackendFallback)
            {
                return false;
            }

            int alternateBackend = failedBackend == BackendArgb32
                ? BackendRInt
                : BackendArgb32;
            if (WasBackendTried(alternateBackend))
            {
                return false;
            }

            return TryStartProbeBackend(alternateBackend);
        }

        private bool TryStartProbeBackend(int backend)
        {
            MarkBackendTried(backend);
            _operation = OperationProbe;
            _probingBackend = backend;
            _usingArgb32Backend = backend == BackendArgb32;

            if (!TryCreateReadbackRenderTexture(4, 4, _usingArgb32Backend))
            {
                LastTransportProbeError = GetBackendName(backend) + ":RenderTextureCreateFailed";
                _operation = OperationIdle;
                return false;
            }

            VRCGraphics.Blit(
                GetActiveSourceForBlit(),
                _blockRenderTexture,
                GetRuntimeEncoderMaterial(),
                _usingArgb32Backend ? 3 : 2);
            VRCAsyncGPUReadback.Request(_blockRenderTexture, 0, this);
            return true;
        }

        private bool TryStartEncodeBackend(int backend)
        {
            _operation = OperationEncode;
            _usingArgb32Backend = backend == BackendArgb32;

            if (!TryCreateReadbackRenderTexture(
                _blockWidth * 4,
                _blockHeight,
                _usingArgb32Backend))
            {
                _operation = OperationIdle;
                return false;
            }

            Material runtimeMaterial = GetRuntimeEncoderMaterial();
            runtimeMaterial.SetVector(
                "_SourceSize",
                new Vector4(
                    _sourceWidth,
                    _sourceHeight,
                    1f / _sourceWidth,
                    1f / _sourceHeight));
            runtimeMaterial.SetFloat("_AlphaErrorWeight", alphaErrorWeight);
            runtimeMaterial.SetFloat("_FlipSourceY", flipSourceVertically ? 1f : 0f);
            runtimeMaterial.SetFloat("_FlipOutputY", _cachedReadbackRowsReversed ? 1f : 0f);
            runtimeMaterial.SetFloat("_OutputSrgb", _outputSrgbActive ? 1f : 0f);
            runtimeMaterial.SetFloat("_SourceDownscale", _downscaleActive);

            BeginStripSchedule(_blockWidth, _blockHeight);
            _nextStripStartRow = 0;
            BlitNextStrip();
            return true;
        }

        public void ContinueEncodeStrips()
        {
            _stripEventScheduled = false;
            if (!IsBusy || _operation != OperationEncode || _blockRenderTexture == null)
            {
                return;
            }

            // After the last strip this tick only feeds that strip's frame time
            // into the cost model; the readback is already in flight.
            if (_nextStripStartRow >= _blockHeight)
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
            if (stripEndRow > _blockHeight)
            {
                stripEndRow = _blockHeight;
            }

            Material runtimeMaterial = GetRuntimeEncoderMaterial();
            runtimeMaterial.SetVector(
                "_StripRange",
                new Vector4(_nextStripStartRow, stripEndRow, 0f, 0f));
            VRCGraphics.Blit(
                GetActiveSourceForBlit(),
                _blockRenderTexture,
                runtimeMaterial,
                _usingArgb32Backend ? 1 : 0);
            _nextStripStartRow = stripEndRow;

            if (_nextStripStartRow >= _blockHeight)
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

        private bool TryCreateReadbackRenderTexture(int width, int height, bool useArgb32)
        {
            RenderTextureFormat renderTextureFormat = useArgb32
                ? RenderTextureFormat.ARGB32
                : RenderTextureFormat.RInt;

            _blockRenderTexture = new RenderTexture(
                width,
                height,
                0,
                renderTextureFormat,
                RenderTextureReadWrite.Linear);
            _blockRenderTexture.antiAliasing = 1;
            _blockRenderTexture.useMipMap = false;
            _blockRenderTexture.autoGenerateMips = false;

            if (!_blockRenderTexture.Create() || !_blockRenderTexture.IsCreated())
            {
                ReleaseBlockRenderTexture();
                return false;
            }
            return true;
        }

        private void HandleEncodeTransportFailure(string reason)
        {
            int failedBackend = _usingArgb32Backend ? BackendArgb32 : BackendRInt;
            ReleaseBlockRenderTexture();
            _encodedBytes = null;
            InvalidateTransportCache(GetBackendName(failedBackend) + ":" + reason);
            LogWarning(LastTransportProbeError + "; trying alternate backend if available.");

            if (TryStartAlternateProbe(failedBackend))
            {
                return;
            }

            Fail("ByteTransportUnsupported");
        }

        private void InvalidateTransportCache(string reason)
        {
            _cachedBackend = BackendUnknown;
            _cachedReadbackRowsReversed = false;
            TransportProbeCompleted = false;
            TransportRowsReversed = false;
            LastTransportProbeError = reason;
        }

        private int GetTransportProbeRowOrder(byte[] probeBytes)
        {
            if (probeBytes == null || probeBytes.Length != 64)
            {
                return 0;
            }

            bool normalMatches = true;
            bool reversedMatches = true;
            for (int physicalIndex = 0; physicalIndex < 64; physicalIndex++)
            {
                int row = physicalIndex / 16;
                int columnByte = physicalIndex - row * 16;
                int reversedExpectedIndex = (3 - row) * 16 + columnByte;
                byte actual = probeBytes[physicalIndex];
                if (actual != GetExpectedTransportProbeByte(physicalIndex))
                {
                    normalMatches = false;
                }
                if (actual != GetExpectedTransportProbeByte(reversedExpectedIndex))
                {
                    reversedMatches = false;
                }
            }

            if (normalMatches == reversedMatches)
            {
                return 0;
            }
            return normalMatches ? 1 : 2;
        }

        private byte GetExpectedTransportProbeByte(int byteIndex)
        {
            if (byteIndex == 0) return 0;
            if (byteIndex == 1) return 255;
            if (byteIndex == 2) return 127;
            if (byteIndex == 3) return 128;
            return (byte)((byteIndex * 73 + 19) & 255);
        }

        private void MarkBackendTried(int backend)
        {
            if (backend == BackendArgb32)
            {
                _triedArgb32ThisRequest = true;
            }
            else if (backend == BackendRInt)
            {
                _triedRIntThisRequest = true;
            }
        }

        private bool WasBackendTried(int backend)
        {
            return backend == BackendArgb32
                ? _triedArgb32ThisRequest
                : _triedRIntThisRequest;
        }

        private string GetBackendName(int backend)
        {
            return backend == BackendArgb32 ? "ARGB32" : "RInt";
        }

        private bool IsSupportedBuildTarget()
        {
    #if UNITY_ANDROID || UNITY_IOS
            return true;
    #else
            return false;
    #endif
        }

        private bool GetPreferredArgb32Backend()
        {
    #if UNITY_IOS
            return preferArgb32BackendOnIos;
    #else
            return preferArgb32Backend;
    #endif
        }

        private bool HasValidAstcBlocks()
        {
            if (_encodedBytes == null || _encodedBytes.Length != _expectedByteCount)
            {
                return false;
            }

            // Transport faults (channel swizzle, word byte order, sRGB conversion)
            // corrupt every block the same way, so samples spread across the payload
            // including the first and last block are as conclusive as a full sweep.
            int blockCount = _expectedByteCount / 16;
            int sampleCount = blockCount < BlockValidationSampleCount
                ? blockCount
                : BlockValidationSampleCount;
            int lastBlockIndex = blockCount - 1;
            for (int sample = 0; sample < sampleCount; sample++)
            {
                int blockIndex = sampleCount < 2
                    ? 0
                    : sample * lastBlockIndex / (sampleCount - 1);
                int byteOffset = blockIndex * 16;

                // Exact fixed header: block mode 0x042, one partition, CEM 12,
                // and the 15 reserved bits between endpoints and weights cleared.
                if (_encodedBytes[byteOffset] != 0x42
                    || _encodedBytes[byteOffset + 1] != 0x80
                    || (_encodedBytes[byteOffset + 2] & 0x01) != 0x01
                    || (_encodedBytes[byteOffset + 10] & 0xFE) != 0
                    || _encodedBytes[byteOffset + 11] != 0)
                {
                    return false;
                }
            }

            return true;
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
            _activeSourceRenderTexture = null;
            LastEncodeSucceeded = false;
            LastError = reason;
            LastDurationMilliseconds = _encodeStartedAt > 0f
                ? (Time.realtimeSinceStartup - _encodeStartedAt) * 1000f
                : 0f;
            IsBusy = false;
            _operation = OperationIdle;

            Debug.LogError("[RuntimeAstc4x4Encoder] " + reason);
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
                Debug.LogWarning("[RuntimeAstc4x4Encoder] " + message);
            }
        }
    }
}
