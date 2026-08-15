using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using System;
using Mapbox.Unity.Location;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using System.Security.Claims;

#if !UNITY_WEBGL
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#endif

public class AnimController : MonoBehaviour
{
    // =========================
    // 🔧 VARIABLES
    // =========================

    public GameObject player;
    public TextMeshProUGUI gpsOutLat;
    public TextMeshProUGUI gpsOutLon;
    public ARQuery CouponArray;
    public ARInstant Downloader;
    public PostScript Claimer;

    public string shopifyLink;
    public GameObject shopifyBtn;
    public Button claimBtn;
    public RuntimeAnimatorController cubeAnimatorController;
    public Camera WebGLcam;

    public GameObject tap3;
    public GameObject tap2;
    public GameObject tap1;
    int tapCount = 0;
    bool isAnimating = false;
    GameObject cubeObj;
    Renderer cubeRenderer;
    int realRarity;
    Texture2D realTexture;

    public Texture2D presentTexture;
    public Button backButton;
    public GameObject selfButton;

    // Audio
    public AudioSource audioSource;
    public AudioClip spinSound;
    public AudioClip sparkleLoop;

    // UI
    public GameObject[] stars;

#if !UNITY_WEBGL
    public ARAnchorManager anchorManager;
#endif

    // =========================
    // 🚀 UNITY LIFECYCLE
    // =========================

    void Start()
    {
        StartCoroutine(WaitForTrackingThenPlace());
    }

    // =========================
    // 🌍 INITIAL SETUP
    // =========================

    private IEnumerator WaitForTrackingThenPlace()
    {
        while (CouponArray == null || string.IsNullOrEmpty(CouponArray.GetCoupType()))
            yield return null;

        #if !UNITY_WEBGL
                while (ARSession.notTrackingReason != NotTrackingReason.None)
                {
                    Debug.Log("Waiting for AR tracking...");
                    yield return null;
                }
        #endif

        if (CouponArray.GetCoupType() == "ImgGen")
        {
            //Add External function at bottom to activate button that loads new scene on click
            shopifyBtn.SetActive(true);
            tap3.SetActive(true);
            StartCoroutine(LoadImagesAndSpawn());
        }
        else if (CouponArray.GetCoupType() == "ImgShopify")
        {
            shopifyBtn.SetActive(true);
            tap3.SetActive(true);
            shopifyLink = CouponArray.GetShopifyLink();  
            StartCoroutine(LoadImagesAndSpawn());
        }
        else if (CouponArray.GetCoupType() == "ImgGenShopify")
        {
            shopifyBtn.SetActive(true);
            tap3.SetActive(true);
            shopifyLink = CouponArray.GetShopifyLink();  //Remove this line on new update
            StartCoroutine(LoadImagesAndSpawn());
        }
        else if (CouponArray.GetCoupType() == "ModelGen")
        {
            selfButton.SetActive(false);
            claimBtn.interactable = true;
            backButton.interactable = true;
            StartCoroutine(DownloadModelAndTexture(CouponArray.GetImgModName()));
        }
        else if (CouponArray.GetCoupType() == "ModelShopify")
        {
            shopifyLink = CouponArray.GetShopifyLink();
            shopifyBtn.SetActive(true);
            selfButton.SetActive(false);
            claimBtn.interactable = true;
            backButton.interactable = true;
            StartCoroutine(DownloadModelAndTexture(CouponArray.GetImgModName()));
        }
        else if (CouponArray.GetCoupType() == "ModelGenShopify" || CouponArray.GetCoupType() == "ModelAudio")
        {
            shopifyBtn.SetActive(true);
            selfButton.SetActive(false);
            claimBtn.interactable = true;
            backButton.interactable = true;
            StartCoroutine(DownloadModelAndTexture(CouponArray.GetImgModName()));
        }
    }

    // =========================
    // 🧱 OBJECT PLACEMENT
    // =========================

