using UnityEngine;

namespace SpatialSoundHunt
{
    /// <summary>
    /// "Audio + Visual" condition only: a small arrow floats in front of the participant (below eye level)
    /// and points towards the active target. In the "Audio Only" condition it is hidden, and the
    /// targets themselves never reveal which one is active.
    /// </summary>
    public class VisualGuidance : MonoBehaviour
    {
        [Header("Wiring (assigned by the builder)")]
        public ExperimentManager manager;
        public Material arrowMaterial;

        [Header("Placement")]
        public float distanceFromHead = 1.1f;
        public float verticalOffset = -0.38f;
        public float arrowScale = 1.2f;

        GameObject arrowRoot;
        bool shown;

        void Awake()
        {
            BuildArrow();
            SetShown(false);
        }

        void LateUpdate()
        {
            if (manager == null || arrowRoot == null) return;

            Transform head = HeadPose.Head;
            SoundTarget target = manager.ActiveTarget;
            bool visible = head != null
                           && target != null
                           && manager.State == ExperimentState.Exploring
                           && manager.Condition == ExperimentCondition.AudioVisual;

            SetShown(visible);
            if (!visible) return;

            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
            forward.Normalize();

            Vector3 desiredPosition = head.position + forward * distanceFromHead + Vector3.up * verticalOffset;
            Vector3 toTarget = target.AimPoint - desiredPosition;
            Quaternion desiredRotation = toTarget.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(toTarget.normalized, Vector3.up)
                : arrowRoot.transform.rotation;

            Transform t = arrowRoot.transform;
            float dt = Time.deltaTime;
            t.position = Vector3.Lerp(t.position, desiredPosition, 1f - Mathf.Exp(-14f * dt));
            t.rotation = Quaternion.Slerp(t.rotation, desiredRotation, 1f - Mathf.Exp(-10f * dt));
            t.localScale = Vector3.one * (arrowScale * (1f + 0.06f * Mathf.Sin(Time.time * 4f)));
        }

        void SetShown(bool value)
        {
            if (shown == value && arrowRoot != null && arrowRoot.activeSelf == value) return;
            shown = value;
            if (arrowRoot != null) arrowRoot.SetActive(value);
        }

        void BuildArrow()
        {
            arrowRoot = new GameObject("GuidanceArrow");
            arrowRoot.transform.SetParent(transform, false);

            Material material = arrowMaterial != null ? arrowMaterial : CreateFallbackMaterial();

            // Arrow points along +Z: shaft + two chevron arms.
            AddBar(arrowRoot.transform, new Vector3(0f, 0f, -0.12f), new Vector3(0f, 0f, 0.12f), 0.035f, material);
            AddBar(arrowRoot.transform, new Vector3(0f, 0f, 0.13f), new Vector3(-0.08f, 0f, 0.03f), 0.035f, material);
            AddBar(arrowRoot.transform, new Vector3(0f, 0f, 0.13f), new Vector3(0.08f, 0f, 0.03f), 0.035f, material);
        }

        static void AddBar(Transform parent, Vector3 from, Vector3 to, float thickness, Material material)
        {
            GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = "ArrowPart";

            // The arrow must never intercept XR rays or head-gaze raycasts.
            Collider collider = bar.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            Renderer renderer = bar.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            Vector3 direction = to - from;
            bar.transform.SetParent(parent, false);
            bar.transform.localPosition = (from + to) * 0.5f;
            bar.transform.localRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            bar.transform.localScale = new Vector3(thickness, thickness * 0.5f, direction.magnitude + thickness);
        }

        static Material CreateFallbackMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            Material material = new Material(shader);
            material.color = new Color(0.1f, 0.9f, 1f, 1f);
            return material;
        }
    }
}
