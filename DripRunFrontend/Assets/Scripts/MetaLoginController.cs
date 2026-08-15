using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
#if UNITY_ANDROID
using Facebook.Unity;
#endif
using TMPro;

public class MetaLoginController : MonoBehaviour
{
    [Header("UI References")]
    public GameObject SplashScreen;
    public GameObject SignIn;
    public AutoSign ShowSplash;

    [Header("Tiny Prompt (Toast)")]
    public GameObject ToastPanel;
    public TMP_Text ToastText;
    public GameObject ToastPanel2;
    public TMP_Text ToastText2;

    private bool fbReady = false;
    private bool pendingLogin = false;

#if UNITY_ANDROID
    void Awake()
    {
        if (!FB.IsInitialized)
        {
            FB.Init(OnFBInit, OnHideUnity);
        }
        else
        {
            OnFBInit();
        }
    }

    private void OnFBInit()
    {
        fbReady = true;
        FB.ActivateApp();
        Debug.Log("FB.Init complete");

        if (pendingLogin)
        {
            pendingLogin = false;
            StartCoroutine(LoginFlow());
        }
    }

    public void SignInWithFacebook()
    {
        Debug.Log("[MetaLogin] SignInWithFacebook() CALLED");
        if (!fbReady)
        {
            pendingLogin = true;
            Debug.LogWarning("FB SDK not ready yet — deferring login until init completes.");
            ShowSplash.ShowLoginUI();
            return;
        }

        StartCoroutine(LoginFlow());
    }

    private IEnumerator LoginFlow()
    {
        ShowSplash.ShowSplash();

        if (FB.IsLoggedIn) FB.LogOut();
        yield return null;

        var perms = new List<string> { "public_profile", "email" };
        bool callbackReturned = false;

        FB.LogInWithReadPermissions(perms, result =>
        {
            callbackReturned = true;
            AuthCallback(result);
        });

        float safetyTimeout = 30f;
        while (!callbackReturned && safetyTimeout > 0f)
        {
            safetyTimeout -= Time.unscaledDeltaTime;
            yield return null;
        }

        if (!callbackReturned)
        {
            Debug.LogError("FB login timed out (no callback).");
            Fail("Sign-In failed. Download the latest version of Drip Run and try again");
        }
    }

    private void AuthCallback(ILoginResult result)
    {
        Debug.Log($"FB login callback: IsLoggedIn={FB.IsLoggedIn} Cancelled={result?.Cancelled} Error={result?.Error}");

        if (!string.IsNullOrEmpty(result?.Error))
        {
            Debug.LogError("FB login error: " + result.Error);
            Fail("Sign-In failed. Download the latest version of Drip Run and try again");
            return;
        }

        if (result?.Cancelled == true)
        {
            Debug.Log("FB login cancelled by user.");
            Fail("Sign-In failed. Download the latest version of Drip Run and try again");
            return;
        }

        var at = AccessToken.CurrentAccessToken;
        if (!FB.IsLoggedIn || at == null || string.IsNullOrEmpty(at.TokenString))
        {
            Debug.LogError("FB login finished but token is null/empty.");
            Fail("Sign-In failed. Download the latest version of Drip Run and try again");
            return;
        }

        var accessToken = at.TokenString;
        StartCoroutine(SendOAuthTokenToBackend(accessToken));

        FB.API("/me?fields=id,name,email", HttpMethod.GET, OnFacebookUserInfoReceived);
    }

    private void OnFacebookUserInfoReceived(IGraphResult res)
    {
        Debug.Log($"Graph /me -> Error={res.Error} Cancelled={res.Cancelled} Raw={res.RawResult}");
        if (!string.IsNullOrEmpty(res.Error)) return;

        string email = null, name = null, id = null;
        if (res.ResultDictionary != null)
        {
            if (res.ResultDictionary.TryGetValue("email", out var e)) email = e as string;
            if (res.ResultDictionary.TryGetValue("name", out var n)) name = n as string;
            if (res.ResultDictionary.TryGetValue("id", out var i)) id = i as string;
        }
        Debug.Log($"FB /me parsed -> id={id} name={name} email={email}");
    }

    private IEnumerator SendOAuthTokenToBackend(string accessToken)
    {
        const string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/facebook-login-1.5";

        var req = new UnityWebRequest(url, "POST");
        req.SetRequestHeader("Content-Type", "application/json");

        var body = JsonUtility.ToJson(new FacebookTokenRequest { AccessToken = accessToken });
        req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success && req.responseCode == 200)
        {
            var response = req.downloadHandler.text;
            var auth = JsonUtility.FromJson<AuthResponse>(response);

            // 🔸 Save refresh token securely
            if (!string.IsNullOrEmpty(auth.refreshToken))
                SecureStoreAndroidKeystore.SetString("dr_refresh_token", auth.refreshToken.Trim());

            // 🔸 Save in-memory user info
            UserInfoSingleton.Instance.SetUserInfo(auth.email, auth.name, auth.jwtToken);

            // 🔸 No PlayerPrefs("LoginProvider")
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
        else
        {
            Debug.LogError($"Facebook Authentication failed. HTTP={req.responseCode} Body={req.downloadHandler.text}");
            Fail("Sign-In failed. Download the latest version of Drip Run and try again");
        }
    }

    // ───────────────────────── HELPERS ─────────────────────────
    private void Fail(string message)
    {
        Debug.LogWarning(message);
        ShowPrompt(message);
        ShowSplash.ShowLoginUI();
    }

    public void ShowPrompt(string message)
    {
        StopCoroutineSafe(nameof(ToastRoutine));
        StartCoroutine(ToastRoutine(message));
    }

    private IEnumerator ToastRoutine(string msg)
    {
        if (ToastPanel != null) ToastPanel.SetActive(true);
        if (ToastText  != null) ToastText.text = msg;
        if (ToastPanel2 != null) ToastPanel2.SetActive(true);
        if (ToastText2  != null) ToastText2.text = msg;

        yield return new WaitForSeconds(4f);

        if (ToastPanel != null) ToastPanel.SetActive(false);
        if (ToastPanel2 != null) ToastPanel2.SetActive(false);
    }

    private void StopCoroutineSafe(string routineName)
    {
        try { StopCoroutine(routineName); } catch { }
    }

    private void OnHideUnity(bool isGameShown) => Time.timeScale = isGameShown ? 1 : 0;

    // ───────────────────────── DTOs ─────────────────────────
    [System.Serializable] public class FacebookTokenRequest { public string AccessToken; }

    [System.Serializable]
    public class AuthResponse
    {
        public string email;
        public string name;
        public string jwtToken;
        public string refreshToken; // 🔸 Added for parity
    }

#endif
}
