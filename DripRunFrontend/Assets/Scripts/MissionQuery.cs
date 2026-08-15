using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

public class MissionQuery : MonoBehaviour
{
    public MissionDisplayData[] Missions;
    public GameObject PopulateCanvas; // This gets activated when missions are ready to display

    public void Start()
    {
        StartCoroutine(FetchMissions());
    }

    public IEnumerator FetchMissions()
    {
        string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/query/missionsquery";

        UnityWebRequest request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string json = request.downloadHandler.text;
            Missions = JsonHelper.FromJson<MissionDisplayData>(json);
            PopulateCanvas.SetActive(true);
        }
        else if (request.responseCode == 401)
        {
            SceneManager.LoadScene("Login");
            yield break;
        }
        else
        {
            Debug.LogError($"Error fetching missions: {request.error}");
        }
    }

    // JSON array helper (same as your coupon script)
    public static class JsonHelper
    {
        public static T[] FromJson<T>(string json)
        {
            string wrappedJson = "{\"array\":" + json + "}";
            Wrapper<T> wrapper = JsonUtility.FromJson<Wrapper<T>>(wrappedJson);
            return wrapper.array;
        }

        [System.Serializable]
        private class Wrapper<T>
        {
            public T[] array;
        }
    }

    // DTO to match the backend fields exactly
    [System.Serializable]
    public class MissionDisplayData
    {
        public string name;
        public string status;
        public string imgName;
        public string locationName;
        public string couponProgress; // <-- new: "X/Y" from backend
        // (If you later need raw numbers, we can add owned/total and parse them backend-side.)
    }

    // Getters for PopulateCanvas to access array data
    public int GetMissionLength()
    {
        return Missions.Length;
    }

    public string GetMissionName(int i)
    {
        return Missions[i].name;
    }

    public string GetMissionStatus(int i)
    {
        return Missions[i].status;
    }

    public string GetMissionImgName(int i)
    {
        return Missions[i].imgName;
    }

    public string GetMissionLocationName(int i)
    {
        return Missions[i].locationName;
    }

    // NEW helper: returns "X/Y" progress string for a mission
    public string GetMissionProgress(int i)
    {
        return Missions[i].couponProgress;
    }
}