    void CubeAndSkin(Texture2D initialTexture)
    {
        if (initialTexture == null)
            initialTexture = presentTexture;

        realRarity = CouponArray.GetCouponRarity();

        Vector3 spawnPosition;
        Quaternion spawnRotation;
        Transform parent;

    #if !UNITY_WEBGL
        Transform xrCamera = Camera.main.transform;
        Vector3 offset = new Vector3(0, 2.0f, 5.0f);
        spawnPosition = xrCamera.position + xrCamera.forward * 0.5f + offset;
        spawnRotation = Quaternion.identity;
        parent = CreateStableFreeWorldAnchor(spawnPosition, spawnRotation).transform;
    #else
        Transform cam = WebGLcam.transform;
        spawnPosition = cam.position + cam.forward * 5.0f + Vector3.up * 0.2f;
        Vector3 forward = cam.forward + Vector3.up * 0.2f; // tweak this value
        spawnRotation = Quaternion.LookRotation(forward);
        var go = new GameObject("PreviewParent");
        go.transform.position = spawnPosition;
        go.transform.rotation = spawnRotation;
        parent = go.transform;
    #endif

        cubeObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cubeObj.transform.localScale = new Vector3(2f, 2f, 2f);
        cubeObj.transform.SetParent(parent, false);

        cubeObj.transform.localPosition = Vector3.zero;
        cubeObj.transform.localRotation = Quaternion.identity;

        Material material = new Material(Shader.Find("Standard"));
        material.mainTexture = initialTexture;
        cubeRenderer = cubeObj.GetComponent<Renderer>();
        cubeRenderer.material = material;

        // 🔥 Animator starts immediately
        var animator = cubeObj.AddComponent<Animator>();
        animator.runtimeAnimatorController = cubeAnimatorController;

        if (!IsSFXMuted())
        {
            audioSource.clip = sparkleLoop;
            audioSource.loop = true;
            audioSource.volume = 0.5f;
            audioSource.Play();
        }
        else
        {
            audioSource.Stop();
        }
    }

    public IEnumerator DownloadModelAndTexture(string modelName)
    {
        GameObject model = null;
        bool loadComplete = false;

        #if !UNITY_WEBGL
                Transform xrCamera = Camera.main.transform;
                Vector3 offset = new Vector3(0, 2.0f, 5.0f);
                Vector3 spawnPosition = xrCamera.position + xrCamera.forward * 0.5f + offset;
                Quaternion spawnRotation = Quaternion.identity;
        #else
                Transform cam = WebGLcam.transform;
                Vector3 spawnPosition = cam.position + cam.forward * 5.0f + Vector3.up * 0.2f;
                Quaternion spawnRotation = Quaternion.LookRotation(cam.forward);
        #endif

        yield return StartCoroutine(Downloader.DownloadAndLoadModel(modelName, result =>
        {
            model = result;
            loadComplete = true;
        }));

        while (!loadComplete) yield return null;

        if (model == null)
        {
            Debug.LogError("❌ TriLib returned null GameObject after loading.");
            yield break;
        }

        model.name = "DownloadedModel";

        Transform parent;

        #if !UNITY_WEBGL
                parent = CreateStableFreeWorldAnchor(spawnPosition, spawnRotation).transform;
        #else
                var go = new GameObject("PreviewParent");
                go.transform.position = spawnPosition;
                go.transform.rotation = spawnRotation;
                parent = go.transform;
        #endif

        model.transform.SetParent(parent, false);
        model.SetActive(true);

        Downloader.AutoScaleModel(model, 4f);

        foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            renderer.enabled = true;
            if (renderer.material == null || renderer.material.name == "Default-Material")
            {
                renderer.material = new Material(Shader.Find("Standard"));
                renderer.material.color = Color.green;
            }
        }

        Animator animator = model.GetComponent<Animator>();

