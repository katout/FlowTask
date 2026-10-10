using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.TestTools;
using UnityEngine;
using UnityEngine.TestTools;

[assembly: TestPlayerBuildModifier(typeof(Katout.FlowTask.Verification.TestPlayerSetup))]
[assembly: PostBuildCleanup(typeof(Katout.FlowTask.Verification.TestPlayerSetup))]

namespace Katout.FlowTask.Verification
{
    /// <summary>
    /// Adjusts the player that the Unity Test Framework builds for <c>-runTests -testPlatform &lt;player&gt;</c>.
    /// Controlled by environment variables set in tools/unity/run-tests.ps1:
    /// <list type="bullet">
    /// <item><c>FLOWTASK_TEST_BACKEND</c> = <c>Mono</c> | <c>IL2CPP</c>: scripting backend of the test player.</item>
    /// <item><c>FLOWTASK_TEST_BUILD_ONLY</c> = output directory: build the test player there without running it (the
    /// "split build" pattern from the Test Framework manual), e.g. an Android IL2CPP player when no device is attached.</item>
    /// </list>
    /// </summary>
    public class TestPlayerSetup : ITestPlayerBuildModifier, IPostBuildCleanup
    {
        static bool s_buildOnly;

        public BuildPlayerOptions ModifyOptions(BuildPlayerOptions options)
        {
            var group = BuildPipeline.GetBuildTargetGroup(options.target);
            var named = NamedBuildTarget.FromBuildTargetGroup(group);
            var backend = Environment.GetEnvironmentVariable("FLOWTASK_TEST_BACKEND");
            if (!string.IsNullOrEmpty(backend))
            {
                var impl = backend.Equals("IL2CPP", StringComparison.OrdinalIgnoreCase) ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x;
                PlayerSettings.SetScriptingBackend(named, impl);
            }

            if (options.target == BuildTarget.Android)
            {
                PlayerSettings.SetApplicationIdentifier(named, "com.katout.flowtask.tests");
                if (PlayerSettings.GetScriptingBackend(named) == ScriptingImplementation.IL2CPP)
                    PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            }

            var outDir = Environment.GetEnvironmentVariable("FLOWTASK_TEST_BUILD_ONLY");
            if (!string.IsNullOrEmpty(outDir))
            {
                options.options &= ~(BuildOptions.AutoRunPlayer | BuildOptions.ConnectToHost);
                var file = Path.GetFileName(options.locationPathName);
                options.locationPathName = Path.Combine(Path.GetFullPath(outDir), string.IsNullOrEmpty(file) ? "FlowTaskTestPlayer" : file);
                s_buildOnly = true;
            }

            Debug.Log($"[FlowTask] test player: target={options.target} backend={PlayerSettings.GetScriptingBackend(named)} " +
                      $"stripping={PlayerSettings.GetManagedStrippingLevel(named)} options={options.options} path={options.locationPathName} buildOnly={s_buildOnly}");
            return options;
        }

        public void Cleanup()
        {
            if (!s_buildOnly) return;
            // Build-only run: exit after the build instead of waiting for a player that is never launched.
            EditorApplication.update += () => EditorApplication.Exit(0);
        }
    }
}
