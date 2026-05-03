using System;
using System.IO;
using UnityEngine;
using PianoARGame.Services;

namespace PianoARGame.AR
{
    /// <summary>
    /// Serviço de captura de webcam com detecção ONNX simplificada.
    /// Fluxo: iniciar detecção -> (opcional) ativar tracking -> iniciar jogo.
    /// </summary>
    public class TestWebcamController : MonoBehaviour
    {
        [Header("Webcam")]
        [Tooltip("Nome do dispositivo. Padrão: EMEET.")]
        public string webcamName = "EMEET";
        [Range(15, 60)] public int requestedFPS = 60;
        public int requestedWidth = 1920;
        public int requestedHeight = 1080;

        [Header("Detecção")]
        public PianoDetector detector;
        public ConfigService configService;
        [Range(0.05f, 0.5f)] public float detectionIntervalSeconds = 0.12f;
        [Range(64, 4096)] public int detectionWidth = 640;
        [Range(0f, 1f)] public float trackingSmoothFactor = 0.35f;
        public bool showDetectionOverlay = true;
        public bool showTrackingOverlay = true;

        public Texture2D LastFrameTexture { get; private set; }
        public DetectionResult LastDetection { get; private set; }
        public float CurrentFPS { get; private set; }
        public float CameraFPS { get; private set; }
        public string SelectedMidiPath { get; private set; }
        public event Action<string, DetectionResult> OnStartGameRequested;
        public bool DrawFullscreen { get; set; }

        private WebCamTexture _webcam;
        private RenderTexture _detectRT;
        private Texture2D _detectTex;
        private int _deviceIndex;

        private float _fpsTimer;
        private int _fpsFrames;
        private float _cameraFpsTimer;
        private int _cameraFrames;
        private float _detectionTimer;

        private Rect _windowRect = new Rect(10, 10, PANEL_W, PANEL_H);
        private bool _uiMinimized;
        private int _activeTab;
        private string[] _midiFiles = Array.Empty<string>();
        private int _midiSelIndex = -1;
        private Vector2 _midiScroll;
        private Vector2 _panelScroll;
        private string _midiFolder = "";
        private string _statusMsg = "Aguardando...";
        private bool _webcamRuntimeInfoLogged;

        private bool _detectionRunning;
        private bool _trackingEnabled;
        private Rect _trackingArea;
        private bool _trackingAreaValid;
        private DetectionResult _trackingDetection;

        private GUIStyle _titleStyle;
        private GUIStyle _greenStyle;
        private GUIStyle _redStyle;
        private GUIStyle _bigBtnStyle;
        private GUIStyle _wrapLabelStyle;
        private GUIStyle _wrapBoxStyle;
        private bool _stylesReady;

        private const float PANEL_W = 420f;
        private const float PANEL_H = 510f;

        void Awake()
        {
            if (configService != null)
                _midiFolder = configService.GetMusicFolderPath();
        }

        void Start()
        {
            EnsureDetectionSampleWidth();
            RefreshMidiList();
            StartWebcam();
        }

        void OnDisable()
        {
            StopWebcam();
            ReleaseGpuResources();
        }

        void Update()
        {
            UpdateGameFps();
            UpdateCameraFps();

            if (_webcam == null || !_webcam.isPlaying || _webcam.width <= 16)
                return;

            if (!_webcamRuntimeInfoLogged)
            {
                _webcamRuntimeInfoLogged = true;
                Debug.Log($"[Webcam] Resolução real: {_webcam.width}x{_webcam.height} | rotation={_webcam.videoRotationAngle} | mirrored={_webcam.videoVerticallyMirrored}");
            }

            if (_detectionRunning)
            {
                _detectionTimer += Time.unscaledDeltaTime;
                if (_detectionTimer >= detectionIntervalSeconds)
                {
                    _detectionTimer = 0f;
                    RunDetection();
                }
            }
        }

        private void StartWebcam()
        {
            var devices = WebCamTexture.devices;
            if (devices.Length == 0)
            {
                _statusMsg = "Nenhuma webcam encontrada.";
                Debug.LogWarning("[Webcam] Nenhum dispositivo de câmera disponível.");
                return;
            }

            _deviceIndex = FindDeviceIndex(devices);
            webcamName = devices[_deviceIndex].name;
            _webcam = new WebCamTexture(webcamName, requestedWidth, requestedHeight, requestedFPS);
            _webcam.Play();
            _webcamRuntimeInfoLogged = false;
            _statusMsg = $"Câmera iniciada: {webcamName}";
            Debug.Log($"[Webcam] Dispositivo: {webcamName} ({requestedWidth}x{requestedHeight} @{requestedFPS}fps)");
        }

