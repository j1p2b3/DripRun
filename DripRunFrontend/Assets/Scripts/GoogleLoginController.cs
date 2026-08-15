using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using UnityEngine.SceneManagement; 
using System.Threading.Tasks;
using UnityEngine.Rendering;

#if UNITY_ANDROID
using Google;
#endif

using TMPro;

public class GoogleLoginController : MonoBehaviour
{
    [Header("UI References")]
    public GameObject SplashScreen;
    public GameObject SignIn;
    public AutoSign ShowSplash;

    [Header("Tiny Prompt (Toast)")]
    public GameObject ToastPanel;
    public TMP_Text   ToastText;
    public GameObject ToastPanel2;
    public TMP_Text   ToastText2;

    private string _clientId = "1044348217685-0ab15pj9dlv89q263edlsi5op1skc1hm.apps.googleusercontent.com";
    private static bool isGoogleConfigSet = false;

#if UNITY_ANDROID
    public void SignInWithGoogle()
    {
        ShowSplash.ShowSplash();

        if (!isGoogleConfigSet)
        {
            GoogleSignIn.Configuration = new GoogleSignInConfiguration
            {
                WebClientId   = _clientId,
                RequestIdToken = true,
                RequestEmail   = true
            };
            isGoogleConfigSet = true;
        }

        GoogleSignIn.DefaultInstance.SignIn().ContinueWith(OnAuthenticationFinished);
    }

    private void OnAuthenticationFinished(Task<GoogleSignInUser> task)
    {
        if (task.IsCanceled)
        {
            Debug.Log("Google sign-in canceled");
            Fail("Sign-In failed. Download the latest version of Drip Run and try again");
            return;
        }

        if (task.IsFaulted)
        {
            Debug.LogError("Google sign-in faulted.");
            Fail("Sign-In failed. Download the latest version of Drip Run and try again");
            return;
        }

        var user = task.Result;
        if (user == null || string.IsNullOrEmpty(user.IdToken))
        {
            Debug.LogError("Google sign-in returned no user or missing IdToken.");
            Fail("Sign-In failed. Download the latest version of Drip Run and try again");
            return;
        }

        StartCoroutine(SendTokenRequest(user.IdToken, user.Email, user.DisplayName)); // 🔸 Pass email/name too
    }

    private IEnumerator SendTokenRequest(string idToken, string email, string name)
    {
        const string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/google-login-1.5";

        var body = new GoogleTokenRequest { IdToken = idToken };
        string jsonPayload = JsonUtility.ToJson(body);
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonPayload);

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success && request.responseCode == 200)
            {
                string response = request.downloadHandler.text;
                var authResponse = JsonUtility.FromJson<AuthResponse>(response);

                // 🔸 Store refresh token securely
                if (!string.IsNullOrEmpty(authResponse.refreshToken))
                    SecureStoreAndroidKeystore.SetString("dr_refresh_token", authResponse.refreshToken.Trim());

                // 🔸 Store access token in memory (and name/email if provided)
                UserInfoSingleton.Instance.SetUserInfo(
                    !string.IsNullOrEmpty(authResponse.email) ? authResponse.email : email,
                    !string.IsNullOrEmpty(authResponse.name) ? authResponse.name : name,
                    authResponse.jwtToken
                );

                // 🔸 No more PlayerPrefs("LoginProvider")
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
                Debug.LogError($"Google Authentication failed. HTTP={request.responseCode} Body={request.downloadHandler.text}");
                Fail("Sign-In failed. Download the latest version of Drip Run and try again");
            }
        }
    }
#endif

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
}

// ───────────────────────── DTOs ─────────────────────────
[System.Serializable]
public class GoogleTokenRequest
{
    public string IdToken;
}

[System.Serializable]
public class AuthResponse
{
    public string email;
    public string name;
    public string jwtToken;
    public string refreshToken; // 🔸 Added to match backend
}
