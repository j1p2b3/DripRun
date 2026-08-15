using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using UnityEngine.SceneManagement;
#if UNITY_ANDROID
    using Google;
    using Facebook.Unity;
#endif

public class DeleteAcc : MonoBehaviour
{
    public TMP_InputField confirmField; // Assign in Inspector
    public BtnCont btnController;       // 👈 Assign in Inspector (the same one used in your UI)

    public void DeleteAccount()
    {
        if (confirmField.text.Trim().Equals("Confirm", System.StringComparison.OrdinalIgnoreCase))
        {
            PlayerPrefs.DeleteKey("HasLaunchedBefore");
            StartCoroutine(DeleteAccountCoroutine());
        }
        else
        {
            Debug.LogWarning("⚠️ User must type 'Confirm' to delete the account.");
        }
    }

    private IEnumerator DeleteAccountCoroutine()
    {
        string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/user/delete";

        using (UnityWebRequest request = UnityWebRequest.Delete(url))
        {
            request.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success || request.responseCode == 204)
            {
                Debug.Log("✅ Account deleted successfully.");

                // Instead of duplicating logic — call BtnCont.SignOut() to cleanly handle logout across all providers
                if (btnController != null)
                {
                    UserInfoSingleton.Instance.ClearUserInfo();
                    // 2) Wipe local refresh token securely
                    btnController.DeleteRefreshToken();

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
                else
                {
                    Debug.LogWarning("⚠️ BtnCont reference not set! Please assign it in the Inspector.");
                }
            }
            else
            {
                Debug.LogError($"❌ Failed to delete account: {request.responseCode} - {request.error}");
            }
        }
    }
}