        private void StopWebcam()
        {
            if (_webcam != null && _webcam.isPlaying)
                _webcam.Stop();
        }

        private int FindDeviceIndex(WebCamDevice[] devices)
        {
            if (!string.IsNullOrWhiteSpace(webcamName))
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    if (devices[i].name.IndexOf(webcamName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return i;
                }
            }

            for (int i = 0; i < devices.Length; i++)
            {
                if (devices[i].name.IndexOf("EMEET", StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }

            return 0;
        }

        public void SwitchDevice(int direction)
        {
            var devices = WebCamTexture.devices;
            if (devices.Length == 0)
                return;

            _deviceIndex = (_deviceIndex + direction + devices.Length) % devices.Length;
            webcamName = devices[_deviceIndex].name;
            StopWebcam();
            ReleaseGpuResources();
            StartWebcam();
        }

        public void SetWebcamByName(string name)
        {
            webcamName = name;
            StopWebcam();
            StartWebcam();
        }

        public void SetWebcamByIndex(int index)
        {
            var devices = WebCamTexture.devices;
            if (devices.Length == 0)
                return;

            _deviceIndex = Mathf.Clamp(index, 0, devices.Length - 1);
            webcamName = devices[_deviceIndex].name;
            StopWebcam();
            StartWebcam();
        }

        public string GetCurrentDeviceName() => _webcam?.deviceName ?? webcamName;
        public void ToggleFullscreen() => DrawFullscreen = !DrawFullscreen;
        public void SetFullscreen(bool value) => DrawFullscreen = value;

        public void RunDetection()
        {
            if (detector == null)
            {
                _statusMsg = "PianoDetector não atribuído.";
                return;
            }

            if (_webcam == null || !_webcam.isPlaying || _webcam.width <= 16)
            {
                _statusMsg = "Webcam não está ativa.";
                return;
            }

            try
            {
                Texture2D tex = BuildDetectionTexture();
                if (tex == null)
                {
                    _statusMsg = "Falha ao capturar frame.";
                    return;
                }

                LastFrameTexture = tex;
                LastDetection = detector.Detect(LastFrameTexture);

                bool hasArea = HasStage1Detection(LastDetection);
                if (hasArea)
                {
                    _statusMsg = $"keyboard_area {(LastDetection.stage1Confidence * 100f):0.0}%";
                    if (_trackingEnabled)
                        UpdateTrackingArea(LastDetection);
                }
                else
                {
                    _statusMsg = "Sem detecção de keyboard_area.";
                    if (_trackingEnabled)
                        _trackingDetection = BuildDetectionFromTrackingArea();
                }
            }
            catch (Exception ex)
            {
                _statusMsg = $"Erro na detecção: {ex.Message}";
                Debug.LogWarning("[Detector] " + ex);
            }
        }

        public void CaptureAndDetect() => RunDetection();

        public void ToggleDetection()
        {
            _detectionRunning = !_detectionRunning;
            if (_detectionRunning)
            {
                _detectionTimer = detectionIntervalSeconds;
                _statusMsg = "Detecção iniciada.";
            }
            else
            {
                _statusMsg = "Detecção pausada.";
            }
        }

        public void ToggleTracking()
        {
            _trackingEnabled = !_trackingEnabled;

            if (_trackingEnabled)
            {
                if (!_detectionRunning)
                {
                    _detectionRunning = true;
                    _detectionTimer = detectionIntervalSeconds;
                }

                if (HasStage1Detection(LastDetection))
                {
                    _trackingArea = LastDetection.boundingBox;
                    _trackingAreaValid = true;
                    _trackingDetection = BuildDetectionFromRect(_trackingArea, LastDetection.stage1Confidence, "Tracking ativo");
                }
                else if (_trackingAreaValid)
                {
                    _trackingDetection = BuildDetectionFromTrackingArea();
                }

                _statusMsg = _trackingAreaValid
                    ? "Tracking ativado. Área laranja ativa."
                    : "Tracking ativado. Aguardando primeira detecção.";
            }
            else
            {
                _trackingAreaValid = false;
                _trackingDetection = null;
                _statusMsg = "Tracking desativado.";
            }
        }

        public void ResetDetectionWorkflow()
        {
            _detectionRunning = false;
            _trackingEnabled = false;
            _trackingAreaValid = false;
            _trackingDetection = null;
            _detectionTimer = 0f;
            LastDetection = null;
            _statusMsg = "Detecção resetada.";
        }

        private void UpdateTrackingArea(DetectionResult detection)
        {
            if (detection == null || detection.boundingBox.width <= 1f || detection.boundingBox.height <= 1f)
                return;

            Rect target = detection.boundingBox;
            if (!_trackingAreaValid)
            {
                _trackingArea = target;
                _trackingAreaValid = true;
            }
            else
            {
                float t = Mathf.Clamp01(trackingSmoothFactor);
                _trackingArea = LerpRect(_trackingArea, target, t);
            }

            _trackingDetection = BuildDetectionFromRect(_trackingArea, detection.stage1Confidence, "Tracking ativo");
        }

        private Rect LerpRect(Rect current, Rect target, float t)
        {
            float xMin = Mathf.Lerp(current.xMin, target.xMin, t);
            float yMin = Mathf.Lerp(current.yMin, target.yMin, t);
            float xMax = Mathf.Lerp(current.xMax, target.xMax, t);
            float yMax = Mathf.Lerp(current.yMax, target.yMax, t);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private DetectionResult BuildDetectionFromTrackingArea()
        {
            if (!_trackingAreaValid)
                return LastDetection;

            float confidence = HasStage1Detection(LastDetection) ? LastDetection.stage1Confidence : 0.5f;
            return BuildDetectionFromRect(_trackingArea, confidence, "Tracking ativo sem nova detecção");
        }

        private DetectionResult BuildDetectionFromRect(Rect area, float confidence, string status)
        {
            int frameW = LastFrameTexture != null ? LastFrameTexture.width : requestedWidth;
            int frameH = LastFrameTexture != null ? LastFrameTexture.height : requestedHeight;
            int[] keyColumns = BuildUniformColumns(area, frameW, detector != null ? detector.keyCountForMapping : 88);

            return new DetectionResult
            {
                polygon = new[]
                {
                    new Vector2(area.xMin, area.yMin),
                    new Vector2(area.xMax, area.yMin),
                    new Vector2(area.xMax, area.yMax),
                    new Vector2(area.xMin, area.yMax)
                },
                pose = Pose.identity,
                confidence = Mathf.Clamp01(confidence),
                keyColumns = keyColumns,
                keyCount = Mathf.Max(0, keyColumns.Length - 1),
                processingTimeMs = LastDetection != null ? LastDetection.processingTimeMs : 0f,
                gradientMean = 0f,
                gradientMax = 0f,
                detectionThreshold = 0f,
                isTrackingStable = true,
                reprojectionError = 0f,
                statusMessage = status,
                boundingBox = area,
                stage1Confidence = Mathf.Clamp01(confidence),
                stage1CandidateCenters = LastDetection != null ? LastDetection.stage1CandidateCenters : Array.Empty<Vector2>(),
                stage1CandidateBoxes = LastDetection != null ? LastDetection.stage1CandidateBoxes : Array.Empty<Rect>(),
                stage1CandidateScores = LastDetection != null ? LastDetection.stage1CandidateScores : Array.Empty<float>(),
                stage1CandidateCount = LastDetection != null ? LastDetection.stage1CandidateCount : 0,
                modelBackend = LastDetection != null ? LastDetection.modelBackend : ConfigService.OnnxCandidateABackend,
                inferenceTimeMs = LastDetection != null ? LastDetection.inferenceTimeMs : 0f,
                stage2KeyCountRaw = 0,
                periodicityScore = 0f,
                stage2Roi = area,
                stage2PeaksRaw = Array.Empty<int>(),
                stage2ExpectedKeyCount = detector != null ? detector.keyCountForMapping : 88
            };
        }

        private int[] BuildUniformColumns(Rect area, int frameWidth, int keyCount)
        {
            int count = Mathf.Max(1, keyCount);
            int xMin = Mathf.Clamp(Mathf.FloorToInt(area.xMin), 0, Mathf.Max(0, frameWidth - 1));
            int xMax = Mathf.Clamp(Mathf.CeilToInt(area.xMax), xMin + 1, Mathf.Max(xMin + 1, frameWidth));
            float span = Mathf.Max(1f, xMax - xMin);
            float step = span / count;
            int[] cols = new int[count + 1];
            cols[0] = xMin;
            for (int i = 1; i < count; i++)
                cols[i] = Mathf.Clamp(Mathf.RoundToInt(xMin + (i * step)), cols[i - 1] + 1, xMax - (count - i));
            cols[count] = xMax;
            return cols;
        }

        private bool HasStage1Detection(DetectionResult detection)
            => detection != null && detection.boundingBox.width > 1f && detection.boundingBox.height > 1f;

        private DetectionResult GetOverlayDetection()
        {
            if (_trackingEnabled && _trackingAreaValid && _trackingDetection != null)
                return _trackingDetection;

            return LastDetection;
        }

        private DetectionResult GetGameplayDetection()
        {
            if (_trackingEnabled && _trackingAreaValid)
                return BuildDetectionFromTrackingArea();

            return LastDetection;
        }

        private Texture2D BuildDetectionTexture()
        {
            int tw = Mathf.Min(detectionWidth, _webcam.width);
            float ratio = (float)tw / Mathf.Max(1, _webcam.width);
            int th = Mathf.Max(1, Mathf.RoundToInt(_webcam.height * ratio));

            if (_detectRT == null || _detectRT.width != tw || _detectRT.height != th)
            {
                ReleaseGpuResources();
                _detectRT = new RenderTexture(tw, th, 0, RenderTextureFormat.ARGB32);
                _detectRT.Create();
            }

            Graphics.Blit(_webcam, _detectRT);

            if (_detectTex == null || _detectTex.width != tw || _detectTex.height != th)
                _detectTex = new Texture2D(tw, th, TextureFormat.RGB24, false);

            var prevRT = RenderTexture.active;
            RenderTexture.active = _detectRT;
            _detectTex.ReadPixels(new Rect(0, 0, tw, th), 0, 0, false);
            _detectTex.Apply(false);
            RenderTexture.active = prevRT;

            return _detectTex;
        }

        private void EnsureDetectionSampleWidth()
        {
            int targetWidth = detector != null ? Mathf.Max(64, detector.inferenceInputSize) : 640;
            if (detectionWidth < targetWidth)
            {
                Debug.Log($"[Webcam] Ajustando detectionWidth de {detectionWidth}px para {targetWidth}px para corresponder ao input do modelo.");
                detectionWidth = targetWidth;
            }
        }

        private void ReleaseGpuResources()
        {
            if (_detectRT != null)
            {
                _detectRT.Release();
                UnityEngine.Object.Destroy(_detectRT);
                _detectRT = null;
            }
        }

        public void RefreshMidiList()
        {
            if (string.IsNullOrEmpty(_midiFolder) || !Directory.Exists(_midiFolder))
            {
                _midiFiles = Array.Empty<string>();
                return;
            }

            try
            {
                _midiFiles = Directory.GetFiles(_midiFolder, "*.mid", SearchOption.TopDirectoryOnly);
                if (_midiSelIndex >= _midiFiles.Length)
                    _midiSelIndex = -1;
            }
            catch
            {
                _midiFiles = Array.Empty<string>();
            }
        }

        private void UpdateGameFps()
        {
            _fpsFrames++;
            _fpsTimer += Time.unscaledDeltaTime;
            if (_fpsTimer >= 0.5f)
            {
                CurrentFPS = _fpsFrames / _fpsTimer;
                _fpsTimer = 0f;
                _fpsFrames = 0;
            }
        }

        private void UpdateCameraFps()
        {
            if (_webcam != null && _webcam.isPlaying && _webcam.didUpdateThisFrame)
                _cameraFrames++;

            _cameraFpsTimer += Time.unscaledDeltaTime;
            if (_cameraFpsTimer >= 0.5f)
            {
                CameraFPS = _cameraFrames / _cameraFpsTimer;
                _cameraFrames = 0;
                _cameraFpsTimer = 0f;
            }
        }

        void OnGUI()
        {
            InitStyles();
            DrawCameraBackground();
            if (_uiMinimized)
                DrawMinimizedButton();
            else
            {
                _windowRect = GUI.Window(42, _windowRect, DrawPanelWindow, "Piano AR - Setup");
                GUI.BringWindowToFront(42);
            }
        }

        private void DrawCameraBackground()
        {
            if (_webcam == null || !_webcam.isPlaying)
                return;

            float aspect = (float)_webcam.width / Mathf.Max(1, _webcam.height);
            float drawW = Screen.width;
            float drawH = drawW / aspect;
            if (drawH > Screen.height)
            {
                drawH = Screen.height;
                drawW = drawH * aspect;
            }

            float ox = (Screen.width - drawW) * 0.5f;
            float oy = (Screen.height - drawH) * 0.5f;

            GUI.DrawTexture(new Rect(ox, oy, drawW, drawH), _webcam, ScaleMode.ScaleToFit, false);

            if (LastFrameTexture == null)
                return;

            float texW = Mathf.Max(1f, LastFrameTexture.width);
            float texH = Mathf.Max(1f, LastFrameTexture.height);
            float scaleX = drawW / texW;
            float scaleY = drawH / texH;

            DetectionResult raw = LastDetection;
            if (showDetectionOverlay && HasStage1Detection(raw))
            {
                Rect screenRect = FrameRectToScreenRect(raw.boundingBox, texH, ox, oy, scaleX, scaleY);
                DrawBox(screenRect, new Color(0.15f, 1f, 0.25f, 0.92f), 2f);
                GUI.color = Color.white;
                GUI.Label(
                    new Rect(screenRect.x, Mathf.Max(0f, screenRect.y - 22f), 280f, 22f),
                    $"keyboard_area {raw.stage1Confidence * 100f:0.0}%");
            }

            if (showTrackingOverlay && _trackingEnabled && _trackingAreaValid)
            {
                Rect screenRect = FrameRectToScreenRect(_trackingArea, texH, ox, oy, scaleX, scaleY);
                DrawBox(screenRect, new Color(1f, 0.55f, 0.15f, 0.95f), 3f);
                GUI.color = Color.white;
                GUI.Label(
                    new Rect(screenRect.x, Mathf.Max(0f, screenRect.y - 22f), 280f, 22f),
                    "tracking area");
            }

            GUI.color = Color.white;
        }

        private void DrawBox(Rect rect, Color color, float thickness)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y + rect.height - thickness, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x + rect.width - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private Rect FrameRectToScreenRect(Rect frameRect, float frameHeight, float originX, float originY, float scaleX, float scaleY)
        {
            float x = originX + frameRect.xMin * scaleX;
            float yTop = originY + (frameHeight - frameRect.yMax) * scaleY;
            float width = frameRect.width * scaleX;
            float height = frameRect.height * scaleY;
            return new Rect(x, yTop, width, height);
        }

        private void DrawMinimizedButton()
        {
            if (GUI.Button(new Rect(Screen.width - 130, 6, 120, 28), "▲ Piano AR"))
                _uiMinimized = false;
        }

        private void DrawPanelWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"FPS jogo: {CurrentFPS:0.0}", GUILayout.Width(110));
            GUILayout.Label($"FPS câmera: {CameraFPS:0.0}", GUILayout.Width(125));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("▼", GUILayout.Width(26)))
                _uiMinimized = true;
            GUILayout.EndHorizontal();

            string[] tabs = { "🎥 Câmera", "🎹 Detecção", "🎵 MIDI", "▶ Jogar" };
            _activeTab = GUILayout.Toolbar(_activeTab, tabs);
            GUILayout.Space(4);

            float scrollHeight = Mathf.Max(120f, PANEL_H - 168f);
            _panelScroll = GUILayout.BeginScrollView(_panelScroll, GUILayout.Height(scrollHeight));

            switch (_activeTab)
            {
                case 0:
                    DrawTabCamera();
                    break;
                case 1:
                    DrawTabDetection();
                    break;
                case 2:
                    DrawTabMidi();
                    break;
                case 3:
                    DrawTabPlay();
                    break;
            }

            GUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            GUILayout.Box(_statusMsg, GUILayout.ExpandWidth(true));
            GUI.DragWindow(new Rect(0, 0, PANEL_W, 22));
        }

