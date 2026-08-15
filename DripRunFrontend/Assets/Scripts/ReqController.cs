using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using System.Collections;
using UnityEngine.SceneManagement;

public class BrandRequestSubmitter : MonoBehaviour
{
    public TMP_InputField input1;
    public TMP_InputField input2;
    public TMP_InputField input3;
    public GameObject SuccessTxt;
    public GameObject ComeBackTomorrowTxt;

    private string apiUrl = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/brandrequests";

    public void SubmitBrands()
    {
        string brand1 = input1.text.Trim();
        string brand2 = input2.text.Trim();
        string brand3 = input3.text.Trim();

        if (string.IsNullOrEmpty(brand1) || string.IsNullOrEmpty(brand2) || string.IsNullOrEmpty(brand3))
        {
            
            return;
        }
        StartCoroutine(SendBrandRequests(brand1, brand2, brand3));
    }

    private IEnumerator SendBrandRequests(string b1, string b2, string b3)
    {
        var dto = new BrandRequestDto { Brand1 = b1, Brand2 = b2, Brand3 = b3 };
        string json = JsonUtility.ToJson(dto);

        UnityWebRequest request = new UnityWebRequest(apiUrl, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            if (request.responseCode == 401)
            {
                SceneManager.LoadScene("Login");
            }
            else
            {
                SuccessTxt.SetActive(false);
                ComeBackTomorrowTxt.SetActive(true);
            }
            yield break;
        }

        SuccessTxt.SetActive(true);
        ComeBackTomorrowTxt.SetActive(false);
    }


    [System.Serializable]
    private class BrandRequestDto
    {
        public string Brand1;
        public string Brand2;
        public string Brand3;
    }
}