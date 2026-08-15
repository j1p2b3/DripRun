using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class UserInfoSingleton : MonoBehaviour
{
   private static UserInfoSingleton _instance;
    public static UserInfoSingleton Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = new GameObject("UserManager").AddComponent<UserInfoSingleton>();
                 DontDestroyOnLoad(_instance.gameObject);  // This makes sure it persists across scenes
            }
            return _instance;
        }
    }
    
    // Ensure no other instances are created
    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);  // Destroy this duplicate instance
        }
        else
        {
            _instance = this;
            DontDestroyOnLoad(gameObject);  // Keep the instance across scenes
        }
    }

    // Store the user's information and access token
    public string Email { get; private set; }
    public string Name { get; private set; }
    public string AccessToken { get; private set; }

    // Method to set user info and access token
    public void SetUserInfo(string email, string name, string accessToken)
    {
        Email = email;
        Name = name;
        AccessToken = accessToken;
    }

    // Method to clear user info (for logout)
    public void ClearUserInfo()
    {
        Email = null;
        Name = null;
        AccessToken = null;
    }
}