        if (animator != null && animator.runtimeAnimatorController != null)
        {
            // Existing behaviour (works for FBX with controllers)
            AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
            if (clips.Length > 0)
            {
                animator.Play(clips[0].name);
                Debug.Log("▶ Playing Animator clip: " + clips[0].name);
            }
        }
        else
        {
            // 🔥 GLB fallback
            Animation anim = model.GetComponent<Animation>();
            if (anim != null)
            {
                if (anim.clip != null)
                {
                    anim.wrapMode = WrapMode.Loop;
                    anim.Play();
                    Debug.Log("▶ Playing animation: " + anim.clip.name);
                }
                else
                {
                    foreach (AnimationState state in anim)
                    {
                        anim.clip = state.clip;
                        anim.wrapMode = WrapMode.Loop;
                        anim.Play();
                        Debug.Log("▶ Playing fallback animation: " + state.clip.name);
                        break;
                    }
                }
            }
            else
            {
                Debug.LogWarning("⚠ No Animation component found on model.");
            }
        }

        Debug.Log("✅ Model placed on stable free-world anchor.");

        if (CouponArray.GetCoupType() == "ModelAudio")
        {
            Debug.Log("🎵 ModelAudio detected - downloading audio");
            AudioClip downloadedClip = null;
            bool audioLoaded = false;
            yield return StartCoroutine(Downloader.DownloadAudioFromBackEnd(modelName, clip =>
            {
                downloadedClip = clip;
                audioLoaded = true;
            }));
            while (!audioLoaded) yield return null;
            if (downloadedClip != null)
            {
                audioSource.clip = downloadedClip;
                audioSource.loop = true;
                audioSource.volume = 1.0f;
                audioSource.Play();

                Debug.Log("▶ Playing synced audio with animation");
            }
            else
            {
                Debug.LogWarning("⚠ Audio failed to load");
            }
        }
        else
        {
            // ✨ Original sparkle behaviour
            audioSource.clip = sparkleLoop;
            audioSource.loop = true;
            audioSource.volume = 0.5f;
            audioSource.Play();
        }       
    }

#if !UNITY_WEBGL
    private ARAnchor CreateStableFreeWorldAnchor(Vector3 spawnPosition, Quaternion spawnRotation)
    {
        GameObject anchorObject = new GameObject("FreeWorldAnchor");
        anchorObject.transform.position = spawnPosition;
        anchorObject.transform.rotation = spawnRotation;

        ARAnchor anchor = anchorObject.AddComponent<ARAnchor>();

#if UNITY_ANDROID || UNITY_IOS
        if (anchorManager != null)
        {
            anchorManager.enabled = false;
        }
#endif

        return anchor;
    }
#endif

