using System;
using System.Linq;
using UnityEngine;

namespace PianoARGame
{
    public sealed partial class ArPianoGame
    {
        private void ConfigureFramePacing()
        {
            QualitySettings.vSyncCount = 0;

            int displayRefresh = Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value);
            if (displayRefresh <= 0)
            {
                displayRefresh = 60;
            }

            int preferredFrameRate = Mathf.Clamp(requestedFps > 0 ? requestedFps : displayRefresh, 30, displayRefresh);
            Application.targetFrameRate = preferredFrameRate;
        }

        private void UpdateMeasuredCameraFps()
        {
            if (webcam == null || !webcam.isPlaying || !webcam.didUpdateThisFrame)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (cameraFpsSampleStartTime <= 0f)
            {
                cameraFpsSampleStartTime = now;
                cameraFpsSampleFrameCount = 0;
            }

            cameraFpsSampleFrameCount++;
            float elapsed = now - cameraFpsSampleStartTime;
            if (elapsed < 0.5f)
            {
                return;
            }

            measuredCameraFps = cameraFpsSampleFrameCount / elapsed;
            cameraFpsSampleStartTime = now;
            cameraFpsSampleFrameCount = 0;
        }

        private void StartCamera()
        {
            try
            {
                StopCamera();

                if (cameraDevices == null || cameraDevices.Length == 0)
                {
                    RefreshCameraSelectionState();
                }

                var devices = cameraDevices;
                if (devices == null || devices.Length == 0)
                {
                    lastError = "Nenhuma camera encontrada.";
                    return;
                }

                selectedCameraIndex = Mathf.Clamp(selectedCameraIndex, 0, devices.Length - 1);
                string targetName = devices[selectedCameraIndex].name;

                if (selectedCameraModeIndex >= 0 && selectedCameraModeIndex < cameraModeOptions.Count)
                {
                    CameraModeOption option = cameraModeOptions[selectedCameraModeIndex];
                    requestedWidth = option.width;
                    requestedHeight = option.height;
                    requestedFps = option.fps;
                }

#if UNITY_ANDROID && !UNITY_EDITOR
                // Keep camera acquisition aligned with model input and request 60 FPS.
                requestedWidth = 640;
                requestedHeight = 640;
                requestedFps = 60;
#endif

                cameraDeviceName = targetName;
                webcam = new WebCamTexture(targetName, requestedWidth, requestedHeight, requestedFps);
                webcam.requestedFPS = requestedFps;
                webcam.requestedWidth = requestedWidth;
                webcam.requestedHeight = requestedHeight;
                webcam.Play();
                cameraDiagnosticsLogged = false;
                lastError = string.Empty;
                measuredCameraFps = 0f;
                cameraFpsSampleStartTime = 0f;
                cameraFpsSampleFrameCount = 0;
                Debug.Log($"[ArPianoGame] Webcam requested: {targetName} {requestedWidth}x{requestedHeight} @{requestedFps}");

                if (cameraStartupCheckRoutine != null)
                {
                    StopCoroutine(cameraStartupCheckRoutine);
                }

                cameraStartupCheckRoutine = StartCoroutine(CheckCameraStartup());
            }
            catch (Exception ex)
            {
                lastError = "Falha ao iniciar camera neste dispositivo. Tente outro modo.";
                Debug.LogError($"[ArPianoGame] StartCamera failed: {ex}");
            }
        }

