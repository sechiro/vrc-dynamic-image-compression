using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using HatagoWorks.DynamicImageCompression;

/// <summary>
/// Loads several PosterDisplayController boards one after another through a
/// single shared DRCompressedImageDownloader. The facade processes one
/// request at a time and VRChat rate-limits image downloads to one every
/// five seconds, so boards are started sequentially with a fixed interval.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PosterDisplayManager : UdonSharpBehaviour
{
    [Header("Shared Downloader")]
    [SerializeField] private DRCompressedImageDownloader downloader;

    [Header("Boards (loaded in this order)")]
    [SerializeField] private PosterDisplayController[] posters;

    [Header("Diagnostics (optional)")]
    [SerializeField] private PosterDisplayDebugPanel debugPanel;

    [Header("Sequence")]
    [SerializeField] private bool loadOnStart = true;
    [SerializeField] private float startDelaySeconds = 1f;
    [Tooltip("Gap between one board finishing and the next starting. Keep above VRChat's 5 second image download limit.")]
    [SerializeField] private float intervalSeconds = 5.5f;
    [SerializeField] private bool verboseLogging = true;

    [HideInInspector] public bool IsLoadingAll;
    [HideInInspector] public int LoadedCount;
    [HideInInspector] public int FailedCount;

    private int _nextIndex;
    private bool _nextScheduled;
    private PosterDisplayController _current;

    private void Start()
    {
        if (posters != null)
        {
            for (int i = 0; i < posters.Length; i++)
            {
                if (posters[i] != null)
                {
                    posters[i].SetDownloader(downloader);
                    posters[i].SetManager(this);
                    posters[i].SetDebugPanel(debugPanel);
                }
            }
        }

        if (loadOnStart)
        {
            SendCustomEventDelayedSeconds(nameof(LoadAll), startDelaySeconds);
        }
    }

    /// <summary>
    /// Starts loading every board in order. Boards that are already loaded
    /// are reloaded. No-op while a sequence is running.
    /// </summary>
    public void LoadAll()
    {
        if (IsLoadingAll)
        {
            LogWarning("A load sequence is already running.");
            return;
        }
        if (downloader == null)
        {
            Debug.LogError("[PosterDisplayManager] Downloader is not assigned.");
            return;
        }

        IsLoadingAll = true;
        LoadedCount = 0;
        FailedCount = 0;
        _nextIndex = 0;
        _current = null;
        PanelLog("sequence start (" + (posters == null ? 0 : posters.Length) + " boards)");
        LoadNext();
    }

    /// <summary>
    /// Releases every board and starts the sequence again. A request that is
    /// still in flight is cancelled through its handle; the facade drains it
    /// before accepting the restarted sequence.
    /// </summary>
    public void ReloadAll()
    {
        UnloadAll();
        IsLoadingAll = false;
        if (!_nextScheduled)
        {
            _nextScheduled = true;
            SendCustomEventDelayedSeconds(nameof(LoadAllDelayed), intervalSeconds);
        }
    }

    public void LoadAllDelayed()
    {
        _nextScheduled = false;
        LoadAll();
    }

    public void UnloadAll()
    {
        if (posters == null)
        {
            return;
        }
        for (int i = 0; i < posters.Length; i++)
        {
            if (posters[i] != null)
            {
                posters[i].Unload();
            }
        }
        _current = null;
    }

    public void LoadNext()
    {
        _nextScheduled = false;
        if (!IsLoadingAll)
        {
            return;
        }

        while (posters != null && _nextIndex < posters.Length && posters[_nextIndex] == null)
        {
            _nextIndex++;
        }

        if (posters == null || _nextIndex >= posters.Length)
        {
            IsLoadingAll = false;
            _current = null;
            PanelLog("sequence finished: loaded=" + LoadedCount + " failed=" + FailedCount);
            if (verboseLogging)
            {
                Debug.Log("[PosterDisplayManager] Sequence finished: loaded=" + LoadedCount
                    + " failed=" + FailedCount);
            }
            return;
        }

        _current = posters[_nextIndex];
        _nextIndex++;
        PanelLog("load " + _current.gameObject.name);
        _current.BeginLoad();
    }

    /// <summary>
    /// Called by a board when its load succeeded or gave up. Schedules the
    /// next board after the rate-limit interval.
    /// </summary>
    public void OnPosterLoadFinished(PosterDisplayController poster, bool succeeded)
    {
        if (!IsLoadingAll || poster != _current)
        {
            return;
        }

        if (succeeded)
        {
            LoadedCount++;
        }
        else
        {
            FailedCount++;
        }

        if (!_nextScheduled)
        {
            _nextScheduled = true;
            SendCustomEventDelayedSeconds(nameof(LoadNext), intervalSeconds);
        }
    }

    private void PanelLog(string message)
    {
        if (debugPanel != null)
        {
            debugPanel.Log("manager: " + message);
        }
    }

    private void LogWarning(string message)
    {
        if (verboseLogging)
        {
            Debug.LogWarning("[PosterDisplayManager] " + message);
        }
    }
}
