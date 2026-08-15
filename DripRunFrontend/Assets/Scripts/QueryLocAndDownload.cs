using Mapbox.CheapRulerCs;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

public class QueryLocAndDownload : MonoBehaviour
{
    // ===== Backend =====
    readonly string QueryApiUrl = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/query/locations-logos";
    readonly string DownloadApiUrl = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/download/image/";

    // ===== Data / Map / Camera =====

    [SerializeField]
    POIManager _poiManager;

    [SerializeField]
    float _maxPOIDistanceMeters = 1000f;

    public bool checkPOIDone; // probably not needed now
    Location[] _allLocations;
    public int AllLocationsCount => _allLocations?.Length ?? 0;


    // ===== Hint UI (Optional – assign in Inspector) =====
    public GameObject HintPanel;   // panel under your existing Canvas
    public TMP_Text HintLabel;   // text element inside the panel

    // Hold delay (seconds) before showing hint on press
    [SerializeField]
    float HoldDelaySeconds = 0.1f;

    // ==== Throttling / Caching ====
    [SerializeField]
    int maxConcurrentDownloads = 12;

    [SerializeField]
    int maxSpawnsPerFrame = 100;

    readonly Dictionary<string, Sprite> _spriteCache = new(128);
    readonly HashSet<string> _inFlight = new();
    Coroutine _poiWorker;

    // ==== Readiness handshake ====
    public bool LocationsReady { get; private set; } // probably not needed now
    public System.Action OnLocationsReady;

    void Awake()
    {
        Input.simulateMouseWithTouches = true;
    }

    //void Start() // Should probably be replaced with OnEnable, and be enabled by LocationManager *after* the player's location is valid
    //{
    //StartCoroutine(GetLocationsFromBackend());
    //}

    void OnEnable()
    {
        StartCoroutine(GetLocationsFromBackend());
    }

    private IEnumerator GetLocationsFromBackend()
    {
        var request = UnityWebRequest.Get(QueryApiUrl);
        request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);
        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string response = request.downloadHandler.text;
            _allLocations = JsonHelper.FromJson<Location>(response);

            // Mark ready and notify listeners (e.g., StatMapController)
            LocationsReady = true;
            OnLocationsReady?.Invoke();