        private void StopCamera()
        {
            StopDetectionWorker();

            if (cameraStartupCheckRoutine != null)
            {
                StopCoroutine(cameraStartupCheckRoutine);
                cameraStartupCheckRoutine = null;
            }

            if (webcam != null)
            {
                if (webcam.isPlaying)
                {
                    webcam.Stop();
                }

                webcam = null;
            }

            correctedFramePixels = null;
            sourceFramePixels = null;
            resizedPixelsBuffer = null;
            inputTensorBuffer = null;
            detectionInputBuffer = null;
            detectionInputVersion = 0;
            detectionPreprocessedInputBuffer = null;
            detectionPreprocessedVersion = 0;
            detectionPreprocessedAppliedVersion = 0;
            detectionOutputVersion = 0;
            detectionOutputAppliedVersion = 0;
            latestCorrectedFrameWidth = 0;
            latestCorrectedFrameHeight = 0;

            if (frameTexture != null)
            {
                Destroy(frameTexture);
                frameTexture = null;
            }
        }

        private void RestartCamera()
        {
            StopCamera();
            StartCamera();
        }

        private void CaptureDefaultCameraSelectionIfNeeded()
        {
            if (cameraDefaultsCaptured)
            {
                return;
            }

            defaultCameraDeviceName = cameraDeviceName;
            defaultRequestedWidth = requestedWidth;
            defaultRequestedHeight = requestedHeight;
            defaultRequestedFps = requestedFps;
            cameraDefaultsCaptured = true;
        }

        private void RestoreDefaultCameraSelection()
        {
            if (!cameraDefaultsCaptured)
            {
                RefreshCameraSelectionState();
                return;
            }

            cameraDeviceName = defaultCameraDeviceName;
            requestedWidth = defaultRequestedWidth;
            requestedHeight = defaultRequestedHeight;
            requestedFps = defaultRequestedFps;
            RefreshCameraSelectionState();
        }

        private System.Collections.IEnumerator CheckCameraStartup()
        {
            const float timeoutSeconds = 3f;
            float start = Time.realtimeSinceStartup;
            while (webcam != null && webcam.isPlaying && Time.realtimeSinceStartup - start < timeoutSeconds)
            {
                if (webcam.width > 16 && webcam.height > 16)
                {
                    yield break;
                }

                yield return null;
            }

            if (webcam == null || !webcam.isPlaying || webcam.width <= 16 || webcam.height <= 16)
            {
                lastError = "Camera indisponivel. Troque o dispositivo/modo e toque em Aplicar camera.";
            }
        }

        private void RefreshCameraSelectionState()
        {
            cameraDevices = WebCamTexture.devices ?? Array.Empty<WebCamDevice>();
            if (cameraDevices.Length == 0)
            {
                selectedCameraIndex = -1;
                cameraModeOptions.Clear();
                selectedCameraModeIndex = -1;
                return;
            }

            selectedCameraIndex = ResolveDefaultCameraIndex(cameraDevices);
            cameraDeviceName = cameraDevices[selectedCameraIndex].name;
            BuildCameraModeOptions();
        }

