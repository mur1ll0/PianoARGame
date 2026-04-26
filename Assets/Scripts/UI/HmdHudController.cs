using UnityEngine;
using UnityEngine.UI;
using PianoARGame.AR;

namespace PianoARGame.UI
{
    /// <summary>
    /// Runtime HUD status panel for HMD-first interaction.
    /// </summary>
    public class HmdHudController : MonoBehaviour
    {
        public UIManager uiManager;
        public CalibrationManager calibrationManager;
        public TestWebcamController webcamController;
        public Text statusText;

        private float nextUpdateTime;

        void Update()
        {
            if (Time.unscaledTime < nextUpdateTime)
            {
                return;
            }

            nextUpdateTime = Time.unscaledTime + 0.2f;
            RefreshStatus();
        }

        public void RefreshStatus()
        {
            if (statusText == null)
            {
                return;
            }

            string ui = uiManager != null ? uiManager.LastUiMessage : "UI message unavailable";
            string calib = "Calibration: n/a";
            if (calibrationManager != null && calibrationManager.LastProfile != null)
            {
                calib = calibrationManager.LastProfile.isValid
                    ? $"Calibration: OK ({calibrationManager.LastProfile.qualityScore:0.00})"
                    : "Calibration: invalid";
            }

            string detector = "Detector: n/a";
            if (webcamController != null && webcamController.LastDetection != null)
            {
                var d = webcamController.LastDetection;
                detector =
                    $"Detector conf {d.confidence:0.00} | keys {d.keyCount} | {d.processingTimeMs:0.0}ms";
            }

            statusText.text = ui + "\n" + calib + "\n" + detector;
        }
    }
}
