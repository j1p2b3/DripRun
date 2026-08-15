using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using UnityEngine.SceneManagement;

public class DownloadForAR : MonoBehaviour
{
    private string QueryApiUrl = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/query/ARqueryEarly-V2";
    private string DownloadImgApiUrl = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/download/image/";
    private string DownloadModelApiUrl = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/download/model/file/";

    public bool isrunning = false;

    public Location[] locations;

    public IEnumerator QueryAndDownload(int locationId){
        isrunning = true;
        string url = $"{QueryApiUrl}?locationId={UnityWebRequest.EscapeURL(locationId.ToString())}";
        UnityWebRequest request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken); 

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string response = request.downloadHandler.text;
            Debug.Log($"✅ Response");

            // Deserialize as an array with 1 element
            locations = JsonHelper.FromJson<Location>(response);
        }else if(request.responseCode == 401){
            SceneManager.LoadScene("Login");
            yield break;
        }


        if(locations[0].couponType == "ImgGen" || locations[0].couponType == "ImgGenShopify"){
            StartCoroutine(DownloadImg(locations[0].modelImgName)); // reward
            StartCoroutine(DownloadImg(locations[0].missionImgName)); // 🆕 mission
            Debug.Log("DoneDownloadingFileEarly");

        }else if(locations[0].couponType == "ModelGen" || locations[0].couponType == "ModelGenShopify" || locations[0].couponType == "ModelAudio" ){
            StartCoroutine(DownloadModel());
        }
    }


    private IEnumerator DownloadImg(string imageName){
        string fileName = UnityWebRequest.EscapeURL(imageName); // Safe file name
        string cachedPath = Path.Combine(Application.persistentDataPath, fileName);
        //If file exists locally, load from cache
        if (File.Exists(cachedPath))
        {
            isrunning = false;
            yield break;
        }
        //If not in cache, download from Azure
        string url = $"{DownloadImgApiUrl}{fileName}";
        Debug.Log($"Downloading");
        UnityWebRequest request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);
        yield return request.SendWebRequest();
        if (request.result == UnityWebRequest.Result.Success)
        {
            byte[] imageBytes = request.downloadHandler.data;
            // Save to persistent cache
            File.WriteAllBytes(cachedPath, imageBytes);
            Debug.Log($"Saved image to cache: {cachedPath}");
        }else if(request.responseCode == 401){
            SceneManager.LoadScene("Login");
            yield break;
        }
        isrunning = false;
    }


    private IEnumerator DownloadModel(){
        string fileName = UnityWebRequest.EscapeURL(locations[0].modelImgName); // Safe file name
        string filePath = Path.Combine(Application.persistentDataPath, fileName);
        //If file exists locally, load from cache
        if (File.Exists(filePath))
        {
            Debug.Log("📦 Model already cached");
            // 🔊 If ModelAudio, still ensure audio is cached
            if (locations[0].couponType == "ModelAudio")
            {
                StartCoroutine(DownloadAudio(locations[0].modelImgName));
            }
            isrunning = false;
            yield break;
        }
        string url = $"{DownloadModelApiUrl}{UnityWebRequest.EscapeURL(locations[0].modelImgName)}";
        Debug.Log($"📥 Model Download");
        UnityWebRequest request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Accept-Encoding", "identity");
        yield return request.SendWebRequest();
        if (request.result == UnityWebRequest.Result.Success)
        {
            byte[] modelBytes = request.downloadHandler.data;
            File.WriteAllBytes(filePath, modelBytes);
            // 🔊 Download audio alongside model if needed
            if (locations[0].couponType == "ModelAudio")
            {
                StartCoroutine(DownloadAudio(locations[0].modelImgName));
            }
            FileInfo info = new FileInfo(filePath);
        }else if(request.responseCode == 401){
            SceneManager.LoadScene("Login");
            yield break;
        }
        isrunning = false;
    }

    private IEnumerator DownloadAudio(string modelName)
    {
        string audioName = Path.GetFileNameWithoutExtension(modelName) + ".mp3";
        string filePath = Path.Combine(Application.persistentDataPath, audioName);

        if (File.Exists(filePath))
        {
            Debug.Log($"🔊 Audio already cached: {audioName}");
            yield break;
        }

        string url = $"{DownloadModelApiUrl}{UnityWebRequest.EscapeURL(audioName)}";
        Debug.Log($"📥 Audio Download");

        UnityWebRequest request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            byte[] audioBytes = request.downloadHandler.data;
            File.WriteAllBytes(filePath, audioBytes);

            Debug.Log($"💾 Saved audio to cache: {filePath}");
        }
        else if (request.responseCode == 401)
        {
            SceneManager.LoadScene("Login");
            yield break;
        }
        else
        {
            Debug.LogError("❌ Error downloading audio: " + request.error);
        }
    }

   // Define a class to represent the Location structure
    [System.Serializable]
    public class Location
    {
        public string modelImgName;
        public string missionImgName;
        public string couponType;
    }

    // Helper class to parse the JSON array response into an array of objects
    public static class JsonHelper
    {
        // Method to parse JSON array into an array of objects
        public static T[] FromJson<T>(string json)
        {
            // Wrap the JSON string in brackets and parse it as an array
            string wrappedJson = "{\"array\":" + json + "}";
            Wrapper<T> wrapper = JsonUtility.FromJson<Wrapper<T>>(wrappedJson);
            return wrapper.array;
        }

        // Wrapper class to help parse the array response
        [System.Serializable]
        private class Wrapper<T>
        {
            public T[] array;
        }
    }
}
