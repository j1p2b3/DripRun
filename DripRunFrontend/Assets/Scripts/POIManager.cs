using Mapbox.Unity.Map;
using System.Collections.Generic;
using UnityEngine;

public class POIManager : MonoBehaviour
{
    //public delegate void POIEvent(POI poi);
    //public event POIEvent POIRegistered;

    [SerializeField]
    AbstractMap _map;

    List<POI> _visiblePOIs = new();

    public int POICount => _visiblePOIs.Count;

    public class POI
    {
        public int locationId;
        public double latitude;
        public double longitude;
        public GameObject sprite;
    }

    public void UpdatePOISpritePositions()
    {
        foreach (var poi in _visiblePOIs)
        {
            UpdatePOISpritePosition(poi);
        }
    }

    public void UpdatePOISpritePosition(POI poi)
    {
        var sprite = poi.sprite;
        if (sprite == null)
            return;

        var position = _map.GeoToWorldPosition(new(poi.latitude, poi.longitude));
        position.y = 0.1f;
        sprite.transform.position = position;
    }

    public void ClearAndDestroyAll()
    {
        foreach (var poi in _visiblePOIs)
        {
            var sprite = poi.sprite;
            if (sprite != null)
            {
                Destroy(sprite);
            }
        }

        _visiblePOIs.Clear();
    }

    public void Register(int locationId, double latitude, double longitude, GameObject sprite)
    {
        var poi = new POI()
        {
            locationId = locationId,
            latitude = latitude,
            longitude = longitude,
            sprite = sprite
        };

        _visiblePOIs.Add(poi);

        UpdatePOISpritePosition(poi);
        //POIRegistered?.Invoke(poi);
    }

    public POI GetPOI(int index)
    {
        if (index < 0 || index > POICount - 1)
            return null;

        return _visiblePOIs[index];
    }
}
