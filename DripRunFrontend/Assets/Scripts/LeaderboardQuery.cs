using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using System.Collections;
using System.Collections.Generic;

[System.Serializable]
public class LeaderboardEntry
{
    public int rank;
    public string name;
    public int points;
    public bool isCurrentUser;
}

[System.Serializable]
public class LeaderboardWrapper
{
    public List<LeaderboardEntry> entries;
}

public class LeaderboardQuery : MonoBehaviour
{
    public GameObject leaderboardEntryPrefab; // Assign in Inspector
    public Transform leaderboardContainer;    // Assign VerticalLayoutGroup in Inspector
   
    private readonly string leaderboardUrl = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/leaderboard";

    private void Start()
    {
        StartCoroutine(FetchLeaderboard());
    }

    private IEnumerator FetchLeaderboard()
    {
        UnityWebRequest request = UnityWebRequest.Get(leaderboardUrl);
        request.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string rawJson = request.downloadHandler.text;
            Debug.Log("Raw leaderboard JSON: " + rawJson);
            string json = "{\"entries\":" + rawJson + "}";
            Debug.Log("Wrapped JSON for parsing: " + json);

            LeaderboardWrapper wrapper = JsonUtility.FromJson<LeaderboardWrapper>(request.downloadHandler.text);

            foreach (Transform child in leaderboardContainer)
                Destroy(child.gameObject);

            foreach (var entry in wrapper.entries)
            {
                GameObject go = Instantiate(leaderboardEntryPrefab, leaderboardContainer);
                TextMeshProUGUI textComp = go.GetComponentInChildren<TextMeshProUGUI>();
                textComp.text = $"{entry.rank}. {entry.name} - {entry.points} pts";

                if (entry.isCurrentUser)
                {
                    Color highlightColor;
                    if (ColorUtility.TryParseHtmlString("#D5E785", out highlightColor)){textComp.color = highlightColor;}else{Debug.LogWarning("Invalid color hex code!");}
                    textComp.fontStyle = FontStyles.Bold | FontStyles.Italic;
                }
            }
        }
        else
        {
            Debug.LogError($"Failed to fetch leaderboard: {request.error}");
        }
    }
}