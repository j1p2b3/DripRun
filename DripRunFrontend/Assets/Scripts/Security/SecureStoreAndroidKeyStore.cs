#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Text;
using UnityEngine;

public static class SecureStoreAndroidKeystore
{
    // Config
    private const string Alias = "dr_refresh_key_aes";
    private const string PrefFile = "dr.secure.prefs";

    // Entry points
    public static void SetString(string key, string value)
    {
        if (!EnsureKey()) { Debug.LogError("Keystore init failed"); return; }
        var ctx = GetContext();
        var b64 = new AndroidJavaClass("android.util.Base64");

        // Encrypt
        var keyStore = AndroidJavaObjectFromStatic("java.security.KeyStore", "getInstance", "AndroidKeyStore");
        keyStore.Call("load", (AndroidJavaObject)null);

        var secretKey = keyStore.Call<AndroidJavaObject>("getKey", Alias, null);

        var cipher = AndroidJavaObjectFromStatic("javax.crypto.Cipher", "getInstance", "AES/GCM/NoPadding");
        // Cipher.ENCRYPT_MODE = 1
        cipher.Call("init", 1, secretKey);

        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = cipher.Call<byte[]>("doFinal", plaintext);
        var iv = cipher.Call<byte[]>("getIV");

        string ivB64 = b64.CallStatic<string>("encodeToString", iv, 2); // Base64.NO_WRAP=2
        string ctB64 = b64.CallStatic<string>("encodeToString", ciphertext, 2);

        // Store in SharedPreferences
        var prefs = ctx.Call<AndroidJavaObject>("getSharedPreferences", PrefFile, 0);
        var edit = prefs.Call<AndroidJavaObject>("edit");
        edit.Call<AndroidJavaObject>("putString", key + ":iv", ivB64).Dispose();
        edit.Call<AndroidJavaObject>("putString", key + ":ct", ctB64).Dispose();
        edit.Call<bool>("commit"); // or apply()
    }

    public static string GetString(string key)
    {
        if (!EnsureKey()) { Debug.LogError("Keystore init failed"); return null; }
        var ctx = GetContext();
        var b64 = new AndroidJavaClass("android.util.Base64");

        var prefs = ctx.Call<AndroidJavaObject>("getSharedPreferences", PrefFile, 0);
        string ivB64 = prefs.Call<string>("getString", key + ":iv", null);
        string ctB64 = prefs.Call<string>("getString", key + ":ct", null);
        if (string.IsNullOrEmpty(ivB64) || string.IsNullOrEmpty(ctB64)) return null;

        var iv = b64.CallStatic<byte[]>("decode", ivB64, 0);
        var ct = b64.CallStatic<byte[]>("decode", ctB64, 0);

        var keyStore = AndroidJavaObjectFromStatic("java.security.KeyStore", "getInstance", "AndroidKeyStore");
        keyStore.Call("load", (AndroidJavaObject)null);
        var secretKey = keyStore.Call<AndroidJavaObject>("getKey", Alias, null);

        var cipher = AndroidJavaObjectFromStatic("javax.crypto.Cipher", "getInstance", "AES/GCM/NoPadding");
        var gcmSpec = new AndroidJavaObject("javax.crypto.spec.GCMParameterSpec", 128, iv); // 128-bit tag
        // Cipher.DECRYPT_MODE = 2
        cipher.Call("init", 2, secretKey, gcmSpec);

        try
        {
            var pt = cipher.Call<byte[]>("doFinal", ct);
            return Encoding.UTF8.GetString(pt);
        }
        catch (Exception e)
        {
            Debug.LogWarning("Decrypt failed (token may be from old install/restore): " + e.Message);
            return null;
        }
    }

    public static void Delete(string key)
    {
        var ctx = GetContext();
        var prefs = ctx.Call<AndroidJavaObject>("getSharedPreferences", PrefFile, 0);
        var edit = prefs.Call<AndroidJavaObject>("edit");
        edit.Call<AndroidJavaObject>("remove", key + ":iv").Dispose();
        edit.Call<AndroidJavaObject>("remove", key + ":ct").Dispose();
        edit.Call<bool>("commit");
    }

    // --- internals ---

