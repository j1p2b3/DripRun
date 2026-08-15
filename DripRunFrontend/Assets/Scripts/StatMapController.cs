using Mapbox.CheapRulerCs;
using Mapbox.Unity.Map;
using Mapbox.Utils;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class StatMapController : MonoBehaviour
{
    [SerializeField]
    POIManager _poiManager;

    [SerializeField]
    AbstractMap _map;

    [SerializeField]
    GameObject _player;

    [SerializeField]
    Button _arButton;

    [SerializeField]
    Sprite _greySprite;

    [SerializeField]
    Sprite _normalSprite;

    [SerializeField]
    Sprite _noneSprite;

    [SerializeField]
    QueryLocAndDownload _queryScript;

    [SerializeField]
    DownloadForAR _arDownloader;

    [SerializeField]
    GameObject _canvas;

    [SerializeField]
    GameObject _cameraObject;

    [SerializeField]
    TextMeshProUGUI _pointsText;

    [SerializeField]
    float _distanceToStartDownloadMeters = 50f;

    [SerializeField]
    float _distanceToCollectPOIMeters = 10f;

    [SerializeField]
    public GameObject AudioSource;

    // The POI that the player is close enough to collect
    POIManager.POI _collectablePOI = null;

    readonly List<int> _downloadedAlreadyList = new();

    // Try to avoid distance checks every frame
    Vector2d _lastPlayerPosition;
    float _forceUpdateTimer;

    // For measuring distance between player and POIs
    readonly double[] _rulerPositionPlayer = new double[2];
    readonly double[] _rulerPositionPOI = new double[2];

    void OnEnable()
    {
        // Show map + UI
        _map.gameObject.SetActive(true);
        _player.SetActive(true);
        _cameraObject.SetActive(true);
        _canvas.SetActive(true); // Enables PanZoom, which handles map positioning

        // Points flow
        StartCoroutine(SendLoginPoints());

        if (!IsSFXMuted()) AudioSource.SetActive(true);
        // Update sprite positions of any already-added POIs
        //_poiManager.UpdatePOISpritePositions();

        //map.transform.position = StatPlayer.transform.position + Vector3.up * 2f;
    }

    void Update()
    {
        bool doCheck = true;

        var lastLat = _lastPlayerPosition.x;
        var lastLon = _lastPlayerPosition.y;

        // Player not moving
        if (lastLat == LocationManager.Latitude && lastLon == LocationManager.Longitude)
        {
            doCheck = false;
        }

        _lastPlayerPosition = new(LocationManager.Latitude, LocationManager.Longitude);

        if (_forceUpdateTimer >= 1f)
        {
            _forceUpdateTimer = 0f;
            doCheck = true;
        }

        _forceUpdateTimer += Time.deltaTime;

        if (/*_queryScript.checkPOIDone &&*/ doCheck)
        {
            CheckIfPlayerNearPOI();
        }
    }

    public void CheckIfPlayerNearPOI()
    {
        _collectablePOI = null;

        Image buttonImage = _arButton.GetComponent<Image>();

        if (_poiManager.POICount == 0)
        {
            buttonImage.sprite = _noneSprite;
            _arButton.interactable = false;
            return;
        }

        var ruler = new CheapRuler(LocationManager.Latitude, CheapRulerUnits.Meters);

        _rulerPositionPlayer[0] = LocationManager.Latitude;
        _rulerPositionPlayer[1] = LocationManager.Longitude;

        for (int i = 0; i < _poiManager.POICount; i++)
        {
            var poi = _poiManager.GetPOI(i);
            _rulerPositionPOI[0] = poi.latitude;
            _rulerPositionPOI[1] = poi.longitude;

            var distance = ruler.Distance(_rulerPositionPlayer, _rulerPositionPOI);

            if (distance <= _distanceToStartDownloadMeters && !_downloadedAlreadyList.Contains(poi.locationId))
            {
                _downloadedAlreadyList.Add(poi.locationId);
                StartCoroutine(_arDownloader.QueryAndDownload(poi.locationId));
            }
            if (distance <= _distanceToCollectPOIMeters && !_arDownloader.isrunning)
            {
                _arButton.interactable = true;
                buttonImage.sprite = _normalSprite;
                _collectablePOI = poi;
                return;
            }
        }
        _arButton.interactable = false;
        buttonImage.sprite = _greySprite;
    }

    public void GoAR()
    {
        if(_collectablePOI != null && !_arDownloader.isrunning){
            PlayerPrefs.SetInt("locID", _collectablePOI.locationId);
            SceneManager.LoadScene("AR");
        }
    }

    IEnumerator SendLoginPoints()
    {
        string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/points/login";
        UnityWebRequest request = new UnityWebRequest(url, "POST");
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[LoginPoints] Failed: {request.responseCode} - {request.error}");
        }
        else
        {
            Debug.Log("[LoginPoints] Points awarded successfully.");
        }
        StartCoroutine(FetchLoginPoints());
    }

    // Restored to your working version
    IEnumerator FetchLoginPoints()
    {
        string jwt = UserInfoSingleton.Instance.AccessToken;
        if (string.IsNullOrEmpty(jwt))
        {
            Debug.LogError("No JWT found in UserSession.");
            yield break;
        }

        UnityWebRequest request = UnityWebRequest.Get("https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/query/login-points");
        request.SetRequestHeader("Authorization", "Bearer " + jwt);

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string json = request.downloadHandler.text;
            LoginPointsResponse response = JsonUtility.FromJson<LoginPointsResponse>(json);

            PlayerPrefs.SetInt("LoginPoints", response.points);
            PlayerPrefs.Save();

            if (_pointsText != null)
                _pointsText.text = response.points.ToString();
        }
        else
        {
            Debug.LogError("Failed to fetch login points: " + request.error);
        }
    }

    [System.Serializable]
    private class LoginPointsResponse
    {
        public int points;
    }


    bool IsSFXMuted()
    {
        return PlayerPrefs.GetInt("SFXMuted", 0) == 1;
    }
}
