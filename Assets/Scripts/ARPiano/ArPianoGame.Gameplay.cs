using System;
using System.IO;
using UnityEngine;

namespace PianoARGame
{
    public sealed partial class ArPianoGame
    {
        private void StartSelectedSong()
        {
            if (midiFiles.Count == 0)
            {
                return;
            }

            if (webcam == null || !webcam.isPlaying)
            {
                RefreshCameraSelectionState();
                StartCamera();
            }

            string path = midiFiles[Mathf.Clamp(selectedIndex, 0, midiFiles.Count - 1)];
            try
            {
                LoadMidiEvents(path, events, out songDuration);
                songName = Path.GetFileName(path);
                if (events.Count == 0)
                {
                    songDuration = 1f;
                }

                state = GameState.Align;
                ResetTrackingState();
                lastError = string.Empty;
            }
            catch (Exception ex)
            {
                lastError = "Falha ao ler MIDI: " + ex.Message;
            }
        }

        private void StartGameplay()
        {
            state = GameState.Game;
            gameStartTime = Time.realtimeSinceStartup;
        }

        private void ResetToMenu()
        {
            ExitHmdMode();
            StopDetectionWorker();
            RestoreDefaultCameraSelection();
            RestartCamera();
            ResetTrackingState();
            state = GameState.MainMenu;
            showSettingsOverlay = false;
            menuTouchScrollActive = false;
            menuTouchScrollFingerId = -1;
        }

        private void ResetTrackingState()
        {
            stableHits = 0;
            bestConf = 0f;
            keyboardArea = null;
            frameCount = 0;
            adaptiveDetectInterval = -1;
            lastConfiguredDetectInterval = -1;
            cameraDiagnosticsLogged = false;
            dumpedInferenceArtifacts = 0;
            detectionOutputAppliedVersion = detectionOutputVersion;
        }

        private void UpdateRenderFps()
        {
            float now = Time.realtimeSinceStartup;
            float dt = Mathf.Max(1e-6f, now - lastFrameTime);
            lastFrameTime = now;
            renderFps = 1f / dt;
            if (renderFpsEma <= 0f)
            {
                renderFpsEma = renderFps;
            }
            else
            {
                renderFpsEma = 0.9f * renderFpsEma + 0.1f * renderFps;
            }
        }

        private static Rect SmoothRect(Rect? previous, Detection detection, float alpha)
        {
            if (!previous.HasValue)
            {
                return Rect.MinMaxRect(detection.x1, detection.y1, detection.x2, detection.y2);
            }

            Rect p = previous.Value;
            float nx1 = Mathf.Lerp(p.xMin, detection.x1, alpha);
            float ny1 = Mathf.Lerp(p.yMin, detection.y1, alpha);
            float nx2 = Mathf.Lerp(p.xMax, detection.x2, alpha);
            float ny2 = Mathf.Lerp(p.yMax, detection.y2, alpha);
            return Rect.MinMaxRect(nx1, ny1, nx2, ny2);
        }

        private static float PitchToX(int pitch, Rect area)
        {
            float norm = Mathf.Clamp01((pitch - 21) / 87f);
            return area.xMin + norm * Mathf.Max(1f, area.width);
        }

        private static bool IsBlackKey(int pitch)
        {
            int mod = Mathf.Abs(pitch % 12);
            return mod == 1 || mod == 3 || mod == 6 || mod == 8 || mod == 10;
        }
    }
}
