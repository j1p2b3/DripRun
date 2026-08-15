mergeInto(LibraryManager.library, {
  StartWebLocationWatch: function () {
    if (!navigator.geolocation) {
      console.warn("Geolocation not supported");
      SendMessage("LocationManager", "OnWebLocationError", "Geolocation not supported");
      return;
    }

    // Avoid starting multiple watches
    if (Module._dr_watchId !== undefined && Module._dr_watchId !== -1) {
      return;
    }

    Module._dr_watchId = navigator.geolocation.watchPosition(
      function (pos) {
        const lat = pos.coords.latitude;
        const lon = pos.coords.longitude;

        // Send "lat,lon" back into Unity
        SendMessage("LocationManager", "OnWebLocation", lat.toString() + "," + lon.toString());
      },
      function (err) {
        console.warn("Geolocation error", err);
        SendMessage(
          "LocationManager",
          "OnWebLocationError",
          (err && err.message) ? err.message : "Unknown geolocation error"
        );
      },
      {
        enableHighAccuracy: true,
        maximumAge: 0,
        timeout: 10000
      }
    );
  },

  StopWebLocationWatch: function () {
    if (!navigator.geolocation) return;

    if (Module._dr_watchId !== undefined && Module._dr_watchId !== -1) {
      navigator.geolocation.clearWatch(Module._dr_watchId);
      Module._dr_watchId = -1;
    }
  }
});