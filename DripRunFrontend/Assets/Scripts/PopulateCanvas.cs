using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System;
using System.IO; // <-- for caching
using UnityEngine.SceneManagement;
using TriLibCore;
using TriLibCore.General;
using TriLibCore.Extensions;


public class CouponDisplayManager : MonoBehaviour
{
    public GameObject couponEntryPrefab;
    public GameObject emptytext;
    public Transform contentContainer; // Set this to ScrollView > Content
    public CouponQuery couponQueryScript;
    public Camera previewCamera;
    public Transform previewSpawnPoint;
    private Vector3 originalCamPos;
    private Quaternion originalCamRot;
    public int previewResolution = 512;
    public Texture2D loadingTexture;
    public GameObject mainCanv;
    public GameObject genCanv;
    public SpecificCouponController genCanvasController;
    private string imageApiBase => "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/download/secure-image/by-coupon/";
    private string modelApiBase => "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/download/secure-model/by-coupon/";

    // Cache directory
    private string CacheDir => Path.Combine(Application.persistentDataPath, "imgcache");

    void OnEnable()
    {
        // Ensure cache directory exists
        if (!Directory.Exists(CacheDir))
            Directory.CreateDirectory(CacheDir);
        originalCamPos = previewCamera.transform.position;
        originalCamRot = previewCamera.transform.rotation;
        StartCoroutine(PopulateUI());
    }

    IEnumerator PopulateUI()
    {
        if (couponQueryScript.GetCouponLength() == 0)
        {
            emptytext.SetActive(true);
            yield break;
        }
        emptytext.SetActive(false);

        for (int i = 0; i < couponQueryScript.GetCouponLength(); i++)
        {
            int couponId = couponQueryScript.GetCoupID(i);
            string type = couponQueryScript.GetCoupType(i);

            GameObject entry = Instantiate(couponEntryPrefab, contentContainer);

            string tosLink = couponQueryScript.GetCoupTosLink(i);
            string shopifyLink = couponQueryScript.GetShopifyLink(i);

            RawImage raw = entry.transform.Find("CouponImage").GetComponent<RawImage>();
            // Show loading texture immediately
            raw.texture = loadingTexture;

            if (type.Contains("Model"))
            {
                yield return GetModelPreview(couponId, texture =>
                {
                    raw.texture = texture;
                });
            }
            else
            {
                yield return GetSpriteCached(couponId, sprite =>
                {
                    raw.texture = sprite != null ? sprite.texture : null;
                });
            }
            CheckTypeAndRun(i, entry, tosLink, shopifyLink);
        }
    }

    // ---------- CACHING HELPERS ----------
    private string GetCachePath(int couponId)
    {
        return Path.Combine(CacheDir, $"coupon_{couponId}.png");
    }

