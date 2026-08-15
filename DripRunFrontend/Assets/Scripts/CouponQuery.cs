using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using System.Text;
using UnityEngine.SceneManagement;
using Unity.VisualScripting;
using System;
using UnityEngine.UI;

public class CouponQuery : MonoBehaviour
{
    public CouponDisplayData[] Coupons;
    public GameObject[] placedPrefabObjects;
    public GameObject PopulateCanvas;
    public Button coup;
    public Button collect;
    public bool isCoup = true;

    public void Start()
    {
        StartCoroutine(FetchUserCoupons());
    }

    public IEnumerator FetchUserCoupons()
    {
        coup.interactable = false;
        collect.interactable = true;
        string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/query/user-coupons-v2";

        UnityWebRequest request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string json = request.downloadHandler.text;
            Coupons = JsonHelper.FromJson<CouponDisplayData>(json);
            PopulateCanvas.SetActive(true);
        }
        else if (request.responseCode == 401)
        {
            SceneManager.LoadScene("Login");
            yield break;
        }
        else { Debug.LogError($"Error fetching coupons"); }
    }

    public IEnumerator FetchUserCollectibles()
    {
        collect.interactable = false;
        coup.interactable = true;
        string url = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/query/user-couponcollectible-v2";
        

        UnityWebRequest request = UnityWebRequest.Get(url);
        request.SetRequestHeader("Authorization", $"Bearer {UserInfoSingleton.Instance.AccessToken}");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string json = request.downloadHandler.text;
            Coupons = JsonHelper.FromJson<CouponDisplayData>(json);
            PopulateCanvas.SetActive(true);
        }
        else if (request.responseCode == 401)
        {
            SceneManager.LoadScene("Login");
            yield break;
        }
        else { Debug.LogError($"Error fetching coupons"); }
    }


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


    [System.Serializable]
    public class CouponDisplayData
    {
        public int couponId;
        public string imageName;
        public string shopifyLink;
        public string couponType;
        public string tosLink;
        public int couponLevel;
        public string description;
        public string companyName;
    }

    //Getters for Populate Canvas To Access the Array Data
    public int GetCoupID(int i)
    {
        return Coupons[i].couponId;
    }
    public string GetCoupImgName(int i)
    {
        return Coupons[i].imageName;
    }

    public string GetShopifyLink(int i)
    {
        return Coupons[i].shopifyLink;;
    }
    public string GetCoupType(int i)
    {
        return Coupons[i].couponType;
    }
    public int GetCouponLength()
    {
        return Coupons.Length;
    }
    public string GetCoupTosLink(int i)
    {
        return Coupons[i].tosLink;
    }

    public int GetCouponLevel(int i)
    {
        return Coupons[i].couponLevel;
    }

    public string GetDescription(int i)
    {
        return Coupons[i].description;
    }

    public string GetCompanyName(int i)
    {
        return Coupons[i].companyName;
    }

    public void changeHaulPageBtn()
    {
        PopulateCanvas.SetActive(false);
        DestroyPlacedEntries();
        Coupons = Array.Empty<CouponDisplayData>();
        if (isCoup)
        {
            StartCoroutine(FetchUserCollectibles());
        }
        else
        {
            StartCoroutine(FetchUserCoupons());
        }
        isCoup = !isCoup;
    }
    
    void DestroyPlacedEntries()
    {
        // Find all GameObjects with the name "PlacedImage"
        placedPrefabObjects = GameObject.FindGameObjectsWithTag("CoupEntry");
        // Destroy each found GameObject
        Debug.Log(placedPrefabObjects.Length);
        foreach (GameObject placedObject in placedPrefabObjects)
        {
            Destroy(placedObject);
        }
    }
}
