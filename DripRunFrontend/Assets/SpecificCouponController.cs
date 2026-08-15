using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using System;
using System.IO;

public class SpecificCouponController : MonoBehaviour
{
    public GameObject[] stars;
    public RawImage imageDisplay;
    public TMP_Text descriptionText;
    public TMP_Text companyText;
    public Button shopifyButton;
    public GameObject mainCanv;
    public GameObject genCanv;
    public GameObject modelZoom;
    public GameObject ImgZoom;
    public GameObject zoomPanel;        // full screen panel (disabled by default)
    public RawImage zoomImageDisplay;  // image inside that panel
    private int coupId;

    public void Populate(Texture tex, int couponId, int couponLevel, string description, string companyName, string couponType, string shopifyLink)
    {
        coupId = couponId;
        // ⭐ 1. Disable all stars
        foreach (GameObject star in stars)
        {
            star.SetActive(false);
        }

        // ⭐ 2. Activate correct rarity star
        int rarity = GetCouponRarity(couponLevel);

        if (rarity >= 0 && rarity < stars.Length)
        {
            stars[rarity].SetActive(true);
        }

        // 🖼️ 3. Set image texture
        if (imageDisplay != null && tex != null)
        {
            imageDisplay.texture = tex;
        }

        // 📝 4. Set description text
        if (descriptionText != null)
        {
            descriptionText.text = description;
        }

        // 🏷️ 5. Set company text
        if (companyText != null)
        {
            companyText.text = "Author: " + companyName;
        }

        // 🛒 6. Handle Shopify button
        if (shopifyButton != null)
        {
            shopifyButton.onClick.RemoveAllListeners();

            if ((couponType == "ModelGenShopify" || couponType == "ImgGenShopify") 
                && !string.IsNullOrEmpty(shopifyLink))
            {
                shopifyButton.gameObject.SetActive(true);

                string capturedLink = shopifyLink.Trim();

                shopifyButton.onClick.AddListener(() =>
                {
                    Application.OpenURL(capturedLink);
                });
            }
            else
            {
                shopifyButton.gameObject.SetActive(false);
            }
        }

        // 🔍 7. Handle Zoom Mode (Model vs Image)
        if (modelZoom != null) modelZoom.SetActive(false);
        if (ImgZoom != null) ImgZoom.SetActive(false);
        if (!string.IsNullOrEmpty(couponType))
        {
            if (couponType.Contains("Model"))
            {
                if (modelZoom != null) modelZoom.SetActive(true);
            }
            else if (couponType.Contains("Img"))
            {
                if (ImgZoom != null) ImgZoom.SetActive(true);
            }
        }
    }


    public void OnImgZoomClicked()
    {
        if (imageDisplay == null || zoomImageDisplay == null) return;

        // Copy current texture into zoom view
        zoomImageDisplay.texture = imageDisplay.texture;

        // Enable zoom panel
        zoomPanel.SetActive(true);
    }
    public void OnModelZoomClicked()
    {
        PlayerPrefs.SetInt("locID", coupId);
        SceneManager.LoadScene("AR");
    }
    
    public void CloseZoom()
    {
        zoomPanel.SetActive(false);
    }

    public void GoBack()
    {
        genCanv.SetActive(false);
        mainCanv.SetActive(true);

        // Clean up zoom if open
        if (zoomPanel != null)
            zoomPanel.SetActive(false);
    }



    public int GetCouponRarity(int couponLevel)
        {
        if(couponLevel == 100 || couponLevel== 99 || couponLevel == 94) {
            return 4;
        }
        if(couponLevel == 93) {
            return 3;
        }
        if(couponLevel == 92) {
            return 2;
        }
        if(couponLevel == 91) {
            return 1;
        }
        if(couponLevel <=90) {
            return 0;
        }
        return 0;
    }

}
