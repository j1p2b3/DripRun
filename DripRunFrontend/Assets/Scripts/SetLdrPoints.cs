using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class SetLdrPoints : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI pointsText;

    private void Start()
    {
        int points = PlayerPrefs.GetInt("LoginPoints", 0); // Default to 0 if not set

        if (pointsText != null)
        {
            pointsText.text = points.ToString();
        }
        else
        {
            Debug.LogWarning("Points TextMeshProUGUI reference is not assigned.");
        }
    }
}
