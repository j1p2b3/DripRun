using System;
using System.Runtime.InteropServices;
using UnityEngine;

public interface ISecureStore
{
    void SaveRefresh(string token);
    string LoadRefresh();
    void DeleteRefresh();
}

public class IOSKeychainSecureStore : ISecureStore
{
    // Change if you want different keys
    private const string Service = "com.driprun.app.refresh";
    private const string Account = "apple";

#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern bool   Keychain_Set(string service, string account, string value);
    [DllImport("__Internal")] private static extern IntPtr Keychain_Get(string service, string account);
    [DllImport("__Internal")] private static extern bool   Keychain_Delete(string service, string account);
    [DllImport("__Internal")] private static extern void   Keychain_Free(IntPtr p);
#endif

    public void SaveRefresh(string token)
    {
#if UNITY_IOS && !UNITY_EDITOR
        if (string.IsNullOrEmpty(token)) { DeleteRefresh(); return; }
        if (!Keychain_Set(Service, Account, token))
            Debug.LogWarning("Keychain_Set failed");
#else
        // Editor/other platforms fallback so you can test flows
        PlayerPrefs.SetString("DRIPRUN_REFRESH_APPLE", token ?? "");
        PlayerPrefs.Save();
#endif
    }

    public string LoadRefresh()
    {
#if UNITY_IOS && !UNITY_EDITOR
        IntPtr p = Keychain_Get(Service, Account);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringAnsi(p); }
        finally { Keychain_Free(p); }
#else
        return PlayerPrefs.GetString("DRIPRUN_REFRESH_APPLE", null);
#endif
    }

    public void DeleteRefresh()
    {
#if UNITY_IOS && !UNITY_EDITOR
        if (!Keychain_Delete(Service, Account))
            Debug.LogWarning("Keychain_Delete failed or not found");
#else
        if (PlayerPrefs.HasKey("DRIPRUN_REFRESH_APPLE"))
        {
            PlayerPrefs.DeleteKey("DRIPRUN_REFRESH_APPLE");
            PlayerPrefs.Save();
        }
#endif
    }
}