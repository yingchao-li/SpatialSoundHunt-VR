using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialSoundHunt
{
    /// <summary>
    /// Easy turning for desktop / XR Interaction Simulator use.
    /// Hold  ,  (comma) or U to turn left and  .  (period) or O to turn right.
    /// The XR Origin is rotated around the camera, so the head stays where it is and
    /// W A S D (simulator movement) follows the new facing direction.
    /// Speed is time based (degrees per second), so it does not depend on the frame rate.
    /// It never creates anything: it only rotates the XR Origin that already exists in the scene.
    /// </summary>
    public class LookAssist : MonoBehaviour
    {
        [Tooltip("Turn with the comma / period keys.")]
        public bool enableKeyboardTurn = true;

        [Tooltip("Turn speed in degrees per second while a turn key is held.")]
        [Min(1f)] public float turnSpeedDegPerSecond = 70f;

        XROrigin origin;

        void Update()
        {
            if (!enableKeyboardTurn) return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            float direction = 0f;
            if (keyboard.periodKey.isPressed || keyboard.numpadPeriodKey.isPressed || keyboard.oKey.isPressed) direction += 1f;
            if (keyboard.commaKey.isPressed || keyboard.uKey.isPressed) direction -= 1f;
            if (direction == 0f) return;

            if (origin == null) origin = UnityEngine.Object.FindAnyObjectByType<XROrigin>();
            if (origin == null) return;

            origin.RotateAroundCameraUsingOriginUp(direction * turnSpeedDegPerSecond * Time.unscaledDeltaTime);
        }
    }
}