    private static bool EnsureKey()
    {
        // Require API 23+ (Android 6.0) for Keystore + AES-GCM
        int sdk = new AndroidJavaClass("android.os.Build$VERSION").GetStatic<int>("SDK_INT");
        if (sdk < 23) { Debug.LogError("Android Keystore requires API 23+"); return false; }

        try
        {
            var ks = AndroidJavaObjectFromStatic("java.security.KeyStore", "getInstance", "AndroidKeyStore");
            ks.Call("load", (AndroidJavaObject)null);
            bool exists = ks.Call<bool>("containsAlias", Alias);
            if (exists) return true;

            // Generate AES key in Keystore (try StrongBox, then fallback)
            var kgen = AndroidJavaObjectFromStatic("javax.crypto.KeyGenerator", "getInstance", "AES", "AndroidKeyStore");

            var purpose = 3; // KeyProperties.PURPOSE_ENCRYPT | PURPOSE_DECRYPT (1|2)
            var builder = new AndroidJavaObject("android.security.keystore.KeyGenParameterSpec$Builder", Alias, purpose);

            // Use KeyProperties constants and pass varargs correctly
            var kp = new AndroidJavaClass("android.security.keystore.KeyProperties");
            string BLOCK_MODE_GCM = kp.GetStatic<string>("BLOCK_MODE_GCM");
            string ENCRYPTION_PADDING_NONE = kp.GetStatic<string>("ENCRYPTION_PADDING_NONE");

            builder = builder.Call<AndroidJavaObject>("setBlockModes", new object[] { new string[] { BLOCK_MODE_GCM } });
            builder = builder.Call<AndroidJavaObject>("setEncryptionPaddings", new object[] { new string[] { ENCRYPTION_PADDING_NONE } });
            try { builder = builder.Call<AndroidJavaObject>("setKeySize", 256); } catch { /* older API may ignore */ }

            bool generated = false;

            // First attempt: StrongBox
            try
            {
                try { builder.Call<AndroidJavaObject>("setIsStrongBoxBacked", true); } catch { /* pre-28 or not present */ }
                var specStrong = builder.Call<AndroidJavaObject>("build");
                kgen.Call("init", specStrong);
                kgen.Call<AndroidJavaObject>("generateKey").Dispose();
                generated = true;
            }
            catch (AndroidJavaException e)
            {
                // If StrongBox is unavailable, fall back to non-StrongBox
                if (e.Message != null && e.Message.Contains("StrongBox"))
                {
                    builder = new AndroidJavaObject("android.security.keystore.KeyGenParameterSpec$Builder", Alias, purpose);
                    builder = builder.Call<AndroidJavaObject>("setBlockModes", new object[] { new string[] { BLOCK_MODE_GCM } });
                    builder = builder.Call<AndroidJavaObject>("setEncryptionPaddings", new object[] { new string[] { ENCRYPTION_PADDING_NONE } });
                    try { builder = builder.Call<AndroidJavaObject>("setKeySize", 256); } catch {}

                    var spec = builder.Call<AndroidJavaObject>("build");
                    kgen.Call("init", spec);
                    kgen.Call<AndroidJavaObject>("generateKey").Dispose();
                    generated = true;
                    Debug.Log("[Keystore] StrongBox unavailable; fell back to normal keystore.");
                }
                else
                {
                    throw; // different error — bubble up
                }
            }

            if (!generated) throw new Exception("Key generation did not complete");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("EnsureKey error: " + e.Message);
            return false;
        }
    }

    private static AndroidJavaObject GetContext()
    {
        var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        return unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
    }

    private static AndroidJavaObject AndroidJavaObjectFromStatic(string className, string method, params object[] args)
    {
        var cls = new AndroidJavaClass(className);
        return cls.CallStatic<AndroidJavaObject>(method, args);
    }
}
#else
public static class SecureStoreAndroidKeystore
{
    public static void SetString(string key, string value)
        => UnityEngine.PlayerPrefs.SetString(key, value);
    public static string GetString(string key)
        => UnityEngine.PlayerPrefs.GetString(key, null);
    public static void Delete(string key)
        => UnityEngine.PlayerPrefs.DeleteKey(key);
}
#endif