        private int ResolveDefaultCameraIndex(WebCamDevice[] devices)
        {
            if (!string.IsNullOrWhiteSpace(cameraDeviceName))
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    if (string.Equals(devices[i].name, cameraDeviceName, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }
            }

            if (preferBackCamera)
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    if (!devices[i].isFrontFacing)
                    {
                        return i;
                    }
                }
            }

            return 0;
        }

        private void BuildCameraModeOptions()
        {
            cameraModeOptions.Clear();
            if (selectedCameraIndex < 0 || selectedCameraIndex >= cameraDevices.Length)
            {
                selectedCameraModeIndex = -1;
                return;
            }

            AddCameraModeOption(1920, 1080, 60);
            AddCameraModeOption(1920, 1080, 30);
            AddCameraModeOption(1280, 720, 60);
            AddCameraModeOption(1280, 720, 30);
            AddCameraModeOption(640, 640, 60);
            AddCameraModeOption(640, 640, 30);
            AddCameraModeOption(640, 480, 60);
            AddCameraModeOption(640, 480, 30);
            AddCameraModeOption(requestedWidth, requestedHeight, requestedFps);

            selectedCameraModeIndex = Application.platform == RuntimePlatform.Android
                ? FindModeIndex(640, 640, 60)
                : FindModeIndex(1920, 1080, 60);
            if (selectedCameraModeIndex < 0)
            {
                selectedCameraModeIndex = FindModeIndex(requestedWidth, requestedHeight, requestedFps);
            }

            if (selectedCameraModeIndex < 0 && cameraModeOptions.Count > 0)
            {
                selectedCameraModeIndex = 0;
            }
        }

        private void AddCameraModeOption(int width, int height, int fps)
        {
            if (width <= 0 || height <= 0 || fps <= 0)
            {
                return;
            }

            bool exists = cameraModeOptions.Any(m => m.width == width && m.height == height && m.fps == fps);
            if (!exists)
            {
                cameraModeOptions.Add(new CameraModeOption(width, height, fps));
            }
        }

        private int FindModeIndex(int width, int height, int fps)
        {
            for (int i = 0; i < cameraModeOptions.Count; i++)
            {
                CameraModeOption mode = cameraModeOptions[i];
                if (mode.width == width && mode.height == height && mode.fps == fps)
                {
                    return i;
                }
            }

            return -1;
        }

        private void SelectCameraRelative(int delta)
        {
            if (cameraDevices == null || cameraDevices.Length == 0)
            {
                RefreshCameraSelectionState();
                return;
            }

            if (selectedCameraIndex < 0)
            {
                selectedCameraIndex = 0;
            }
            else
            {
                selectedCameraIndex = (selectedCameraIndex + delta + cameraDevices.Length) % cameraDevices.Length;
            }

            cameraDeviceName = cameraDevices[selectedCameraIndex].name;
            BuildCameraModeOptions();
        }

        private void SelectModeRelative(int delta)
        {
            if (cameraModeOptions.Count == 0)
            {
                BuildCameraModeOptions();
                return;
            }

            if (selectedCameraModeIndex < 0)
            {
                selectedCameraModeIndex = 0;
            }
            else
            {
                selectedCameraModeIndex = (selectedCameraModeIndex + delta + cameraModeOptions.Count) % cameraModeOptions.Count;
            }
        }

        private void ApplySelectedCameraAndMode()
        {
            if (cameraDevices == null || cameraDevices.Length == 0)
            {
                RefreshCameraSelectionState();
                lastError = "Nenhuma camera encontrada.";
                return;
            }

            if (selectedCameraIndex < 0 || selectedCameraIndex >= cameraDevices.Length)
            {
                selectedCameraIndex = 0;
            }

            if (selectedCameraModeIndex < 0 || selectedCameraModeIndex >= cameraModeOptions.Count)
            {
                BuildCameraModeOptions();
                if (selectedCameraModeIndex < 0)
                {
                    lastError = "Nenhum modo de camera disponivel.";
                    return;
                }
            }

            RestartCamera();
        }

        private string GetSelectedCameraLabel()
        {
            if (cameraDevices == null || cameraDevices.Length == 0)
            {
                return "Nenhuma camera detectada";
            }

            int index = Mathf.Clamp(selectedCameraIndex, 0, cameraDevices.Length - 1);
            WebCamDevice device = cameraDevices[index];
            string facing = device.isFrontFacing ? "frontal" : "principal";
            return $"{index + 1}/{cameraDevices.Length}: {device.name} ({facing})";
        }

        private string GetSelectedModeLabel()
        {
            if (cameraModeOptions.Count == 0)
            {
                return "Sem modos";
            }

            int index = Mathf.Clamp(selectedCameraModeIndex, 0, cameraModeOptions.Count - 1);
            return cameraModeOptions[index].Label;
        }

        private string GetCameraStatusLabel()
        {
            string requested = $"Solicitado: {requestedWidth}x{requestedHeight} @ {requestedFps} FPS";
            if (webcam == null)
            {
                return requested;
            }

            return $"{requested} | Ativo: {Mathf.Max(0, webcam.width)}x{Mathf.Max(0, webcam.height)}";
        }
    }
}
