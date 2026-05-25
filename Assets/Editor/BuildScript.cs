using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class BuildScript
{
    private const string TargetEnvVar = "BUILD_TARGET";
    private const string VersionEnvVar = "BUILD_VERSION";
    private const string OutputRoot = "Builds";

    // Unity CLI entrypoint:
    // Unity -batchmode -quit -projectPath . -executeMethod BuildScript.Build
    public static void Build()
    {
        BuildPlayer();
    }

    public static void BuildPlayer()
    {
        var targetName = Environment.GetEnvironmentVariable(TargetEnvVar);
        if (string.IsNullOrWhiteSpace(targetName))
        {
            targetName = "windows";
        }

        var version = ResolveVersion();
        var commitHash = ResolveCommitHash();
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();

        if (scenes.Length == 0)
        {
            throw new InvalidOperationException("No enabled scenes found in EditorBuildSettings.");
        }

        var targetConfig = ResolveTarget(targetName);
        var buildName = BuildName(version, commitHash, targetConfig.label);
        var outputDir = Path.Combine(OutputRoot, targetConfig.label);
        Directory.CreateDirectory(outputDir);

        var locationPath = Path.Combine(outputDir, buildName + targetConfig.extension);

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            target = targetConfig.target,
            locationPathName = locationPath,
            options = BuildOptions.None
        };

        UnityEngine.Debug.Log($"Starting build target={targetConfig.label} version={version} commit={commitHash}");
        var report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Build failed for {targetConfig.label} with result {report.summary.result}."
            );
        }

        UnityEngine.Debug.Log($"Build succeeded: {locationPath}");
        UnityEngine.Debug.Log($"BUILD_ARTIFACT_PATH={locationPath}");
    }

    private static string ResolveVersion()
    {
        var versionFromEnv = Environment.GetEnvironmentVariable(VersionEnvVar);
        if (!string.IsNullOrWhiteSpace(versionFromEnv))
        {
            return versionFromEnv.Trim();
        }

        var versionFilePath = Path.Combine(Directory.GetCurrentDirectory(), "VERSION");
        if (File.Exists(versionFilePath))
        {
            var fileVersion = File.ReadAllText(versionFilePath).Trim();
            if (!string.IsNullOrWhiteSpace(fileVersion))
            {
                return fileVersion;
            }
        }

        if (!string.IsNullOrWhiteSpace(PlayerSettings.bundleVersion))
        {
            return PlayerSettings.bundleVersion.Trim();
        }

        return "0.0.0";
    }

    private static string ResolveCommitHash()
    {
        try
        {
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse --short HEAD",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return output;
                }
            }
        }
        catch
        {
            // Ignore and fall back to local marker.
        }

        return "local";
    }

    private static string BuildName(string version, string commitHash, string target)
    {
        var product = string.IsNullOrWhiteSpace(PlayerSettings.productName)
            ? "neon-companion"
            : PlayerSettings.productName;

        var normalized = product.Trim().Replace(" ", "-").ToLowerInvariant();
        return $"{normalized}-v{version}-{commitHash}-{target}";
    }

    private static (BuildTarget target, string extension, string label) ResolveTarget(string rawTarget)
    {
        var normalized = rawTarget.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "windows":
            case "win":
            case "win64":
                return (BuildTarget.StandaloneWindows64, ".exe", "windows");
            case "linux":
            case "linux64":
                return (BuildTarget.StandaloneLinux64, ".x86_64", "linux");
            case "android":
                return (BuildTarget.Android, ".apk", "android");
            default:
                throw new ArgumentException(
                    $"Unsupported BUILD_TARGET '{rawTarget}'. Use windows, linux, or android."
                );
        }
    }
}
