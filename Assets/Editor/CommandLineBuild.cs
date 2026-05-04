using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class CommandLineBuild
{
    private const string BuildNameArgument = "-buildName";
    private const string BuildOutputDirArgument = "-buildOutputDir";
    private const string CustomBuildTargetArgument = "-customBuildTarget";
    private const string DefaultBuildName = "MARL-ants";
    private const string DefaultBuildOutputDir = "Builds";

    public static void BuildStandalone()
    {
        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrEmpty(projectRoot))
        {
            throw new InvalidOperationException("Could not resolve the Unity project root.");
        }

        var buildName = GetArgument(BuildNameArgument, DefaultBuildName);
        ValidateBuildName(buildName);

        var outputDir = GetArgument(BuildOutputDirArgument, Path.Combine(projectRoot, DefaultBuildOutputDir));
        if (!Path.IsPathRooted(outputDir))
        {
            outputDir = Path.GetFullPath(Path.Combine(projectRoot, outputDir));
        }

        Directory.CreateDirectory(outputDir);

        var buildTarget = ResolveBuildTarget(GetArgument(CustomBuildTargetArgument, null));
        var outputPath = ResolveBuildPath(outputDir, buildName, buildTarget);
        var scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            const string fallbackScene = "Assets/Scenes/SampleScene.unity";
            if (!File.Exists(Path.Combine(projectRoot, fallbackScene)))
            {
                throw new InvalidOperationException("No enabled scenes are listed in EditorBuildSettings.");
            }

            scenes = new[] { fallbackScene };
        }

        Debug.Log($"Building {buildName} for {buildTarget} at {outputPath}");
        Debug.Log($"Scenes: {string.Join(", ", scenes)}");

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = buildTarget,
            options = BuildOptions.None
        });

        var summary = report.summary;
        if (summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Build failed with result {summary.result}: {summary.totalErrors} error(s), {summary.totalWarnings} warning(s).");
        }

        Debug.Log($"Build succeeded: {outputPath} ({summary.totalSize} bytes)");
    }

    private static string GetArgument(string name, string defaultValue)
    {
        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }

        return defaultValue;
    }

    private static void ValidateBuildName(string buildName)
    {
        if (string.IsNullOrWhiteSpace(buildName))
        {
            throw new ArgumentException("Build name cannot be empty.");
        }

        if (buildName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || buildName.Contains("/") || buildName.Contains("\\"))
        {
            throw new ArgumentException($"Build name contains invalid file name characters: {buildName}");
        }
    }

    private static BuildTarget ResolveBuildTarget(string rawBuildTarget)
    {
        if (string.IsNullOrWhiteSpace(rawBuildTarget))
        {
            return EditorUserBuildSettings.activeBuildTarget;
        }

        switch (rawBuildTarget.Trim().ToLowerInvariant())
        {
            case "mac":
            case "macos":
            case "osx":
            case "standaloneosx":
                return BuildTarget.StandaloneOSX;
            case "linux":
            case "linux64":
            case "standalonelinux64":
                return BuildTarget.StandaloneLinux64;
            case "win":
            case "windows":
            case "windows64":
            case "standalonewindows64":
                return BuildTarget.StandaloneWindows64;
            default:
                if (Enum.TryParse(rawBuildTarget, true, out BuildTarget parsedTarget))
                {
                    return parsedTarget;
                }

                throw new ArgumentException($"Unsupported build target: {rawBuildTarget}");
        }
    }

    private static string ResolveBuildPath(string outputDir, string buildName, BuildTarget buildTarget)
    {
        switch (buildTarget)
        {
            case BuildTarget.StandaloneOSX:
                return Path.Combine(outputDir, $"{buildName}.app");
            case BuildTarget.StandaloneWindows:
            case BuildTarget.StandaloneWindows64:
                return Path.Combine(outputDir, $"{buildName}.exe");
            case BuildTarget.StandaloneLinux64:
                return Path.Combine(outputDir, buildName);
            default:
                return Path.Combine(outputDir, buildName);
        }
    }
}
