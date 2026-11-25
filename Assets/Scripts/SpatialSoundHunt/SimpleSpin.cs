using UnityEngine;

namespace SpatialSoundHunt
{
    /// <summary>Tiny decorative rotator (hologram ring on the central console).</summary>
    public class SimpleSpin : MonoBehaviour
    {
        public Vector3 degreesPerSecond = new Vector3(0f, 30f, 0f);

        void Update()
        {
            transform.Rotate(degreesPerSecond * Time.deltaTime, Space.Self);
        }
    }
}
