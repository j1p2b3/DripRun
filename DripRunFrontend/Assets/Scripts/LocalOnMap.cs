using Mapbox.Unity.Map;
using Mapbox.Utils;
using UnityEngine;
public class LocalOnMap : MonoBehaviour
{
    public AbstractMap Map;

    void Update()
    {
        var latitude = LocationManager.Latitude;
        var longitude = LocationManager.Longitude;
        Vector3 position = Map.GeoToWorldPosition(new Vector2d(latitude, longitude), true);
        position.y = 0.2f;
        transform.position = position;
    }
}