        private void DrawTabCamera()
        {
            GUILayout.Label("1) Selecione e inicie a câmera:", _titleStyle);
            GUILayout.Space(4);

            var devices = WebCamTexture.devices;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀", GUILayout.Width(28)))
                SwitchDevice(-1);
            string devName = devices.Length > 0 ? devices[_deviceIndex].name : "(nenhuma)";
            GUILayout.Label(devName, GUI.skin.box, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("▶", GUILayout.Width(28)))
                SwitchDevice(1);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            if (GUILayout.Button("Reiniciar Câmera"))
            {
                StopWebcam();
                ReleaseGpuResources();
                StartWebcam();
            }

            GUILayout.Space(10);
            bool camOk = _webcam != null && _webcam.isPlaying && _webcam.width > 16;
            GUILayout.Label(
                camOk
                    ? $"OK Câmera ativa {_webcam.width}x{_webcam.height} | FPS câmera {CameraFPS:0.0}"
                    : "Câmera inativa",
                camOk ? _greenStyle : _redStyle);

            GUILayout.Space(10);
            GUILayout.Label("Resolução de detecção:");
            int maxDetectionWidth = _webcam != null && _webcam.width > 16
                ? _webcam.width
                : Mathf.Max(requestedWidth, 640);
            detectionWidth = Mathf.Clamp(detectionWidth, 64, maxDetectionWidth);
            detectionWidth = Mathf.RoundToInt(GUILayout.HorizontalSlider(detectionWidth, 64, maxDetectionWidth));
            GUILayout.Label($"  {detectionWidth}px de largura");
            if (detector != null)
                GUILayout.Label($"  Modelo ONNX espera {detector.inferenceInputSize}px", _wrapLabelStyle);

            GUILayout.Space(8);
            GUILayout.Box("Dica: deixe o teclado inteiro visível e com boa iluminação.", _wrapBoxStyle);
        }

