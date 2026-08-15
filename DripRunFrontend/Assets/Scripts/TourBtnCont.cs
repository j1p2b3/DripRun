using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Networking;
using TMPro;
using UnityEngine.UI;
using System.Text;

public class TourBtnCont : MonoBehaviour
{
    [Header("Canvases")]
    public GameObject canv1;
    public GameObject canv2;
    public GameObject canv3;
    public GameObject canv4;
    public GameObject canv5;

    [Header("Age Buttons")]
    public Button[] ageButtons;

    public Button age13_17Button;
    public Button age18_24Button;
    public Button age25_34Button;
    public Button age35_44Button;
    public Button age45_54Button;
    public Button age55PlusButton;

    [Header("Gender Buttons")]
    public Button[] genderButtons;
    public Button maleButton;
    public Button femaleButton;
    public Button otherButton;
    
    
    [Header("Selected Values")]
    private string selectedAgeRange = "";
    private string selectedGender = "";

    [Header("Backend")]
    public string baseApiUrl = "https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net";

    public void Start()
    {
        // Check if the app has been launched before
        if (PlayerPrefs.GetInt("HasLaunchedBefore", 0) == 1)
        {
            canv1.SetActive(true);
            canv5.SetActive(false);
        }
    }

    // ---------- Canvas navigation ----------

    public void Open1()
    {
        canv1.SetActive(true);
        canv2.SetActive(false);
    }
    public void Open2()
    {
        canv1.SetActive(false);
        canv3.SetActive(false);
        canv2.SetActive(true);
    }
    public void Open3()
    {
        canv2.SetActive(false);
        canv4.SetActive(false);
        canv3.SetActive(true);
    }
    public void Open4()
    {
        canv3.SetActive(false);
        canv5.SetActive(false);
        canv4.SetActive(true);
    }

    // Step 5: check backend; if age exists -> go Main, else show form
    public void Open5OrLoad()
    {
        StartCoroutine(Open5OrLoadRoutine());
    }

    private IEnumerator Open5OrLoadRoutine()
    {
        yield return StartCoroutine(HasAgeQuery());

        if (_hasAgeResult)
        {
            PlayerPrefs.SetInt("HasLaunchedBefore", 1);
            PlayerPrefs.Save();
            SceneManager.LoadScene("Main");
        }
        else
        {
            canv4.SetActive(false);
            canv5.SetActive(true);
        }
    }

    // ---------- Validation ----------
    private bool IsFormValid()
    {
        return !string.IsNullOrEmpty(selectedAgeRange)
            && !string.IsNullOrEmpty(selectedGender);
    }
    public void SelectAge(string age, Button clickedButton)
    {
        selectedAgeRange = age;
        UpdateButtonGroup(ageButtons, clickedButton);
        CheckAndSubmit();
    }

    public void SelectGender(string gender , Button clickedButton)
    {
        selectedGender = gender;
        UpdateButtonGroup(genderButtons, clickedButton);
        CheckAndSubmit();
    }

    private void CheckAndSubmit()
    {
        if (IsFormValid())
        {
            StartCoroutine(SendDataToBackend());
        }
    }

    private void UpdateButtonGroup(Button[] group, Button selectedButton)
    {
        foreach (Button btn in group)
        {
            btn.interactable = true;
        }
        selectedButton.interactable = false;
    }

    // ---------- GET /api/user/has-age ----------
    private bool _hasAgeResult = false;

    private IEnumerator HasAgeQuery()
    {
        string url = $"{baseApiUrl}/api/user/has-age";

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            if (UserInfoSingleton.Instance == null || string.IsNullOrEmpty(UserInfoSingleton.Instance.AccessToken))
            {
                Debug.LogError("AccessToken missing in UserInfoSingleton.");
                _hasAgeResult = false;
                yield break;
            }

            req.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);

            yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
#else
            if (req.isNetworkError || req.isHttpError)
#endif
            {
                Debug.LogError($"HasAgeQuery error: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
                _hasAgeResult = false;
                yield break;
            }

            string json = req.downloadHandler.text.ToLower();
            _hasAgeResult = json.Contains("\"hasage\":true");
            Debug.Log($"HasAgeQuery => hasAge={_hasAgeResult}");
        }
    }

    // ---------- POST /api/user/details ----------
    private IEnumerator SendDataToBackend()
    {
                string url = $"{baseApiUrl}/api/user/details";

        if (UserInfoSingleton.Instance == null ||
            string.IsNullOrEmpty(UserInfoSingleton.Instance.AccessToken))
        {
            Debug.LogError("AccessToken missing in UserInfoSingleton.");
            yield break;
        }

        var payload = new SimpleJsonObj(
            ("AgeRange", selectedAgeRange),
            ("Gender", selectedGender)
        );

        byte[] bodyRaw = Encoding.UTF8.GetBytes(payload.ToJson());

        using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();

            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + UserInfoSingleton.Instance.AccessToken);

            yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
#else
            if (req.isNetworkError || req.isHttpError)
#endif
            {
                Debug.LogError($"SendDataToBackend error: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
                yield break;
            }

            string json = req.downloadHandler.text.ToLower();

            if (json.Contains("\"success\":true"))
            {
                PlayerPrefs.SetInt("HasLaunchedBefore", 1);
                PlayerPrefs.Save();

                SceneManager.LoadScene("Main");
            }
            else
            {
                Debug.LogWarning($"Backend did not confirm success. Response: {req.downloadHandler.text}");
            }
        }
    }

    // ---------- Tiny JSON Builder ----------
    private struct SimpleJsonObj
    {
        private readonly (string k, string v)[] _pairs;

        public SimpleJsonObj(params (string k, string v)[] pairs)
        {
            _pairs = pairs;
        }

        public string ToJson()
        {
            var sb = new StringBuilder();

            sb.Append('{');

            for (int i = 0; i < _pairs.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');

                sb.Append('\"')
                  .Append(_pairs[i].k)
                  .Append("\":");

                sb.Append('\"')
                  .Append(Escape(_pairs[i].v))
                  .Append('\"');
            }

            sb.Append('}');

            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s ?? "";

            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r");
        }
    }


    // ---------- AGE WRAPPERS ----------

    public void SelectAge13_17()
{
    SelectAge("13-17", age13_17Button);
}

    public void SelectAge18_24()
    {
        SelectAge("18-24", age18_24Button);
    }

    public void SelectAge25_34()
    {
        SelectAge("25-34", age25_34Button);
    }

    public void SelectAge35_44()
    {
        SelectAge("35-44", age35_44Button);
    }

    public void SelectAge45_54()
    {
        SelectAge("45-54", age45_54Button);
    }

    public void SelectAge55Plus()
    {
        SelectAge("55+", age55PlusButton);
    }

    // ---------- GENDER WRAPPERS ----------

    public void SelectMale()
    {
        SelectGender("Male", maleButton);
    }

    public void SelectFemale()
    {
        SelectGender("Female", femaleButton);
    }
    public void SelectOther()
    {
        SelectGender("Female", otherButton);
    }
}
