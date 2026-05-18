using System.Collections;
using UnityEngine;
using UnityEngine.XR.Management;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PianoARGame
{
    public sealed partial class ArPianoGame
    {
        private bool hmdStereoPreviewEnabled;

        private void ConfigureAndroidOrientation()
        {
            if (Application.platform != RuntimePlatform.Android)
            {
                return;
            }

            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
            Screen.orientation = ScreenOrientation.LandscapeLeft;
        }

        private void RequestHmdMode()
        {
            if (hmdRequested || hmdModeActive || hmdStereoPreviewEnabled)
            {
                return;
            }

            hmdRequested = true;
            StartCoroutine(EnableHmdModeCoroutine());
        }

        private void ExitHmdMode()
        {
            XRGeneralSettings settings = XRGeneralSettings.Instance;
            XRManagerSettings manager = settings != null ? settings.Manager : null;
            if (manager != null && manager.activeLoader != null)
            {
                manager.StopSubsystems();
            }

            hmdRequested = false;
            hmdModeActive = false;
            hmdStereoPreviewEnabled = false;
        }

        private IEnumerator EnableHmdModeCoroutine()
        {
            XRGeneralSettings settings = XRGeneralSettings.Instance;
            if (settings == null || settings.Manager == null)
            {
                hmdStereoPreviewEnabled = Application.platform == RuntimePlatform.Android;
                if (!hmdStereoPreviewEnabled)
                {
                    lastError = "Modo HMD indisponivel neste dispositivo.";
                }

                hmdRequested = false;
                yield break;
            }

            XRManagerSettings manager = settings.Manager;
            if (manager.activeLoader == null)
            {
                yield return manager.InitializeLoader();
            }

            if (manager.activeLoader != null)
            {
                manager.StartSubsystems();
                hmdModeActive = true;
            }

            hmdStereoPreviewEnabled = hmdModeActive || Application.platform == RuntimePlatform.Android;
            if (!hmdStereoPreviewEnabled)
            {
                lastError = "Modo HMD indisponivel neste dispositivo.";
            }

            hmdRequested = false;
        }

        private static bool IsEscapeOrQuitPressed()
        {
            return IsKeyPressed(KeyCode.Escape) || IsKeyPressed(KeyCode.Q);
        }

        private static bool IsKeyPressed(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return false;
            }

            switch (key)
            {
                case KeyCode.Escape:
                    return keyboard.escapeKey.wasPressedThisFrame;
                case KeyCode.Q:
                    return keyboard.qKey.wasPressedThisFrame;
                case KeyCode.R:
                    return keyboard.rKey.wasPressedThisFrame;
                case KeyCode.K:
                    return keyboard.kKey.wasPressedThisFrame;
                case KeyCode.J:
                    return keyboard.jKey.wasPressedThisFrame;
                case KeyCode.UpArrow:
                    return keyboard.upArrowKey.wasPressedThisFrame;
                case KeyCode.DownArrow:
                    return keyboard.downArrowKey.wasPressedThisFrame;
                case KeyCode.PageUp:
                    return keyboard.pageUpKey.wasPressedThisFrame;
                case KeyCode.PageDown:
                    return keyboard.pageDownKey.wasPressedThisFrame;
                default:
                    return false;
            }
#else
            return Input.GetKeyDown(key);
#endif
        }
    }
}