        private void DrawTabDetection()
        {
            GUILayout.Label("2) Detecte o teclado do piano:", _titleStyle);
            GUILayout.Space(4);

            GUILayout.Box("Fluxo simples: detecção ONNX contínua com caixa verde e confiança.", _wrapBoxStyle);

            GUILayout.Space(6);
            if (detector != null)
            {
                GUILayout.Label("Limiar de confiança ONNX:");
                detector.onnxConfidenceThreshold = GUILayout.HorizontalSlider(detector.onnxConfidenceThreshold, 0.10f, 0.95f);
                GUILayout.Label($"  {(detector.onnxConfidenceThreshold * 100f):0}%");

                GUILayout.Space(6);
                GUILayout.Label("Quantidade de teclas para mapeamento:");
                detector.keyCountForMapping = Mathf.RoundToInt(GUILayout.HorizontalSlider(detector.keyCountForMapping, 24f, 128f));
                GUILayout.Label($"  {detector.keyCountForMapping} teclas");

                if (detector.onnxModel == null)
                {
                    GUILayout.Label("Nenhum ModelAsset carregado no PianoDetector.", _redStyle);
                    if (!string.IsNullOrWhiteSpace(detector.LastOnnxResolveError))
                        GUILayout.Label(detector.LastOnnxResolveError, _redStyle);
                }
                else
                {
                    GUILayout.Label($"Modelo: {detector.onnxModel.name}", _greenStyle);
                }
            }

            GUILayout.Space(10);
            bool camOk = _webcam != null && _webcam.isPlaying && _webcam.width > 16;
            GUI.enabled = camOk;

            if (GUILayout.Button(_detectionRunning ? "Parar detecção" : "Iniciar detecção", _bigBtnStyle))
                ToggleDetection();

            if (GUILayout.Button(_trackingEnabled ? "Desativar Tracking" : "Tracking", _bigBtnStyle))
                ToggleTracking();

            if (GUILayout.Button("Detectar agora", _bigBtnStyle))
                RunDetection();

            GUI.enabled = true;

            GUILayout.Space(6);
            showDetectionOverlay = GUILayout.Toggle(showDetectionOverlay, "Mostrar caixa verde da detecção");
            showTrackingOverlay = GUILayout.Toggle(showTrackingOverlay, "Mostrar área laranja de tracking");

            GUILayout.Space(8);
            if (LastDetection != null)
            {
                bool areaOk = HasStage1Detection(LastDetection);
                GUILayout.Label(areaOk
                    ? $"OK keyboard_area {(LastDetection.stage1Confidence * 100f):0.0}%"
                    : LastDetection.statusMessage,
                    areaOk ? _greenStyle : _redStyle);
                GUILayout.Label($"Tempo total: {LastDetection.processingTimeMs:0.0} ms | Inferência: {LastDetection.inferenceTimeMs:0.0} ms", _wrapLabelStyle);
                GUILayout.Label($"Backend: {LastDetection.modelBackend}", _wrapLabelStyle);
            }
            else
            {
                GUILayout.Label("Aguardando detecção...");
            }

            GUILayout.Label(_trackingAreaValid
                ? "Tracking ativo: área laranja será usada no jogo e atualizada conforme a detecção."
                : "Tracking inativo.", _trackingAreaValid ? _greenStyle : _redStyle);

            GUILayout.Space(4);
            if (GUILayout.Button("Resetar detecção/tracking"))
                ResetDetectionWorkflow();
        }

