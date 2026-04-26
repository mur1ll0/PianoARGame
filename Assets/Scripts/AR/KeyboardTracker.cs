using UnityEngine;

namespace PianoARGame.AR
{
    public enum KeyboardTrackingState
    {
        Lost,
        Degraded,
        Tracked
    }

    /// <summary>
    /// Lightweight tracking state machine for keyboard detection stability.
    /// Phase-1 scope: confidence-based health states and short-horizon prediction gates.
    /// </summary>
    public class KeyboardTracker : MonoBehaviour
    {
        [Header("Thresholds")]
        [Range(0f, 1f)] public float trackedThreshold = 0.65f;
        [Range(0f, 1f)] public float degradedThreshold = 0.4f;
        [Min(1)] public int lostAfterFrames = 12;

        public KeyboardTrackingState State { get; private set; } = KeyboardTrackingState.Lost;
        public DetectionResult LastStableDetection { get; private set; }
        public int ConsecutiveLowConfidenceFrames { get; private set; }

        public KeyboardTrackingState UpdateTracking(DetectionResult current)
        {
            if (current == null)
            {
                ConsecutiveLowConfidenceFrames++;
                EvaluateLost();
                return State;
            }

            if (current.confidence >= trackedThreshold)
            {
                State = KeyboardTrackingState.Tracked;
                LastStableDetection = current;
                ConsecutiveLowConfidenceFrames = 0;
                return State;
            }

            if (current.confidence >= degradedThreshold)
            {
                State = KeyboardTrackingState.Degraded;
                ConsecutiveLowConfidenceFrames++;
                if (LastStableDetection == null)
                {
                    LastStableDetection = current;
                }

                EvaluateLost();
                return State;
            }

            ConsecutiveLowConfidenceFrames++;
            EvaluateLost();
            return State;
        }

        public DetectionResult GetBestDetection(DetectionResult current)
        {
            if (State == KeyboardTrackingState.Tracked)
            {
                return current;
            }

            if (State == KeyboardTrackingState.Degraded && LastStableDetection != null)
            {
                return LastStableDetection;
            }

            return current;
        }

        private void EvaluateLost()
        {
            if (ConsecutiveLowConfidenceFrames >= lostAfterFrames)
            {
                State = KeyboardTrackingState.Lost;
            }
            else if (State != KeyboardTrackingState.Tracked)
            {
                State = KeyboardTrackingState.Degraded;
            }
        }
    }
}
