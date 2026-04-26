using UnityEngine;
using PianoARGame.Services;

namespace PianoARGame.AR
{
    /// <summary>
    /// Gerencia UI básica e eventos do menu.
    /// </summary>
    public class UIManager : MonoBehaviour
    {
        [Header("References")]
        public CalibrationManager calibrationManager;
        public ARSessionManager arSessionManager;
        public TestWebcamController webcamController;
        public ConfigService configService;

        public string LastUiMessage { get; private set; }

        public void OnDetectPianoClicked()
        {
            if (calibrationManager == null)
            {
                LastUiMessage = "CalibrationManager is not assigned.";
                Debug.LogWarning(LastUiMessage);
                return;
            }

            var success = calibrationManager.RunSingleStepCalibration();
            LastUiMessage = success
                ? "Calibration completed."
                : "Calibration failed. Keep keyboard visible and retry.";

            Debug.Log(LastUiMessage);
        }

        public void OnOpenMusicFolderClicked()
        {
            if (configService == null)
            {
                LastUiMessage = "ConfigService is not assigned.";
                Debug.LogWarning(LastUiMessage);
                return;
            }

            var currentPath = configService.GetMusicFolderPath();
            LastUiMessage = $"Music folder path: {currentPath}";
            Debug.Log(LastUiMessage);
        }

        public void OnStartSessionClicked()
        {
            if (arSessionManager == null)
            {
                LastUiMessage = "ARSessionManager is not assigned.";
                Debug.LogWarning(LastUiMessage);
                return;
            }

            arSessionManager.StartSession();
            LastUiMessage = "AR session started.";
            Debug.Log(LastUiMessage);
        }

        public void OnStopSessionClicked()
        {
            if (arSessionManager == null)
            {
                LastUiMessage = "ARSessionManager is not assigned.";
                Debug.LogWarning(LastUiMessage);
                return;
            }

            arSessionManager.StopSession();
            LastUiMessage = "AR session stopped.";
            Debug.Log(LastUiMessage);
        }

        public void OnToggleFullscreenClicked()
        {
            if (webcamController == null)
            {
                LastUiMessage = "TestWebcamController is not assigned.";
                Debug.LogWarning(LastUiMessage);
                return;
            }

            webcamController.ToggleFullscreen();
            LastUiMessage = webcamController.DrawFullscreen ? "Fullscreen enabled." : "Fullscreen disabled.";
            Debug.Log(LastUiMessage);
        }
    }
}
