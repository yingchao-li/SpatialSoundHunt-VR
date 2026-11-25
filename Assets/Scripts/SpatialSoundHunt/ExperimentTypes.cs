namespace SpatialSoundHunt
{
    /// <summary>Finite-state-machine states of the experiment.</summary>
    public enum ExperimentState
    {
        Ready = 0,
        Exploring = 1,
        TargetFound = 2,
        Transition = 3,
        Completed = 4
    }

    /// <summary>The two experimental conditions.</summary>
    public enum ExperimentCondition
    {
        /// <summary>Only the spatial audio beacon guides the participant.</summary>
        AudioOnly = 0,

        /// <summary>Spatial audio plus a visual direction arrow and highlighted target.</summary>
        AudioVisual = 1
    }

    /// <summary>Human readable labels used by the UI.</summary>
    public static class ExperimentLabels
    {
        public static string Label(this ExperimentCondition condition)
        {
            return condition == ExperimentCondition.AudioOnly ? "Audio Only" : "Audio + Visual";
        }

        public static string Label(this ExperimentState state)
        {
            switch (state)
            {
                case ExperimentState.Ready: return "Ready";
                case ExperimentState.Exploring: return "Exploring";
                case ExperimentState.TargetFound: return "Target Found";
                case ExperimentState.Transition: return "Transition";
                case ExperimentState.Completed: return "Completed";
                default: return state.ToString();
            }
        }
    }
}
