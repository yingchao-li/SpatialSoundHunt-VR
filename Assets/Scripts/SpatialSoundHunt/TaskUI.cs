using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SpatialSoundHunt
{
    /// <summary>
    /// Presents the world-space mission console (state, condition, progress, instructions, buttons)
    /// and an optional tiny head-locked status line. All widgets are created by the editor builder;
    /// this component only binds them to the <see cref="ExperimentManager"/> events.
    /// </summary>
    public class TaskUI : MonoBehaviour
    {
        [Header("Wiring (assigned by the builder)")]
        public ExperimentManager manager;
        public SessionRecorder recorder;
        [Tooltip("Optional: shows the Python analysis status. Found automatically when empty.")]
        public AnalysisBridge analysis;
        public Canvas worldCanvas;

        [Header("Texts")]
        public TMP_Text stateText;
        public TMP_Text conditionText;
        public TMP_Text progressText;
        public TMP_Text timerText;
        public TMP_Text errorsText;
        public TMP_Text instructionText;
        public TMP_Text feedbackText;
        public TMP_Text hudText;

        [Header("Progress bar (fill rect is stretched via its right anchor)")]
        public RectTransform progressFill;

        [Header("Buttons")]
        public Button startButton;
        public Button conditionButton;
        public Button resetButton;
        public TMP_Text startButtonLabel;
        public TMP_Text conditionButtonLabel;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Color Good = new Color(0.35f, 1f, 0.55f, 1f);
        static readonly Color Bad = new Color(1f, 0.35f, 0.35f, 1f);
        static readonly Color Info = new Color(0.75f, 0.9f, 1f, 1f);

        float feedbackTimer;
        Color feedbackColor = Info;
        float refreshTimer;

        void Awake()
        {
            if (startButton != null) startButton.onClick.AddListener(OnStartClicked);
            if (conditionButton != null) conditionButton.onClick.AddListener(OnConditionClicked);
            if (resetButton != null) resetButton.onClick.AddListener(OnResetClicked);
        }

        void OnEnable()
        {
            if (analysis == null) analysis = UnityEngine.Object.FindAnyObjectByType<AnalysisBridge>();
            if (analysis != null) analysis.StatusChanged += OnAnalysisStatus;

            if (manager == null) return;
            manager.StateChanged += OnStateChanged;
            manager.TargetFound += OnTargetFound;
            manager.WrongTargetSelected += OnWrongTarget;
            manager.ConditionChanged += OnConditionChanged;
            manager.ExperimentCompleted += OnCompleted;
            manager.ExperimentReset += OnReset;
        }

        void OnDisable()
        {
            if (analysis != null) analysis.StatusChanged -= OnAnalysisStatus;

            if (manager == null) return;
            manager.StateChanged -= OnStateChanged;
            manager.TargetFound -= OnTargetFound;
            manager.WrongTargetSelected -= OnWrongTarget;
            manager.ConditionChanged -= OnConditionChanged;
            manager.ExperimentCompleted -= OnCompleted;
            manager.ExperimentReset -= OnReset;
        }

        void Start()
        {
            BindCamera();
            RefreshStatic();
            RefreshDynamic();
        }

        void Update()
        {
            if (worldCanvas != null && worldCanvas.worldCamera == null) BindCamera();

            refreshTimer -= Time.unscaledDeltaTime;
            if (refreshTimer <= 0f)
            {
                refreshTimer = 0.1f;
                RefreshDynamic();
            }

            if (feedbackText != null && feedbackTimer > 0f)
            {
                feedbackTimer -= Time.unscaledDeltaTime;
                float alpha = Mathf.Clamp01(feedbackTimer / 0.6f);
                Color c = feedbackColor;
                c.a = feedbackTimer > 0.6f ? 1f : alpha;
                feedbackText.color = c;
                if (feedbackTimer <= 0f) feedbackText.text = "";
            }
        }

        // ---- manager events -----------------------------------------------------------------
        void OnStateChanged(ExperimentState previous, ExperimentState next)
        {
            RefreshStatic();
            RefreshDynamic();
        }

        void OnTargetFound(SoundTarget target, int index, float seconds)
        {
            ShowFeedback("Source " + target.targetId + " found in " + seconds.ToString("F1", Inv) + " s", Good, 3f);
        }

        void OnWrongTarget(SoundTarget selected, SoundTarget expected)
        {
            ShowFeedback("Wrong source selected - keep listening!", Bad, 2.5f);
            RefreshDynamic();
        }

        void OnConditionChanged(ExperimentCondition condition)
        {
            ShowFeedback("Condition: " + condition.Label(), Info, 2.5f);
            RefreshStatic();
        }

        void OnCompleted()
        {
            string path = recorder != null ? recorder.LastSavedPath : "";
            ShowFeedback("All sources found! Data saved to:\n" + path, Good, 12f);
        }

        void OnReset()
        {
            ShowFeedback("Experiment reset.", Info, 2f);
        }

        /// <summary>Status of the Python analysis (running / report opened / failed - the CSV is always kept).</summary>
        void OnAnalysisStatus(AnalysisState state, string message)
        {
            switch (state)
            {
                case AnalysisState.Running:
                    ShowFeedback("Data saved.\n" + message, Info, 600f);
                    break;
                case AnalysisState.Succeeded:
                    ShowFeedback("Data saved.\n" + message, Good, 20f);
                    break;
                case AnalysisState.Failed:
                case AnalysisState.Unavailable:
                    ShowFeedback("Data saved.\n" + message, Bad, 25f);
                    break;
            }
        }

        // ---- buttons ------------------------------------------------------------------------
        void OnStartClicked()
        {
            if (manager != null) manager.StartOrRestart();
        }

        void OnConditionClicked()
        {
            if (manager != null) manager.ToggleCondition();
        }

        void OnResetClicked()
        {
            if (manager != null) manager.ResetToReady();
        }

        // ---- rendering ----------------------------------------------------------------------
        void BindCamera()
        {
            if (worldCanvas != null && HeadPose.Camera != null)
                worldCanvas.worldCamera = HeadPose.Camera;
        }

        void ShowFeedback(string message, Color color, float seconds)
        {
            if (feedbackText == null) return;
            feedbackText.text = message;
            feedbackColor = color;
            feedbackText.color = color;
            feedbackTimer = seconds;
        }

        /// <summary>Texts that only change on state / condition changes.</summary>
        void RefreshStatic()
        {
            if (manager == null) return;

            ExperimentState state = manager.State;
            string condition = manager.Condition.Label();

            if (stateText != null) stateText.text = state.Label().ToUpperInvariant();
            if (conditionText != null) conditionText.text = "CONDITION:  " + condition.ToUpperInvariant();
            if (startButtonLabel != null) startButtonLabel.text = state == ExperimentState.Ready ? "START" : "RESTART";
            if (conditionButtonLabel != null) conditionButtonLabel.text = "SWITCH MODE";
            if (instructionText != null) instructionText.text = InstructionFor(state);
        }

        /// <summary>Timer / progress, refreshed 10x per second.</summary>
        void RefreshDynamic()
        {
            if (manager == null) return;

            int total = Mathf.Max(1, manager.TargetCount);
            int found = manager.FoundCount;
            float elapsed = manager.SessionElapsed;
            string time = FormatTime(elapsed);

            if (progressText != null)
            {
                string line = "Sources found: " + found + " / " + total;
                if (manager.State == ExperimentState.Exploring)
                    line += "\nSearching for source " + (manager.CurrentIndex + 1) + " of " + total;
                progressText.text = line;
            }

            if (timerText != null) timerText.text = "Time  " + time;
            if (errorsText != null) errorsText.text = "Wrong selections: " + manager.WrongCount;

            if (progressFill != null)
            {
                RectTransform fill = progressFill;
                fill.anchorMin = new Vector2(0f, 0f);
                fill.anchorMax = new Vector2(Mathf.Clamp01((float)found / total), 1f);
                fill.offsetMin = Vector2.zero;
                fill.offsetMax = Vector2.zero;
            }

            if (hudText != null)
            {
                string label = manager.State == ExperimentState.Ready
                    ? "Ready - press Enter or use the console"
                    : manager.Condition.Label() + "  |  " + found + "/" + total + "  |  " + time;
                hudText.text = label;
            }
        }

        string InstructionFor(ExperimentState state)
        {
            switch (state)
            {
                case ExperimentState.Ready:
                    return "Three hidden sound sources must be found one after another.\n" +
                           "Press START. Listen, walk towards the sound and select the source with the controller ray " +
                           "(grip / left mouse click in the simulator).";
                case ExperimentState.Exploring:
                    return "Listen carefully. Only the active source is audible - it gets faster and higher-pitched as you approach.\n" +
                           "Point the ray at the source and select it.";
                case ExperimentState.TargetFound:
                    return "Correct! Source found.";
                case ExperimentState.Transition:
                    return "Switching sound source...";
                case ExperimentState.Completed:
                    return "Experiment complete. Press RESTART to run it again, or switch the condition first.";
                default:
                    return "";
            }
        }

        static string FormatTime(float seconds)
        {
            int minutes = (int)(seconds / 60f);
            float rest = seconds - minutes * 60f;
            return minutes.ToString("00", Inv) + ":" + rest.ToString("00.0", Inv);
        }
    }
}
