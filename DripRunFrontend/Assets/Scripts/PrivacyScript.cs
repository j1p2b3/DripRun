using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using System;

public class PrivacyScript : MonoBehaviour
{
    public Toggle privacyToggle;
    public Toggle soundToggle;
    private bool isUpdating = false;

    [Serializable]
    private class PrivacyWrapper
    {
        public bool isPublic;
    }

    [Serializable]
    private class PrivacyRequest
    {
        public bool isPublic;
    }

    private void Start()
    {
        StartCoroutine(FetchPrivacyStatus());
        privacyToggle.onValueChanged.AddListener(OnTogglePrivacy);

        bool isEnabled = PlayerPrefs.GetInt("SFXMuted", 0) == 1;
        soundToggle.SetIsOnWithoutNotify(isEnabled);
    }

    private IEnumerator FetchPrivacyStatus()
    {
        string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/user/privacy";

        UnityWebRequest request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string json = request.downloadHandler.text;
            Debug.Log("Received privacy JSON: " + json);

            // Manual parse to enforce casing
            PrivacyWrapper response = JsonUtility.FromJson<PrivacyWrapper>(json.Replace("isPublic", "isPublic"));

            isUpdating = true;
            Debug.Log("Toggle set to: " + response.isPublic);
            privacyToggle.isOn = response.isPublic;
            isUpdating = false;
        }
        else
        {
            Debug.LogError("Failed to fetch privacy status: " + request.error);
        }
    }

    private void OnTogglePrivacy(bool isPublic)
    {
        if (!isUpdating)
        {
            StartCoroutine(UpdatePrivacyStatus(isPublic));
        }
    }

    private IEnumerator UpdatePrivacyStatus(bool isPublic)
    {
        string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/user/privacy";

        PrivacyRequest payload = new PrivacyRequest { isPublic = isPublic };
        string jsonData = JsonUtility.ToJson(payload);

        UnityWebRequest request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Failed to update privacy status: " + request.error);
        }
    }
}