    // Core: Load from cache if present, else download (with auth), save, then create sprite
    private IEnumerator GetSpriteCached(int couponId, Action<Sprite> onReady)
    {
        string cachePath = GetCachePath(couponId);

        // 1) Try cache
        if (File.Exists(cachePath))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(cachePath);
                Texture2D tex = new Texture2D(2, 2, UnityEngine.TextureFormat.ARGB32, false);
                if (tex.LoadImage(bytes))
                {
                    tex.Apply(false, false);
                    onReady?.Invoke(Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), Vector2.one * 0.5f));
                    yield break;
                }
            }
            catch { /* fall through to download */ }
        }

        // 2) Download from (secure container) using couponId
        string imageUrl = imageApiBase + couponId;

        using (UnityWebRequest req = UnityWebRequest.Get(imageUrl))
        {
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Image download failed for couponId {couponId} ({req.error}) code={req.responseCode}");
                if (req.responseCode == 401) { SceneManager.LoadScene("Login"); yield break; }
                onReady?.Invoke(null);
                yield break;
            }

            byte[] imgBytes = req.downloadHandler.data;

            // Save cache (best-effort)
            try { File.WriteAllBytes(cachePath, imgBytes); }
            catch (Exception e) { Debug.LogWarning($"Could not cache couponId {couponId}: {e.Message}"); }

            Texture2D tex = new Texture2D(2, 2, UnityEngine.TextureFormat.ARGB32, false);
            if (!tex.LoadImage(imgBytes))
            {
                Debug.LogError($"LoadImage failed for couponId {couponId}");
                onReady?.Invoke(null);
                yield break;
            }

            tex.Apply(false, false);
            onReady?.Invoke(Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), Vector2.one * 0.5f));
        }
        
    }

    // ---------- EXISTING LOGIC (unchanged) ----------

    public void CheckTypeAndRun(int i, GameObject Entry, String tosLink, String shopifyLink)
    {
        string Type = couponQueryScript.GetCoupType(i);
        int couponId = couponQueryScript.GetCoupID(i);
        int couponLevel = couponQueryScript.GetCouponLevel(i);
        string description = couponQueryScript.GetDescription(i);
        string companyName = couponQueryScript.GetCompanyName(i);

        if (Type == "ImgShopify" || Type == "ModelShopify")
        {
            ShopifyBtn(shopifyLink, Entry);
            tosBtn(Entry, tosLink);
        }else if (Type == "ImgGenShopify" || Type == "ModelGenShopify" || Type == "ImgGen" || Type == "ModelGen" || Type == "ModelAudio")
        {
            GenBtn(Entry, couponId, couponLevel, description, companyName, Type, shopifyLink);
            tosBtn(Entry, tosLink);
        }
    }

    public void ShopifyBtn(string shopifyLink, GameObject Entry)
    {
        Button shopifyButton = Entry.transform.Find("Button")?.GetComponent<Button>();
        if (shopifyButton != null)
        {
            shopifyButton.onClick.RemoveAllListeners();
            if (!string.IsNullOrEmpty(shopifyLink))
            {
                string capturedLink = shopifyLink.Trim();
                shopifyButton.gameObject.SetActive(true);
                shopifyButton.onClick.AddListener(() =>
                {
                    Debug.Log(capturedLink);
                    Application.OpenURL(capturedLink);
                });
            }
            else
            {
                shopifyButton.gameObject.SetActive(false);
            }
        }
    }

    public void GenBtn(GameObject Entry, int couponId, int couponLevel, string description, string companyName, string couponType, string shopifyLink)
    {
          Button shopifyButton = Entry.transform.Find("Button")?.GetComponent<Button>();
        if (shopifyButton != null)
        {
            shopifyButton.onClick.RemoveAllListeners();
            shopifyButton.gameObject.SetActive(true);
            shopifyButton.onClick.AddListener(() =>
            {
                mainCanv.SetActive(false);
                genCanv.SetActive(true);
                RawImage raw = Entry.transform.Find("CouponImage").GetComponent<RawImage>();
                Texture tex = raw.texture;
                genCanvasController.Populate(tex, couponId, couponLevel, description, companyName, couponType, shopifyLink);
            });
        }
    }


    public void tosBtn(GameObject Entry, String tosLink)
    {
        Button tosButton = Entry.transform.Find("Button/TOSBtn")?.GetComponent<Button>();
        if (tosButton != null)
        {
            tosButton.onClick.RemoveAllListeners();
            if (!string.IsNullOrEmpty(tosLink))
            {
                string capturedLinkTOS = tosLink.Trim();
                tosButton.gameObject.SetActive(true);
                tosButton.onClick.AddListener(() =>
                {
                    Application.OpenURL(capturedLinkTOS);
                });
            }
            else
            {
                tosButton.gameObject.SetActive(false);
            }
        }
    }


    void AutoFrameModel(GameObject model)
    {
        Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);

        foreach (Renderer r in model.GetComponentsInChildren<Renderer>())
            bounds.Encapsulate(r.bounds);

        Vector3 center = bounds.center;
        float size = bounds.extents.magnitude;

        previewCamera.transform.position = center + new Vector3(0, 0, size * 2f);
        previewCamera.transform.LookAt(center);
    }
    string ExtractFileName(string contentDisposition)
    {
        if (string.IsNullOrEmpty(contentDisposition))
            return null;

        var parts = contentDisposition.Split(';');
        foreach (var part in parts)
        {
            if (part.Trim().StartsWith("filename="))
                return part.Split('=')[1].Trim().Trim('"');
        }
        return null;
    }


    private IEnumerator GetModelPreview(int couponId, Action<Texture2D> onReady)
    {
        string cachePath = GetModelPreviewCachePath(couponId);

        // 🔥 1. Try cache first
        if (File.Exists(cachePath))
        {
            byte[] bytes = File.ReadAllBytes(cachePath);

            Texture2D cachedTex  = new Texture2D(2, 2);
            if (cachedTex .LoadImage(bytes))
            {
                cachedTex .Apply(false, false);
                Debug.Log($"⚡ Loaded cached preview for {couponId}");
                onReady?.Invoke(cachedTex);
                yield break;
            }
        }

        string url = modelApiBase + couponId;

        UnityWebRequest req = UnityWebRequest.Get(url);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"❌ Model download failed {couponId}");
            onReady?.Invoke(null);
            yield break;
        }

        byte[] modelBytes = req.downloadHandler.data;

        // ✅ Extract filename (you asked about this earlier)
        string fileName = ExtractFileName(req.GetResponseHeader("Content-Disposition"));
        if (string.IsNullOrEmpty(fileName))
            fileName = $"coupon_{couponId}.glb"; // fallback

        string filePath = Path.Combine(Application.persistentDataPath, fileName);

        if (!File.Exists(filePath))
        {
            File.WriteAllBytes(filePath, modelBytes);
        }

        bool loaded = false;
        GameObject model = null;

        AssetLoader.LoadModelFromFile(
            filePath,
            onLoad: (ctx) =>
            {
                model = ctx.RootGameObject;
                loaded = true;
            },
            onError: (err) =>
            {
                Debug.LogError("❌ TriLib load failed: " + err);
                loaded = true;
            }
        );

        while (!loaded) yield return null;

        if (model == null)
        {
            onReady?.Invoke(null);
            yield break;
        }

        // ===== DEBUG: CHECK RENDERERS =====
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        Debug.Log($"🧪 Renderer count: {renderers.Length}");

        Debug.Log($"🧪 Model name: {model.name}");
        Debug.Log($"🧪 Child count: {model.transform.childCount}");


        // ===== FORCE RENDERERS ENABLED =====
        foreach (var r in renderers)
        {
            r.enabled = true;
        }


        // ===== FORCE LAYER (ENSURE CAMERA CAN SEE IT) =====
        foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
        {
            t.gameObject.layer = 0; // Default layer
        }


        // ===== FINAL AUTO-FRAME SETUP =====

        // Remove parent influence
        model.transform.SetParent(null);

        // Reset transform
        model.transform.position = Vector3.zero;
        model.transform.rotation = Quaternion.identity;

        // Calculate bounds
        Renderer[] renderers2 = model.GetComponentsInChildren<Renderer>();
        Bounds bounds = renderers2[0].bounds;

        foreach (var r in renderers2)
        {
            bounds.Encapsulate(r.bounds);
        }

        // Get center + size
        Vector3 center = bounds.center;
        float size = bounds.extents.magnitude;

        // Center the model at origin
        model.transform.position -= center;

        // Position camera based on size
        float distance = size * 2.5f;
        previewCamera.transform.position = new Vector3(-distance, distance * 0.6f, -distance);
        previewCamera.transform.LookAt(Vector3.zero);

        // Adjust clipping
        previewCamera.nearClipPlane = 0.01f;
        previewCamera.farClipPlane = distance * 10f;

        // Debug
        Debug.Log($"📐 Model size: {size}");
        Debug.Log($"📐 Camera distance: {distance}");



        // 🔥 Render to texture
        RenderTexture rt = new RenderTexture(previewResolution, previewResolution, 16);
        previewCamera.targetTexture = rt;

        Debug.Log($"📸 Camera enabled: {previewCamera.enabled}");
        Debug.Log($"📸 Camera pos: {previewCamera.transform.position}");
        Debug.Log($"📸 Camera forward: {previewCamera.transform.forward}");
        Debug.Log($"📸 TargetTexture assigned: {previewCamera.targetTexture != null}");

        // 🔥 FORCE CAMERA RENDER
        previewCamera.Render();
        Debug.Log("🎥 Camera.Render() called");
        // Give Unity a frame just in case
        yield return new WaitForEndOfFrame();
        RenderTexture.active = rt;

        Texture2D tex = new Texture2D(previewResolution, previewResolution, UnityEngine.TextureFormat.RGBA32, false);
        RenderTexture.active = rt;

        Texture2D debugTex = new Texture2D(4, 4);
        debugTex.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
        debugTex.Apply();

        Color c = debugTex.GetPixel(0, 0);
        Debug.Log($"🧪 RT sample pixel: {c}");

        tex.ReadPixels(new Rect(0, 0, previewResolution, previewResolution), 0, 0);
        tex.Apply();

        RenderTexture.active = null;
        previewCamera.targetTexture = null;

        previewCamera.transform.position = originalCamPos;
        previewCamera.transform.rotation = originalCamRot;

        Destroy(rt);
        Destroy(model);

        // 🔥 Save to cache
        try
        {
            byte[] png = tex.EncodeToPNG();
            File.WriteAllBytes(GetModelPreviewCachePath(couponId), png);
            Debug.Log($"💾 Cached preview for {couponId}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to cache preview: {e.Message}");
        }

        onReady?.Invoke(tex);
    }

    void SetLayerRecursively(GameObject obj, int layer)
    {
        obj.layer = layer;
        foreach (Transform child in obj.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }

    private string GetModelPreviewCachePath(int couponId)
    {
        return Path.Combine(CacheDir, $"model_preview_{couponId}.png");
    }


}