        private void DrawTabMidi()
        {
            GUILayout.Label("3) Selecione uma música MIDI:", _titleStyle);
            GUILayout.Space(4);

            GUILayout.Label("Pasta de músicas:");
            GUILayout.Label(string.IsNullOrEmpty(_midiFolder) ? "(não configurada)" : _midiFolder, GUI.skin.textArea, GUILayout.Height(36));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Abrir Pasta"))
            {
                if (Directory.Exists(_midiFolder))
                    Application.OpenURL("file://" + _midiFolder.Replace("\\", "/"));
                else
                    _statusMsg = $"Pasta não existe: {_midiFolder}";
            }
            if (GUILayout.Button("Atualizar"))
                RefreshMidiList();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label($"Arquivos .mid encontrados: {_midiFiles.Length}");

            if (_midiFiles.Length == 0)
            {
                GUILayout.Space(4);
                GUILayout.Box("Coloque arquivos .mid na pasta acima.", _wrapBoxStyle);
            }
            else
            {
                _midiScroll = GUILayout.BeginScrollView(_midiScroll, GUILayout.Height(170));
                for (int i = 0; i < _midiFiles.Length; i++)
                {
                    bool selected = i == _midiSelIndex;
                    string label = Path.GetFileNameWithoutExtension(_midiFiles[i]);
                    if (GUILayout.Toggle(selected, " " + label) && !selected)
                    {
                        _midiSelIndex = i;
                        SelectedMidiPath = _midiFiles[i];
                        _statusMsg = $"MIDI: {label}";
                    }
                }
                GUILayout.EndScrollView();
            }
        }

