using UnityEditor;
using UnityEngine;

namespace CausticMeshDxr.Editor
{
    [CustomEditor(typeof(CameraPoseRecorder))]
    public sealed class CameraPoseRecorderEditor : UnityEditor.Editor
    {
        SerializedProperty targetCamera;
        SerializedProperty maxRecordCount;
        SerializedProperty copyCameraChangesToCurrentPose;
        SerializedProperty smoothTransitions;
        SerializedProperty positionSmoothTime;
        SerializedProperty rotationSmoothTime;

        void OnEnable()
        {
            targetCamera = serializedObject.FindProperty("targetCamera");
            maxRecordCount = serializedObject.FindProperty("maxRecordCount");
            copyCameraChangesToCurrentPose = serializedObject.FindProperty("copyCameraChangesToCurrentPose");
            smoothTransitions = serializedObject.FindProperty("smoothTransitions");
            positionSmoothTime = serializedObject.FindProperty("positionSmoothTime");
            rotationSmoothTime = serializedObject.FindProperty("rotationSmoothTime");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var recorder = (CameraPoseRecorder)target;

            EditorGUILayout.PropertyField(targetCamera);
            EditorGUILayout.PropertyField(maxRecordCount);
            EditorGUILayout.PropertyField(copyCameraChangesToCurrentPose);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Smoothing (Play Mode)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(smoothTransitions);
            if (smoothTransitions.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(positionSmoothTime);
                EditorGUILayout.PropertyField(rotationSmoothTime);
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Recorded Positions ({recorder.PoseCount}/{recorder.MaxRecordCount})", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(recorder.PoseCount == 0))
            {
                DrawPoseSelector(recorder);

                using (new EditorGUI.DisabledScope(recorder.TargetCamera == null))
                {
                    if (GUILayout.Button("Copy Camera To Current Position"))
                    {
                        Undo.RecordObject(recorder.SelectedPose, "Update Camera Position");
                        recorder.CopyCameraToSelectedPose();
                        EditorUtility.SetDirty(recorder.SelectedPose);
                    }
                }
            }

            using (new EditorGUI.DisabledScope(
                recorder.TargetCamera == null || recorder.PoseCount >= recorder.MaxRecordCount))
            {
                if (GUILayout.Button("Record Current Camera"))
                {
                    Undo.RecordObject(recorder, "Record Camera Position");
                    var pose = recorder.RecordPose();
                    if (pose != null)
                    {
                        Undo.RegisterCreatedObjectUndo(pose.gameObject, "Record Camera Position");
                        EditorUtility.SetDirty(recorder);
                        Selection.activeTransform = pose;
                    }
                }
            }

            if (recorder.PoseCount >= recorder.MaxRecordCount)
                EditorGUILayout.HelpBox("Maximum record count reached.", MessageType.Info);
            else if (recorder.TargetCamera == null)
                EditorGUILayout.HelpBox("Assign a target camera before recording.", MessageType.Warning);

            EditorGUILayout.HelpBox(
                "Direct child Transforms are used as recorded camera positions. Keep unrelated children on another GameObject.",
                MessageType.None);
        }

        void DrawPoseSelector(CameraPoseRecorder recorder)
        {
            var poseCount = recorder.PoseCount;
            if (poseCount == 0)
            {
                EditorGUILayout.LabelField("Position Index", "No positions recorded");
                return;
            }

            var currentIndex = recorder.SelectedIndex;
            EditorGUI.BeginChangeCheck();
            var nextIndex = EditorGUILayout.IntSlider("Position Index", currentIndex, 0, poseCount - 1);
            if (EditorGUI.EndChangeCheck())
                SelectPose(recorder, nextIndex);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(currentIndex <= 0))
                {
                    if (GUILayout.Button("Previous"))
                        SelectPose(recorder, currentIndex - 1);
                }
                using (new EditorGUI.DisabledScope(currentIndex >= poseCount - 1))
                {
                    if (GUILayout.Button("Next"))
                        SelectPose(recorder, currentIndex + 1);
                }
            }

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Current Transform", recorder.SelectedPose, typeof(Transform), true);
        }

        static void SelectPose(CameraPoseRecorder recorder, int index)
        {
            if (recorder.TargetCamera != null)
            {
                Undo.RecordObjects(
                    new Object[] { recorder, recorder.TargetCamera.transform },
                    "Select Camera Position");
            }
            else
            {
                Undo.RecordObject(recorder, "Select Camera Position");
            }
            recorder.SelectPose(index);
            EditorUtility.SetDirty(recorder);
            if (recorder.TargetCamera != null)
                EditorUtility.SetDirty(recorder.TargetCamera.transform);
            SceneView.RepaintAll();
        }
    }
}