            // If the map is already ready, ensure first-run POIs appear now
            CheckPOIThrottled();
        }
        else if (request.responseCode == 401)
        {
            SceneManager.LoadScene("Login");
        }
        else
        {
            Debug.LogError("Error fetching locations");
        }
    }

    [System.Serializable]
    public class Location
    {
        public int locationId;
        public double latitude;
        public double longitude;
        public string logoImage;
        public string coupHint; // nullable hint text
    }

    public static class JsonHelper
    {
        public static T[] FromJson<T>(string json)
        {
            string wrappedJson = "{\"array\":" + json + "}";
            Wrapper<T> wrapper = JsonUtility.FromJson<Wrapper<T>>(wrappedJson);
            return wrapper.array;
        }
        [System.Serializable] private class Wrapper<T> { public T[] array; }
    }

    public int GetLengthOfLoc() => _allLocations?.Length ?? 0;

    // ===== Throttled POI population entry point =====
    public void CheckPOIThrottled()
    {
        if (_poiWorker != null) StopCoroutine(_poiWorker);
        _poiWorker = StartCoroutine(POIWorker());
    }

    private IEnumerator POIWorker()
    {
        checkPOIDone = false;

        if (_allLocations == null || _allLocations.Length == 0 /*|| _map == null || StaticCam == null*/)
        {
            checkPOIDone = true;
            yield break;
        }

        // 1) Gather visible locations first

        var visibleLocations = new List<Location>();

        for (int i = 0; i < _allLocations.Length; i++)
        {
            var location = _allLocations[i];

            if (IsInView(location))
            {
                visibleLocations.Add(location);
            }
        }

        // 2) Process with budgets (spawn N per frame, D concurrent downloads)
        // (Spawn the actual sprite objects)

        _poiManager.ClearAndDestroyAll();

        int spawnedThisFrame = 0;
        int activeDownloads = 0;

        int spawnedFromDisk = 0;
        int spawnedFromCache = 0;
        int spawnedFromDownload = 0;

        Debug.Log($"Visible Locations: {visibleLocations.Count}");

        for (int i = 0; i < visibleLocations.Count; i++)
        {
            if (spawnedThisFrame >= maxSpawnsPerFrame)
            {
                spawnedThisFrame = 0;
                yield return null; // let the frame breathe
            }

            var location = visibleLocations[i];


            // RAM CACHE

            // Try in-memory cache
            if (!string.IsNullOrEmpty(location.logoImage) && _spriteCache.TryGetValue(location.logoImage, out var cachedSprite))
            {
                CreateFromSprite(cachedSprite, location);
                spawnedThisFrame++;
                spawnedFromCache++;
                continue;
            }

            // Try disk cache
            string fileName = UnityWebRequest.EscapeURL(location.logoImage);
            string cachedPath = Path.Combine(Application.persistentDataPath, fileName);
            if (File.Exists(cachedPath))
            {
                var tex = new Texture2D(2, 2);
                tex.LoadImage(File.ReadAllBytes(cachedPath), true); // non-readable
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                _spriteCache[location.logoImage] = sprite;

                CreateFromSprite(sprite, location);
                spawnedThisFrame++;
                spawnedFromDisk++;
                continue;
            }

            // Else download (with concurrency cap)
            while (activeDownloads >= maxConcurrentDownloads)
            {
                yield return null;
            }

            if (_inFlight.Add(location.logoImage))
            {
                activeDownloads++;
                /*                StartCoroutine(DownloadThenSpawn(location.logoImage, location, () =>
                                {
                                    activeDownloads--;
                                    _inFlight.Remove(location.logoImage);
                                    spawnedThisFrame++;
                                    spawnedFromDownload++;
                                }));*/

                yield return DownloadThenSpawn(location.logoImage, location, () =>
                {
                    activeDownloads--;
                    _inFlight.Remove(location.logoImage);
                    spawnedThisFrame++;
                    spawnedFromDownload++;
                });
            }
        }

        // Wait for any remaining downloads to finish
        while (_inFlight.Count > 0)
        {
            yield return null;
        }

        //_poiManager.UpdatePOISpritePositions(_map);

        checkPOIDone = true;


        Debug.Log($"Cache: {spawnedFromCache}, Disk: {spawnedFromDisk}, Download: {spawnedFromDownload}");
        Debug.Log($"Total Spawned: {spawnedFromCache + spawnedFromDisk + spawnedFromDownload}");
    }



    // NEW
    IEnumerator DownloadThenSpawn(string imageName, Location location, System.Action onDone)
    {
        string fileName = UnityWebRequest.EscapeURL(imageName);
        string cachedPath = Path.Combine(Application.persistentDataPath, fileName);

        string url = $"{DownloadApiUrl}{fileName}";
        var req = UnityWebRequest.Get(url);
        req.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var data = req.downloadHandler.data;
            File.WriteAllBytes(cachedPath, data);

            var tex = new Texture2D(2, 2);
            var succcess = tex.LoadImage(data, true);
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f)); // COME BACK
            _spriteCache[imageName] = sprite;

            CreateFromSprite(sprite, location);

            if (!succcess)
            {
                Debug.Log($"{imageName} not loaded. ({location.locationId})");
            }

        }
        else if (req.responseCode == 401)
        {
            SceneManager.LoadScene("Login");
        }
        else
        {
            Debug.LogError($"Error downloading image '{imageName}': {req.error}");
        }

        onDone?.Invoke();
    }

    // Keep for now

    /*    private bool IsInViewOld(Vector3 target, Camera camera)
        {
            Vector3 vp = camera.WorldToViewportPoint(target);
            return vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f;
        }*/


    private bool IsInView(Location location)
    {
        var ruler = new CheapRuler(LocationManager.Latitude, CheapRulerUnits.Meters);

        var playerLocation = new double[] { LocationManager.Latitude, LocationManager.Longitude };
        var poiLocation = new double[] { location.latitude, location.longitude };

        var distance = ruler.Distance(playerLocation, poiLocation);

        // 1km distance
        return distance <= _maxPOIDistanceMeters;
    }

    // ===== Creation helpers (from cached Sprite) =====

    // Updated to always spawn clickable
    // NEW
    private void CreateFromSprite(Sprite sprite, Location location)
    {
        var go = new GameObject("POI_Image_Clickable");

        // Orient and scale the sprite correctly
        go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        go.transform.localScale = new Vector3(1.8f, 1.8f, 0.1f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;

        go.name = location.locationId.ToString();
        go.tag = "PlacedImage";

        // Substantially oversized collider → easier to tap
        var col = go.AddComponent<BoxCollider>();
        var size = sr.bounds.size; // world-space after scaling
        col.size = new Vector3(size.x * 3.0f, 3.0f, size.z * 3.0f);
        col.center = Vector3.zero;

        // Press & hold to show, release/exit to hide
        var hold = go.AddComponent<ClickHoldToShowHint>();
        hold.Init(this, string.IsNullOrEmpty(location.coupHint) ? "There's no hint for this drop." : location.coupHint, HoldDelaySeconds);

        _poiManager.Register(location.locationId, location.latitude, location.longitude, go);
    }

    // ===== Hint helpers =====
    public void ShowHint(string hint)
    {
        if (HintPanel != null && HintLabel != null)
        {
            HintLabel.text = hint;
            HintPanel.SetActive(true);
        }
        else
        {
            Debug.Log($"[HINT] {hint}");
        }
    }

    public void HideHint()
    {
        if (HintPanel != null) HintPanel.SetActive(false);
    }

    // ===== Inner class: press & hold behavior (mobile via simulateMouseWithTouches) =====
    private class ClickHoldToShowHint : MonoBehaviour
    {
        private QueryLocAndDownload owner;
        private string hint;
        private float holdDelay;
        private bool pressed;
        private bool shown;
        private Coroutine holdCo;

        public void Init(QueryLocAndDownload owner, string hint, float holdDelay)
        {
            this.owner = owner;
            this.hint = hint;
            this.holdDelay = Mathf.Max(0.05f, holdDelay);
        }

        void OnMouseDown()
        {
            pressed = true;
            shown = false;
            if (holdCo != null) owner.StopCoroutine(holdCo);
            holdCo = owner.StartCoroutine(HoldRoutine());
        }

        void OnMouseUp() => Cancel();
        void OnMouseExit() => Cancel();
        void OnDisable() => Cancel();

        private IEnumerator HoldRoutine()
        {
            float t = 0f;
            while (pressed && t < holdDelay)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            if (pressed && !shown)
            {
                owner.ShowHint(hint);
                shown = true;
            }

            while (pressed) yield return null;
            holdCo = null;
        }

        private void Cancel()
        {
            pressed = false;
            if (shown) owner.HideHint();
            shown = false;
            if (holdCo != null) { owner.StopCoroutine(holdCo); holdCo = null; }
        }
    }
}
