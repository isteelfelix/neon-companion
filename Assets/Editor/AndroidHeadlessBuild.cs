using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using System.Linq;

public static class AndroidHeadlessBuild
{
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
