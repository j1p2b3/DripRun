using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.IO;
using TMPro;

public class LeaderboardPrizeImage : MonoBehaviour
{
    public Image prizeImage; // Assign in inspector

    private string prizeQueryUrl => "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/query/leaderboard-prize";
    private string imageApiBase => "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/download/image/";

    void Start()
    {
        StartCoroutine(GetPrizeImage());
    }

    IEnumerator GetPrizeImage()
    {
        UnityWebRequest request = UnityWebRequest.Get(prizeQueryUrl);
        request.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Failed to fetch leaderboard prize");
            yield break;
        }

        PrizeResponse response = JsonUtility.FromJson<PrizeResponse>(request.downloadHandler.text);

        if (response == null || string.IsNullOrEmpty(response.prizeImageName))
        {
            Debug.LogError("Invalid leaderboard prize response");
            yield break;
        }

        yield return StartCoroutine(LoadPrizeImage(response.prizeImageName));
    }

    IEnumerator LoadPrizeImage(string imageName)
    {
        string localPath = Path.Combine(Application.persistentDataPath, imageName);

        Sprite sprite;

        if (File.Exists(localPath))
        {
            // Load cached image
            byte[] fileData = File.ReadAllBytes(localPath);
            Texture2D tex = new Texture2D(2, 2);
            tex.LoadImage(fileData);
            sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), Vector2.one * 0.5f);
        }
        else
        {
            // Download from backend
            string imageUrl = imageApiBase + imageName;

            UnityWebRequest imgRequest = UnityWebRequestTexture.GetTexture(imageUrl);
            imgRequest.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");

            yield return imgRequest.SendWebRequest();

            if (imgRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Failed to download prize image: {imageName}");
                yield break;
            }

            Texture2D tex = DownloadHandlerTexture.GetContent(imgRequest);
            sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), Vector2.one * 0.5f);

            // Save to cache
            byte[] bytes = tex.EncodeToPNG();
            File.WriteAllBytes(localPath, bytes);
        }

        // Set the UI image
        prizeImage.sprite = sprite;
        prizeImage.color = Color.white;
    }

    [System.Serializable]
    public class PrizeResponse
    {
        public string prizeImageName;
    }
}