IEnumerator LoadImagesAndSpawn()
{
    Texture2D rewardTexture = null;
    Texture2D missionTexture = null;

    bool rewardLoaded = false;
    bool missionLoaded = false;

    string rewardName = CouponArray.GetImgModName();
    string missionName = CouponArray.GetMissionImgName();

    // 🔹 Load reward image (ALWAYS)
    StartCoroutine(Downloader.DownloadImageFromBackEnd(rewardName, tex =>
    {
        rewardTexture = tex;
        rewardLoaded = true;
    }));

    // 🔹 Load mission image (OPTIONAL)
    if (!string.IsNullOrEmpty(missionName))
    {
        StartCoroutine(Downloader.DownloadImageFromBackEnd(missionName, tex =>
        {
            missionTexture = tex;
            missionLoaded = true;
        }));
    }
    else
    {
        missionLoaded = true; // skip
    }

    // ⏳ Wait for both
    while (!rewardLoaded || !missionLoaded)
        yield return null;

    // 🎯 Decide initial texture
    Texture2D initialTexture = missionTexture != null ? missionTexture : presentTexture;

    // 💾 Store real reward texture
    realTexture = rewardTexture;

    // 🚀 Spawn cube
    CubeAndSkin(initialTexture);
}


    // =========================
    // 🎮 USER INPUT
    // =========================

    public void PresentBtnPress()
    {
        if (isAnimating) return;

        if (tapCount < 2)
        {
            StartCoroutine(HandleTap());
            tapCount++;
        }
        else if (tapCount == 2)
        {
            StartCoroutine(FinalReveal());
            tapCount++;
        }
    }

    // =========================
    // 🎬 TAP LOGIC
    // =========================

    IEnumerator HandleTap()
    {
        isAnimating = true;

        if (!IsSFXMuted()) audioSource.PlayOneShot(spinSound);

        if(tap3.activeSelf){tap2.SetActive(true); tap3.SetActive(false);}
        if(tap2.activeSelf){tap1.SetActive(true); tap2.SetActive(false);}
        if(tap1.activeSelf){tap1.SetActive(false);}

        yield return StartCoroutine(CubePop(1.15f));

        isAnimating = false;
    }

    IEnumerator FinalReveal()
    {
        isAnimating = true;

        if (!IsSFXMuted()) audioSource.PlayOneShot(spinSound);

        yield return StartCoroutine(CubePop(1.3f));

        // 🐶 SAFETY: fallback if reward missing
        if (realTexture == null)
            realTexture = presentTexture;

        cubeRenderer.material.mainTexture = realTexture;

        UpdateStars(realRarity);

        audioSource.volume = 0.9f;

        selfButton.SetActive(false);
        claimBtn.interactable = true;
        backButton.interactable = true;

        isAnimating = false;
    }

    // =========================
    // 💥 ANIMATION HELPERS ⭐ UI HELPERS
    // =========================

    IEnumerator CubePop(float multiplier = 1.2f)
    {
        Vector3 baseScale = cubeObj.transform.localScale;

        Vector3 target = baseScale * multiplier;
        Vector3 overshoot = baseScale * (multiplier + 0.06f);

        float t = 0f;

        // ⚡ Phase 1: VERY fast punch out
        float duration1 = 0.06f;
        while (t < duration1)
        {
            float p = t / duration1;
            p = 1 - Mathf.Pow(1 - p, 4); // stronger ease-out
            cubeObj.transform.localScale = Vector3.Lerp(baseScale, target, p);
            t += Time.deltaTime;
            yield return null;
        }

        cubeObj.transform.localScale = target;

        // ⚡ Phase 2: tiny overshoot (quick)
        t = 0f;
        float duration2 = 0.04f;
        while (t < duration2)
        {
            float p = t / duration2;
            cubeObj.transform.localScale = Vector3.Lerp(target, overshoot, p);
            t += Time.deltaTime;
            yield return null;
        }

        cubeObj.transform.localScale = overshoot;

        // ⚡ Phase 3: SNAPPY return (this is the key change)
        t = 0f;
        float duration3 = 0.08f;
        while (t < duration3)
        {
            float p = t / duration3;
            p = Mathf.Sin(p * Mathf.PI * 0.5f); // fast snap curve
            cubeObj.transform.localScale = Vector3.Lerp(overshoot, baseScale, p);
            t += Time.deltaTime;
            yield return null;
        }
        cubeObj.transform.localScale = baseScale;
    }

    void UpdateStars(int count)
    {
        stars[count].SetActive(true);
    }


    // =========================
    // 🔗 EXTERNAL ACTIONS
    // =========================

    public void Claim()
    {
        Claimer.ClaimCoupon();
        if (!CouponArray.IsRaid())
        {
            SceneManager.LoadScene("Coupons");
        }
    }
    public void OpenSelectedCoupon()
    {
        return;
    }

    public void BackButton()
    {
        Claimer.ClaimCoupon();
        SceneManager.LoadScene("Main");
    }

    bool IsSFXMuted()
    {
        return PlayerPrefs.GetInt("SFXMuted", 0) == 1;
    }
}