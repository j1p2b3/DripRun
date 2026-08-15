using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Networking;
using System;
using System.IO;
using TriLibCore;
using TriLibCore.General;
using TriLibCore.Extensions;
using UnityEngine.SceneManagement;

public class ARInstant : MonoBehaviour
{
    private string DownloadImgApiUrl = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/download/image/";
    private string DownloadModelApiUrl = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/download/model/file/";


    public IEnumerator DownloadImageFromBackEnd(string imageName, Action<Texture2D> onComplete)
    {
        string localPath = Path.Combine(Application.persistentDataPath, imageName);

        if (File.Exists(localPath))
        {
            // Load from disk
            byte[] localBytes = File.ReadAllBytes(localPath);
            Texture2D texture = new Texture2D(2, 2);
            texture.LoadImage(localBytes);
            onComplete?.Invoke(texture);
        }
        else
        {
            // Download from backend
            string url = $"{DownloadImgApiUrl}{UnityWebRequest.EscapeURL(imageName)}";

            UnityWebRequest request = UnityWebRequest.Get(url);
            request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                byte[] imageBytes = request.downloadHandler.data;

                // Save to persistent path
                File.WriteAllBytes(localPath, imageBytes);

                // Load into texture
                Texture2D texture = new Texture2D(2, 2);
                texture.LoadImage(imageBytes);
                onComplete?.Invoke(texture);
            }
            else if(request.responseCode == 401)
            {
                SceneManager.LoadScene("Login");
                yield break;
            }else{Debug.LogError("Error downloading image" );}
        }
    }

    public IEnumerator DownloadAndLoadModel(string modelName, Action<GameObject> onComplete)
    {
        string filePath = Path.Combine(Application.persistentDataPath, modelName);

        if (File.Exists(filePath))
        {
            FileInfo info = new FileInfo(filePath);
            Debug.Log($"📦 Model already exists: {filePath} ({info.Length} bytes)");

            AssetLoader.LoadModelFromFile(
                filePath,
                onLoad: (AssetLoaderContext context) =>
                {
                    Debug.Log("✅ Model loaded successfully from local storage");
                    onComplete?.Invoke(context.RootGameObject);
                },
                onError: (IContextualizedError error) =>
                {
                    Debug.LogError("❌ TriLib failed to load model from local file: " + error?.ToString());
                    onComplete?.Invoke(null);
                }
            );
        }
        else
        {
            string url = $"{DownloadModelApiUrl}{UnityWebRequest.EscapeURL(modelName)}";
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

                FileInfo info = new FileInfo(filePath);
                Debug.Log($"📦 Model downloaded and saved to: {filePath} ({info.Length} bytes)");

                AssetLoader.LoadModelFromFile(
                    filePath,
                    onLoad: (AssetLoaderContext context) =>
                    {
                        Debug.Log("✅ Model loaded successfully");
                        onComplete?.Invoke(context.RootGameObject);
                    },
                    onError: (IContextualizedError error) =>
                    {
                        Debug.LogError("❌ TriLib failed to load model: " + error?.ToString());
                        onComplete?.Invoke(null);
                    }
                );
            }
            else if(request.responseCode == 401)
            {
                SceneManager.LoadScene("Login");
                yield break;
            }else{Debug.LogError("❌ Error downloading model: " + request.error); }
        }
    }


    public IEnumerator DownloadAudioFromBackEnd(string modelName, Action<AudioClip> onComplete)
    {
    // 🔁 Replace extension (.glb / .obj → .mp3)
    string audioName = Path.GetFileNameWithoutExtension(modelName) + ".mp3";

    string localPath = Path.Combine(Application.persistentDataPath, audioName);

    if (File.Exists(localPath))
    {
        Debug.Log($"🔊 Audio already exists: {localPath}");

        using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip("file://" + localPath, AudioType.MPEG))
        {
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(DownloadHandlerAudioClip.GetContent(req));
            }
            else
            {
                Debug.LogError("❌ Failed to load local audio");
                onComplete?.Invoke(null);
            }
        }
    }
    else
    {
        string url = $"{DownloadModelApiUrl}{UnityWebRequest.EscapeURL(audioName)}";
        Debug.Log($"📥 Audio Download: {audioName}");

        UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG);
        request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            byte[] audioBytes = request.downloadHandler.data;

            // Save locally
            File.WriteAllBytes(localPath, audioBytes);

            AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
            onComplete?.Invoke(clip);
        }
        else if (request.responseCode == 401)
        {
            SceneManager.LoadScene("Login");
            yield break;
        }
        else
        {
            Debug.LogError("❌ Error downloading audio: " + request.error);
            onComplete?.Invoke(null);
        }
    }
}


    public string GetTransformHierarchy(Transform t, string indent = "")
    {
        string result = indent + t.name + "\n";
        foreach (Transform child in t)
        {
            result += GetTransformHierarchy(child, indent + "  ");
        }
        return result;
    }

    public void AutoScaleModel(GameObject model, float targetSize = 1f)
    {
        Bounds bounds = new Bounds(model.transform.position, Vector3.zero);
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
        {
            bounds.Encapsulate(renderer.bounds);
        }

        float maxSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        if (maxSize > 0f)
        {
            float scaleFactor = targetSize / maxSize;
            model.transform.localScale *= scaleFactor;
            Debug.Log($"📏 Auto-scaled model by {scaleFactor:F2}x to fit within {targetSize} units.");
        }
    }
}
