using UnityEngine;

namespace PianoARGame.AR
{
    /// <summary>
    /// Simple runtime inspector for detection results. Shows whether a keyboard
    /// was found and how many keys were estimated. Works in Editor with
    /// TestWebcamController (OnGUI-based, no Canvas required).
    /// </summary>
    public class DetectionChecker : MonoBehaviour
    {
        public TestWebcamController webcamController;
        public PianoDetector detector;
        public KeyEstimator estimator;

        private DetectionResult manualResult;

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, Screen.height - 120, 320, 110), GUI.skin.box);
            GUILayout.Label("Detection Checker");

            string device = webcamController != null ? webcamController.GetCurrentDeviceName() : "(no webcam controller)";
            GUILayout.Label($"Device: {device}");

            DetectionResult current = webcamController != null ? webcamController.LastDetection : null;
            if (current != null && current.keyCount > 0)
            {
                GUILayout.Label($"Detected: YES — Keys: {current.keyCount} — Conf: {current.confidence:F2}");
            }
            else
            {
                GUILayout.Label("Detected: NO");
            }

            if (GUILayout.Button("Run manual check") && detector != null && webcamController != null && webcamController.LastFrameTexture != null)
            {
                manualResult = detector.Detect(webcamController.LastFrameTexture);
            }

            if (manualResult != null)
            {
                GUILayout.Label($"Manual: Keys {manualResult.keyCount} — Conf {manualResult.confidence:F2}");
            }

            GUILayout.EndArea();
        }

        // Programmatic trigger
        public DetectionResult RunCheck()
        {
            if (detector == null || webcamController == null || webcamController.LastFrameTexture == null)
                return null;
            manualResult = detector.Detect(webcamController.LastFrameTexture);
            return manualResult;
        }
    }
}
