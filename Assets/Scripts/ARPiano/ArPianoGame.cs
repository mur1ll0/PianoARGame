using System;
using System.Collections;
using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;

namespace PianoARGame
{
    public sealed partial class ArPianoGame : MonoBehaviour
    {
        private enum GameState
        {
            MainMenu,
            SongSelect,
            Align,
            Game,
            End
        }

        private enum SettingsSection
        {
            Midi,
            Camera,
            Detection,
            Gameplay,
            Diagnostics
        }

        private readonly struct Detection
        {
            public readonly float x1;
            public readonly float y1;
            public readonly float x2;
            public readonly float y2;
            public readonly float score;

            public Detection(float x1, float y1, float x2, float y2, float score)
            {
                this.x1 = x1;
                this.y1 = y1;
                this.x2 = x2;
                this.y2 = y2;
                this.score = score;
            }
        }

        private readonly struct CameraModeOption
        {
            public readonly int width;
            public readonly int height;
            public readonly int fps;

            public CameraModeOption(int width, int height, int fps)
            {
                this.width = Mathf.Max(1, width);
                this.height = Mathf.Max(1, height);
                this.fps = Mathf.Max(1, fps);
            }

            public string Label => $"{width}x{height} @ {fps} FPS";
        }

        private struct MidiNoteEvent
        {
            public int pitch;
            public float start;
            public float end;
            public int velocity;
            public char hand;
        }

        [Header("Model")]
        [SerializeField] private ModelAsset onnxModel;
        [SerializeField] private string resourcesModelPath = "AIModels/piano_SGD";
        [SerializeField] private BackendType backendType = BackendType.CPU;
        [SerializeField, Min(32)] private int fallbackInputSize = 640;
        [SerializeField, Min(1)] private int numClasses = 1;
        [SerializeField, Range(0.05f, 0.95f)] private float confThreshold = 0.3f;
        [SerializeField, Range(0.05f, 0.95f)] private float iouThreshold = 0.45f;

        [Header("Camera")]
        [SerializeField] private string cameraDeviceName = "";
        [SerializeField] private int requestedWidth = 1920;
        [SerializeField] private int requestedHeight = 1080;
        [SerializeField] private int requestedFps = 60;
        [SerializeField] private bool preferBackCamera = true;

        [Header("MIDI")]
        [SerializeField] private string midiDirectoryDesktop = @"C:\Users\Murillo\Music\MIDI";
        [SerializeField] private string midiStreamingAssetsSubFolder = "MIDI";
        [SerializeField] private string midiDirectoryAndroidDownloads = "/storage/emulated/0/Download";

        [Header("Gameplay")]
        [SerializeField, Min(1)] private int detectInterval = 5;
        [SerializeField, Min(1)] private int stableHitsRequired = 12;
        [SerializeField] private float countdownSeconds = 3f;
        [SerializeField] private float travelTime = 2f;
        [SerializeField, Range(0.5f, 2f)] private float songSpeed = 1f;
        [SerializeField] private bool autoStartFirst = false;

        [Header("Android XR")]
        [SerializeField] private bool enableHmdModeOnGameStart = true;

        [Header("Diagnostics")]
        [SerializeField] private bool dumpInferenceArtifacts = true;
        [SerializeField, Min(1)] private int dumpInferenceArtifactLimit = 3;
        [SerializeField] private string dumpInferenceFolderName = "DebugDumps/Unity";

        private WebCamTexture webcam;
        private Texture2D frameTexture;
        private Texture2D whitePixel;
        private Color32[] sourceFramePixels;
        private Color32[] correctedFramePixels;
        private Color32[] resizedPixelsBuffer;
        private float[] inputTensorBuffer;

        private Model runtimeModel;
        private Worker worker;
        private string[] outputNames = Array.Empty<string>();
        private int modelInputW;
        private int modelInputH;

        private readonly List<string> midiFiles = new List<string>();
        private int selectedIndex;
        private int menuScroll;
        private string midiDirectoryInput = string.Empty;
        private string activeMidiRoot = string.Empty;

