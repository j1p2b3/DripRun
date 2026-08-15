using Mapbox.CheapRulerCs;
using Mapbox.Unity.Map;
using UnityEngine;
using UnityEngine.EventSystems;

public class PanZoom : MonoBehaviour, IPointerUpHandler, IPointerDownHandler
{
    [SerializeField]
    Camera _camera;

    [SerializeField]
    AbstractMap _map;

    [SerializeField]
    POIManager _poiManager;

    [SerializeField]
    float _minZoom = 13f;

    [SerializeField]
    float _maxZoom = 20f;

    [SerializeField]
    float _mouseScrollZoomDelta = 0.125f;

    [SerializeField]
    float _touchZoomDelta = 1f;

    [SerializeField]
    float _maxCameraDistanceToPlayer = 1000f;

    [SerializeField]
    float _homeTime = 0.5f;

    int _touchCount;
    readonly PointerEventData[] _touches = new PointerEventData[2];


    float _homeTimer = 0f;
    bool _homing;
    bool _following = true;
    Vector3 _homeStartPosition;

    double[] _rulerPlayerPosition = new double[2];
    double[] _rulerCameraPosition = new double[2];

    // HACK!!!@!

    int _forceSpawnCounter;

    void OnEnable()
    {
        _map.Initialize(new(LocationManager.Latitude, LocationManager.Longitude), (int)_minZoom);
        //_map.SetCenterLatitudeLongitude(new(LocationManager.Latitude, LocationManager.Longitude));
        _map.UpdateMap(_minZoom);
    }

    // HACK!!!!!!
    void FixedUpdate()
    {
        if (_forceSpawnCounter <= 10)
        {
            _forceSpawnCounter++;
            _poiManager.UpdatePOISpritePositions();
        }
    }

    void Update()
    {
        var dragging = _touchCount is 1 or 2 && !_homing;

        // Pan
        if (dragging)
        {
            _following = false;

            var touchOne = _touches[0];
            var dragStart = touchOne.position;
            var delta = touchOne.delta;

            if (_touchCount == 2)
            {
                var touchTwo = _touches[1];
                dragStart += touchTwo.position;
                dragStart /= 2f;
                delta += touchTwo.delta;
                delta /= 2f;
            }

            var dragStartWorld = _camera.ScreenToWorldPoint(dragStart);
            var dragEndWorld = _camera.ScreenToWorldPoint(dragStart + delta);
            var displacement = dragStartWorld - dragEndWorld;
            _camera.transform.position += displacement;

            // Limit pan distance from player location

            if (_maxCameraDistanceToPlayer > 0)
            {
                var ruler = new CheapRuler(LocationManager.Latitude, CheapRulerUnits.Meters);
                _rulerPlayerPosition[0] = LocationManager.Latitude;
                _rulerPlayerPosition[1] = LocationManager.Longitude;

                var cameraPos = _map.WorldToGeoPosition(_camera.transform.position);
                _rulerCameraPosition[0] = cameraPos.x;
                _rulerCameraPosition[1] = cameraPos.y;

                var distance = ruler.Distance(_rulerPlayerPosition, _rulerCameraPosition);

                if (distance > _maxCameraDistanceToPlayer)
                {
                    var bearing = ruler.Bearing(_rulerPlayerPosition, _rulerCameraPosition);
                    var destination = ruler.Destination(_rulerPlayerPosition, _maxCameraDistanceToPlayer, bearing);
                    var clampedCamPos = _map.GeoToWorldPosition(new(destination[0], destination[1]));
                    clampedCamPos.y = _camera.transform.position.y;
                    _camera.transform.position = clampedCamPos;
                }
            }
        }


        // Zoom
        if (dragging && _touchCount == 2)
        {
            var touchOne = _touches[0];
            var touchTwo = _touches[1];

            var prevPosOne = touchOne.position - touchOne.delta;
            var prevPosTwo = touchTwo.position - touchTwo.delta;

            var prevMagnitude = (prevPosOne - prevPosTwo).magnitude;
            var curMagnitude = (touchOne.position - touchTwo.position).magnitude;

            var delta = curMagnitude - prevMagnitude;

            var zoom = _map.Zoom;
            zoom += delta * _touchZoomDelta;
            zoom = Mathf.Clamp(zoom, _minZoom, _maxZoom);

            var camGeoPos = _map.WorldToGeoPosition(_camera.transform.position);

            _map.UpdateMap(zoom);
            _poiManager.UpdatePOISpritePositions();

            var camWorldPos = _map.GeoToWorldPosition(camGeoPos);
            _camera.transform.position = camWorldPos + Vector3.up * 2f;
        }

        // Home

        if (_homing)
        {
            var fraction = 1f - _homeTimer / _homeTime;
            var ease = 1f - Mathf.Pow(1 - fraction, 3f);

            var endPos = _map.GeoToWorldPosition(new(LocationManager.Latitude, LocationManager.Longitude));
            endPos.y = _camera.transform.position.y;
            _camera.transform.position = Vector3.Lerp(_homeStartPosition, endPos, ease);

            Debug.Log($"[Homing] Start: {_homeStartPosition}, End: {endPos}, Time: {_homeTimer}, Fraction: {fraction}");

            _homeTimer -= Time.deltaTime;
            _homeTimer = Mathf.Max(_homeTimer, 0f);
            if (_homeTimer == 0f)
            {
                _homing = false;
                _following = true;
            }
        }

        // Follow

        if (_following)
        {
            var endPos = _map.GeoToWorldPosition(new(LocationManager.Latitude, LocationManager.Longitude));
            endPos.y = _camera.transform.position.y;
            _camera.transform.position = endPos;
        }

        // Zoom with mouse in editor
        var scrollDelta = Input.GetAxis("Mouse ScrollWheel");

        if (scrollDelta != 0f)
        {
            _following = false;

            // get the new zoom value
            var zoom = _map.Zoom;
            zoom += scrollDelta * _mouseScrollZoomDelta;
            zoom = Mathf.Clamp(zoom, _minZoom, _maxZoom);

            // get the location of the camera relative to geo-coords
            var camGeoPos = _map.WorldToGeoPosition(_camera.transform.position);

            _map.UpdateMap(zoom);
            _poiManager.UpdatePOISpritePositions();

            var camWorldPos = _map.GeoToWorldPosition(camGeoPos);
            _camera.transform.position = camWorldPos + Vector3.up * 2f;
        }
    }

    public void StartHome()
    {
        if (_homing)
            return;

        _homing = true;
        _following = false;
        _homeStartPosition = _camera.transform.position;
        _homeTimer = _homeTime;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (_touchCount < 2)
        {
            _touches[_touchCount] = eventData;
        }

        _touchCount++;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        _touchCount--;
        _touchCount = Mathf.Max(_touchCount, 0);
    }
}
