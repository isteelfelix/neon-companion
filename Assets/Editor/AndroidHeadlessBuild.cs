using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using System.Linq;

public static class AndroidHeadlessBuild
{
    // Быстрый диагност без сборки: печатает реальный энум applicationEntry,
    // его значения и текущее значение проекта. Запуск:
    //   Unity -batchmode -nographics -projectPath . -executeMethod AndroidHeadlessBuild.DiagEntry -quit
    public static void DiagEntry()
    {
        var prop = typeof(PlayerSettings.Android).GetProperty("applicationEntry");
        if (prop == null)
        {
            Debug.Log("[DiagEntry] PlayerSettings.Android.applicationEntry NOT FOUND");
            return;
        }

        var enumType = prop.PropertyType;
        var current = prop.GetValue(null);
        Debug.Log($"[DiagEntry] enum={enumType.FullName} currentValue={current} currentInt={System.Convert.ToInt32(current)}");
        foreach (var name in System.Enum.GetNames(enumType))
        {
            object v = System.Enum.Parse(enumType, name);
            Debug.Log($"[DiagEntry]   {name} = {System.Convert.ToInt32(v)}");
        }
    }

    private static void ForceClassicActivityEntry()
    {
        try
        {
            var prop = typeof(PlayerSettings.Android).GetProperty("applicationEntry");
            if (prop == null)
            {
                Debug.LogWarning("[AndroidHeadlessBuild] applicationEntry property not found");
                return;
            }

            var enumType = prop.PropertyType;
            object before = prop.GetValue(null);
            object activity = System.Enum.Parse(enumType, "Activity");
            prop.SetValue(null, activity);
            object after = prop.GetValue(null);
            // Персистим, чтобы открытый редактор и будущие сборки были консистентны.
            AssetDatabase.SaveAssets();
            Debug.Log($"[AndroidHeadlessBuild] applicationEntry: {before} ({System.Convert.ToInt32(before)}) -> {after} ({System.Convert.ToInt32(after)})");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[AndroidHeadlessBuild] ForceClassicActivityEntry failed: " + e.Message);
        }
    }

    public static void Build()
    {
        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        Debug.Log($"[AndroidHeadlessBuild] Building {scenes.Length} scenes: {string.Join(", ", scenes)}");

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = "Builds/Android/neon-companion.apk",
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None
        };

        // Ensure IL2CPP + ARM64
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

        // Unity 6 can ship the app with the launcher UnityPlayerActivity marked
        // android:enabled="false" (GameActivity mode), so the app installs but
        // cannot be opened. Force the classic Activity entry BY NAME via
        // reflection — no compile-time dependency on the Android extension enum
        // and no reliance on a serialized magic int.
        ForceClassicActivityEntry();

        // Set Android launcher icon
        var iconAsset = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/UI/Branding/app-icon-1024.png");
        if (iconAsset != null)
        {
            PlayerSettings.SetIcons(NamedBuildTarget.Android, new[] { iconAsset }, IconKind.Application);
            Debug.Log("[AndroidHeadlessBuild] Android icon set from app-icon-1024.png");
        }
        else
        {
            Debug.LogWarning("[AndroidHeadlessBuild] app-icon-1024.png not found, using default icon");
        }

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        Debug.Log($"[AndroidHeadlessBuild] Result: {summary.result}");
        Debug.Log($"[AndroidHeadlessBuild] Total errors: {summary.totalErrors}");
        Debug.Log($"[AndroidHeadlessBuild] Total warnings: {summary.totalWarnings}");
        Debug.Log($"[AndroidHeadlessBuild] Total time: {summary.totalTime}");
        Debug.Log($"[AndroidHeadlessBuild] Output: {summary.outputPath} ({summary.totalSize} bytes)");

        if (summary.result != BuildResult.Succeeded)
        {
            Debug.LogError("[AndroidHeadlessBuild] BUILD FAILED");
            foreach (var step in report.steps)
            {
                foreach (var msg in step.messages)
                {
                    if (msg.type == LogType.Error || msg.type == LogType.Exception)
                        Debug.LogError($"  [{step.name}] {msg.content}");
                }
            }
            EditorApplication.Exit(1);
        }
        else
        {
            Debug.Log("[AndroidHeadlessBuild] BUILD SUCCEEDED");
            EditorApplication.Exit(0);
        }
    }
}
