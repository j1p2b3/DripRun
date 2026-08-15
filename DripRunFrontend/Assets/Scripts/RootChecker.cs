using UnityEngine;
using UnityEngine.SceneManagement; // Needed for SceneManager
using System.Collections;
using System.IO;
using System;
using System.Text;
using UnityEngine.Networking;

public class RootDetection : MonoBehaviour
{
    public AutoSign AutoSign;      // leave AutoSign disabled in the Inspector at edit-time

    public static bool IsDeviceRooted()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        string[] paths = {
            "/sbin/su", "/system/bin/su", "/system/xbin/su",
            "/data/local/xbin/su", "/data/local/bin/su",
            "/system/sd/xbin/su", "/system/bin/failsafe/su",
            "/data/local/su"
        };
        foreach (string p in paths)
            if (File.Exists(p)) return true;

        try {
            using (var build = new AndroidJavaClass("android.os.Build"))
            {
                string tags = build.GetStatic<string>("TAGS");
                if (!string.IsNullOrEmpty(tags) && tags.Contains("test-keys")) return true;
            }
        } catch { /* ignore */ }
#endif
        return false;
    }

    private IEnumerator Start()
    {
        if (IsDeviceRooted())
        {
            Application.Quit();
            yield break;
        }

        // Let the engine present one frame with the splash BEFORE enabling heavy init
        yield return null;                          // 1 frame
        // yield return new WaitForSecondsRealtime(0.05f); // tiny buffer (optional)
        
        AutoSign.enabled = true;
    }



    [Serializable]
    private class WebAuthPayload
    {
        public string email;
        public string name;
        public string accessToken;
    }

    // Called by the WEBSITE using unityInstance.SendMessage(...)
    public void ReceiveAuthJson(string json)
    {
    #if UNITY_WEBGL && !UNITY_EDITOR
        var payload = JsonUtility.FromJson<WebAuthPayload>(json);

        if (payload == null || string.IsNullOrEmpty(payload.accessToken))
        {
            Debug.LogError("[AutoSign] Invalid auth payload received.");
            return; // stay on splash
        }

        UserInfoSingleton.Instance.SetUserInfo("", "", payload.accessToken);
        AutoSign.LoadNextScene();
    #endif
    }

}