        private readonly List<MidiNoteEvent> events = new List<MidiNoteEvent>();
        private float songDuration;
        private string songName = string.Empty;

        private GameState state = GameState.MainMenu;
        private Rect? keyboardArea;
        private int stableHits;
        private float bestConf;
        private int frameCount;

        private float gameStartTime;
        private float lastFrameTime;
        private float renderFps;
        private float renderFpsEma;

        private Vector2 menuScrollPos;
        private Vector2 settingsScrollPos;
        private string lastError = string.Empty;
        private string midiImportNotification = string.Empty;
        private float midiImportNotificationUntil;
        private bool menuTouchScrollActive;
        private bool menuTouchScrollDragging;
        private int menuTouchScrollFingerId = -1;
        private float menuTouchScrollStartY;
        private float menuTouchScrollStartScrollY;
        private Rect currentSongListRect;
        private float currentSongListMaxScrollY;
        private float currentSongListRowHeight;
        private int latestCorrectedFrameWidth;
        private int latestCorrectedFrameHeight;
        private int adaptiveDetectInterval = -1;
        private int lastConfiguredDetectInterval = -1;
        private float measuredCameraFps;
        private float cameraFpsSampleStartTime;
        private int cameraFpsSampleFrameCount;

        private bool showSettingsOverlay;
        private SettingsSection settingsSection;
        private bool settingsShowAdvancedDetection;
        private float uiScale = 1f;

        private bool hmdModeActive;
        private bool hmdRequested;
        private bool guiInitialized;
        private bool modelInitAttempted;
        private bool cameraDiagnosticsLogged;
        private bool modelDiagnosticsLogged;
        private bool dumpDirectoryLogged;
        private int dumpedInferenceArtifacts;
        private string dumpDirectoryPath = string.Empty;
        private WebCamDevice[] cameraDevices = Array.Empty<WebCamDevice>();
        private readonly List<CameraModeOption> cameraModeOptions = new List<CameraModeOption>();
        private string defaultCameraDeviceName = string.Empty;
        private int defaultRequestedWidth;
        private int defaultRequestedHeight;
        private int defaultRequestedFps;
        private bool cameraDefaultsCaptured;
        private int selectedCameraIndex = -1;
        private int selectedCameraModeIndex = -1;
        private Coroutine cameraStartupCheckRoutine;
        private readonly List<Detection> decodeCandidates = new List<Detection>(512);
        private readonly List<Detection> decodeKept = new List<Detection>(256);

        private GUIStyle headerStyle;
        private GUIStyle titleStyle;
        private GUIStyle textStyle;
        private GUIStyle smallTextStyle;
        private GUIStyle buttonStyle;
        private GUIStyle selectedRowStyle;
        private GUIStyle rowStyle;
        private GUIStyle iconButtonStyle;
        private TouchScreenKeyboard midiPathKeyboard;

        private const float MidiImportNotificationDurationSeconds = 10f;

        private void Awake()
        {
            Application.runInBackground = true;
            ConfigureAndroidOrientation();
            ConfigureFramePacing();
            InitializeMidiRepository();
            StartCoroutine(BootstrapCameraStartup());

            if (autoStartFirst && midiFiles.Count > 0)
            {
                selectedIndex = 0;
                StartSelectedSong();
            }

            lastFrameTime = Time.realtimeSinceStartup;
        }

        private IEnumerator BootstrapCameraStartup()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera);
                float timeout = 8f;
                float start = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - start < timeout)
                {
                    if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
                    {
                        break;
                    }

                    yield return null;
                }

                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
                {
                    lastError = "Permissao de camera negada. Autorize nas configuracoes do Android.";
                    yield break;
                }
            }

            // Recarregar lista MIDI com pasta interna do app (nao depende de permissao de storage compartilhado)
            InitializeMidiRepository();