        private void DrawTabPlay()
        {
            GUILayout.Label("4) Tudo pronto? Inicie o jogo!", _titleStyle);
            GUILayout.Space(8);

            bool camOk = _webcam != null && _webcam.isPlaying && _webcam.width > 16;
            bool midiOk = !string.IsNullOrEmpty(SelectedMidiPath) && File.Exists(SelectedMidiPath);
            bool trackingOk = _trackingEnabled && _trackingAreaValid;
            DetectionResult playDetection = GetGameplayDetection();

            DrawChecklist("Câmera ativa", camOk);
            DrawChecklist("Área de tracking ativa", trackingOk);
            DrawChecklist(midiOk ? $"MIDI ({Path.GetFileNameWithoutExtension(SelectedMidiPath)})" : "MIDI selecionado", midiOk);

            GUILayout.Space(14);
            bool canStart = camOk && midiOk && trackingOk;
            GUI.enabled = canStart;
            if (GUILayout.Button(canStart ? "INICIAR JOGO" : "INICIAR JOGO (complete os itens acima)", _bigBtnStyle))
                OnStartGameRequested?.Invoke(SelectedMidiPath, playDetection);
            GUI.enabled = true;

            if (!camOk) GUILayout.Label("- Vá para Câmera e inicie a webcam.", _redStyle);
            if (!trackingOk) GUILayout.Label("- Vá para Detecção, clique em Iniciar detecção e depois Tracking.", _redStyle);
            if (!midiOk) GUILayout.Label("- Vá para MIDI e selecione uma música.", _redStyle);
        }

        private void DrawChecklist(string label, bool ok)
            => GUILayout.Label((ok ? "OK " : "X ") + label, ok ? _greenStyle : _redStyle);

        private void InitStyles()
        {
            if (_stylesReady)
                return;

            _titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 12 };
            _greenStyle = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(0.2f, 0.95f, 0.35f) } };
            _redStyle = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(1f, 0.3f, 0.2f) } };
            _bigBtnStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 13, fixedHeight = 38, wordWrap = true };
            _wrapLabelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            _wrapBoxStyle = new GUIStyle(GUI.skin.box) { wordWrap = true, alignment = TextAnchor.MiddleLeft };
            _stylesReady = true;
        }
    }
}
