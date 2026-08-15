using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.Text;
using System;
using UnityEngine.SceneManagement;
using TMPro;

public class PostScript : MonoBehaviour
{
    public ARQuery CouponDat;
    public Button claimButton;
    public GameObject waitingRaidText;
    public TMP_Text raidCountText;

    [Serializable]
    public class UserCouponPayload
    {
        public int couponId;
        public int locationId;
    }

    public void ClaimCoupon()
{
    int couponID = CouponDat.GetCouponId();
    int locationID = CouponDat.GetlocID();
    int couponLevel = CouponDat.GetCouponRarity();

    // RAID COUPON
    if (CouponDat.IsRaid())
    {
        // 1) Join raid
        StartCoroutine(PostRaidJoin(couponID, locationID));
        // 2) Disable claim button
        claimButton.interactable = false;
        // 3) Update UI
        waitingRaidText.SetActive(true);
        // 4) Start polling
        StartCoroutine(RaidCheckRoutine(couponID, locationID));
    }
    else
    {
        // NORMAL COUPON
        StartCoroutine(PostUserCoupon(couponID, locationID));
    }
}

IEnumerator PostRaidJoin(int couponID, int locationID)
{
    string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/CouponAdder/raid-join";

    UserCouponPayload dto = new UserCouponPayload
    {
        couponId = couponID,
        locationId = locationID
    };

    string json = JsonUtility.ToJson(dto);

    using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
    {
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();

        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", "Bearer " +  UserInfoSingleton.Instance.AccessToken);

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Raid join failed: " + request.error);
        }
        else
        {
            Debug.Log("Joined raid successfully");
        }
    }
}

IEnumerator RaidCheckRoutine(int couponID, int locationID)
{
    string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/CouponAdder/raid-check";

    while (true)
    {
        UserCouponPayload dto = new UserCouponPayload
        {
            couponId = couponID,
            locationId = locationID
        };

        string json = JsonUtility.ToJson(dto);

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();

            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                string responseText = request.downloadHandler.text;
                Debug.Log("Raid check response: " + responseText);

                RaidCheckResponse response = JsonUtility.FromJson<RaidCheckResponse>(responseText);

                if (response.success)
                {
                    Debug.Log("Raid completed!");

                    // ✅ Exit loop
                    break;
                }
                else
                {
                    // Optional: update UI with count
                    UpdateRaidCountUI(response.currentCount, response.requiredCount);
                }
            }
            else
            {
                Debug.LogError("Raid check failed: " + request.error);
            }
        }

        // ⏱ Wait 3–5 seconds
        yield return new WaitForSeconds(4f);
    }
        SceneManager.LoadScene("Coupons");
}




    private IEnumerator PostUserCoupon(int couponID, int locationID)
    {
    string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/couponadder/v1";

    UserCouponPayload payload = new UserCouponPayload
    {
        couponId = couponID,
        locationId = locationID
    };

    string jsonPayload = JsonUtility.ToJson(payload);
    UnityWebRequest request = new UnityWebRequest(url, "POST");
    byte[] jsonToSend = Encoding.UTF8.GetBytes(jsonPayload);
    request.uploadHandler = new UploadHandlerRaw(jsonToSend);
    request.downloadHandler = new DownloadHandlerBuffer();
    request.SetRequestHeader("Content-Type", "application/json");
    request.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");

    yield return request.SendWebRequest();

    if (request.result == UnityWebRequest.Result.Success)
    {
        Debug.Log("Coupon claimed successfully!");
    }
    else if (request.responseCode == 401)
    {
        SceneManager.LoadScene("Login");
        yield break;
    }
    else
    {
        Debug.LogError($"Error claiming coupon");
    }
}






//RAID HELPER FUNCTIONS
void UpdateRaidCountUI(int current, int required)
{
    raidCountText.text = current + " / " + required + " players";
}

[System.Serializable]
public class RaidCheckResponse
{
    public bool success;
    public int currentCount;
    public int requiredCount;
    public bool alreadyOwned;
}

}
