using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CausticMeshDxr.Editor
{
    public static class CausticBenchmarkRunner
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";
        const string OutputDirectory = "Captures";
        const string ReportPath = "docs/benchmark_results.md";
        const string ArtifactsDirectory = "C:/Users/nakata/.gemini/antigravity-ide/brain/90775f12-2953-4dd8-bf7f-5a8f2ea4f314";
        const int WarmupFrames = 10;
        const int BenchmarkFrames = 60;
        const int CaptureWidth = 1920;
        const int CaptureHeight = 1080;

        [MenuItem("Caustics/Run Benchmark & Captures")]
        public static void RunFromMenu()
        {
            ExecutePipeline(isBatchMode: false);
        }

        [MenuItem("Caustics/Copy Captures to Paper Artifacts")]
        public static void CopyCapturesToArtifacts()
        {
            try
            {
                if (!Directory.Exists(ArtifactsDirectory))
                    Directory.CreateDirectory(ArtifactsDirectory);

                if (!Directory.Exists(OutputDirectory))
                    return;

                var files = Directory.GetFiles(OutputDirectory, "*.png");
                foreach (var file in files)
                {
                    var dest = Path.Combine(ArtifactsDirectory, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);
                }
                Debug.Log($"[CausticBenchmarkRunner] Synced {files.Length} capture images to {ArtifactsDirectory}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CausticBenchmarkRunner] Could not copy to artifacts: {ex.Message}");
            }
        }

        static readonly RectInt CropRectPose03 = new RectInt(820, 380, 480, 440); // Middle cube & floor boundary

        [MenuItem("Caustics/Generate Cropped Inset Captures")]
        public static void GenerateCroppedInsets()
        {
            if (!Directory.Exists(OutputDirectory))
                return;

            string[] targets = { "caustic_pose03_subdivision_off", "caustic_pose03_subdivision_on", "caustic_pose03_boundary_highlight" };
            foreach (var name in targets)
            {
                var srcPath = Path.Combine(OutputDirectory, $"{name}.png");
                if (!File.Exists(srcPath))
                    continue;

                var bytes = File.ReadAllBytes(srcPath);
                var srcTex = new Texture2D(2, 2);
                if (!srcTex.LoadImage(bytes))
                {
                    UnityEngine.Object.DestroyImmediate(srcTex);
                    continue;
                }

                var rx = Mathf.Clamp(CropRectPose03.x, 0, srcTex.width);
                var ry = Mathf.Clamp(CropRectPose03.y, 0, srcTex.height);
                var rw = Mathf.Clamp(CropRectPose03.width, 1, srcTex.width - rx);
                var rh = Mathf.Clamp(CropRectPose03.height, 1, srcTex.height - ry);

                var cropPixels = srcTex.GetPixels(rx, ry, rw, rh);
                var cropTex = new Texture2D(rw, rh, TextureFormat.RGB24, false);
                cropTex.SetPixels(cropPixels);
                cropTex.Apply();

                var cropBytes = cropTex.EncodeToPNG();
                var cropPath = Path.Combine(OutputDirectory, $"{name.Replace("pose03", "pose03_inset")}.png");
                File.WriteAllBytes(cropPath, cropBytes);
                Debug.Log($"[CausticBenchmarkRunner] Saved cropped inset to {cropPath}");

                UnityEngine.Object.DestroyImmediate(srcTex);
                UnityEngine.Object.DestroyImmediate(cropTex);
            }

            CopyCapturesToArtifacts();
            AssetDatabase.Refresh();
        }

        public static void RunFromCommandLine()
        {
            var success = false;
            try
            {
                success = ExecutePipeline(isBatchMode: true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CausticBenchmarkRunner] Fatal error during benchmark: {ex}");
            }
            finally
            {
                EditorApplication.Exit(success ? 0 : 1);
            }
        }

        static bool ExecutePipeline(bool isBatchMode)
        {
            Debug.Log("[CausticBenchmarkRunner] Starting Caustic Benchmark & Capture pipeline...");

            if (!File.Exists(ScenePath))
            {
                Debug.LogError($"[CausticBenchmarkRunner] Scene not found at: {ScenePath}");
                return false;
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError("[CausticBenchmarkRunner] Failed to open sample scene.");
                return false;
            }

            var causticTest = UnityEngine.Object.FindAnyObjectByType<CausticRayQueryTest>();
            if (causticTest == null)
            {
                Debug.LogError("[CausticBenchmarkRunner] CausticRayQueryTest not found in scene.");
                return false;
            }

            var poseRecorder = UnityEngine.Object.FindAnyObjectByType<CameraPoseRecorder>();
            var camera = poseRecorder != null && poseRecorder.TargetCamera != null
                ? poseRecorder.TargetCamera
                : Camera.main;

            if (camera == null)
            {
                Debug.LogError("[CausticBenchmarkRunner] Target camera not found.");
                return false;
            }

            if (!Directory.Exists(OutputDirectory))
                Directory.CreateDirectory(OutputDirectory);

            var report = new StringBuilder();
            report.AppendLine("# Caustic Mesh DXR - Benchmark & Verification Report");
            report.AppendLine();
            report.AppendLine($"- **Date**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"- **GPU**: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})");
            report.AppendLine($"- **VRAM**: {SystemInfo.graphicsMemorySize} MB");
            report.AppendLine($"- **Unity**: {Application.unityVersion}");
            report.AppendLine();

            // 1. Comparison Captures (Subdivision OFF vs ON vs Highlight)
            Debug.Log("[CausticBenchmarkRunner] Capturing comparison images...");
            CaptureComparisonSet(causticTest, camera, poseRecorder, report);

            // 2. Performance Benchmark
            Debug.Log("[CausticBenchmarkRunner] Running grid resolution benchmarks...");
            RunResolutionBenchmarks(causticTest, camera, report);

            // Save report
            var reportDir = Path.GetDirectoryName(ReportPath);
            if (!string.IsNullOrEmpty(reportDir) && !Directory.Exists(reportDir))
                Directory.CreateDirectory(reportDir);

            File.WriteAllText(ReportPath, report.ToString());
            Debug.Log($"[CausticBenchmarkRunner] Benchmark report written to {ReportPath}");

            GenerateCroppedInsets();
            CopyCapturesToArtifacts();

            AssetDatabase.Refresh();
            Debug.Log("[CausticBenchmarkRunner] Pipeline finished successfully.");
            return true;
        }

        static void CaptureComparisonSet(
            CausticRayQueryTest test,
            Camera camera,
            CameraPoseRecorder poseRecorder,
            StringBuilder report)
        {
            report.AppendLine("## 1. Visual Verification & Comparison Captures");
            report.AppendLine();
            report.AppendLine("| Case | Description | Output Image |");
            report.AppendLine("|---|---|---|");

            var serialized = new SerializedObject(test);
            var subdivProp = serialized.FindProperty("subdivideReceiverBoundaries");
            var boundaryDebugProp = serialized.FindProperty("showReceiverBoundaries");
            // Warm up wave simulation so ripples spread across the surface (4.0s of propagation)
            const float dt = 1f / 60f;
            const int warmupSteps = 240;
            Debug.Log("[CausticBenchmarkRunner] Warming up wave simulation (240 steps)...");
            for (var i = 0; i < warmupSteps; i++)
            {
                var t = (i + 1) * dt;
                test.ForceEvaluateAndDispatch(t, dt);
            }
            const float captureTime = warmupSteps * dt;

            // Target poses to capture: Pose 02 (index 1) and Pose 03 (index 2)
            var targetPoses = new (int index, string name, string label)[]
            {
                (1, "pose02", "Camera Pose 02 (Front View)"),
                (2, "pose03", "Camera Pose 03 (Close-up Diagonal)")
            };

            foreach (var (poseIndex, poseName, poseLabel) in targetPoses)
            {
                if (poseRecorder != null && poseRecorder.PoseCount > poseIndex)
                {
                    poseRecorder.SelectPose(poseIndex);
                    Debug.Log($"[CausticBenchmarkRunner] Switched to {poseLabel} (index {poseIndex})");
                }

                report.AppendLine($"### {poseLabel}");
                report.AppendLine();
                report.AppendLine("| Case | Description | Output Image |");
                report.AppendLine("|---|---|---|");

                // Case A: Subdivision OFF
                subdivProp.boolValue = false;
                boundaryDebugProp.boolValue = false;
                serialized.ApplyModifiedProperties();
                test.ForceEvaluateAndDispatch(captureTime, dt);
                var fileOff = Path.Combine(OutputDirectory, $"caustic_{poseName}_subdivision_off.png");
                CaptureCamera(camera, fileOff);
                report.AppendLine($"| Subdivision OFF | Baseline without boundary handling (stretched / flying triangles) | `[{Path.GetFileName(fileOff)}]({fileOff})` |");

                // Case B: Subdivision ON
                subdivProp.boolValue = true;
                boundaryDebugProp.boolValue = false;
                serialized.ApplyModifiedProperties();
                test.ForceEvaluateAndDispatch(captureTime, dt);
                var fileOn = Path.Combine(OutputDirectory, $"caustic_{poseName}_subdivision_on.png");
                CaptureCamera(camera, fileOn);
                report.AppendLine($"| Subdivision ON | Proposed adaptive edge subdivision & boundary culling | `[{Path.GetFileName(fileOn)}]({fileOn})` |");

                // Case C: Boundary Highlight
                subdivProp.boolValue = true;
                boundaryDebugProp.boolValue = true;
                serialized.ApplyModifiedProperties();
                test.ForceEvaluateAndDispatch(captureTime, dt);
                var fileDebug = Path.Combine(OutputDirectory, $"caustic_{poseName}_boundary_highlight.png");
                CaptureCamera(camera, fileDebug);
                report.AppendLine($"| Boundary Highlight | Detected edge discontinuities colored in magenta | `[{Path.GetFileName(fileDebug)}]({fileDebug})` |");

                report.AppendLine();

                // Maintain default alias files for the primary close-up pose (Pose 03)
                if (poseName == "pose03")
                {
                    File.Copy(fileOff, Path.Combine(OutputDirectory, "caustic_subdivision_off.png"), overwrite: true);
                    File.Copy(fileOn, Path.Combine(OutputDirectory, "caustic_subdivision_on.png"), overwrite: true);
                    File.Copy(fileDebug, Path.Combine(OutputDirectory, "caustic_boundary_highlight.png"), overwrite: true);
                }
            }

            // Revert debug flag
            boundaryDebugProp.boolValue = false;
            subdivProp.boolValue = true;
            serialized.ApplyModifiedProperties();
            test.ForceEvaluateAndDispatch(captureTime, dt);
        }

        static void RunResolutionBenchmarks(
            CausticRayQueryTest test,
            Camera camera,
            StringBuilder report)
        {
            report.AppendLine("## 2. Grid Resolution & Performance Benchmark");
            report.AppendLine();
            report.AppendLine("| Grid Cells | Source Resolution | Total Triangles | Avg Frame Time (ms) | Approx FPS |");
            report.AppendLine("|---|---|---|---|---|");

            var serialized = new SerializedObject(test);
            var sourceSizeProp = serialized.FindProperty("sourceSize");
            var cellSizeProp = serialized.FindProperty("cellSize");

            sourceSizeProp.vector2Value = new Vector2(4, 4);

            var rt = RenderTexture.GetTemporary(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32);
            var prevTarget = camera.targetTexture;

            try
            {
                camera.targetTexture = rt;

                float[] testCellSizes = { 0.25f, 0.125f, 0.0625f, 0.03125f }; // ~16x16, ~32x32, ~64x64, ~128x128
                foreach (var size in testCellSizes)
                {
                    cellSizeProp.floatValue = size;
                    serialized.ApplyModifiedProperties();
                    test.ForceEvaluateAndDispatch(4.0f, 1f / 60f);

                    // Warmup
                    for (var w = 0; w < WarmupFrames; w++)
                        camera.Render();

                    // Benchmark
                    var sw = Stopwatch.StartNew();
                    for (var f = 0; f < BenchmarkFrames; f++)
                        camera.Render();
                    sw.Stop();

                    var avgMs = (float)sw.Elapsed.TotalMilliseconds / BenchmarkFrames;
                    var fps = 1000f / Mathf.Max(0.0001f, avgMs);

                    var cellsX = Mathf.RoundToInt(4f / size);
                    var cellsZ = Mathf.RoundToInt(4f / size);
                    var triangles = cellsX * cellsZ * 2;

                    report.AppendLine($"| {cellsX} x {cellsZ} | CellSize = {size:0.0000} | {triangles:N0} | {avgMs:F2} ms | {fps:0.0} FPS |");
                }
            }
            finally
            {
                camera.targetTexture = prevTarget;
                RenderTexture.ReleaseTemporary(rt);
            }

            report.AppendLine();
        }

        static void CaptureCamera(Camera camera, string filePath)
        {
            var rt = RenderTexture.GetTemporary(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32);
            var prevTarget = camera.targetTexture;
            var prevActive = RenderTexture.active;

            try
            {
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, CaptureWidth, CaptureHeight), 0, 0);
                tex.Apply();

                var bytes = tex.EncodeToPNG();
                File.WriteAllBytes(filePath, bytes);
                UnityEngine.Object.DestroyImmediate(tex);
                Debug.Log($"[CausticBenchmarkRunner] Saved capture to {filePath}");
            }
            finally
            {
                camera.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
