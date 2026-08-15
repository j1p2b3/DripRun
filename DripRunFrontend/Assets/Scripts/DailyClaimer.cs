using System;
using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

public class OnceADayClaimUI : MonoBehaviour
{
    [Header("Endpoint")]
    public string dailyClaimUrl = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/OnceADayCoupon/daily-claim-v2";

    [Header("UI")]
    public Button claimButton; // assign in Inspector

    // PlayerPrefs keys
    const string PP_NextClaimDateLocal = "DR_NextClaimDateLocal"; // "yyyy-MM-dd" (Sydney date)

    [Header("Tiny Prompt (Toast)")]
    public GameObject ToastPanel;     // Bottom-anchored panel (inactive by default)
    public TMP_Text ToastText;        // Child text of the panel

    [Serializable] class SuccessPayload { public int couponId; public int locationId; public string nextDateLocal;}
    [Serializable] class FailPayload { public bool failed; public string nextDateLocal; }

    void Start()
    {
        SetButtonStateFromPrefs();
    }

    void SetButtonStateFromPrefs()
    {
        string next = PlayerPrefs.GetString(PP_NextClaimDateLocal, "");
        bool canClaim = string.IsNullOrEmpty(next) || SydneyTodayOnOrAfter(next);
        if (claimButton) claimButton.interactable = canClaim;
    }

    // Compare device "now" converted to Australia/Sydney with the server's nextDateLocal (Sydney date)
    bool SydneyTodayOnOrAfter(string nextDateLocal)
    {
        if (!DateTime.TryParseExact(nextDateLocal, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var next))
            return true; // if we can't parse, don't block

        DateTime sydneyNow = GetSydneyNow();
        DateTime sydneyToday = sydneyNow.Date;
        return sydneyToday >= next;
    }

    // Try to get Australia/Sydney tz across platforms; fallback to UTC+10 if missing (backend still enforces truth)
    DateTime GetSydneyNow()
    {
        try
        {
            // IANA (Android/iOS/macOS/Linux)
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch { /* try Windows ID */ }

        try
        {
            // Windows ID
            var tz = TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch
        {
            // Fallback (no DST): UTC+10. Good enough for gating UX; backend is authoritative anyway.
            return DateTime.UtcNow.AddHours(10);
        }
    }

    public void StartClaim()
    {
        StartCoroutine(ClaimRoutine());
    }

    IEnumerator ClaimRoutine()
    {
        var req = new UnityWebRequest(dailyClaimUrl, UnityWebRequest.kHttpVerbPOST);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.uploadHandler = new UploadHandlerRaw(new byte[0]);
        req.SetRequestHeader("Accept", "application/json");
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");

        yield return req.SendWebRequest();

        if (req.responseCode == 200 && req.result == UnityWebRequest.Result.Success)
        {
            // { couponId:int, nextDateLocal:"yyyy-MM-dd" }
            var payload = JsonUtility.FromJson<SuccessPayload>(req.downloadHandler.text);
            if (payload != null && !string.IsNullOrEmpty(payload.nextDateLocal))
            {
                PlayerPrefs.SetInt("locID", payload.locationId);
                PlayerPrefs.SetString(PP_NextClaimDateLocal, payload.nextDateLocal);
                Debug.Log($"Claim OK. locationId={payload.locationId}, next={payload.nextDateLocal}");
                SceneManager.LoadScene("AR");
            }
            else
            {
                Debug.LogError("Malformed success payload.");
            }
        }
        else if (req.responseCode == 409)
        {
            // { failed:true, nextDateLocal:"yyyy-MM-dd" }
            var payload = JsonUtility.FromJson<FailPayload>(req.downloadHandler.text);
            if (payload != null && !string.IsNullOrEmpty(payload.nextDateLocal))
            {
                PlayerPrefs.SetString(PP_NextClaimDateLocal, payload.nextDateLocal);
                PlayerPrefs.Save();
                ShowPrompt("Come Back Tomorrow");
                Debug.Log($"Claim blocked. Next date: {payload.nextDateLocal}");
            }
            else
            {
                Debug.LogError("Malformed fail payload.");
            }
        }
        else if (req.responseCode == 401)
        {
            SceneManager.LoadScene("Login");
            yield break;
        }
        else
        {
            Debug.LogError($"Daily claim error: HTTP {req.responseCode} - {req.error}");
        }

        // Refresh button state after any outcome
        SetButtonStateFromPrefs();
    }


    public void ShowPrompt(string message)
    {
        StopCoroutineSafe(nameof(ToastRoutine));
        StartCoroutine(ToastRoutine(message));
    }
    private IEnumerator ToastRoutine(string msg)
    {
        if (ToastPanel != null) ToastPanel.SetActive(true);
        if (ToastText != null) ToastText.text = msg;

        yield return new WaitForSeconds(2f);

        if (ToastPanel != null) ToastPanel.SetActive(false);
    }
    private void StopCoroutineSafe(string routineName)
    {
        try { StopCoroutine(routineName); } catch { }
    }
}
