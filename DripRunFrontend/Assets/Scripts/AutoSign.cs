using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

public class AutoSign : MonoBehaviour
{
    public GameObject LoginCanv;
    public GameObject AutoSignIn;

    public GameObject appleButton;
    public GameObject googleButton;
    public GameObject facebookButton;
    public GameObject SplashScreen;

    #if UNITY_IOS
    private IOSKeychainSecureStore _secureStore;
    #endif

    void Awake()
    {
        #if UNITY_IOS
        _secureStore = new IOSKeychainSecureStore();
        #endif
    }

    void Start()
    {
        // 🔸 Removed LoginProvider check — we only rely on refresh now
        ShowSplash();
            #if UNITY_WEBGL && !UNITY_EDITOR 
                return;
            #endif
            #if UNITY_IOS
                var storedRefresh = _secureStore.LoadRefresh();
                if (!string.IsNullOrEmpty(storedRefresh))
                {
                    StartCoroutine(TryAppleRefresh(storedRefresh));
                    return;
                }
            #elif UNITY_ANDROID
                var storedRefresh = SecureStoreAndroidKeystore.GetString("dr_refresh_token");
                if (!string.IsNullOrEmpty(storedRefresh))
                {
                    StartCoroutine(TryAndroidRefresh(storedRefresh)); // 🔸 Added
                    return;
                }
            #endif
            ShowLoginUI();
    }

#if UNITY_IOS
    private IEnumerator TryAppleRefresh(string refreshToken)
    {
        const string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/refresh-1.5";

        string cleanToken = (refreshToken ?? string.Empty).Trim().Replace("\r", "").Replace("\n", "");
        if (string.IsNullOrEmpty(cleanToken))
        {
            ShowLoginUI();
            yield break;
        }

        var body = new RefreshRequest { RefreshToken = cleanToken };
        var json = JsonUtility.ToJson(body);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success && req.responseCode == 200)
            {
                var resp = JsonUtility.FromJson<RefreshResponse>(req.downloadHandler.text);

                if (!string.IsNullOrEmpty(resp.refreshToken))
                    _secureStore.SaveRefresh(resp.refreshToken.Trim());

                UserInfoSingleton.Instance.SetUserInfo("", "", resp.accessToken);
                LoadNextScene();
                yield break;
            }
            ShowLoginUI();
        }
    }
#endif

#if UNITY_ANDROID
    private IEnumerator TryAndroidRefresh(string refreshToken)
    {
        const string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/refresh-1.5"; // 🔸 same as Apple

        string cleanToken = (refreshToken ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(cleanToken))
        {
            ShowLoginUI();
            yield break;
        }

        var body = new RefreshRequest { RefreshToken = cleanToken };
        var json = JsonUtility.ToJson(body);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success && req.responseCode == 200)
            {
                var resp = JsonUtility.FromJson<RefreshResponse>(req.downloadHandler.text);

                if (!string.IsNullOrEmpty(resp.refreshToken))
                    SecureStoreAndroidKeystore.SetString("dr_refresh_token", resp.refreshToken.Trim());

                UserInfoSingleton.Instance.SetUserInfo("", "", resp.accessToken);
                LoadNextScene();
                yield break;
            }
            ShowLoginUI();
        }
    }
#endif

    public void LoadNextScene()
    {
        if (!PlayerPrefs.HasKey("HasLaunchedBefore"))
        {
            PlayerPrefs.SetInt("HasLaunchedBefore", 1);
            PlayerPrefs.Save();
            SceneManager.LoadScene("TourScene");
        }
        else
        {
            SceneManager.LoadScene("Main");
        }
    }

    public void ShowLoginUI()
    {
    #if UNITY_IOS
            if (facebookButton) facebookButton.SetActive(false);
            if (googleButton) googleButton.SetActive(false);
            if (appleButton) appleButton.SetActive(true);
    #else
            if (appleButton)    appleButton.SetActive(false);
            if (facebookButton) facebookButton.SetActive(true);
            if (googleButton)   googleButton.SetActive(true);
    #endif
        if (SplashScreen) SplashScreen.SetActive(false);
        if (LoginCanv) LoginCanv.SetActive(true);
    }
    
    public void ShowSplash()
    {
        SplashScreen.SetActive(true);
        LoginCanv.SetActive(false);
    }

    [Serializable]
    private class RefreshRequest { public string RefreshToken; }

    [Serializable]
    private class RefreshResponse
    {
        public string accessToken;
        public string refreshToken;
        public long accessExpiresInSec;
    }
}
