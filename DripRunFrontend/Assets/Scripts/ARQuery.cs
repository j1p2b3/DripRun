using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

public class ARQuery : MonoBehaviour
{
    private string QueryApiUrl =
        "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/query/ARquery-V3";


    // Single object now (endpoint returns an object, not an array)
    public Location location;

    private int coupID;
    private int locID;

    public GameObject animController;

    void Start()
    {
        locID = PlayerPrefs.GetInt("locID", 0);
        PlayerPrefs.DeleteKey("locID");
        StartCoroutine(GetCoupsFromBackend());
    }

    private IEnumerator GetCoupsFromBackend()
    {
        string url = $"{QueryApiUrl}?locationId={UnityWebRequest.EscapeURL(locID.ToString())}";
        UnityWebRequest request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string response = request.downloadHandler.text;
            Debug.Log("✅ ARquery-v1 success");

            location = JsonUtility.FromJson<Location>(response);

            if (location != null && !string.IsNullOrEmpty(location.modelImgName))
            {
                coupID = location.couponId; // keep this if other scripts use it
                animController.SetActive(true);
            }
            else
            {
                Debug.LogWarning("🟡 No metadata returned (null/empty modelImgName)");
            }
        }
        else if (request.responseCode == 401)
        {
            SceneManager.LoadScene("Login");
            yield break;
        }
        else if (request.responseCode == 403)
        {
            // Rejected due to suspicious travel speed
            Debug.LogWarning($"🚫 ARquery rejected (403). Body: {request.downloadHandler.text}");
            // Optional: take user back / show UI message / etc.
            yield break;
        }
        else
        {
            Debug.LogError($"❌ Error fetching coupon metadata. Code={request.responseCode} Err={request.error}");
        }
    }

    // --- Data models (match backend JSON) ---

    [System.Serializable]
    public class Location
    {
        public int couponId;
        public string modelImgName;
        public string missionImgName;
        public string couponType;
        public string shopifyLink;
        public int couponLevel;
    }


    // --- Getters used by other scripts ---

    public string GetImgModName()
    {
        return location != null ? location.modelImgName : null;
    }

    public string GetCoupType()
    {
        return location != null ? location.couponType : null;
    }

    public int GetlocID()
    {
        return locID;
    }

    public int GetCouponId()
    {
        return location != null ? location.couponId : 0;
    }

    public string GetShopifyLink()
    {
        return location != null ? location.shopifyLink : null;
    }

    public string GetMissionImgName()
    {
        return location != null ? location.missionImgName : null;
    }


    public bool IsRaid()
    {
        if(location.couponLevel == 99) {return true;}
        return false;
    }
    public int GetCouponRarity()
    {
        if(location.couponLevel == 100 || location.couponLevel == 99 || location.couponLevel == 94) {
            return 4;
        }
        if(location.couponLevel == 93) {
            return 3;
        }
        if(location.couponLevel == 92) {
            return 2;
        }
        if(location.couponLevel == 91) {
            return 1;
        }
        if(location.couponLevel <=90) {
            return 0;
        }
        return 0;
    }
}
