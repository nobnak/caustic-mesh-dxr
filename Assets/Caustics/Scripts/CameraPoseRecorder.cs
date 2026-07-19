using UnityEngine;

namespace CausticMeshDxr
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class CameraPoseRecorder : MonoBehaviour
    {
        const float PositionEpsilon = 0.000001f;
        const float RotationEpsilon = 0.001f;

        [SerializeField] Camera targetCamera;
        [SerializeField, Min(1)] int maxRecordCount = 20;
        [SerializeField, Min(0)] int selectedIndex;
        [SerializeField] bool copyCameraChangesToCurrentPose = true;

        [Header("Smoothing (Play Mode)")]
        [SerializeField] bool smoothTransitions = true;
        [SerializeField, Min(0.001f)] float positionSmoothTime = 0.25f;
        [SerializeField, Min(0.001f)] float rotationSmoothTime = 0.25f;

        Vector3 positionVelocity;
        Vector3 lastCameraPosition;
        Quaternion lastCameraRotation;
        bool cameraStateInitialized;
        bool moveToSelectedPose;

        public Camera TargetCamera => targetCamera;
        public int MaxRecordCount => maxRecordCount;
        public int PoseCount => transform.childCount;
        public int SelectedIndex => Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, PoseCount - 1));
        public Transform SelectedPose => PoseCount > 0 ? transform.GetChild(SelectedIndex) : null;

        void OnEnable()
        {
            ClampSettings();
            CacheCameraState();
        }

        void OnValidate()
        {
            ClampSettings();
            positionVelocity = Vector3.zero;
            CacheCameraState();
        }

        void LateUpdate()
        {
            if (targetCamera == null || SelectedPose == null)
                return;

            if (!cameraStateInitialized)
            {
                CacheCameraState();
                return;
            }

            if (copyCameraChangesToCurrentPose && CameraMovedSinceLastUpdate())
            {
                CopyCameraToSelectedPose();
                CacheCameraState();
                moveToSelectedPose = false;
                positionVelocity = Vector3.zero;
                return;
            }

            if (!moveToSelectedPose)
                return;

            if (!Application.isPlaying || !smoothTransitions)
            {
                SnapCameraToSelectedPose();
                return;
            }

            SmoothCameraToSelectedPose();
        }

        public Transform RecordPose()
        {
            if (targetCamera == null || PoseCount >= maxRecordCount)
                return null;

            var poseObject = new GameObject($"Camera Pose {PoseCount + 1:D2}");
            var pose = poseObject.transform;
            pose.SetParent(transform, true);
            pose.SetPositionAndRotation(targetCamera.transform.position, targetCamera.transform.rotation);
            selectedIndex = pose.GetSiblingIndex();
            moveToSelectedPose = false;
            positionVelocity = Vector3.zero;
            CacheCameraState();
            return pose;
        }

        public void SelectPose(int index)
        {
            if (PoseCount == 0)
                return;

            selectedIndex = Mathf.Clamp(index, 0, PoseCount - 1);
            moveToSelectedPose = true;
            positionVelocity = Vector3.zero;

            // The camera is intentionally about to move. Cache its current state so the
            // automatic camera-to-pose synchronization does not treat that as user input.
            CacheCameraState();

            if (!Application.isPlaying || !smoothTransitions)
                SnapCameraToSelectedPose();
        }

        public void CopyCameraToSelectedPose()
        {
            var pose = SelectedPose;
            if (targetCamera == null || pose == null)
                return;

            pose.SetPositionAndRotation(targetCamera.transform.position, targetCamera.transform.rotation);
            CacheCameraState();
        }

        void SmoothCameraToSelectedPose()
        {
            var cameraTransform = targetCamera.transform;
            var pose = SelectedPose;
            var deltaTime = Mathf.Max(Time.deltaTime, 0.000001f);
            var position = Vector3.SmoothDamp(
                cameraTransform.position,
                pose.position,
                ref positionVelocity,
                positionSmoothTime,
                Mathf.Infinity,
                deltaTime);
            var rotationLerp = 1 - Mathf.Exp(-deltaTime / rotationSmoothTime);
            var rotation = Quaternion.Slerp(cameraTransform.rotation, pose.rotation, rotationLerp);
            cameraTransform.SetPositionAndRotation(position, rotation);

            if ((position - pose.position).sqrMagnitude <= PositionEpsilon
                && Quaternion.Angle(rotation, pose.rotation) <= RotationEpsilon)
            {
                cameraTransform.SetPositionAndRotation(pose.position, pose.rotation);
                moveToSelectedPose = false;
                positionVelocity = Vector3.zero;
            }

            CacheCameraState();
        }

        void SnapCameraToSelectedPose()
        {
            var pose = SelectedPose;
            if (targetCamera == null || pose == null)
                return;

            targetCamera.transform.SetPositionAndRotation(pose.position, pose.rotation);
            moveToSelectedPose = false;
            positionVelocity = Vector3.zero;
            CacheCameraState();
        }

        bool CameraMovedSinceLastUpdate()
        {
            var cameraTransform = targetCamera.transform;
            return (cameraTransform.position - lastCameraPosition).sqrMagnitude > PositionEpsilon
                || Quaternion.Angle(cameraTransform.rotation, lastCameraRotation) > RotationEpsilon;
        }

        void CacheCameraState()
        {
            if (targetCamera == null)
            {
                cameraStateInitialized = false;
                return;
            }

            lastCameraPosition = targetCamera.transform.position;
            lastCameraRotation = targetCamera.transform.rotation;
            cameraStateInitialized = true;
        }

        void ClampSettings()
        {
            maxRecordCount = Mathf.Max(1, maxRecordCount);
            selectedIndex = Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, PoseCount - 1));
            positionSmoothTime = Mathf.Max(0.001f, positionSmoothTime);
            rotationSmoothTime = Mathf.Max(0.001f, rotationSmoothTime);
        }
    }
}
