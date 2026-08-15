using System.Collections;
using System.Text; // for Encoding.UTF8
using UnityEngine;
using TMPro;
using UnityEngine.SceneManagement;
using UnityEngine.Networking;

#if UNITY_ANDROID
using Google;
using Facebook.Unity;
#endif

public class BtnCont : MonoBehaviour
{
    public GameObject HamburgerMenu;
    public GameObject DltCan;
    public TMP_InputField confirmField; // Assign in Inspector

#if UNITY_IOS
    private IOSKeychainSecureStore _secureStore;
#endif

    void Awake()
    {
#if UNITY_IOS
        _secureStore = new IOSKeychainSecureStore();
#endif
    }

    public void GOMAIN() => SceneManager.LoadScene("Main");

    public void SignOut()
    {
        // Clear in-memory access immediately
        UserInfoSingleton.Instance.ClearUserInfo();
        StartCoroutine(HandleSignOut());
    }

    private IEnumerator HandleSignOut()
    {
        // 1) Revoke refresh token family on backend (works for Apple/Google/Meta — agnostic)
        string refresh = LoadRefreshToken();
        if (!string.IsNullOrEmpty(refresh))
        {
            const string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/logout-mobile";
            var body = new RefreshRequest { RefreshToken = refresh.Trim() };
            string json = JsonUtility.ToJson(body);

            using (var req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");

                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success || req.responseCode == 204)
                    Debug.Log("✅ Refresh family revoked.");
                else
                    Debug.LogWarning($"⚠️ Logout endpoint returned {req.responseCode}: {req.downloadHandler.text}");
            }
        }

        // 2) Wipe local refresh token securely
        DeleteRefreshToken();

        // 3) Best-effort: sign out of SDK sessions (agnostic; no provider checks)
#if UNITY_ANDROID
        try { GoogleSignIn.DefaultInstance?.SignOut(); GoogleSignIn.DefaultInstance?.Disconnect(); } catch { }
        try { if (FB.IsInitialized && FB.IsLoggedIn) FB.LogOut(); } catch { }
#endif
        // (No Apple SDK sign-out needed; we only store refresh locally.)

        // 5) Tiny delay lets SDKs finish cleanup
        yield return new WaitForSeconds(0.1f);

        Debug.Log("✅ Signed out. Returning to Login scene.");
        SceneManager.LoadScene("Login");
    }

    // ---- Secure token helpers (platform-specific) ----
    private string LoadRefreshToken()
    {
#if UNITY_IOS
        return _secureStore?.LoadRefresh();
#elif UNITY_ANDROID && !UNITY_EDITOR
        return SecureStoreAndroidKeystore.GetString("dr_refresh_token");
#else
        return PlayerPrefs.GetString("dr_refresh_token", null); // editor fallback
#endif
    }

    public void DeleteRefreshToken()
    {
#if UNITY_IOS
        _secureStore?.DeleteRefresh();
#elif UNITY_ANDROID && !UNITY_EDITOR
        SecureStoreAndroidKeystore.Delete("dr_refresh_token");
        SecureStoreAndroidKeystore.SetString("dr_refresh_token", "");
#else
        PlayerPrefs.DeleteKey("dr_refresh_token"); // editor fallback
#endif
    }

    [System.Serializable]
    private class RefreshRequest { public string RefreshToken; }

    // ───────────────────────── OTHER UI FUNCTIONS ─────────────────────────
    public void GoToCoup() => SceneManager.LoadScene("Coupons");
    public void OpenCloseHamburgerMenu() => HamburgerMenu.SetActive(!HamburgerMenu.activeSelf);
    public void CopyShareLink()
    {
        string text = "Come Join Me And Try Driprun to Earn Rewards \n https://www.driprun.com.au/";
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (AndroidJavaObject clipboardManager = activity.Call<AndroidJavaObject>("getSystemService", "clipboard"))
            using (AndroidJavaClass clipDataClass = new AndroidJavaClass("android.content.ClipData"))
            {
                AndroidJavaObject clip = clipDataClass.CallStatic<AndroidJavaObject>("newPlainText", "label", text);
                clipboardManager.Call("setPrimaryClip", clip);
            }
        }
        catch { Debug.LogError("Clipboard copy failed"); }
#else
        GUIUtility.systemCopyBuffer = text;
        Debug.Log("Copied to clipboard in Editor");
#endif
    }

    public void OpenSettings() => SceneManager.LoadScene("Settings");
    public void OpenMisions() => SceneManager.LoadScene("Missions");
    public void BrandReq() => SceneManager.LoadScene("BrandReq");
    public void OpenLeaderbaord() => SceneManager.LoadScene("Leaderboard");
    public void DeleteUser() => DltCan.SetActive(true);
    public void CnclDel() { confirmField.text = ""; DltCan.SetActive(false); }
    public void OpenTour() => SceneManager.LoadScene("TourScene");
    public void OpenPolicy() => Application.OpenURL("https://www.driprun.com.au/policies");

   public void OnSFXToggleChanged(bool isOn)
    {
        PlayerPrefs.SetInt("SFXMuted", isOn ? 1 : 0);
        PlayerPrefs.Save();
    }
}
