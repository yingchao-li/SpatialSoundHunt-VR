using Unity.XR.CoreUtils;
using UnityEngine;

namespace SpatialSoundHunt
{
    /// <summary>
    /// Resolves the participant's head (HMD / XR Simulator camera) without creating anything new.
    /// It re-uses the camera of the XR Origin that comes with the VR Template.
    /// </summary>
    public static class HeadPose
    {
        static Camera s_Camera;
        static float s_NextResolveTime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_Camera = null;
            s_NextResolveTime = 0f;
        }

        /// <summary>The XR camera (null when no XR Origin / camera exists yet).</summary>
        public static Camera Camera
        {
            get
            {
                if (s_Camera == null && Time.unscaledTime >= s_NextResolveTime)
                {
                    s_Camera = Resolve();
                    // Throttle the (expensive) scene search while no camera exists.
                    s_NextResolveTime = Time.unscaledTime + 0.5f;
                }

                return s_Camera;
            }
        }

        public static Transform Head
        {
            get
            {
                Camera cam = Camera;
                return cam != null ? cam.transform : null;
            }
        }

        /// <summary>World-space head pose. Returns false if there is no camera.</summary>
        public static bool TryGet(out Vector3 position, out Quaternion rotation)
        {
            Transform head = Head;
            if (head == null)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }

            head.GetPositionAndRotation(out position, out rotation);
            return true;
        }

        static Camera Resolve()
        {
            XROrigin origin = UnityEngine.Object.FindAnyObjectByType<XROrigin>();
            if (origin != null && origin.Camera != null)
                return origin.Camera;

            return UnityEngine.Camera.main;
        }
    }
}
