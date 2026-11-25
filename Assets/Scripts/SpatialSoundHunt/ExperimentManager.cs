using System;
using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SpatialSoundHunt
{
    /// <summary>
    /// Experiment state machine:  Ready -> Exploring -> TargetFound -> Transition -> (Exploring | Completed).
    /// Owns target ordering, correct / wrong target detection, condition switching and the keyboard shortcuts.
    /// Everything else (UI, recorder, visual guidance) listens to the events declared here.
    /// </summary>
    public class ExperimentManager : MonoBehaviour
    {
        [Header("Wiring (assigned by the builder)")]
        public List<SoundTarget> targets = new List<SoundTarget>();
        public SessionRecorder recorder;
        public AudioSource uiAudioSource;

        [Header("Experiment settings")]
        public ExperimentCondition condition = ExperimentCondition.AudioOnly;
        [Tooltip("Randomise the target order at every start. Off = A, B, C.")]
        public bool shuffleOrder = false;
        [Tooltip("0 = different random order each time.")]
        public int orderSeed = 0;
        public float foundAnimationSeconds = 1.4f;
        public float transitionSeconds = 1.2f;
        public float wrongSelectionCooldown = 0.6f;

        [Header("Input")]
        public bool enableKeyboardShortcuts = true;
        public float headGazeMaxDistance = 40f;

        // ---- events -----------------------------------------------------------------------
        public event Action<ExperimentState, ExperimentState> StateChanged;
        public event Action<SoundTarget, int> TargetActivated;
        public event Action<SoundTarget, int, float> TargetFound;
        public event Action<SoundTarget, SoundTarget> WrongTargetSelected;
        public event Action<ExperimentCondition> ConditionChanged;
        public event Action ExperimentCompleted;
        public event Action ExperimentReset;

        // ---- public state -----------------------------------------------------------------
        public ExperimentState State { get; private set; } = ExperimentState.Ready;
        public ExperimentCondition Condition => condition;
        public int CurrentIndex => currentIndex;
        public int TargetCount => order.Count > 0 ? order.Count : targets.Count;
        public int FoundCount { get; private set; }
        public int WrongCount { get; private set; }

        /// <summary>The target whose beacon is playing right now (null outside the Exploring state).</summary>
        public SoundTarget ActiveTarget
        {
            get
            {
                if (State != ExperimentState.Exploring) return null;
                if (currentIndex < 0 || currentIndex >= order.Count) return null;
                return order[currentIndex];
            }
        }

        public string CurrentTargetId
        {
            get
            {
                SoundTarget t = ActiveTarget;
                return t != null ? t.targetId : "None";
            }
        }

        /// <summary>Seconds since the session started (frozen after completion).</summary>
        public float SessionElapsed
        {
            get
            {
                if (State == ExperimentState.Ready) return 0f;
                if (State == ExperimentState.Completed) return lastSessionDuration;
                return Time.time - sessionStartTime;
            }
        }

        // ---- internals --------------------------------------------------------------------
        readonly List<SoundTarget> order = new List<SoundTarget>();
        int currentIndex;
        float sessionStartTime;
        float targetActivatedTime;
        float lastSessionDuration;
        Coroutine sequenceRoutine;
        readonly Dictionary<SoundTarget, float> lastWrongTime = new Dictionary<SoundTarget, float>();

        AudioClip startClip;
        AudioClip successClip;
        AudioClip errorClip;
        AudioClip completeClip;

        // ---- unity ------------------------------------------------------------------------
        void Awake()
        {
            if (targets == null) targets = new List<SoundTarget>();
            if (targets.Count == 0)
            {
                SoundTarget[] found = UnityEngine.Object.FindObjectsByType<SoundTarget>();
                targets.AddRange(found);
            }

            targets.Sort((a, b) => a.targetIndex.CompareTo(b.targetIndex));

            if (recorder == null) recorder = GetComponent<SessionRecorder>();

            if (uiAudioSource == null)
            {
                uiAudioSource = gameObject.AddComponent<AudioSource>();
                uiAudioSource.playOnAwake = false;
                uiAudioSource.spatialBlend = 0f; // 2D feedback sounds
            }

            startClip = ProceduralAudio.CreateStart();
            successClip = ProceduralAudio.CreateSuccess();
            errorClip = ProceduralAudio.CreateError();
            completeClip = ProceduralAudio.CreateComplete();

            order.AddRange(targets);
        }

        void OnEnable()
        {
            foreach (SoundTarget t in targets)
                if (t != null) t.Selected += OnTargetSelected;
        }

        void OnDisable()
        {
            foreach (SoundTarget t in targets)
                if (t != null) t.Selected -= OnTargetSelected;
        }

        void Start()
        {
            foreach (SoundTarget t in targets)
                if (t != null) t.ResetTarget();

            State = ExperimentState.Ready;
            StateChanged?.Invoke(ExperimentState.Ready, ExperimentState.Ready);
        }

        void Update()
        {
            if (enableKeyboardShortcuts) HandleKeyboard();
        }

        void HandleKeyboard()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                StartOrRestart();
            if (keyboard.mKey.wasPressedThisFrame)
                ToggleCondition();
            if (keyboard.nKey.wasPressedThisFrame)
                ResetToReady();
            if (keyboard.fKey.wasPressedThisFrame)
                TryHeadGazeSelect();
        }

        // ---- public commands ---------------------------------------------------------------
        /// <summary>Start from Ready, otherwise restart the experiment.</summary>
        public void StartOrRestart()
        {
            if (State == ExperimentState.Ready)
            {
                StartExperiment();
            }
            else
            {
                ResetToReady();
                StartExperiment();
            }
        }

        public void StartExperiment()
        {
            if (State != ExperimentState.Ready) return;
            if (targets.Count == 0)
            {
                Debug.LogError("[ExperimentManager] No SoundTargets assigned.");
                return;
            }

            StopSequence();
            BuildOrder();
            currentIndex = 0;
            FoundCount = 0;
            WrongCount = 0;
            lastWrongTime.Clear();

            foreach (SoundTarget t in targets)
                if (t != null) t.ResetTarget();

            sessionStartTime = Time.time;
            lastSessionDuration = 0f;

            if (recorder != null) recorder.BeginSession(OrderDescription());

            PlayUiSfx(startClip, 0.8f);
            EnterState(ExperimentState.Exploring);
            ActivateCurrentTarget();
        }

        /// <summary>Abort whatever is running and return to the Ready state (all targets silent).</summary>
        public void ResetToReady()
        {
            StopSequence();

            if (recorder != null && recorder.IsRecording)
            {
                recorder.LogEvent("ExperimentAborted", CurrentTargetId, "state=" + State);
                recorder.EndSession(State == ExperimentState.Completed ? "Completed" : "Aborted");
            }

            foreach (SoundTarget t in targets)
                if (t != null) t.ResetTarget();

            currentIndex = 0;
            FoundCount = 0;
            WrongCount = 0;
            EnterState(ExperimentState.Ready);
            ExperimentReset?.Invoke();
        }

        public void ToggleCondition()
        {
            SetCondition(condition == ExperimentCondition.AudioOnly ? ExperimentCondition.AudioVisual : ExperimentCondition.AudioOnly);
        }

        /// <summary>Switch the condition. It also works mid-run: the hint is applied immediately and the change is logged.</summary>
        public void SetCondition(ExperimentCondition newCondition)
        {
            if (newCondition == condition) return;

            condition = newCondition;

            SoundTarget active = ActiveTarget;
            if (active != null) active.SetVisualHint(condition == ExperimentCondition.AudioVisual);

            if (recorder != null) recorder.LogEvent("ConditionChanged", CurrentTargetId, condition.ToString());
            ConditionChanged?.Invoke(condition);
        }

        /// <summary>Keyboard fallback for selection: confirm the target the head is looking at.</summary>
        public void TryHeadGazeSelect()
        {
            Transform head = HeadPose.Head;
            if (head == null) return;

            RaycastHit hit;
            if (!Physics.Raycast(head.position, head.forward, out hit, headGazeMaxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return;

            SoundTarget target = hit.collider.GetComponentInParent<SoundTarget>();
            if (target != null) target.RaiseSelected("HeadGaze");
        }

        // ---- debug helpers (Play Mode only) ------------------------------------------------
        // Select the "Experiment" object, then use the three-dots menu of the ExperimentManager
        // component header in the Inspector -> Debug/... . They drive the same events as a real selection.
        [ContextMenu("Debug/Select Active Target (correct)")]
        void DebugSelectActiveTarget()
        {
            if (!Application.isPlaying) return;
            SoundTarget t = ActiveTarget;
            if (t != null) t.RaiseSelected("DebugMenu");
        }

        [ContextMenu("Debug/Select Another Target (wrong)")]
        void DebugSelectWrongTarget()
        {
            if (!Application.isPlaying) return;
            SoundTarget expected = ActiveTarget;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] == null || targets[i] == expected) continue;
                targets[i].RaiseSelected("DebugMenu");
                return;
            }
        }

        [ContextMenu("Debug/Place Player 3 m In Front Of Active Target")]
        void DebugPlacePlayerNearActiveTarget()
        {
            if (!Application.isPlaying) return;
            SoundTarget t = ActiveTarget;
            Transform head = HeadPose.Head;
            XROrigin origin = UnityEngine.Object.FindAnyObjectByType<XROrigin>();
            if (t == null || head == null || origin == null) return;

            Vector3 toTarget = t.AimPoint - head.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f) return;
            toTarget.Normalize();

            float headHeight = head.position.y;
            origin.MatchOriginUpCameraForward(Vector3.up, toTarget);
            origin.MoveCameraToWorldLocation(new Vector3(t.AimPoint.x, headHeight, t.AimPoint.z) - toTarget * 3f);
            Physics.SyncTransforms();
        }

        // ---- state machine -----------------------------------------------------------------
        void OnTargetSelected(SoundTarget target, string source)
        {
            if (State != ExperimentState.Exploring) return;

            SoundTarget expected = ActiveTarget;
            if (expected == null) return;

            if (target == expected) HandleCorrectTarget(target, source);
            else HandleWrongTarget(target, expected, source);
        }

        void HandleCorrectTarget(SoundTarget target, string source)
        {
            float reaction = Time.time - targetActivatedTime;
            FoundCount++;

            if (recorder != null)
                recorder.LogEvent("TargetSelected", target.targetId,
                    "result=Correct;input=" + source + ";search_time_s=" + reaction.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

            EnterState(ExperimentState.TargetFound);

            target.Deactivate();
            target.MarkCompleted();
            target.PlayFoundAnimation(foundAnimationSeconds);
            target.PlaySfx(successClip, 1f);

            TargetFound?.Invoke(target, currentIndex, reaction);

            StopSequence();
            sequenceRoutine = StartCoroutine(AfterFoundRoutine());
        }

        void HandleWrongTarget(SoundTarget target, SoundTarget expected, string source)
        {
            float last;
            if (lastWrongTime.TryGetValue(target, out last) && Time.time - last < wrongSelectionCooldown)
                return;
            lastWrongTime[target] = Time.time;

            WrongCount++;
            target.FlashWrong();
            target.PlaySfx(errorClip, 1f);

            if (recorder != null)
                recorder.LogEvent("TargetSelected", target.targetId, "result=Wrong;input=" + source + ";expected=" + expected.targetId);

            WrongTargetSelected?.Invoke(target, expected);
        }

        IEnumerator AfterFoundRoutine()
        {
            yield return new WaitForSeconds(foundAnimationSeconds);

            EnterState(ExperimentState.Transition);
            yield return new WaitForSeconds(transitionSeconds);

            currentIndex++;
            if (currentIndex < order.Count)
            {
                EnterState(ExperimentState.Exploring);
                ActivateCurrentTarget();
            }
            else
            {
                CompleteExperiment();
            }

            sequenceRoutine = null;
        }

        void ActivateCurrentTarget()
        {
            SoundTarget target = order[currentIndex];
            targetActivatedTime = Time.time;
            target.Activate(condition == ExperimentCondition.AudioVisual);

            if (recorder != null)
                recorder.LogEvent("TargetActivated", target.targetId, "index=" + (currentIndex + 1) + "/" + order.Count);

            TargetActivated?.Invoke(target, currentIndex);
        }

        void CompleteExperiment()
        {
            lastSessionDuration = Time.time - sessionStartTime;
            EnterState(ExperimentState.Completed);
            PlayUiSfx(completeClip, 0.9f);

            if (recorder != null)
            {
                recorder.LogEvent("ExperimentCompleted", "",
                    "total_time_s=" + lastSessionDuration.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    ";wrong_selections=" + WrongCount);
                recorder.EndSession("Completed");
            }

            ExperimentCompleted?.Invoke();
        }

        void EnterState(ExperimentState next)
        {
            ExperimentState previous = State;
            State = next;
            if (recorder != null && recorder.IsRecording)
                recorder.LogEvent("StateChanged", CurrentTargetId, previous + "->" + next);
            StateChanged?.Invoke(previous, next);
        }

        // ---- helpers -----------------------------------------------------------------------
        void BuildOrder()
        {
            order.Clear();
            foreach (SoundTarget t in targets)
                if (t != null) order.Add(t);

            if (!shuffleOrder) return;

            System.Random rng = orderSeed != 0 ? new System.Random(orderSeed) : new System.Random();
            for (int i = order.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                SoundTarget tmp = order[i];
                order[i] = order[j];
                order[j] = tmp;
            }
        }

        string OrderDescription()
        {
            string s = "";
            for (int i = 0; i < order.Count; i++)
                s += (i > 0 ? ">" : "") + order[i].targetId;
            return s;
        }

        void StopSequence()
        {
            if (sequenceRoutine != null)
            {
                StopCoroutine(sequenceRoutine);
                sequenceRoutine = null;
            }
        }

        void PlayUiSfx(AudioClip clip, float volume)
        {
            if (uiAudioSource != null && clip != null)
                uiAudioSource.PlayOneShot(clip, volume);
        }
    }
}
