using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace SpatialSoundHunt
{
    /// <summary>
    /// One hidden sound source in the lab. Owns the 3D beacon AudioSource, the visual feedback
    /// (orb / ring / beam) and listens to XR Interaction Toolkit select events.
    /// The experiment logic itself lives in <see cref="ExperimentManager"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public class SoundTarget : MonoBehaviour
    {
        [Header("Identity")]
        public int targetIndex;
        public string targetId = "A";

        [Header("Visuals (assigned by the builder)")]
        public Transform orbTransform;
        public Renderer orbRenderer;
        public Renderer ringRenderer;
        public Renderer beamRenderer;

        [Header("XR interaction")]
        public XRBaseInteractable interactable;

        [Header("Spatial audio")]
        public AudioSource beaconSource;
        public AudioSource sfxSource;
        public float baseFrequency = 440f;
        [Range(0f, 1f)] public float noiseMix = 0.35f;
        [Range(0f, 1f)] public float beaconVolume = 0.9f;
        public float minDistance = 1.2f;
        public float maxDistance = 35f;
        [Tooltip("Distance (m) at which the one-shot proximity tick plays.")]
        public float proximityRadius = 2.5f;
        [Tooltip("Distance range (m) over which pitch / tempo of the beacon rises while approaching.")]
        public float feedbackRange = 9f;
        [Tooltip("Only enable if a spatializer plug-in (e.g. Steam Audio, Meta XR Audio, Resonance) is selected in Project Settings > Audio. Unity's built-in 3D audio is panning + attenuation, not a validated HRTF renderer.")]
        public bool useSpatializerPlugin = false;

        // ---- public state ---------------------------------------------------------------
        public bool IsActive { get; private set; }
        public bool IsCompleted { get; private set; }
        public bool ShowVisualHint { get; private set; }
        public Vector3 AimPoint => orbTransform != null ? orbTransform.position : transform.position;

        /// <summary>Raised when the participant selects this target (source = "Controller" or "HeadGaze").</summary>
        public event Action<SoundTarget, string> Selected;

        /// <summary>Raised once when the participant gets within <see cref="proximityRadius"/> of the active target.</summary>
        public event Action<SoundTarget> ProximityEntered;

        // ---- colours ----------------------------------------------------------------------
        static readonly Color IdleColor = new Color(0.45f, 0.50f, 0.58f, 1f);
        static readonly Color IdleRingColor = new Color(0.25f, 0.30f, 0.36f, 1f);
        static readonly Color HoverColor = new Color(1.00f, 0.85f, 0.20f, 1f);
        static readonly Color SelectedColor = new Color(1.00f, 0.95f, 0.60f, 1f);
        static readonly Color HintColor = new Color(0.10f, 0.90f, 1.00f, 1f);
        static readonly Color CorrectColor = new Color(0.20f, 1.00f, 0.40f, 1f);
        static readonly Color WrongColor = new Color(1.00f, 0.20f, 0.20f, 1f);
        static readonly Color DoneColor = new Color(0.25f, 0.60f, 0.35f, 1f);

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        // ---- runtime ----------------------------------------------------------------------
        MaterialPropertyBlock propertyBlock;
        AudioClip proximityClip;
        Vector3 orbBaseLocalPosition;
        Vector3 orbBaseScale = Vector3.one;
        Vector3 ringBaseScale = Vector3.one;
        Coroutine foundRoutine;

        Color flashColor = WrongColor;
        float flashTimer;
        float shakeTimer;
        float animScale = 1f;
        float animLift;
        float ringScale = 1f;
        float ringFade;
        float currentScaleFactor = 1f;
        float currentVolume;
        bool inProximity;

        // ---- unity ------------------------------------------------------------------------
        void Awake()
        {
            if (orbTransform == null) orbTransform = transform;
            if (interactable == null) interactable = GetComponentInChildren<XRBaseInteractable>();
            if (orbRenderer == null) orbRenderer = orbTransform.GetComponentInChildren<Renderer>();

            orbBaseLocalPosition = orbTransform.localPosition;
            orbBaseScale = orbTransform.localScale;
            if (ringRenderer != null) ringBaseScale = ringRenderer.transform.localScale;

            propertyBlock = new MaterialPropertyBlock();
            ConfigureAudio();
        }

        void OnEnable()
        {
            if (interactable != null)
                interactable.selectEntered.AddListener(OnSelectEntered);
        }

        void OnDisable()
        {
            if (interactable != null)
                interactable.selectEntered.RemoveListener(OnSelectEntered);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (flashTimer > 0f) flashTimer -= dt;
            if (shakeTimer > 0f) shakeTimer -= dt;

            UpdateAudio(dt);
            UpdateVisuals(dt);
        }

        // ---- public API (called by ExperimentManager) -------------------------------------
        /// <summary>Starts the beacon of this target. The visual hint is only shown in the Audio + Visual condition.</summary>
        public void Activate(bool showVisualHint)
        {
            IsActive = true;
            ShowVisualHint = showVisualHint;
            inProximity = false;
            if (beaconSource != null) beaconSource.time = 0f;
        }

        /// <summary>Mutes the beacon (fades out) and removes any hint.</summary>
        public void Deactivate()
        {
            IsActive = false;
            ShowVisualHint = false;
            inProximity = false;
        }

        public void SetVisualHint(bool show)
        {
            ShowVisualHint = show;
        }

        public void MarkCompleted()
        {
            IsCompleted = true;
        }

        /// <summary>Back to the initial, silent, neutral state.</summary>
        public void ResetTarget()
        {
            if (foundRoutine != null)
            {
                StopCoroutine(foundRoutine);
                foundRoutine = null;
            }

            IsActive = false;
            IsCompleted = false;
            ShowVisualHint = false;
            inProximity = false;
            flashTimer = 0f;
            shakeTimer = 0f;
            animScale = 1f;
            animLift = 0f;
            ringScale = 1f;
            ringFade = 0f;
        }

        public void PlayFoundAnimation(float duration)
        {
            if (foundRoutine != null) StopCoroutine(foundRoutine);
            foundRoutine = StartCoroutine(FoundRoutine(Mathf.Max(0.2f, duration)));
        }

        public void FlashWrong()
        {
            flashColor = WrongColor;
            flashTimer = 0.7f;
            shakeTimer = 0.4f;
        }

        public void PlaySfx(AudioClip clip, float volume = 1f)
        {
            if (sfxSource != null && clip != null)
                sfxSource.PlayOneShot(clip, volume);
        }

        /// <summary>Selection that does not come from an XR interactor (e.g. head-gaze fallback).</summary>
        public void RaiseSelected(string source)
        {
            Selected?.Invoke(this, source);
        }

        // ---- internals --------------------------------------------------------------------
        void OnSelectEntered(SelectEnterEventArgs args)
        {
            RaiseSelected("Controller");
        }

        void ConfigureAudio()
        {
            if (beaconSource == null) beaconSource = gameObject.AddComponent<AudioSource>();
            if (sfxSource == null) sfxSource = gameObject.AddComponent<AudioSource>();

            Configure3D(beaconSource, true);
            Configure3D(sfxSource, false);

            beaconSource.clip = ProceduralAudio.CreateBeacon("Beacon_" + targetId, baseFrequency, noiseMix, targetIndex + 17);
            beaconSource.volume = 0f;
            proximityClip = ProceduralAudio.CreateProximityTick();
        }

        void Configure3D(AudioSource source, bool loop)
        {
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 1f;                       // fully 3D
            source.spatialize = useSpatializerPlugin;       // only meaningful with a spatializer plug-in
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.dopplerLevel = 0f;
            source.spread = 0f;
        }

        void UpdateAudio(float dt)
        {
            float desired = IsActive ? beaconVolume : 0f;
            float closeness = 0f;

            Transform head = HeadPose.Head;
            if (IsActive && head != null)
            {
                float distance = Vector3.Distance(head.position, AimPoint);
                closeness = Mathf.Clamp01(1f - (distance - proximityRadius * 0.4f) / Mathf.Max(0.01f, feedbackRange));

                if (!inProximity && distance <= proximityRadius)
                {
                    inProximity = true;
                    PlaySfx(proximityClip, 0.9f);
                    ProximityEntered?.Invoke(this);
                }
                else if (inProximity && distance > proximityRadius * 1.3f)
                {
                    inProximity = false;
                }
            }

            currentVolume = Mathf.MoveTowards(currentVolume, desired, dt * 5f);

            if (beaconSource == null) return;

            beaconSource.volume = currentVolume;
            beaconSource.pitch = Mathf.Lerp(1f, 1.5f, closeness); // pitch + tempo rise when approaching

            if (currentVolume > 0.001f)
            {
                if (!beaconSource.isPlaying) beaconSource.Play();
            }
            else if (beaconSource.isPlaying)
            {
                beaconSource.Stop();
            }
        }

        void UpdateVisuals(float dt)
        {
            bool hovered = interactable != null && interactable.isHovered;
            bool selected = interactable != null && interactable.isSelected;
            bool hint = IsActive && ShowVisualHint;
            float t = Time.time;

            // Priority: flash (found / wrong) > selected > hovered > hint > completed > idle
            Color baseColor;
            Color emission;
            if (flashTimer > 0f)
            {
                baseColor = flashColor;
                emission = flashColor * 2.4f;
            }
            else if (selected)
            {
                baseColor = SelectedColor;
                emission = SelectedColor * 1.8f;
            }
            else if (hovered)
            {
                baseColor = HoverColor;
                emission = HoverColor * 1.3f;
            }
            else if (hint)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(t * 4f);
                baseColor = HintColor;
                emission = HintColor * (1.0f + 1.6f * pulse);
            }
            else if (IsCompleted)
            {
                baseColor = DoneColor;
                emission = DoneColor * 0.5f;
            }
            else
            {
                baseColor = IdleColor;
                emission = IdleColor * 0.35f;
            }

            SetColors(orbRenderer, baseColor, emission);

            // scale / bob
            float targetFactor = 1f;
            if (hovered) targetFactor *= 1.12f;
            if (hint) targetFactor *= 1f + 0.06f * Mathf.Sin(t * 5f);
            currentScaleFactor = Mathf.Lerp(currentScaleFactor, targetFactor, 1f - Mathf.Exp(-12f * dt));

            float bob = 0.04f * Mathf.Sin(t * 1.3f + targetIndex * 1.7f);
            float shake = shakeTimer > 0f ? 0.05f * Mathf.Sin(t * 70f) : 0f;
            orbTransform.localScale = orbBaseScale * (currentScaleFactor * animScale);
            orbTransform.localPosition = orbBaseLocalPosition + new Vector3(shake, bob + animLift, 0f);

            // ring
            if (ringRenderer != null)
            {
                Color ringColor;
                float ringEmission;
                if (flashTimer > 0f)
                {
                    ringColor = flashColor;
                    ringEmission = Mathf.Max(0.3f, ringFade * 2f);
                }
                else if (hint)
                {
                    ringColor = HintColor;
                    ringEmission = 1.6f;
                }
                else
                {
                    ringColor = IdleRingColor;
                    ringEmission = 0.25f;
                }

                SetColors(ringRenderer, ringColor, ringColor * ringEmission);
                ringRenderer.transform.localScale = new Vector3(ringBaseScale.x * ringScale, ringBaseScale.y, ringBaseScale.z * ringScale);
            }

            // beam (visual hint only)
            if (beamRenderer != null)
            {
                bool showBeam = hint && flashTimer <= 0f;
                if (beamRenderer.enabled != showBeam) beamRenderer.enabled = showBeam;
                if (showBeam)
                    SetColors(beamRenderer, HintColor, HintColor * (1.2f + 0.8f * Mathf.Sin(t * 3f)));
            }
        }

        void SetColors(Renderer target, Color baseColor, Color emission)
        {
            if (target == null) return;
            target.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor(BaseColorId, baseColor);
            propertyBlock.SetColor(EmissionColorId, emission);
            target.SetPropertyBlock(propertyBlock);
        }

        IEnumerator FoundRoutine(float duration)
        {
            flashColor = CorrectColor;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float u = Mathf.Clamp01(elapsed / duration);
                flashTimer = Mathf.Max(duration - elapsed, 0.02f); // keep the green flash alive for the whole animation
                animScale = 1f + 0.8f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(u * 1.4f));
                animLift = 0.35f * Mathf.Sin(Mathf.PI * u);
                ringScale = 1f + 5f * u;
                ringFade = 1f - u;
                yield return null;
            }

            animScale = 1f;
            animLift = 0f;
            ringScale = 1f;
            ringFade = 0f;
            flashTimer = 0f;
            foundRoutine = null;
        }
    }
}
