using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using System.IO;
using TMPro;

public class PopulateMissionCanv : MonoBehaviour
{
    public GameObject missionEntryPrefab;
    public Transform contentContainer; // Drag in your ScrollView/Content
    public MissionQuery missionQueryScript;
    private string imageApiBase => "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/download/image/";

    void OnEnable()
    {
        StartCoroutine(PopulateUI());
    }

    IEnumerator PopulateUI()
    {
        for (int i = 0; i < missionQueryScript.GetMissionLength(); i++)
        {
            string imageName = missionQueryScript.GetMissionImgName(i);

            // Prepare local cache path
            string localPath = Path.Combine(Application.persistentDataPath, imageName);

            Sprite sprite;

            if (File.Exists(localPath))
            {
                // Load from disk cache
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
                    Debug.LogError($"Image download failed: {imageName}");
                    if (imgRequest.responseCode == 401)
                    {
                        SceneManager.LoadScene("Login");
                        yield break;
                    }
                    continue;
                }

                Texture2D tex = DownloadHandlerTexture.GetContent(imgRequest);
                sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), Vector2.one * 0.5f);

                // Cache to disk
                byte[] bytes = tex.EncodeToPNG();
                File.WriteAllBytes(localPath, bytes);
            }

            // Instantiate and populate UI
            GameObject entry = Instantiate(missionEntryPrefab, contentContainer);

            Image imgComponent = entry.transform.Find("MissionImage").GetComponent<Image>();
            imgComponent.sprite = sprite;

            // Find and set the TitleName text
            TMP_Text titleText = entry.transform.Find("TitleName").GetComponent<TMP_Text>();
            titleText.text = missionQueryScript.GetMissionName(i);

            // Find and set the Info text
            TMP_Text infoText = entry.transform.Find("TitleName/Info").GetComponent<TMP_Text>();
            infoText.text =
                $"Location: {missionQueryScript.GetMissionLocationName(i)}\n" +
                $"Mission Status: {missionQueryScript.GetMissionStatus(i)}";

            // ✅ NEW: Find and set the CollectedLbl text
            TMP_Text collectedText = entry.transform.Find("TitleName/CollectedLbl").GetComponent<TMP_Text>();
            collectedText.text = $"Collected: {missionQueryScript.GetMissionProgress(i)}";

            Debug.Log($"✅ Added mission UI: {missionQueryScript.GetMissionName(i)} | Progress: {missionQueryScript.GetMissionProgress(i)}");
        }
    }
}
