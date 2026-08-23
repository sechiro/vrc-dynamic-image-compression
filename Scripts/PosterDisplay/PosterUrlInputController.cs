using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;

/// <summary>
/// Lets a player type an image URL into a VRCUrlInputField and load it onto
/// the demo boards. Wire a Load button's OnClick to
/// SendCustomEvent("OnLoadRequested") on this behaviour. The URL is written
/// to every target board and the PosterDisplayManager reloads them in
/// sequence, so VRChat's one-download-per-5-seconds limit is respected.
/// The initial URL is pushed into the field on Start.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PosterUrlInputController : UdonSharpBehaviour
{
    [Header("UI")]
    [SerializeField] private VRCUrlInputField urlInput;
    [SerializeField] private Text statusText;

    [Header("Targets")]
    [SerializeField] private PosterDisplayManager manager;
    [SerializeField] private PosterDisplayController[] targetPosters;
    [Tooltip("Shown in the field on Start. Leave empty to start with the first target's own URL.")]
    [SerializeField] private VRCUrl initialUrl;

    [Header("Behavior")]
    [Tooltip("Seconds after a load request during which another request is ignored.")]
    [SerializeField] private float minimumIntervalSeconds = 5.5f;
    [SerializeField] private bool verboseLogging = true;

    [HideInInspector] public string LastRequestedUrl = "";

    private float _lastRequestAt = -1000f;
    private bool _refreshScheduled;

    private void Start()
    {
        if (urlInput != null)
        {
            VRCUrl startUrl = initialUrl;
            if ((startUrl == null || startUrl.Get() == "")
                && targetPosters != null && targetPosters.Length > 0 && targetPosters[0] != null)
            {
                startUrl = targetPosters[0].GetPosterUrl();
            }
            if (startUrl != null && startUrl.Get() != "")
            {
                urlInput.SetUrl(startUrl);
            }
        }

        SetStatus("Enter an image URL and press Load.");
        ScheduleRefresh();
    }

    /// <summary>
    /// Called from the Load button.
    /// </summary>
    public void OnLoadRequested()
    {
        if (urlInput == null || manager == null || targetPosters == null || targetPosters.Length == 0)
        {
            SetStatus("Setup is incomplete.");
            return;
        }

        VRCUrl url = urlInput.GetUrl();
        if (url == null || url.Get() == "")
        {
            SetStatus("URL is empty.");
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (now - _lastRequestAt < minimumIntervalSeconds)
        {
            SetStatus("Please wait " + (minimumIntervalSeconds - (now - _lastRequestAt)).ToString("F0") + "s before the next load.");
            return;
        }
        _lastRequestAt = now;
        LastRequestedUrl = url.Get();

        for (int i = 0; i < targetPosters.Length; i++)
        {
            if (targetPosters[i] != null)
            {
                targetPosters[i].SetPosterUrl(url);
            }
        }

        // ReloadAll releases every board, then restarts the manager's
        // sequence after its interval so boards load one at a time.
        manager.ReloadAll();
        SetStatus("Loading...");

        if (verboseLogging)
        {
            Debug.Log("[PosterUrlInput] Load requested: " + LastRequestedUrl);
        }
    }

    public void Refresh()
    {
        _refreshScheduled = false;
        if (manager != null && statusText != null && LastRequestedUrl != "")
        {
            if (manager.IsLoadingAll)
            {
                SetStatus("Loading... " + manager.LoadedCount + "/" + targetPosters.Length);
            }
            else if (manager.FailedCount > 0)
            {
                SetStatus("Loaded " + manager.LoadedCount + ", failed " + manager.FailedCount
                    + " (" + FirstError() + ")");
            }
            else if (manager.LoadedCount > 0)
            {
                SetStatus("Loaded " + manager.LoadedCount + " board(s): " + FirstResult());
            }
        }
        ScheduleRefresh();
    }

    private string FirstResult()
    {
        for (int i = 0; i < targetPosters.Length; i++)
        {
            PosterDisplayController poster = targetPosters[i];
            if (poster != null && poster.IsLoaded)
            {
                return poster.LastFormat + " " + poster.LastImageWidth + "x" + poster.LastImageHeight
                    + " " + poster.LastSizeInMemoryBytes + " B";
            }
        }
        return "-";
    }

    private string FirstError()
    {
        for (int i = 0; i < targetPosters.Length; i++)
        {
            PosterDisplayController poster = targetPosters[i];
            if (poster != null && poster.LastError != null && poster.LastError != "")
            {
                return poster.LastError;
            }
        }
        return "-";
    }

    private void ScheduleRefresh()
    {
        if (_refreshScheduled)
        {
            return;
        }
        _refreshScheduled = true;
        SendCustomEventDelayedFrames(nameof(Refresh), 30);
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
        {
            statusText.text = message;
        }
    }
}
