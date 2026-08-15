using System.Collections;
using TMPro;
using UnityEngine;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using System.Globalization;
#endif

public class LocationManager : MonoBehaviour
{
    public static float Latitude { get; private set; }
    public static float Longitude { get; private set; }

    public GameObject StaticMapController;
    public QueryLocAndDownload QueryScript;
    public BtnCont SignOut;
    public TMP_Text gpsOutLat;
    public TMP_Text gpsOutLon;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void StartWebLocationWatch();
    [DllImport("__Internal")] private static extern void StopWebLocationWatch();
    private bool _webHasFix;
#endif

    void Start()
    {
        StartCoroutine(InitLocationAndGyro());
    }

    IEnumerator InitLocationAndGyro()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        // WebGL: use browser geolocation watchPosition (via .jslib)
        _webHasFix = false;

        // Starts continuous location updates; browser will prompt user if needed
        StartWebLocationWatch();

        // Wait until first fix (so downstream systems have real coordinates)
        float timeout = 15f;
        float elapsed = 0f;
        while (!_webHasFix && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (!_webHasFix)
        {
            Debug.LogWarning("❌ WebGL: Timed out waiting for first location fix.");
            SignOut.SignOut();
            yield break;
        }

        // Enable POI query script once we have a fix
        QueryScript.gameObject.SetActive(true);

        // Wait until location data is ready (your existing behavior)
        int arrayWaitTime = 200;
        while (QueryScript.AllLocationsCount == 0)
        {
            yield return new WaitForSeconds(0.1f);
            arrayWaitTime--;

            if (arrayWaitTime <= 0)
            {
                Debug.LogWarning("❌ WebGL: Timed out waiting for location array to populate.");
                SignOut.SignOut();
                yield break;
            }
        }

        Debug.Log("✅ WebGL location stream and data ready. Activating StaticMapController.");
        StaticMapController.SetActive(true);
        yield break;
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        // Android runtime permissions (WebGL never sees this code)
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            Permission.RequestUserPermission(Permission.FineLocation);
            Permission.RequestUserPermission(Permission.CoarseLocation);

            float timeout = 10f;
            float elapsed = 0f;

            while (!Permission.HasUserAuthorizedPermission(Permission.FineLocation) && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
            {
                Debug.LogWarning("❌ Android: Location permission denied. Returning to sign-in scene.");
                SignOut.SignOut();
                yield break;
            }
        }
#endif

        // Mobile/editor path
        yield return StartCoroutine(StartLocationService());
    }

    IEnumerator StartLocationService()
    {
        Input.location.Start();

        // Wait until service initializes
        int maxWait = 200;
        while (Input.location.status == LocationServiceStatus.Initializing && maxWait > 0)
        {
            yield return new WaitForSeconds(0.1f);
            maxWait--;
        }

        if (maxWait < 1)
        {
            Debug.LogWarning("❌ Timed out while waiting for location service.");
            SignOut.SignOut();
            yield break;
        }

        if (Input.location.status == LocationServiceStatus.Failed)
        {
            Debug.LogWarning("❌ Location service failed. Unable to determine location.");
            SignOut.SignOut();
            yield break;
        }

        if (!Input.location.isEnabledByUser)
        {
            Debug.LogWarning("❌ Location services not enabled by user.");
            SignOut.SignOut();
            yield break;
        }

        // We have a valid location, update it and enable the POI query script
        UpdateLocation();
        QueryScript.gameObject.SetActive(true);

        // Wait until location data is ready
        int arrayWaitTime = 200;
        while (QueryScript.AllLocationsCount == 0)
        {
            yield return new WaitForSeconds(0.1f);
            arrayWaitTime--;

            if (arrayWaitTime <= 0)
            {
                Debug.LogWarning("❌ Timed out waiting for location array to populate.");
                SignOut.SignOut();
                yield break;
            }
        }

        Debug.Log("✅ Location service and data ready. Activating StaticMapController.");
        StaticMapController.SetActive(true);
    }

    void Update()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        // WebGL: updates arrive via OnWebLocation callback, not Input.location
        return;
#else
        UpdateLocation();
#endif
    }

    void OnDisable()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        StopWebLocationWatch();
#else
        Input.location.Stop();
#endif
    }

    void UpdateLocation()
    {
        if (Input.location.isEnabledByUser && Input.location.status == LocationServiceStatus.Running)
        {
            Latitude = Input.location.lastData.latitude;
            Longitude = Input.location.lastData.longitude;

            if (gpsOutLat) gpsOutLat.text = Latitude.ToString();
            if (gpsOutLon) gpsOutLon.text = Longitude.ToString();
        }
    }

    // -------- WebGL callbacks from JS (.jslib) --------
    // JS will call: SendMessage("<GameObjectName>", "OnWebLocation", "lat,lon")
    public void OnWebLocation(string latLon)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        if (string.IsNullOrEmpty(latLon)) return;

        var parts = latLon.Split(',');
        if (parts.Length != 2) return;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return;

        Latitude = lat;
        Longitude = lon;

        if (gpsOutLat) gpsOutLat.text = Latitude.ToString(CultureInfo.InvariantCulture);
        if (gpsOutLon) gpsOutLon.text = Longitude.ToString(CultureInfo.InvariantCulture);

        _webHasFix = true;
#endif
    }

    // JS will call: SendMessage("<GameObjectName>", "OnWebLocationError", "message")
    public void OnWebLocationError(string message)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        Debug.LogWarning("❌ WebGL geolocation error: " + message);
        // Decide whether to sign out or keep waiting. For now, sign out like mobile.
        SignOut.SignOut();
        _webHasFix = true; // unblock the "wait for first fix" loop
#endif
    }
}
