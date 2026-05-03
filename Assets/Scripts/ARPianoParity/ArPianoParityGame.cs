using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.XR.Management;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PianoARGame.Parity
{
    public sealed class ArPianoParityGame : MonoBehaviour
    {
        private enum GameState
        {
            Menu,
            Align,
            Game,
            End
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
        private Color32[] correctedFramePixels;

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

        private GameState state = GameState.Menu;
        private Rect? keyboardArea;
        private int stableHits;
        private float bestConf;
        private int frameCount;

        private float gameStartTime;
        private float lastFrameTime;
        private float renderFps;
        private float renderFpsEma;

        private Vector2 menuScrollPos;
        private string lastError = string.Empty;

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
        private int selectedCameraIndex = -1;
        private int selectedCameraModeIndex = -1;
        private Coroutine cameraStartupCheckRoutine;

        private GUIStyle headerStyle;
        private GUIStyle textStyle;
        private GUIStyle buttonStyle;
        private GUIStyle selectedRowStyle;
        private GUIStyle rowStyle;

        private void Awake()
        {
            Application.runInBackground = true;
            ConfigureAndroidOrientation();
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
#endif

            RefreshCameraSelectionState();
            StartCamera();
            yield break;
        }

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

            if (IsEscapeOrQuitPressed())
            {
                Application.Quit();
            }

            if (IsKeyPressed(KeyCode.R))
            {
                ResetToMenu();
            }

            if (state == GameState.Menu)
            {
                HandleMenuKeyboardFallback();
            }

            if (state == GameState.Align || state == GameState.Game)
            {
                UpdateTracker();
            }
        }

        private void OnGUI()
        {
            EnsureGuiReady();

            if (frameTexture != null)
            {
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), frameTexture, ScaleMode.StretchToFill, false);
            }
            else if (webcam != null)
            {
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), webcam, ScaleMode.StretchToFill, false);
            }
            else
            {
                GUI.Box(new Rect(0f, 0f, Screen.width, Screen.height), "");
            }

            DrawHeader();

            switch (state)
            {
                case GameState.Menu:
                    DrawMenu();
                    break;
                case GameState.Align:
                    DrawAlignment();
                    break;
                case GameState.Game:
                    DrawGameplay();
                    break;
                case GameState.End:
                    DrawEnd();
                    break;
            }

            if (!string.IsNullOrWhiteSpace(lastError))
            {
                DrawTextBox(new Rect(20f, Screen.height - 60f, Screen.width - 40f, 34f), lastError, new Color(0.4f, 0.05f, 0.05f, 0.65f));
            }
        }

        private void EnsureGuiReady()
        {
            if (guiInitialized)
            {
                return;
            }

            CreateGuiStyles();
            guiInitialized = true;
        }

        private void CreateGuiStyles()
        {
            whitePixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            whitePixel.SetPixel(0, 0, Color.white);
            whitePixel.Apply();

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            textStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                normal = { textColor = Color.white }
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold
            };

            selectedRowStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 14,
                normal = { textColor = new Color(0.95f, 0.98f, 0.95f, 1f) }
            };

            rowStyle = new GUIStyle(selectedRowStyle)
            {
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f, 1f) }
            };
        }

        private void DrawHeader()
        {
            DrawRect(new Rect(0f, 0f, Screen.width, 84f), new Color(0.08f, 0.08f, 0.08f, 0.6f));
            GUI.Label(new Rect(18f, 8f, 420f, 32f), "AR Piano Trainer", headerStyle);

            string camInfo = webcam == null
                ? "Camera: unavailable"
                : $"Camera: {webcam.width}x{webcam.height} @ {requestedFps} req";
            GUI.Label(new Rect(18f, 46f, 900f, 24f), $"Render FPS: {renderFps,5:0.0} | {camInfo}", textStyle);

            GUI.Label(new Rect(Screen.width - 260f, 46f, 240f, 24f), "Q/ESC sair | R menu", textStyle);
        }

        private void DrawMenu()
        {
            Rect panel = new Rect(40f, 110f, Screen.width - 80f, Screen.height - 180f);
            DrawRect(panel, new Color(0.06f, 0.06f, 0.06f, 0.58f));

            GUI.Label(new Rect(72f, 130f, 600f, 26f), "1) Escolha uma musica MIDI", textStyle);
            GUI.Label(new Rect(72f, 158f, 600f, 24f), "2) Clique em Jogar", textStyle);

            GUI.Label(new Rect(72f, 184f, 420f, 24f), "Repositorio MIDI:", textStyle);
            midiDirectoryInput = GUI.TextField(new Rect(70f, 208f, Screen.width - 540f, 30f), midiDirectoryInput ?? string.Empty);
            if (GUI.Button(new Rect(Screen.width - 452f, 208f, 120f, 30f), "Aplicar", buttonStyle))
            {
                ApplyMidiRepository(midiDirectoryInput);
            }

            if (Application.platform == RuntimePlatform.Android)
            {
                if (GUI.Button(new Rect(Screen.width - 324f, 208f, 120f, 30f), "Downloads", buttonStyle))
                {
                    ApplyMidiRepository(GetAndroidDownloadsDirectory());
                }

                if (GUI.Button(new Rect(Screen.width - 196f, 208f, 120f, 30f), "Streaming", buttonStyle))
                {
                    ApplyMidiRepository(Path.Combine(Application.streamingAssetsPath, midiStreamingAssetsSubFolder));
                }
            }

            DrawTextBox(new Rect(70f, 242f, Screen.width - 140f, 26f), "Pasta ativa: " + ResolveMidiRoot(), new Color(0.08f, 0.08f, 0.08f, 0.55f));

            DrawCameraConfigurationMenu();

            Rect listRect = new Rect(70f, 368f, Screen.width - 140f, Screen.height - 528f);
            Rect viewRect = new Rect(0f, 0f, listRect.width - 20f, Mathf.Max(listRect.height, midiFiles.Count * 34f));

            menuScrollPos = GUI.BeginScrollView(listRect, menuScrollPos, viewRect);
            for (int i = 0; i < midiFiles.Count; i++)
            {
                string fileName = Path.GetFileName(midiFiles[i]);
                bool active = i == selectedIndex;
                Color bg = active ? new Color(0.16f, 0.4f, 0.16f, 0.9f) : new Color(0.13f, 0.13f, 0.13f, 0.9f);
                Rect row = new Rect(0f, i * 34f, viewRect.width - 10f, 30f);
                DrawRect(row, bg);

                if (GUI.Button(row, "  " + fileName, active ? selectedRowStyle : rowStyle))
                {
                    selectedIndex = i;
                }
            }
            GUI.EndScrollView();

            if (midiFiles.Count == 0)
            {
                GUI.Label(new Rect(74f, 280f, Screen.width - 148f, 24f), "Nenhum MIDI encontrado no diretorio configurado.", textStyle);
            }

            if (GUI.Button(new Rect(70f, Screen.height - 110f, 180f, 48f), "Jogar", buttonStyle))
            {
                if (midiFiles.Count > 0)
                {
                    StartSelectedSong();
                }
            }

            if (GUI.Button(new Rect(270f, Screen.height - 110f, 220f, 48f), "Recarregar MIDIs", buttonStyle))
            {
                LoadMidiList();
                selectedIndex = Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, midiFiles.Count - 1));
            }

            HandleMouseWheelMenuScroll();
        }

        private void DrawAlignment()
        {
            DrawGuideBox();

            if (keyboardArea.HasValue)
            {
                Rect displayArea = MapFrameRectToScreen(keyboardArea.Value);
                DrawKeyboardRect(displayArea, new Color(0.1f, 0.9f, 0.25f, 1f));
                DrawTextBox(new Rect(displayArea.x, Mathf.Max(88f, displayArea.y - 34f), 280f, 28f),
                    $"Area detectada conf: {bestConf * 100f:0.0}%", new Color(0.08f, 0.08f, 0.08f, 0.65f));
            }

            float ratio = Mathf.Clamp01(stableHits / (float)Mathf.Max(1, stableHitsRequired));
            Rect bar = new Rect(70f, Screen.height - 120f, Screen.width - 140f, 24f);
            DrawRect(bar, new Color(0.16f, 0.16f, 0.16f, 0.8f));
            DrawRect(new Rect(bar.x, bar.y, bar.width * ratio, bar.height), new Color(0.15f, 0.72f, 0.23f, 0.9f));
            DrawOutline(bar, Color.white, 1f);
            GUI.Label(new Rect(70f, Screen.height - 150f, 320f, 24f), $"Tracking estavel: {stableHits}/{stableHitsRequired}", textStyle);

            GUI.Label(new Rect(70f, Screen.height - 182f, 320f, 24f), $"Velocidade da musica: {songSpeed:0.00}x", textStyle);
            if (GUI.Button(new Rect(360f, Screen.height - 186f, 70f, 48f), "-", buttonStyle))
            {
                songSpeed = Mathf.Clamp(songSpeed - 0.1f, 0.5f, 2f);
            }

            if (GUI.Button(new Rect(440f, Screen.height - 186f, 70f, 48f), "+", buttonStyle))
            {
                songSpeed = Mathf.Clamp(songSpeed + 0.1f, 0.5f, 2f);
            }

            bool canStart = keyboardArea.HasValue && stableHits >= stableHitsRequired;
            GUI.enabled = canStart;
            if (GUI.Button(new Rect(70f, Screen.height - 76f, 210f, 48f), "Iniciar jogo", buttonStyle))
            {
                StartGameplay();
            }

            GUI.enabled = true;
            if (GUI.Button(new Rect(300f, Screen.height - 76f, 210f, 48f), "Trocar musica", buttonStyle))
            {
                ResetToMenu();
            }

            if (Application.platform == RuntimePlatform.Android && !hmdModeActive)
            {
                if (GUI.Button(new Rect(530f, Screen.height - 76f, 210f, 48f), "Entrar modo HMD", buttonStyle))
                {
                    RequestHmdMode();
                }
            }
        }

        private void DrawCameraConfigurationMenu()
        {
            DrawTextBox(new Rect(70f, 274f, Screen.width - 140f, 88f), string.Empty, new Color(0.08f, 0.08f, 0.08f, 0.55f));
            GUI.Label(new Rect(80f, 278f, 220f, 24f), "Camera:", textStyle);

            if (GUI.Button(new Rect(150f, 304f, 34f, 26f), "<", buttonStyle))
            {
                SelectCameraRelative(-1);
            }

            string cameraLabel = GetSelectedCameraLabel();
            GUI.Label(new Rect(190f, 306f, Screen.width - 740f, 24f), cameraLabel, textStyle);

            if (GUI.Button(new Rect(Screen.width - 550f, 304f, 34f, 26f), ">", buttonStyle))
            {
                SelectCameraRelative(1);
            }

            if (GUI.Button(new Rect(Screen.width - 508f, 304f, 120f, 26f), "Atualizar", buttonStyle))
            {
                RefreshCameraSelectionState();
            }

            GUI.Label(new Rect(Screen.width - 378f, 278f, 250f, 24f), "Modo de imagem:", textStyle);
            if (GUI.Button(new Rect(Screen.width - 380f, 304f, 34f, 26f), "<", buttonStyle))
            {
                SelectModeRelative(-1);
            }

            string modeLabel = GetSelectedModeLabel();
            GUI.Label(new Rect(Screen.width - 340f, 306f, 210f, 24f), modeLabel, textStyle);

            if (GUI.Button(new Rect(Screen.width - 126f, 304f, 34f, 26f), ">", buttonStyle))
            {
                SelectModeRelative(1);
            }

            if (GUI.Button(new Rect(Screen.width - 220f, 332f, 150f, 26f), "Aplicar camera", buttonStyle))
            {
                ApplySelectedCameraAndMode();
            }

            GUI.Label(new Rect(80f, 334f, Screen.width - 320f, 24f), GetCameraStatusLabel(), textStyle);
        }

        private void DrawGameplay()
        {
            if (!keyboardArea.HasValue)
            {
                DrawTextBox(new Rect(40f, 120f, 580f, 32f), "Perdi o teclado. Reposicione e aguarde redeteccao.", new Color(0.05f, 0.2f, 0.35f, 0.68f));
                return;
            }

            Rect area = MapFrameRectToScreen(keyboardArea.Value);
            DrawKeyboardRect(area, new Color(0f, 0.67f, 1f, 1f));

            float strikeY = area.y + 0.86f * area.height;
            float spawnY = Mathf.Clamp(area.y - 0.32f * Screen.height, 16f, area.y - 40f);
            DrawRect(new Rect(area.x, strikeY, area.width, 2f), new Color(0.27f, 0.86f, 1f, 0.9f));

            float t = Time.realtimeSinceStartup - gameStartTime - countdownSeconds;
            if (t < 0f)
            {
                int count = Mathf.CeilToInt(-t);
                DrawTextBox(new Rect(40f, 120f, 360f, 34f), $"Prepare-se... {count}", new Color(0.08f, 0.08f, 0.08f, 0.65f));
                DrawTextBox(new Rect(40f, 156f, 420f, 28f), $"Inicio em {countdownSeconds:0}s | Velocidade {songSpeed:0.00}x", new Color(0.08f, 0.08f, 0.08f, 0.65f));
                return;
            }

            float musicT = t * songSpeed;
            float visualT = musicT - travelTime;
            float windowStart = visualT - 0.2f;
            float windowEnd = visualT + travelTime + 0.2f;

            for (int i = 0; i < events.Count; i++)
            {
                MidiNoteEvent ev = events[i];
                if (ev.end < windowStart || ev.start > windowEnd)
                {
                    continue;
                }

                float x = PitchToX(ev.pitch, area);
                float approach = (visualT - (ev.start - travelTime)) / Mathf.Max(0.001f, travelTime);
                float y = Mathf.Lerp(spawnY, strikeY, Mathf.Clamp01(approach));

                float durPx = Mathf.Clamp((ev.end - ev.start) * 140f, 20f, 300f);
                float yTop = y - durPx;
                bool black = IsBlackKey(ev.pitch);
                float half = black ? 4f : 9f;

                Color border;
                Color fill;
                if (ev.hand == 'R')
                {
                    border = new Color(0.27f, 0.9f, 0.35f, 1f);
                    fill = black ? new Color(0.08f, 0.27f, 0.08f, 0.9f) : new Color(0.72f, 0.95f, 0.72f, 0.92f);
                }
                else
                {
                    border = new Color(1f, 0.47f, 0.16f, 1f);
                    fill = black ? new Color(0.35f, 0.14f, 0.07f, 0.9f) : new Color(1f, 0.83f, 0.67f, 0.93f);
                }

                Rect noteRect = new Rect(x - half, yTop, 2f * half, Mathf.Max(1f, y - yTop));
                DrawRect(noteRect, fill);
                DrawOutline(noteRect, border, 1f);

                if (Mathf.Abs(ev.start - musicT) <= 0.08f)
                {
                    DrawOutline(new Rect(x - 12f, strikeY - 12f, 24f, 24f), Color.yellow, 2f);
                }
            }

            float progress = Mathf.Clamp01(musicT / Mathf.Max(0.001f, songDuration));
            Rect progressBar = new Rect(40f, Screen.height - 36f, Screen.width - 80f, 20f);
            DrawRect(progressBar, new Color(0.14f, 0.14f, 0.14f, 0.88f));
            DrawRect(new Rect(progressBar.x, progressBar.y, progressBar.width * progress, progressBar.height), new Color(0.12f, 0.72f, 0.24f, 0.95f));
            DrawOutline(progressBar, Color.white, 1f);

            float remaining = Mathf.Max(0f, songDuration - musicT);
            DrawTextBox(new Rect(40f, 120f, 520f, 30f), $"Musica: {songName}", new Color(0.08f, 0.08f, 0.08f, 0.65f));
            DrawTextBox(new Rect(40f, 152f, 560f, 28f), $"Tempo restante: {remaining:0.0}s | Velocidade: {songSpeed:0.00}x", new Color(0.08f, 0.08f, 0.08f, 0.65f));

            if (musicT > songDuration + 0.5f)
            {
                state = GameState.End;
            }
        }

        private void DrawEnd()
        {
            Rect panel = new Rect(80f, 140f, Screen.width - 160f, Screen.height - 260f);
            DrawRect(panel, new Color(0.05f, 0.05f, 0.05f, 0.72f));

            GUI.Label(new Rect(120f, 220f, 420f, 40f), "Musica finalizada!", headerStyle);
            GUI.Label(new Rect(120f, 260f, 640f, 30f), songName, textStyle);

            if (GUI.Button(new Rect(120f, Screen.height - 190f, 230f, 50f), "Voltar ao menu", buttonStyle))
            {
                ResetToMenu();
            }

            if (GUI.Button(new Rect(380f, Screen.height - 190f, 230f, 50f), "Jogar novamente", buttonStyle))
            {
                state = GameState.Align;
                stableHits = 0;
            }
        }

        private void DrawGuideBox()
        {
            float guideW = Screen.width * 0.7f;
            float guideH = Screen.height * 0.22f;
            float gx = (Screen.width - guideW) * 0.5f;
            float gy = Screen.height * 0.55f;
            DrawOutline(new Rect(gx, gy, guideW, guideH), new Color(0.35f, 0.35f, 0.35f, 1f), 1f);
            DrawTextBox(new Rect(gx + 10f, gy - 32f, 420f, 26f), "Centralize o teclado real dentro da caixa", new Color(0.08f, 0.08f, 0.08f, 0.65f));
        }

        private void DrawKeyboardRect(Rect area, Color color)
        {
            DrawOutline(area, color, 2f);
            for (int i = 1; i < 16; i++)
            {
                float x = area.x + area.width * (i / 16f);
                DrawRect(new Rect(x, area.y, 1f, area.height), new Color(0.16f, 0.31f, 0.43f, 0.75f));
            }
        }

        private Rect MapFrameRectToScreen(Rect frameRect)
        {
            GetActiveFrameSize(out int frameWidth, out int frameHeight);
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                return frameRect;
            }

            float scaleX = Screen.width / (float)frameWidth;
            float scaleY = Screen.height / (float)frameHeight;
            float mappedX = frameRect.x * scaleX;
            float mappedY = (frameHeight - frameRect.yMax) * scaleY;
            return new Rect(mappedX, mappedY, frameRect.width * scaleX, frameRect.height * scaleY);
        }

        private void GetActiveFrameSize(out int width, out int height)
        {
            if (frameTexture != null)
            {
                width = frameTexture.width;
                height = frameTexture.height;
                return;
            }

            if (webcam != null)
            {
                width = Mathf.Max(1, webcam.width);
                height = Mathf.Max(1, webcam.height);
                return;
            }

            width = Screen.width;
            height = Screen.height;
        }

        private void DrawRect(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, whitePixel);
            GUI.color = previous;
        }

        private void DrawOutline(Rect rect, Color color, float thickness)
        {
            DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private void DrawTextBox(Rect rect, string text, Color bgColor)
        {
            DrawRect(rect, bgColor);
            GUI.Label(new Rect(rect.x + 8f, rect.y + 4f, rect.width - 16f, rect.height - 8f), text, textStyle);
        }

        private void HandleMenuKeyboardFallback()
        {
            if (IsKeyPressed(KeyCode.UpArrow) || IsKeyPressed(KeyCode.K))
            {
                menuScroll = Mathf.Max(0, menuScroll - 1);
                menuScrollPos.y = Mathf.Max(0f, menuScroll * 34f);
            }
            else if (IsKeyPressed(KeyCode.DownArrow) || IsKeyPressed(KeyCode.J))
            {
                menuScroll = Mathf.Min(Mathf.Max(0, midiFiles.Count - 1), menuScroll + 1);
                menuScrollPos.y = menuScroll * 34f;
            }
            else if (IsKeyPressed(KeyCode.PageUp))
            {
                menuScroll = Mathf.Max(0, menuScroll - 5);
                menuScrollPos.y = Mathf.Max(0f, menuScroll * 34f);
            }
            else if (IsKeyPressed(KeyCode.PageDown))
            {
                menuScroll = Mathf.Min(Mathf.Max(0, midiFiles.Count - 1), menuScroll + 5);
                menuScrollPos.y = menuScroll * 34f;
            }
        }

        private void HandleMouseWheelMenuScroll()
        {
            Event current = Event.current;
            if (current == null || current.type != UnityEngine.EventType.ScrollWheel)
            {
                return;
            }

            float wheel = -current.delta.y;
            if (Mathf.Abs(wheel) <= 0.01f)
            {
                return;
            }

            menuScrollPos.y = Mathf.Max(0f, menuScrollPos.y - wheel * 36f);
            current.Use();
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

        private void StartSelectedSong()
        {
            if (midiFiles.Count == 0)
            {
                return;
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
                stableHits = 0;
                bestConf = 0f;
                keyboardArea = null;
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
            if (Application.platform == RuntimePlatform.Android && enableHmdModeOnGameStart)
            {
                RequestHmdMode();
            }
        }

        private void ResetToMenu()
        {
            state = GameState.Menu;
            stableHits = 0;
            bestConf = 0f;
            keyboardArea = null;
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

        private void StartCamera()
        {
            if (webcam != null)
            {
                if (webcam.isPlaying)
                {
                    webcam.Stop();
                }

                webcam = null;
            }

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

            cameraDeviceName = targetName;

            webcam = new WebCamTexture(targetName, requestedWidth, requestedHeight, requestedFps);
            webcam.Play();
            cameraDiagnosticsLogged = false;
            lastError = string.Empty;
            Debug.Log($"[ArPianoParityGame] Webcam requested: {targetName} {requestedWidth}x{requestedHeight} @{requestedFps}");

            if (cameraStartupCheckRoutine != null)
            {
                StopCoroutine(cameraStartupCheckRoutine);
            }

            cameraStartupCheckRoutine = StartCoroutine(CheckCameraStartup());
        }

        private IEnumerator CheckCameraStartup()
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
            AddCameraModeOption(640, 480, 30);
            AddCameraModeOption(requestedWidth, requestedHeight, requestedFps);

            selectedCameraModeIndex = FindModeIndex(1920, 1080, 60);
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

            StartCamera();
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

        private void TryInitializeModel()
        {
            if (onnxModel == null && !string.IsNullOrWhiteSpace(resourcesModelPath))
            {
                onnxModel = Resources.Load<ModelAsset>(resourcesModelPath);
            }

            if (onnxModel == null)
            {
                lastError = "Onnx Model vazio. Defina no Inspector ou mantenha piano_SGD.onnx em Assets/Resources/AIModels.";
                return;
            }

            try
            {
                runtimeModel = ModelLoader.Load(onnxModel);
                worker = new Worker(runtimeModel, backendType);
                outputNames = runtimeModel.outputs.Select(o => o.name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
                ResolveModelInputSize(runtimeModel, fallbackInputSize, out modelInputW, out modelInputH);
                modelDiagnosticsLogged = false;
            }
            catch (Exception ex)
            {
                lastError = "Falha ao carregar ONNX: " + ex.Message;
            }
        }

        private void UpdateTracker()
        {
            frameCount++;
            bool shouldDetect = !keyboardArea.HasValue || frameCount % Mathf.Max(1, detectInterval) == 0;
            if (!shouldDetect)
            {
                return;
            }

            Detection? best = DetectKeyboard();
            if (best.HasValue)
            {
                Detection d = best.Value;
                bestConf = d.score;
                keyboardArea = SmoothRect(keyboardArea, d, 0.25f);
                stableHits = Mathf.Min(200, stableHits + 1);
            }
            else
            {
                stableHits = Mathf.Max(0, stableHits - 1);
            }

            if (renderFpsEma < 28f)
            {
                detectInterval = Mathf.Min(12, detectInterval + 1);
            }
            else if (renderFpsEma > 40f)
            {
                detectInterval = Mathf.Max(3, detectInterval - 1);
            }
        }

        private Detection? DetectKeyboard()
        {
            if (worker == null)
            {
                if (!modelInitAttempted)
                {
                    modelInitAttempted = true;
                    TryInitializeModel();
                }

                if (worker == null)
                {
                    return null;
                }
            }

            if (webcam == null || !webcam.isPlaying)
            {
                return null;
            }

            if (webcam.width < 16 || webcam.height < 16)
            {
                return null;
            }

            int correctedWidth;
            int correctedHeight;
            RefreshCorrectedFrame(out correctedWidth, out correctedHeight);

            if (!cameraDiagnosticsLogged)
            {
                Debug.Log($"[ArPianoParityGame] Webcam actual: {webcam.width}x{webcam.height}, corrected={correctedWidth}x{correctedHeight}, rotation={webcam.videoRotationAngle}, mirrored={webcam.videoVerticallyMirrored}");
                cameraDiagnosticsLogged = true;
            }

            if (!modelDiagnosticsLogged)
            {
                Debug.Log($"[ArPianoParityGame] Model input resolved: {modelInputW}x{modelInputH}, fallback={fallbackInputSize}, outputs=[{string.Join(", ", outputNames)}]");
                modelDiagnosticsLogged = true;
            }

            using Tensor<float> inputTensor = PreprocessFrame(correctedFramePixels, correctedWidth, correctedHeight, modelInputW, modelInputH, out float[] inputData, out Color32[] resizedPixels);
            worker.Schedule(inputTensor);
            Tensor<float> output = PickDetectionOutput();
            if (output == null)
            {
                if (ShouldDumpInferenceArtifacts())
                {
                    DumpInferenceArtifacts(correctedWidth, correctedHeight, inputData, resizedPixels, null, null, BuildOutputSummaries());
                }

                return null;
            }

            Detection? best = DecodeBest(output, correctedWidth, correctedHeight, modelInputW, modelInputH, numClasses, confThreshold, iouThreshold);
            if (best.HasValue)
            {
                best = ConvertDetectionToTopLeft(best.Value, correctedHeight);
            }

            if (ShouldDumpInferenceArtifacts())
            {
                DumpInferenceArtifacts(correctedWidth, correctedHeight, inputData, resizedPixels, output, best, BuildOutputSummaries());
            }

            return best;
        }

        private void RefreshCorrectedFrame(out int correctedWidth, out int correctedHeight)
        {
            int sourceWidth = webcam.width;
            int sourceHeight = webcam.height;
            int rotation = NormalizeRotation(webcam.videoRotationAngle);
            bool swapAxes = rotation == 90 || rotation == 270;

            correctedWidth = swapAxes ? sourceHeight : sourceWidth;
            correctedHeight = swapAxes ? sourceWidth : sourceHeight;
            EnsureFrameTexture(correctedWidth, correctedHeight);

            Color32[] sourcePixels = webcam.GetPixels32();
            int total = correctedWidth * correctedHeight;
            if (correctedFramePixels == null || correctedFramePixels.Length != total)
            {
                correctedFramePixels = new Color32[total];
            }

            bool mirrored = webcam.videoVerticallyMirrored;
            for (int y = 0; y < sourceHeight; y++)
            {
                for (int x = 0; x < sourceWidth; x++)
                {
                    int sampleY = mirrored ? (sourceHeight - 1 - y) : y;
                    int srcIndex = sampleY * sourceWidth + x;
                    int dstX;
                    int dstY;

                    switch (rotation)
                    {
                        case 90:
                            dstX = sourceHeight - 1 - y;
                            dstY = x;
                            break;
                        case 180:
                            dstX = sourceWidth - 1 - x;
                            dstY = sourceHeight - 1 - y;
                            break;
                        case 270:
                            dstX = y;
                            dstY = sourceWidth - 1 - x;
                            break;
                        default:
                            dstX = x;
                            dstY = y;
                            break;
                    }

                    correctedFramePixels[dstY * correctedWidth + dstX] = sourcePixels[srcIndex];
                }
            }

            frameTexture.SetPixels32(correctedFramePixels);
            frameTexture.Apply(false, false);
        }

        private static int NormalizeRotation(int angle)
        {
            int normalized = angle % 360;
            if (normalized < 0)
            {
                normalized += 360;
            }

            if (normalized >= 315 || normalized < 45)
            {
                return 0;
            }

            if (normalized < 135)
            {
                return 90;
            }

            if (normalized < 225)
            {
                return 180;
            }

            return 270;
        }

        private void EnsureFrameTexture(int width, int height)
        {
            if (frameTexture != null && frameTexture.width == width && frameTexture.height == height)
            {
                return;
            }

            if (frameTexture != null)
            {
                Destroy(frameTexture);
            }

            frameTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        }

        private Tensor<float> PreprocessFrame(Color32[] sourcePixels, int sourceWidth, int sourceHeight, int inputW, int inputH, out float[] inputData, out Color32[] resizedPixels)
        {
            resizedPixels = ResizePixelsBilinear(sourcePixels, sourceWidth, sourceHeight, inputW, inputH);
            int planeSize = inputW * inputH;
            inputData = new float[3 * planeSize];

            for (int i = 0; i < planeSize; i++)
            {
                Color32 p = resizedPixels[i];
                inputData[i] = p.r / 255f;
                inputData[planeSize + i] = p.g / 255f;
                inputData[(2 * planeSize) + i] = p.b / 255f;
            }

            return new Tensor<float>(new TensorShape(1, 3, inputH, inputW), inputData);
        }

        private bool ShouldDumpInferenceArtifacts()
        {
            return dumpInferenceArtifacts && dumpedInferenceArtifacts < dumpInferenceArtifactLimit;
        }

        private string EnsureDumpDirectory()
        {
            if (string.IsNullOrWhiteSpace(dumpDirectoryPath))
            {
                string folder = string.IsNullOrWhiteSpace(dumpInferenceFolderName) ? "DebugDumps/Unity" : dumpInferenceFolderName;
                string normalizedFolder = folder.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                dumpDirectoryPath = Path.Combine(Application.persistentDataPath, normalizedFolder);
            }

            Directory.CreateDirectory(dumpDirectoryPath);
            if (!dumpDirectoryLogged)
            {
                Debug.Log($"[ArPianoParityGame] Dump directory: {dumpDirectoryPath}");
                dumpDirectoryLogged = true;
            }

            return dumpDirectoryPath;
        }

        private void DumpInferenceArtifacts(int imageWidth, int imageHeight, float[] inputData, Color32[] resizedPixels, Tensor<float> chosenOutput, Detection? best, List<string> outputSummaries)
        {
            string directory = EnsureDumpDirectory();
            string prefix = $"unity_dump_{dumpedInferenceArtifacts:000}";

            SaveTexturePng(frameTexture, Path.Combine(directory, prefix + "_frame.png"));
            SaveResizedInputPng(resizedPixels, modelInputW, modelInputH, Path.Combine(directory, prefix + "_input.png"));
            SaveOverlayPng(imageWidth, imageHeight, best, Path.Combine(directory, prefix + "_overlay.png"));

            var builder = new StringBuilder();
            builder.AppendLine($"frame_size={imageWidth}x{imageHeight}");
            builder.AppendLine($"model_input={modelInputW}x{modelInputH}");
            builder.AppendLine($"camera_rotation={webcam.videoRotationAngle}");
            builder.AppendLine($"camera_mirrored={webcam.videoVerticallyMirrored}");
            builder.AppendLine($"confidence_threshold={confThreshold:F4}");
            builder.AppendLine($"iou_threshold={iouThreshold:F4}");
            builder.AppendLine($"best_detection={FormatDetection(best)}");
            builder.AppendLine(DescribeArrayStats("input_tensor", inputData));
            builder.AppendLine();
            builder.AppendLine("outputs:");
            for (int i = 0; i < outputSummaries.Count; i++)
            {
                builder.AppendLine(outputSummaries[i]);
            }

            if (chosenOutput != null)
            {
                builder.AppendLine();
                builder.AppendLine("chosen_output:");
                builder.AppendLine(DescribeOutputTensor("chosen", chosenOutput));
            }

            File.WriteAllText(Path.Combine(directory, prefix + "_stats.txt"), builder.ToString());
            Debug.Log($"[ArPianoParityGame] Dumped inference artifacts: {prefix} -> {directory}");
            dumpedInferenceArtifacts++;
        }

        private List<string> BuildOutputSummaries()
        {
            var summaries = new List<string>();
            if (worker == null)
            {
                return summaries;
            }

            for (int i = 0; i < outputNames.Length; i++)
            {
                Tensor<float> output = worker.PeekOutput(outputNames[i]) as Tensor<float>;
                if (output == null)
                {
                    summaries.Add($"- {outputNames[i]}: unavailable");
                    continue;
                }

                summaries.Add(DescribeOutputTensor(outputNames[i], output));
            }

            return summaries;
        }

        private string DescribeOutputTensor(string name, Tensor<float> output)
        {
            using Tensor<float> readable = output.ReadbackAndClone();
            float[] data = readable.DownloadToArray();
            string shapeText = DescribeShape(readable.shape);
            string stats = DescribeArrayStats(name, data);

            TensorShape shape = readable.shape;
            if (shape.rank != 3 || shape[0] != 1)
            {
                return $"- {name}: shape={shapeText}, {stats}";
            }

            int dim1 = shape[1];
            int dim2 = shape[2];
            bool featuresFirst = dim1 <= 256 && dim2 > dim1;
            int candidates = featuresFirst ? dim2 : dim1;
            int features = featuresFirst ? dim1 : dim2;
            if (features < 4 + Mathf.Max(1, numClasses))
            {
                return $"- {name}: shape={shapeText}, layout={(featuresFirst ? "features_first" : "candidates_first")}, {stats}";
            }

            float[] topScores = new float[Mathf.Min(5, candidates)];
            for (int i = 0; i < topScores.Length; i++)
            {
                topScores[i] = float.NegativeInfinity;
            }

            for (int candidate = 0; candidate < candidates; candidate++)
            {
                float bestClass = 0f;
                for (int cls = 0; cls < numClasses; cls++)
                {
                    float score = Read(data, candidate, 4 + cls, candidates, features, featuresFirst);
                    if (score > bestClass)
                    {
                        bestClass = score;
                    }
                }

                InsertTopScore(topScores, bestClass);
            }

            string topText = string.Join(", ", topScores.Where(v => !float.IsNegativeInfinity(v)).Select(v => v.ToString("F6")));
            return $"- {name}: shape={shapeText}, layout={(featuresFirst ? "features_first" : "candidates_first")}, candidates={candidates}, features={features}, {stats}, top_scores=[{topText}]";
        }

        private static void InsertTopScore(float[] topScores, float value)
        {
            for (int i = 0; i < topScores.Length; i++)
            {
                if (value <= topScores[i])
                {
                    continue;
                }

                for (int shift = topScores.Length - 1; shift > i; shift--)
                {
                    topScores[shift] = topScores[shift - 1];
                }

                topScores[i] = value;
                return;
            }
        }

        private static string DescribeArrayStats(string name, float[] values)
        {
            if (values == null || values.Length == 0)
            {
                return $"{name}: count=0";
            }

            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;
            double sum = 0.0;
            int previewCount = Mathf.Min(12, values.Length);
            string[] preview = new string[previewCount];
            for (int i = 0; i < values.Length; i++)
            {
                float value = values[i];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }

                sum += value;
                if (i < previewCount)
                {
                    preview[i] = value.ToString("F6");
                }
            }

            double mean = sum / values.Length;
            return $"{name}: count={values.Length}, min={min:F6}, max={max:F6}, mean={mean:F6}, preview=[{string.Join(", ", preview)}]";
        }

        private static string DescribeShape(TensorShape shape)
        {
            if (shape.rank <= 0)
            {
                return "[]";
            }

            int[] dims = new int[shape.rank];
            for (int i = 0; i < shape.rank; i++)
            {
                dims[i] = shape[i];
            }

            return "[" + string.Join(", ", dims) + "]";
        }

        private static string FormatDetection(Detection? detection)
        {
            if (!detection.HasValue)
            {
                return "none";
            }

            Detection value = detection.Value;
            return $"x1={value.x1:F3}, y1={value.y1:F3}, x2={value.x2:F3}, y2={value.y2:F3}, score={value.score:F6}";
        }

        private void SaveResizedInputPng(Color32[] pixels, int width, int height, string path)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.SetPixels32(ConvertTopLeftToBottomLeft(pixels, width, height));
            texture.Apply(false, false);
            SaveTexturePng(texture, path);
            Destroy(texture);
        }

        private void SaveOverlayPng(int width, int height, Detection? best, string path)
        {
            if (correctedFramePixels == null || correctedFramePixels.Length != width * height)
            {
                return;
            }

            Color32[] copy = new Color32[correctedFramePixels.Length];
            Array.Copy(correctedFramePixels, copy, correctedFramePixels.Length);
            if (best.HasValue)
            {
                DrawRectOutline(copy, width, height, best.Value, new Color32(40, 255, 90, 255), 3);
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.SetPixels32(copy);
            texture.Apply(false, false);
            SaveTexturePng(texture, path);
            Destroy(texture);
        }

        private static void DrawRectOutline(Color32[] pixels, int width, int height, Detection rect, Color32 color, int thickness)
        {
            int xMin = Mathf.Clamp(Mathf.RoundToInt(rect.x1), 0, width - 1);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(rect.y1), 0, height - 1);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(rect.x2), 0, width - 1);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(rect.y2), 0, height - 1);

            for (int t = 0; t < thickness; t++)
            {
                int left = Mathf.Clamp(xMin - t, 0, width - 1);
                int right = Mathf.Clamp(xMax + t, 0, width - 1);
                int top = Mathf.Clamp(yMin - t, 0, height - 1);
                int bottom = Mathf.Clamp(yMax + t, 0, height - 1);

                for (int x = left; x <= right; x++)
                {
                    pixels[top * width + x] = color;
                    pixels[bottom * width + x] = color;
                }

                for (int y = top; y <= bottom; y++)
                {
                    pixels[y * width + left] = color;
                    pixels[y * width + right] = color;
                }
            }
        }

        private static void SaveTexturePng(Texture2D texture, string path)
        {
            if (texture == null)
            {
                return;
            }

            byte[] png = texture.EncodeToPNG();
            if (png == null || png.Length == 0)
            {
                return;
            }

            File.WriteAllBytes(path, png);
        }

        private static Color32[] ResizePixelsBilinear(Color32[] sourcePixels, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        {
            var resizedPixels = new Color32[targetWidth * targetHeight];

            for (int y = 0; y < targetHeight; y++)
            {
                float sampleY = targetHeight == 1 ? 0f : (y + 0.5f) * sourceHeight / targetHeight - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(sampleY), 0, sourceHeight - 1);
                int y1 = Mathf.Clamp(y0 + 1, 0, sourceHeight - 1);
                float ty = Mathf.Clamp01(sampleY - y0);

                for (int x = 0; x < targetWidth; x++)
                {
                    float sampleX = targetWidth == 1 ? 0f : (x + 0.5f) * sourceWidth / targetWidth - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(sampleX), 0, sourceWidth - 1);
                    int x1 = Mathf.Clamp(x0 + 1, 0, sourceWidth - 1);
                    float tx = Mathf.Clamp01(sampleX - x0);

                    Color32 c00 = ReadPixelTopLeft(sourcePixels, sourceWidth, sourceHeight, x0, y0);
                    Color32 c01 = ReadPixelTopLeft(sourcePixels, sourceWidth, sourceHeight, x1, y0);
                    Color32 c10 = ReadPixelTopLeft(sourcePixels, sourceWidth, sourceHeight, x0, y1);
                    Color32 c11 = ReadPixelTopLeft(sourcePixels, sourceWidth, sourceHeight, x1, y1);
                    resizedPixels[y * targetWidth + x] = BilinearSample(c00, c01, c10, c11, tx, ty);
                }
            }

            return resizedPixels;
        }

        private static Color32 ReadPixelTopLeft(Color32[] pixels, int width, int height, int x, int y)
        {
            int clampedX = Mathf.Clamp(x, 0, width - 1);
            int clampedY = Mathf.Clamp(y, 0, height - 1);
            int bottomToTopY = height - 1 - clampedY;
            return pixels[bottomToTopY * width + clampedX];
        }

        private static Color32[] ConvertTopLeftToBottomLeft(Color32[] pixels, int width, int height)
        {
            var converted = new Color32[pixels.Length];
            for (int y = 0; y < height; y++)
            {
                int sourceRow = y * width;
                int destinationRow = (height - 1 - y) * width;
                Array.Copy(pixels, sourceRow, converted, destinationRow, width);
            }

            return converted;
        }

        private static Color32 BilinearSample(Color32 c00, Color32 c01, Color32 c10, Color32 c11, float tx, float ty)
        {
            float topR = Mathf.Lerp(c00.r, c01.r, tx);
            float topG = Mathf.Lerp(c00.g, c01.g, tx);
            float topB = Mathf.Lerp(c00.b, c01.b, tx);
            float topA = Mathf.Lerp(c00.a, c01.a, tx);

            float bottomR = Mathf.Lerp(c10.r, c11.r, tx);
            float bottomG = Mathf.Lerp(c10.g, c11.g, tx);
            float bottomB = Mathf.Lerp(c10.b, c11.b, tx);
            float bottomA = Mathf.Lerp(c10.a, c11.a, tx);

            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(topR, bottomR, ty)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(topG, bottomG, ty)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(topB, bottomB, ty)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(topA, bottomA, ty)));
        }

        private Tensor<float> PickDetectionOutput()
        {
            if (worker == null)
            {
                return null;
            }

            for (int i = 0; i < outputNames.Length; i++)
            {
                Tensor<float> named = worker.PeekOutput(outputNames[i]) as Tensor<float>;
                if (named == null)
                {
                    continue;
                }

                TensorShape shape = named.shape;
                if (shape.rank == 3 && shape[0] == 1)
                {
                    int dim1 = shape[1];
                    int dim2 = shape[2];
                    bool featuresFirst = dim1 <= 256 && dim2 > dim1;
                    int featureCount = featuresFirst ? dim1 : dim2;
                    if (featureCount >= 5)
                    {
                        return named;
                    }
                }
            }

            Tensor<float> fallback = worker.PeekOutput() as Tensor<float>;
            return fallback;
        }

        private static Detection? DecodeBest(Tensor<float> outputTensor, int imageW, int imageH, int inputW, int inputH, int classes, float conf, float iou)
        {
            using Tensor<float> readable = outputTensor.ReadbackAndClone();
            TensorShape shape = readable.shape;
            if (shape.rank != 3 || shape[0] != 1)
            {
                return null;
            }

            float[] data = readable.DownloadToArray();
            int dim1 = shape[1];
            int dim2 = shape[2];
            bool featuresFirst = dim1 <= 256 && dim2 > dim1;
            int candidates = featuresFirst ? dim2 : dim1;
            int features = featuresFirst ? dim1 : dim2;
            if (features < 4 + Mathf.Max(1, classes))
            {
                return null;
            }

            var detections = new List<Detection>();
            for (int c = 0; c < candidates; c++)
            {
                float cx = Read(data, c, 0, candidates, features, featuresFirst);
                float cy = Read(data, c, 1, candidates, features, featuresFirst);
                float bw = Read(data, c, 2, candidates, features, featuresFirst);
                float bh = Read(data, c, 3, candidates, features, featuresFirst);

                float bestClass = 0f;
                for (int cls = 0; cls < classes; cls++)
                {
                    float s = Read(data, c, 4 + cls, candidates, features, featuresFirst);
                    if (s > bestClass)
                    {
                        bestClass = s;
                    }
                }

                if (bestClass < conf)
                {
                    continue;
                }

                CxCyWhToXyxy(cx, cy, bw, bh, imageW, imageH, inputW, inputH, out float x1, out float y1, out float x2, out float y2);
                if ((x2 - x1) > 1f && (y2 - y1) > 1f)
                {
                    detections.Add(new Detection(x1, y1, x2, y2, bestClass));
                }
            }

            if (detections.Count == 0)
            {
                return null;
            }

            detections.Sort((a, b) => b.score.CompareTo(a.score));
            var kept = new List<Detection>();
            for (int i = 0; i < detections.Count; i++)
            {
                Detection det = detections[i];
                bool suppress = false;
                for (int k = 0; k < kept.Count; k++)
                {
                    if (IoU(det, kept[k]) > iou)
                    {
                        suppress = true;
                        break;
                    }
                }

                if (!suppress)
                {
                    kept.Add(det);
                }
            }

            return kept[0];
        }

        private static float Read(float[] data, int candidate, int feature, int candidates, int features, bool featuresFirst)
        {
            int index = featuresFirst ? feature * candidates + candidate : candidate * features + feature;
            return data[index];
        }

        private static void CxCyWhToXyxy(float cx, float cy, float bw, float bh, int imageW, int imageH, int inputW, int inputH, out float x1, out float y1, out float x2, out float y2)
        {
            bool normalized = Mathf.Abs(cx) <= 2f && Mathf.Abs(cy) <= 2f && Mathf.Abs(bw) <= 2f && Mathf.Abs(bh) <= 2f;
            float sx = normalized ? imageW : imageW / (float)Mathf.Max(1, inputW);
            float sy = normalized ? imageH : imageH / (float)Mathf.Max(1, inputH);
            float centerX = cx * sx;
            float centerY = cy * sy;
            float width = Mathf.Abs(bw * sx);
            float height = Mathf.Abs(bh * sy);

            x1 = Mathf.Clamp(centerX - 0.5f * width, 0f, imageW - 1f);
            y1 = Mathf.Clamp(centerY - 0.5f * height, 0f, imageH - 1f);
            x2 = Mathf.Clamp(centerX + 0.5f * width, x1 + 1f, imageW);
            y2 = Mathf.Clamp(centerY + 0.5f * height, y1 + 1f, imageH);
        }

        private static Detection ConvertDetectionToTopLeft(Detection detection, int imageHeight)
        {
            float topY = Mathf.Clamp(imageHeight - detection.y2, 0f, imageHeight - 1f);
            float bottomY = Mathf.Clamp(imageHeight - detection.y1, topY + 1f, imageHeight);
            return new Detection(detection.x1, topY, detection.x2, bottomY, detection.score);
        }

        private static float IoU(Detection a, Detection b)
        {
            float x1 = Mathf.Max(a.x1, b.x1);
            float y1 = Mathf.Max(a.y1, b.y1);
            float x2 = Mathf.Min(a.x2, b.x2);
            float y2 = Mathf.Min(a.y2, b.y2);

            float inter = Mathf.Max(0f, x2 - x1) * Mathf.Max(0f, y2 - y1);
            if (inter <= 0f)
            {
                return 0f;
            }

            float areaA = Mathf.Max(0f, a.x2 - a.x1) * Mathf.Max(0f, a.y2 - a.y1);
            float areaB = Mathf.Max(0f, b.x2 - b.x1) * Mathf.Max(0f, b.y2 - b.y1);
            float union = areaA + areaB - inter;
            return union <= 0f ? 0f : inter / union;
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

        private void LoadMidiList()
        {
            midiFiles.Clear();
            try
            {
                string root = ResolveMidiRoot();
                if (!Directory.Exists(root))
                {
                    lastError = "Diretorio MIDI nao existe: " + root;
                    return;
                }

                IEnumerable<string> files = Directory.EnumerateFiles(root)
                    .Where(f =>
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        return ext == ".mid" || ext == ".midi";
                    })
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

                midiFiles.AddRange(files);
                lastError = string.Empty;
            }
            catch (Exception ex)
            {
                lastError = "Falha ao listar MIDI: " + ex.Message;
            }
        }

        private string ResolveMidiRoot()
        {
            if (!string.IsNullOrWhiteSpace(activeMidiRoot))
            {
                return activeMidiRoot;
            }

            if (Application.platform == RuntimePlatform.Android)
            {
                string downloads = GetAndroidDownloadsDirectory();
                if (Directory.Exists(downloads))
                {
                    return downloads;
                }

                return Path.Combine(Application.streamingAssetsPath, midiStreamingAssetsSubFolder);
            }

            return midiDirectoryDesktop;
        }

        private void InitializeMidiRepository()
        {
            activeMidiRoot = ResolveMidiRoot();
            midiDirectoryInput = activeMidiRoot;
            LoadMidiList();
        }

        private void ApplyMidiRepository(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            activeMidiRoot = root.Trim();
            midiDirectoryInput = activeMidiRoot;
            selectedIndex = 0;
            menuScroll = 0;
            menuScrollPos = Vector2.zero;
            LoadMidiList();
        }

        private string GetAndroidDownloadsDirectory()
        {
            if (!string.IsNullOrWhiteSpace(midiDirectoryAndroidDownloads))
            {
                return midiDirectoryAndroidDownloads;
            }

            return "/storage/emulated/0/Download";
        }

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

        private static void LoadMidiEvents(string midiPath, List<MidiNoteEvent> outEvents, out float duration)
        {
            var midiFile = MidiFile.Read(midiPath);
            TempoMap tempoMap = midiFile.GetTempoMap();
            var active = new Dictionary<(int note, int channel), Queue<(double start, int velocity)>>();
            var tempEvents = new List<(MidiNoteEvent ev, int channel)>();
            var channelPitchSum = new Dictionary<int, double>();
            var channelPitchCount = new Dictionary<int, int>();

            long absoluteTicks = 0;
            foreach (TrackChunk chunk in midiFile.GetTrackChunks())
            {
                absoluteTicks = 0;
                foreach (MidiEvent evt in chunk.Events)
                {
                    absoluteTicks += evt.DeltaTime;

                    if (!(evt is NoteOnEvent on))
                    {
                        if (evt is NoteOffEvent off)
                        {
                            HandleNoteOff(off.NoteNumber, off.Channel, absoluteTicks, tempoMap, active, tempEvents);
                        }

                        continue;
                    }

                    if (on.Velocity > 0)
                    {
                        double start = MetricTimeToSeconds(TimeConverter.ConvertTo<MetricTimeSpan>(absoluteTicks, tempoMap));
                        (int note, int channel) key = (on.NoteNumber, on.Channel);
                        if (!active.TryGetValue(key, out Queue<(double start, int velocity)> queue))
                        {
                            queue = new Queue<(double start, int velocity)>();
                            active[key] = queue;
                        }

                        queue.Enqueue((start, on.Velocity));

                        if (!channelPitchSum.ContainsKey(on.Channel))
                        {
                            channelPitchSum[on.Channel] = 0.0;
                            channelPitchCount[on.Channel] = 0;
                        }

                        channelPitchSum[on.Channel] += on.NoteNumber;
                        channelPitchCount[on.Channel] += 1;
                    }
                    else
                    {
                        HandleNoteOff(on.NoteNumber, on.Channel, absoluteTicks, tempoMap, active, tempEvents);
                    }
                }
            }

            foreach (KeyValuePair<(int note, int channel), Queue<(double start, int velocity)>> pair in active)
            {
                while (pair.Value.Count > 0)
                {
                    (double start, int velocity) note = pair.Value.Dequeue();
                    var ev = new MidiNoteEvent
                    {
                        pitch = pair.Key.note,
                        start = (float)note.start,
                        end = (float)(note.start + 0.25),
                        velocity = note.velocity,
                        hand = pair.Key.note >= 60 ? 'R' : 'L'
                    };
                    tempEvents.Add((ev, pair.Key.channel));
                }
            }

            var channelToHand = BuildChannelHandMap(channelPitchSum, channelPitchCount);

            outEvents.Clear();
            for (int i = 0; i < tempEvents.Count; i++)
            {
                MidiNoteEvent ev = tempEvents[i].ev;
                int channel = tempEvents[i].channel;
                if (channelToHand.TryGetValue(channel, out char hand))
                {
                    ev.hand = hand;
                }
                else
                {
                    ev.hand = ev.pitch >= 60 ? 'R' : 'L';
                }

                outEvents.Add(ev);
            }

            outEvents.Sort((a, b) => a.start.CompareTo(b.start));
            duration = outEvents.Count == 0 ? 0f : outEvents.Max(e => e.end);
        }

        private static void HandleNoteOff(int noteNumber, int channel, long absoluteTicks, TempoMap tempoMap,
            Dictionary<(int note, int channel), Queue<(double start, int velocity)>> active,
            List<(MidiNoteEvent ev, int channel)> tempEvents)
        {
            (int note, int channel) key = (noteNumber, channel);
            if (!active.TryGetValue(key, out Queue<(double start, int velocity)> queue) || queue.Count == 0)
            {
                return;
            }

            (double start, int velocity) startInfo = queue.Dequeue();
            double end = MetricTimeToSeconds(TimeConverter.ConvertTo<MetricTimeSpan>(absoluteTicks, tempoMap));
            if (end <= startInfo.start)
            {
                return;
            }

            var ev = new MidiNoteEvent
            {
                pitch = noteNumber,
                start = (float)startInfo.start,
                end = (float)end,
                velocity = startInfo.velocity,
                hand = noteNumber >= 60 ? 'R' : 'L'
            };
            tempEvents.Add((ev, channel));
        }

        private static Dictionary<int, char> BuildChannelHandMap(Dictionary<int, double> sum, Dictionary<int, int> count)
        {
            var averages = new List<(int channel, double avg)>();
            foreach (KeyValuePair<int, int> kv in count)
            {
                if (kv.Value <= 0)
                {
                    continue;
                }

                averages.Add((kv.Key, sum[kv.Key] / kv.Value));
            }

            var map = new Dictionary<int, char>();
            if (averages.Count < 2)
            {
                return map;
            }

            averages.Sort((a, b) => a.avg.CompareTo(b.avg));
            int split = averages.Count / 2;
            for (int i = 0; i < averages.Count; i++)
            {
                map[averages[i].channel] = i < split ? 'L' : 'R';
            }

            return map;
        }

        private static double MetricTimeToSeconds(MetricTimeSpan metric)
        {
            return metric.Hours * 3600.0 + metric.Minutes * 60.0 + metric.Seconds + metric.Milliseconds / 1000.0;
        }

        private static void ResolveModelInputSize(Model model, int fallbackSize, out int inputW, out int inputH)
        {
            inputW = fallbackSize;
            inputH = fallbackSize;

            if (model == null || model.inputs == null || model.inputs.Count == 0)
            {
                return;
            }

            DynamicTensorShape shape = model.inputs[0].shape;
            if (shape.isRankDynamic || shape.rank < 4)
            {
                return;
            }

            if (!shape.IsStatic())
            {
                return;
            }

            TensorShape staticShape = shape.ToTensorShape();
            int h = staticShape[2];
            int w = staticShape[3];
            if (h > 0 && w > 0)
            {
                inputW = w;
                inputH = h;
            }
        }

        private void RequestHmdMode()
        {
            if (hmdRequested || hmdModeActive)
            {
                return;
            }

            hmdRequested = true;
            StartCoroutine(EnableHmdModeCoroutine());
        }

        private IEnumerator EnableHmdModeCoroutine()
        {
            XRGeneralSettings settings = XRGeneralSettings.Instance;
            if (settings == null || settings.Manager == null)
            {
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

            hmdRequested = false;
        }
    }
}
