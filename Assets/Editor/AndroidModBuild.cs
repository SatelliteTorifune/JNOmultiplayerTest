using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Batch entry point for building the mod's Android .sr2-mod.
// Calls the project's own SR2 Mod Builder pipeline with BuildTarget.Android so the produced header
// carries a valid AssetBundleOffsetAndroid (the PC builds only carry Windows/macOS offsets).
public static class AndroidModBuild
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static void Run()
    {
        try
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.Log("[ANDROID-MOD] switching active build target to Android");
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                    throw new Exception("Could not switch the active build target to Android.");
            }
            Type windowType = FindType("ModApi.Editor.ModBuilderWindow");
            if (windowType == null) throw new Exception("ModApi.Editor.ModBuilderWindow not found");
            EditorWindow window = null;
            try
            {
                try { window = EditorWindow.GetWindow(windowType, false, null, false); }
                catch (Exception error) { Debug.LogWarning("[ANDROID-MOD] GetWindow failed: " + error.Message); }
                if (window == null) window = ScriptableObject.CreateInstance(windowType) as EditorWindow;
                if (window == null) throw new Exception("Could not create the mod builder window.");
                if (GetProperty(window, windowType, "Data") == null) Invoke(window, windowType, "Initialize", null);
                object data = GetProperty(window, windowType, "Data");
                if (data == null) throw new Exception("ModData was not loaded (expected Assets/ModData.asset).");
                string modName = GetProperty(data, data.GetType(), "Name") as string;
                if (string.IsNullOrEmpty(modName)) modName = GetProperty(window, windowType, "ModName") as string;
                if (string.IsNullOrEmpty(modName)) throw new Exception("Could not resolve the mod name.");
                string output = Path.GetFullPath(Path.Combine("Builds", "Android", modName + ".sr2-mod"));
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                object result = Invoke(window, windowType, "BuildAssetBundles",
                    new object[] { output, new[] { BuildTarget.Android }, false });
                bool ok = result is bool boolResult && boolResult;
                Debug.Log("[ANDROID-MOD] name=" + modName + " ok=" + ok + " output=" + output + " exists=" + File.Exists(output));
                if (!ok || !File.Exists(output)) throw new Exception("The Android mod build did not produce a file.");
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
            }
            Debug.Log("[ANDROID-MOD] DONE");
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            Debug.LogError("[ANDROID-MOD] FAILED: " + error);
            EditorApplication.Exit(1);
        }
    }

    private static Type FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName);
            if (type != null) return type;
        }
        return null;
    }

    private static MethodInfo FindMethod(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var method = current.GetMethod(name, Instance);
            if (method != null) return method;
        }
        return null;
    }

    private static object Invoke(object target, Type type, string name, object[] arguments)
    {
        var method = FindMethod(type, name);
        if (method == null) throw new MissingMethodException(type.FullName, name);
        return method.Invoke(target, arguments);
    }

    private static object GetProperty(object target, Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(name, Instance);
            if (property != null) return property.GetValue(target);
        }
        return null;
    }
}
