using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using AppleAuth;
using AppleAuth.Enums;
using AppleAuth.Interfaces;
using AppleAuth.Native;
using TMPro;

public class AppleLoginController : MonoBehaviour
{
    [Header("UI References")]
    public GameObject SplashScreen;   // Spinner / loading
    public GameObject SignInCanvas;   // The canvas with Apple / Google buttons
    public AutoSign ShowSplash;

    [Header("Tiny Prompt (Toast)")]
    public GameObject ToastPanel;
    public TMP_Text ToastText;
    public GameObject ToastPanel2;
    public TMP_Text ToastText2;

    private IAppleAuthManager _appleAuthManager;
    private IOSKeychainSecureStore _secureStore;

    // ───────────────────────── UNITY LIFECYCLE ─────────────────────────
    void Awake()
    {
#if UNITY_IOS
        _secureStore = new IOSKeychainSecureStore();

        if (AppleAuthManager.IsCurrentPlatformSupported)
        {
            _appleAuthManager = new AppleAuthManager(new PayloadDeserializer());
        }
#endif
    }

    void Update()
    {
#if UNITY_IOS
        _appleAuthManager?.Update();
#endif
    }

    // ───────────────────────── PUBLIC UI BUTTON ─────────────────────────
    public void SignInWithApple()
    {
        ShowSplash.ShowSplash();

#if UNITY_IOS
        if (_appleAuthManager == null)
        {
            Fail("Sign-In failed. Please update Drip Run and try again.");
            return;
        }

        var loginArgs = new AppleAuthLoginArgs(LoginOptions.IncludeEmail | LoginOptions.IncludeFullName);

        _appleAuthManager.LoginWithAppleId(
            loginArgs,
            credential =>
            {
                var appleIdCred = credential as IAppleIDCredential;
                if (appleIdCred == null)
                {
                    Fail("Sign-In failed. Please update Drip Run and try again.");
                    return;
                }

                // Capture name (first login only)
                string capturedFullName = null;
                if (appleIdCred.FullName != null)
                {
                    var given  = appleIdCred.FullName.GivenName;
                    var family = appleIdCred.FullName.FamilyName;
                    if (!string.IsNullOrEmpty(given) || !string.IsNullOrEmpty(family))
                        capturedFullName = $"{given} {family}".Trim();
                }

                // Capture email (first login only)
                string capturedEmail = string.IsNullOrEmpty(appleIdCred.Email) ? null : appleIdCred.Email;

                var idToken = appleIdCred.IdentityToken;
                if (idToken == null || idToken.Length == 0)
                {
                    Fail("Sign-In failed. Please update Drip Run and try again.");
                    return;
                }

                string tokenStr = Encoding.UTF8.GetString(idToken);

                // Send token + any name/email to backend
                StartCoroutine(SendIdTokenToBackend(tokenStr, capturedFullName, capturedEmail));
            },
            error =>
            {
                Fail("Sign-In failed. Please update Drip Run and try again.");
            });
#else
        Fail("Sign-In failed. Please update Drip Run and try again.");
#endif
    }

    // ───────────────────────── BACKEND POST ─────────────────────────
    private IEnumerator SendIdTokenToBackend(string idToken, string fullNameOptional, string emailOptional)
    {
        const string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/apple-login-1.5";

        var body = new AppleTokenRequest
        {
            IdToken = idToken,
            FullName = string.IsNullOrWhiteSpace(fullNameOptional) ? null : fullNameOptional,
            Email   = string.IsNullOrWhiteSpace(emailOptional)     ? null : emailOptional
        };

        var json = JsonUtility.ToJson(body);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success && req.responseCode == 200)
            {
                var resp = JsonUtility.FromJson<AuthResponse>(req.downloadHandler.text);

                string finalName =
                    (!string.IsNullOrEmpty(resp.name) && resp.name != "Apple User")
                        ? resp.name
                        : (fullNameOptional ?? "Apple User");

                string finalEmail =
                    !string.IsNullOrEmpty(resp.email)
                        ? resp.email
                        : (emailOptional ?? string.Empty);

                // Save refresh token (trim to avoid stray newlines)
                if (!string.IsNullOrEmpty(resp.refreshToken))
                    _secureStore.SaveRefresh(resp.refreshToken.Trim());

                // Short-lived access token → memory
                UserInfoSingleton.Instance.SetUserInfo(finalEmail, finalName, resp.jwtToken);

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
            else if (req.responseCode == 400)
            {
                Fail("Reset Apple Sign-In consent:\nSettings → Apple ID → Password & Security → Apps Using Apple ID → DripRun → Stop Using Apple ID.\nThen sign in again.");
            }
            else
            {
#if UNITY_EDITOR
                Debug.LogError($"[AppleLogin] Backend login failed: {req.responseCode} | {req.downloadHandler.text}");
#endif
                Fail("Sign-In failed. Please update Drip Run and try again.");
            }
        }
    }

    // ───────────────────────── TOAST / PROMPT ─────────────────────────
    public void ShowPrompt(string message)
    {
        StopCoroutineSafe(nameof(ToastRoutine));
        StartCoroutine(ToastRoutine(message));
    }

    private IEnumerator ToastRoutine(string msg)
    {
        if (ToastPanel  != null) ToastPanel.SetActive(true);
        if (ToastText   != null) ToastText.text = msg;
        if (ToastPanel2 != null) ToastPanel2.SetActive(true);
        if (ToastText2  != null) ToastText2.text = msg;

        yield return new WaitForSeconds(4f);

        if (ToastPanel  != null) ToastPanel.SetActive(false);
        if (ToastPanel2 != null) ToastPanel2.SetActive(false);
    }

    private void StopCoroutineSafe(string routineName)
    {
        try { StopCoroutine(routineName); } catch { }
    }

    // ───────────────────────── HELPERS ─────────────────────────
    private void Fail(string message)
    {
        #if UNITY_EDITOR
        Debug.LogWarning($"[AppleLogin] {message}");
        #endif
        ShowPrompt(message);
        ShowSplash.ShowLoginUI();
    }

    // ───────────────────────── DTOs ─────────────────────────
    [System.Serializable]
    private class AppleTokenRequest
    {
        public string IdToken;
        public string FullName;
        public string Email;
    }

    [System.Serializable]
    private class AuthResponse
    {
        // Match backend payload
        public string email;
        public string name;
        public string jwtToken;
        public string refreshToken;
    }
}
