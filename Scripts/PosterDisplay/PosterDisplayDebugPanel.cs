using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

/// <summary>
/// In-world diagnostics for device testing (Quest / iOS / PC builds where
/// the Unity Console is not available). Shows the build target, GPU,
/// encoder backend diagnostics, per-board results, the worst frame time
/// observed while boards were loading, and a scrolling event log that
/// PosterDisplayManager / PosterDisplayController append to.
/// Uses legacy UI Text so no TextMeshPro resources are required.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PosterDisplayDebugPanel : UdonSharpBehaviour
{
    [Header("UI")]
    [SerializeField] private Text statusText;
    [SerializeField] private Text logText;

    [Header("Sources")]
    [SerializeField] private DRCompressedImageDownloader downloader;
    [SerializeField] private PosterDisplayManager manager;
    [SerializeField] private PosterDisplayController[] posters;

    [Header("Behavior")]
    [SerializeField] private int maxLogLines = 18;
    [SerializeField] private int refreshIntervalFrames = 30;
    [Tooltip("Frames ignored at startup before frame-time statistics begin (scene load hitches).")]
    [SerializeField] private int frameStatsWarmupFrames = 90;

    [HideInInspector] public float MaxFrameMsWhileLoading;
    [HideInInspector] public float MaxFrameMsTotal;
    [HideInInspector] public float MaxFrameMsDownloading;
    [HideInInspector] public float MaxFrameMsEncoding;

    private string[] _lines;
    private int _lineCount;
    private bool _refreshScheduled;
    private int _frameCounter;
    private RuntimeAstc4x4EncoderController _astcEncoder;
    private RuntimeBc7EncoderController _bc7Encoder;
    private bool _bypassFlag;
    private bool _passMaterialFlag;
    private bool _flagsRead;
    private int _lastHeartbeat;
    private float _lastHeartbeatChangeAt;
    private int _lastCompletedCount = -1;

    private void Start()
    {
        EnsureBuffer();
        Log("panel start");
        Refresh();
    }

    private void Update()
    {
        _frameCounter++;
        if (_frameCounter < frameStatsWarmupFrames)
        {
            return;
        }

        float frameMs = Time.unscaledDeltaTime * 1000f;
        if (frameMs > MaxFrameMsTotal)
        {
            MaxFrameMsTotal = frameMs;
        }
        if (manager != null && manager.IsLoadingAll && frameMs > MaxFrameMsWhileLoading)
        {
            MaxFrameMsWhileLoading = frameMs;
        }

        // One board finished during the previous frame: that frame's cost
        // (readback copy, texture upload) is this frame's delta. Snapshot the
        // worker's strip statistics now, before the next encode overwrites them.
        if (manager != null)
        {
            int completed = manager.LoadedCount + manager.FailedCount;
            if (_lastCompletedCount < 0)
            {
                _lastCompletedCount = completed;
            }
            else if (completed != _lastCompletedCount)
            {
                _lastCompletedCount = completed;
                Log("  finish frame " + frameMs.ToString("F1") + "ms | " + BuildStripStats());
            }
        }

        // Split by request phase so a native decode hitch (Downloading) is
        // not confused with an encode strip that did not fit the frame.
        if (downloader != null)
        {
            DRCompressedImageDownload active = downloader.ActiveRequest;
            if (active != null)
            {
                if (active.Phase == "Downloading")
                {
                    if (frameMs > MaxFrameMsDownloading)
                    {
                        MaxFrameMsDownloading = frameMs;
                    }
                }
                else if (active.Phase == "Encoding")
                {
                    if (frameMs > MaxFrameMsEncoding)
                    {
                        MaxFrameMsEncoding = frameMs;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Appends one line (with a timestamp) to the on-screen log. Safe to
    /// call before this behaviour's Start has run.
    /// </summary>
    public void Log(string message)
    {
        EnsureBuffer();
        string line = "[" + Time.realtimeSinceStartup.ToString("F1") + "s] " + message;
        if (_lineCount < _lines.Length)
        {
            _lines[_lineCount] = line;
            _lineCount++;
        }
        else
        {
            for (int i = 1; i < _lines.Length; i++)
            {
                _lines[i - 1] = _lines[i];
            }
            _lines[_lines.Length - 1] = line;
        }

        if (logText != null)
        {
            string text = "";
            for (int i = 0; i < _lineCount; i++)
            {
                text = i == 0 ? _lines[i] : text + "\n" + _lines[i];
            }
            logText.text = text;
        }
    }

    public void ResetFrameStats()
    {
        MaxFrameMsWhileLoading = 0f;
        MaxFrameMsTotal = 0f;
        MaxFrameMsDownloading = 0f;
        MaxFrameMsEncoding = 0f;
    }

    public void Refresh()
    {
        _refreshScheduled = false;
        if (statusText != null)
        {
            statusText.text = BuildStatus();
        }

        if (!_refreshScheduled)
        {
            _refreshScheduled = true;
            SendCustomEventDelayedFrames(nameof(Refresh), refreshIntervalFrames < 1 ? 1 : refreshIntervalFrames);
        }
    }

    private string BuildStatus()
    {
        string target = "Windows";
#if UNITY_ANDROID
        target = "Android";
#elif UNITY_IOS
        target = "iOS";
#endif
        // SystemInfo is not exposed to Udon; the worker diagnostics below
        // (transport probe / backend) are the device-specific signal instead.
        string status = "Target: " + target
            + "\nFrame max: download " + MaxFrameMsDownloading.ToString("F1")
            + " ms / encode " + MaxFrameMsEncoding.ToString("F1")
            + " ms / loading " + MaxFrameMsWhileLoading.ToString("F1")
            + " ms / total " + MaxFrameMsTotal.ToString("F1")
            + " ms (now " + (Time.unscaledDeltaTime * 1000f).ToString("F1") + " ms)";

        if (downloader == null)
        {
            status += "\nFacade: none";
        }
        else
        {
            // Worker references and flags are fetched once through method calls;
            // everything per-refresh is a direct heap read so a halted facade
            // cannot feed stale strings into this panel.
            if (!_flagsRead)
            {
                _astcEncoder = downloader.GetAstcEncoder();
                _bc7Encoder = downloader.GetBc7Encoder();
                _bypassFlag = downloader.GetBypassCompressionForDiagnostics();
                _passMaterialFlag = downloader.GetPassMaterialToNativeDownloader();
                _flagsRead = true;
                _lastHeartbeat = downloader.HeartbeatTick;
                _lastHeartbeatChangeAt = Time.realtimeSinceStartup;
            }
            if (downloader.HeartbeatTick != _lastHeartbeat)
            {
                _lastHeartbeat = downloader.HeartbeatTick;
                _lastHeartbeatChangeAt = Time.realtimeSinceStartup;
            }

            status += "\nFacade: active=" + (downloader.ActiveRequest != null)
                + " lastServiceError=" + (downloader.LastServiceError == "" ? "-" : downloader.LastServiceError)
                + " hb=" + downloader.HeartbeatTick
                + " (" + (Time.realtimeSinceStartup - _lastHeartbeatChangeAt).ToString("F0") + "s ago)"
                + " cb ok/err=" + downloader.NativeSuccessCallbacks + "/" + downloader.NativeErrorCallbacks
                + " bypass=" + _bypassFlag + " passMat=" + _passMaterialFlag;
            DRCompressedImageDownload active = downloader.ActiveRequest;
            if (active != null)
            {
                status += "\nRequest: phase=" + active.Phase
                    + " state=" + active.State
                    + " progress=" + active.Progress.ToString("F2")
                    + " elapsed=" + (Time.realtimeSinceStartup - active.RequestStartedAt).ToString("F0") + "s"
                    + " err=" + (active.ErrorMessage == null || active.ErrorMessage == "" ? "-" : active.ErrorMessage);
            }
            if (_astcEncoder != null)
            {
                status += "\nASTC: busy=" + _astcEncoder.IsBusy
                    + " probe=" + _astcEncoder.TransportProbeCompleted
                    + " rowsRev=" + _astcEncoder.TransportRowsReversed
                    + " probeErr=" + (_astcEncoder.LastTransportProbeError == null || _astcEncoder.LastTransportProbeError == "" ? "-" : _astcEncoder.LastTransportProbeError)
                    + " backend=" + (_astcEncoder.LastBackend == null || _astcEncoder.LastBackend == "" ? "-" : _astcEncoder.LastBackend)
                    + " ok=" + _astcEncoder.LastEncodeSucceeded
                    + " err=" + (_astcEncoder.LastError == null || _astcEncoder.LastError == "" ? "-" : _astcEncoder.LastError)
                    + " ms=" + _astcEncoder.LastDurationMilliseconds.ToString("F0")
                    + "\n  strips=" + _astcEncoder.LastStripCount
                    + " blk/frame=" + _astcEncoder.LastMinBlocksPerFrame + ".." + _astcEncoder.LastMaxBlocksPerFrame
                    + " base=" + _astcEncoder.LastBaselineFrameMs.ToString("F1") + "ms"
                    + " worstStrip=" + _astcEncoder.LastWorstStripFrameMs.ToString("F1") + "ms";
            }
            if (_bc7Encoder != null)
            {
                status += "\nBC7/BC1: busy=" + _bc7Encoder.IsBusy
                    + " backend=" + (_bc7Encoder.LastBackend == null || _bc7Encoder.LastBackend == "" ? "-" : _bc7Encoder.LastBackend)
                    + " bc1=" + _bc7Encoder.LastUsedBc1
                    + " ok=" + _bc7Encoder.LastEncodeSucceeded
                    + " err=" + (_bc7Encoder.LastError == null || _bc7Encoder.LastError == "" ? "-" : _bc7Encoder.LastError)
                    + " ms=" + _bc7Encoder.LastDurationMilliseconds.ToString("F0")
                    + "\n  strips=" + _bc7Encoder.LastStripCount
                    + " blk/frame=" + _bc7Encoder.LastMinBlocksPerFrame + ".." + _bc7Encoder.LastMaxBlocksPerFrame
                    + " base=" + _bc7Encoder.LastBaselineFrameMs.ToString("F1") + "ms"
                    + " worstStrip=" + _bc7Encoder.LastWorstStripFrameMs.ToString("F1") + "ms";
            }
        }

        if (manager != null)
        {
            status += "\nManager: loading=" + manager.IsLoadingAll
                + " loaded=" + manager.LoadedCount
                + " failed=" + manager.FailedCount;
        }

        if (posters != null)
        {
            for (int i = 0; i < posters.Length; i++)
            {
                PosterDisplayController poster = posters[i];
                if (poster == null)
                {
                    continue;
                }

                string state = poster.IsLoading ? "loading" : (poster.IsLoaded ? "loaded" : "idle");
                status += "\n" + poster.gameObject.name + ": " + state;
                if (poster.IsLoaded)
                {
                    status += " " + poster.LastFormat
                        + (poster.LastUsedFallback ? "(fallback)" : "")
                        + " " + poster.LastBackend
                        + " " + poster.LastImageWidth + "x" + poster.LastImageHeight
                        + (poster.LastDownscaleDivisor > 1
                            ? " (dl " + poster.LastDownloadedWidth + "x" + poster.LastDownloadedHeight + " /" + poster.LastDownscaleDivisor + ")"
                            : "")
                        + " " + poster.LastSizeInMemoryBytes + " B"
                        + " enc " + poster.LastEncodeMilliseconds.ToString("F0") + " ms";
                }
                if (poster.LastError != null && poster.LastError != "")
                {
                    status += " err=" + poster.LastError;
                }
            }
        }

        return status;
    }

    private string BuildStripStats()
    {
        if (_astcEncoder != null && _astcEncoder.LastStripCount > 0)
        {
            return "astc strips=" + _astcEncoder.LastStripCount
                + " blk/frame=" + _astcEncoder.LastMinBlocksPerFrame + ".." + _astcEncoder.LastMaxBlocksPerFrame
                + " base=" + _astcEncoder.LastBaselineFrameMs.ToString("F1")
                + " worst=" + _astcEncoder.LastWorstStripFrameMs.ToString("F1")
                + " us/blk=" + _astcEncoder.LastMicrosecondsPerBlock.ToString("F2")
                + " enc=" + _astcEncoder.LastDurationMilliseconds.ToString("F0") + "ms";
        }
        if (_bc7Encoder != null && _bc7Encoder.LastStripCount > 0)
        {
            return "bc strips=" + _bc7Encoder.LastStripCount
                + " blk/frame=" + _bc7Encoder.LastMinBlocksPerFrame + ".." + _bc7Encoder.LastMaxBlocksPerFrame
                + " base=" + _bc7Encoder.LastBaselineFrameMs.ToString("F1")
                + " worst=" + _bc7Encoder.LastWorstStripFrameMs.ToString("F1")
                + " us/blk=" + _bc7Encoder.LastMicrosecondsPerBlock.ToString("F2")
                + " enc=" + _bc7Encoder.LastDurationMilliseconds.ToString("F0") + "ms";
        }
        return "no strip stats";
    }

    private void EnsureBuffer()
    {
        if (_lines == null)
        {
            _lines = new string[maxLogLines < 1 ? 1 : maxLogLines];
            _lineCount = 0;
        }
    }
}