#endif

            RefreshCameraSelectionState();
            CaptureDefaultCameraSelectionIfNeeded();
            StartCamera();
            yield break;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool HasAndroidMidiReadPermission()
        {
            const string readMediaAudio = "android.permission.READ_MEDIA_AUDIO";
            string readExternal = UnityEngine.Android.Permission.ExternalStorageRead;
            int sdkInt = GetAndroidSdkInt();
            if (sdkInt >= 33)
            {
                return UnityEngine.Android.Permission.HasUserAuthorizedPermission(readMediaAudio)
                    || UnityEngine.Android.Permission.HasUserAuthorizedPermission(readExternal);
            }

            return UnityEngine.Android.Permission.HasUserAuthorizedPermission(readExternal);
        }

        private static IEnumerator RequestAndroidMidiReadPermission()
        {
            const string readMediaAudio = "android.permission.READ_MEDIA_AUDIO";
            string readExternal = UnityEngine.Android.Permission.ExternalStorageRead;
            int sdkInt = GetAndroidSdkInt();
            bool isAndroid13OrNewer = sdkInt >= 33;
            string[] permissionsToRequest = isAndroid13OrNewer
                ? new[] { readMediaAudio, readExternal }
                : new[] { readExternal };

            bool hasPermission = HasAndroidMidiReadPermission();

            if (hasPermission)
            {
                yield break;
            }

            bool shouldShowRationale = false;
            for (int i = 0; i < permissionsToRequest.Length; i++)
            {
                if (UnityEngine.Android.Permission.ShouldShowRequestPermissionRationale(permissionsToRequest[i]))
                {
                    shouldShowRationale = true;
                    break;
                }
            }

            if (shouldShowRationale)
            {
                Debug.Log("[AR Piano] Requesting storage permission again after rationale.");
            }

            bool completed = false;
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => { completed = true; };
            callbacks.PermissionDenied += _ => { completed = true; };
            callbacks.PermissionDeniedAndDontAskAgain += _ => { completed = true; };

            if (permissionsToRequest.Length == 1)
            {
                UnityEngine.Android.Permission.RequestUserPermission(permissionsToRequest[0], callbacks);
            }
            else
            {
                UnityEngine.Android.Permission.RequestUserPermissions(permissionsToRequest, callbacks);
            }

            float timeout = 8f;
            float start = Time.realtimeSinceStartup;
            while (!completed && Time.realtimeSinceStartup - start < timeout)
            {
                yield return null;
            }

            if (!HasAndroidMidiReadPermission())
            {
                Debug.LogWarning($"[AR Piano] MIDI read permission not granted. sdkInt={sdkInt}, android13OrNewer={isAndroid13OrNewer}");
            }
        }

        private static int GetAndroidSdkInt()
        {
            try
            {
                using var version = new AndroidJavaClass("android.os.Build$VERSION");
                return version.GetStatic<int>("SDK_INT");
            }
            catch (Exception)
            {
                // Assume modern Android when SDK query fails.
                return 33;
            }
        }
#endif

        private void OnDestroy()
        {
            if (webcam != null)
            {
                if (webcam.isPlaying)
                {
                    webcam.Stop();
                }

                webcam = null;
            }

            if (worker != null)
            {
                worker.Dispose();
                worker = null;
            }

            if (frameTexture != null)
            {
                Destroy(frameTexture);
                frameTexture = null;
            }

            if (whitePixel != null)
            {
                Destroy(whitePixel);
                whitePixel = null;
            }
        }

        private void Update()
        {
            UpdateRenderFps();
            UpdateMeasuredCameraFps();

            if (IsEscapeOrQuitPressed())
            {
                Application.Quit();
            }

            if (IsKeyPressed(KeyCode.R))
            {
                ResetToMenu();
            }

            if (state == GameState.SongSelect)
            {
                HandleMenuKeyboardFallback();
            }

            if (state == GameState.Align || state == GameState.Game)
            {
                UpdateTracker();
            }
        }
    }
}
