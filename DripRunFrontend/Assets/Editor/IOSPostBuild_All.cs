#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using System.IO;
using UnityEngine;

public static class IOSPostBuild_All
{
    // ====== EDIT THESE IF YOUR IDS CHANGE ======
    const string FB_APP_ID        = "1382781443080366";
    const string FB_DISPLAY_NAME  = "Drip Run";
    // Fallbacks if GoogleService-Info.plist is missing:
    const string GID_CLIENT_ID_FALLBACK =
        "1044348217685-f76n807u9iqrj2o689afg31trq4nmi81.apps.googleusercontent.com";
    const string GOOGLE_REVERSED_ID_FALLBACK =
        "com.googleusercontent.apps.1044348217685-f76n807u9iqrj2o689afg31trq4nmi81";

    const string TRACKING_TEXT =
        "This identifier helps provide a better sign-in and analytics experience.";
    // ===========================================

    [PostProcessBuild]
    public static void OnPostProcessBuild(BuildTarget target, string buildPath)
    {
        if (target != BuildTarget.iOS) return;

        // Try to read GoogleService-Info.plist that Unity copies into the Xcode project
        string gsipPath = Path.Combine(buildPath, "GoogleService-Info.plist");
        if (!File.Exists(gsipPath))
        {
            // Fallback: if dev placed it under a subfolder (rare), search once
            var hits = Directory.GetFiles(buildPath, "GoogleService-Info.plist", SearchOption.AllDirectories);
            if (hits.Length > 0) gsipPath = hits[0];
        }

        string gidClientId   = GID_CLIENT_ID_FALLBACK;
        string reversedId    = GOOGLE_REVERSED_ID_FALLBACK;

        if (File.Exists(gsipPath))
        {
            var gDoc = new PlistDocument();
            gDoc.ReadFromFile(gsipPath);
            var root = gDoc.root;
            if (root.values.ContainsKey("CLIENT_ID"))
                gidClientId = root["CLIENT_ID"].AsString();
            if (root.values.ContainsKey("REVERSED_CLIENT_ID"))
                reversedId = root["REVERSED_CLIENT_ID"].AsString();
        }

        // ---- Edit Info.plist ----
        string infoPlistPath = Path.Combine(buildPath, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(infoPlistPath);
        var rootDict = plist.root;

        // Facebook keys
        rootDict.SetString("FacebookAppID", FB_APP_ID);
        rootDict.SetString("FacebookDisplayName", FB_DISPLAY_NAME);

        // Tracking usage description
        rootDict.SetString("NSUserTrackingUsageDescription", TRACKING_TEXT);

        // Google client id (some packages read this key)
        rootDict.SetString("GIDClientID", gidClientId);

        // LSApplicationQueriesSchemes (ensure FB schemes exist)
        var queries = GetOrCreateArray(rootDict, "LSApplicationQueriesSchemes");
        AddUniqueString(queries, "fbapi");
        AddUniqueString(queries, "fb-messenger-api");
        AddUniqueString(queries, "fbauth2");
        AddUniqueString(queries, "fbshareextension");

        // URL Types — ensure FB scheme and Google reversed scheme
        var urlTypes = GetOrCreateArray(rootDict, "CFBundleURLTypes");
        EnsureUrlScheme(urlTypes, "facebook-unity-sdk", "fb" + FB_APP_ID);
        EnsureUrlScheme(urlTypes, "google", reversedId);

        // Write plist
        File.WriteAllText(infoPlistPath, plist.WriteToString());

        // ---- Xcode project edits ----
        var projPath = PBXProject.GetPBXProjectPath(buildPath);
        var proj = new PBXProject();
        proj.ReadFromFile(projPath);

#if UNITY_2019_3_OR_NEWER
        string mainTarget = proj.GetUnityMainTargetGuid();
        string frameworkTarget = proj.GetUnityFrameworkTargetGuid();
#else
        string mainTarget = proj.TargetGuidByName("Unity-iPhone");
        string frameworkTarget = proj.TargetGuidByName("UnityFramework");
#endif

        // Embed Swift stdlib (FB iOS SDK uses Swift)
        proj.SetBuildProperty(mainTarget, "ALWAYS_EMBED_SWIFT_STANDARD_LIBRARIES", "YES");
        if (!string.IsNullOrEmpty(frameworkTarget))
            proj.SetBuildProperty(frameworkTarget, "ALWAYS_EMBED_SWIFT_STANDARD_LIBRARIES", "YES");

        // Ensure -ObjC in Other Linker Flags
        AddOtherLdFlag(proj, mainTarget, "-ObjC");
        if (!string.IsNullOrEmpty(frameworkTarget))
            AddOtherLdFlag(proj, frameworkTarget, "-ObjC");

        proj.WriteToFile(projPath);

        // ---- Capabilities (Sign in with Apple) ----
        var caps = new ProjectCapabilityManager(
            projPath,
            "Unity-iPhone.entitlements",
            null,
            mainTarget
        );
        caps.AddSignInWithApple();
        caps.WriteToFile();

        Debug.Log("[iOS PostBuild] Info.plist + capabilities configured. " +
                  $"FB_APP_ID={FB_APP_ID}, GID_CLIENT_ID={gidClientId}, REVERSED_ID={reversedId}");
    }

    // ---------- helpers ----------
    static PlistElementArray GetOrCreateArray(PlistElementDict root, string key)
    {
        if (root.values.ContainsKey(key))
            return root[key].AsArray();
        return root.CreateArray(key);
    }

    static void AddUniqueString(PlistElementArray array, string value)
    {
        foreach (var v in array.values)
            if (v is PlistElementString s && s.value == value) return;
        array.AddString(value);
    }

    static void EnsureUrlScheme(PlistElementArray urlTypes, string name, string scheme)
    {
        // If scheme already present, do nothing
        foreach (var v in urlTypes.values)
        {
            if (v is PlistElementDict d && d.values.ContainsKey("CFBundleURLSchemes"))
            {
                var arr = d["CFBundleURLSchemes"].AsArray();
                foreach (var s in arr.values)
                    if (s is PlistElementString ps && ps.value == scheme) return;
            }
        }
        // Otherwise add a new dict
        var dict = urlTypes.AddDict();
        dict.SetString("CFBundleURLName", name);
        var schemes = dict.CreateArray("CFBundleURLSchemes");
        schemes.AddString(scheme);
    }

    static void AddOtherLdFlag(PBXProject proj, string target, string flag)
    {
        const string key = "OTHER_LDFLAGS";
        var existing = proj.GetBuildPropertyForAnyConfig(target, key);
        if (existing == null || !existing.Contains(flag))
            proj.AddBuildProperty(target, key, flag);
    }
}
#endif
