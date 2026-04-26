using System;
using System.IO;
using UnityEngine;
using PianoARGame.Services;

namespace PianoARGame.AR
{
    public enum DetectionMode { Continuous, Snapshot }

    /// <summary>
    /// Serviço de captura de webcam com detecção de piano.
    /// Melhorias v2: GPU blit para detecção rápida (320px), câmera desenhada antes da UI,
    /// painel overlay com abas (Câmera / Detecção / MIDI / Jogar), modo Snapshot.
    /// </summary>
    public class TestWebcamController : MonoBehaviour
    {
        // ── Inspector ──────────────────────────────────────────────────────────

        [Header("Webcam")]
        [Tooltip("Nome do dispositivo. Deixe vazio para usar o primeiro disponível.")]
        public string webcamName = "";
        [Range(15, 60)] public int requestedFPS = 30;
        public int requestedWidth  = 1280;
        public int requestedHeight = 720;

        [Header("Detecção")]
        public PianoDetector detector;
        public ConfigService configService;
        public DetectionMode detectionMode = DetectionMode.Snapshot;
        [Tooltip("Frames entre cada detecção no modo Contínua.")]
        [Range(10, 120)] public int continuousIntervalFrames = 30;
        [Tooltip("Largura máxima (px) da imagem enviada ao detector. Menor = mais rápido.")]
        [Range(64, 640)] public int detectionWidth = 320;

        // ── API Pública ────────────────────────────────────────────────────────

        public Texture2D LastFrameTexture { get; private set; }
        public DetectionResult LastDetection { get; private set; }
        public bool IsDetectionLocked { get; private set; }
        public float CurrentFPS { get; private set; }
        public string SelectedMidiPath { get; private set; }
        public event Action<string, DetectionResult> OnStartGameRequested;
        public bool DrawFullscreen { get; set; }

        // ── Estado privado ─────────────────────────────────────────────────────

        private WebCamTexture _webcam;
        private RenderTexture _detectRT;
        private Texture2D     _detectTex;
        private int  _deviceIndex;
        private int  _frameCounter;
        private float _fpsTimer;
        private int   _fpsFrames;

        private Rect    _windowRect   = new Rect(10, 10, PANEL_W, PANEL_H);
        private bool    _uiMinimized;
        private int     _activeTab;
        private string[] _midiFiles   = Array.Empty<string>();
        private int     _midiSelIndex = -1;
        private Vector2 _midiScroll;
        private string  _midiFolder   = "";
        private string  _statusMsg    = "Aguardando…";

        private GUIStyle _titleStyle;
        private GUIStyle _greenStyle;
        private GUIStyle _redStyle;
        private GUIStyle _bigBtnStyle;
        private bool     _stylesReady;

        private const float PANEL_W = 340f;
        private const float PANEL_H = 510f;

        // ── MonoBehaviour ──────────────────────────────────────────────────────

        void Awake()
        {
            if (configService != null)
                _midiFolder = configService.GetMusicFolderPath();
        }

        void Start()
        {
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
            UpdateFPS();

            if (_webcam == null || !_webcam.isPlaying || _webcam.width <= 16) return;

            if (detectionMode == DetectionMode.Continuous && !IsDetectionLocked)
            {
                if (++_frameCounter >= continuousIntervalFrames)
                {
                    _frameCounter = 0;
                    RunDetection();
                }
            }
        }

        // ── Gerenciamento de câmera ────────────────────────────────────────────

        private void StartWebcam()
        {
            var devices = WebCamTexture.devices;
            if (devices.Length == 0)
            {
                _statusMsg = "⚠ Nenhuma webcam encontrada.";
                Debug.LogWarning("[Webcam] Nenhum dispositivo de câmera disponível.");
                return;
            }

            _deviceIndex = FindDeviceIndex(devices);
            webcamName   = devices[_deviceIndex].name;
            _webcam      = new WebCamTexture(webcamName, requestedWidth, requestedHeight, requestedFPS);
            _webcam.Play();
            _statusMsg = $"Câmera iniciada: {webcamName}";
            Debug.Log($"[Webcam] Dispositivo: {webcamName} ({requestedWidth}×{requestedHeight} @{requestedFPS}fps)");
        }

        private void StopWebcam()
        {
            if (_webcam != null && _webcam.isPlaying) _webcam.Stop();
        }

        private int FindDeviceIndex(WebCamDevice[] devices)
        {
            if (!string.IsNullOrEmpty(webcamName))
                for (int i = 0; i < devices.Length; i++)
                    if (devices[i].name.IndexOf(webcamName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return i;
            return 0;
        }

        public void SwitchDevice(int direction)
        {
            var devices = WebCamTexture.devices;
            if (devices.Length == 0) return;
            _deviceIndex = (_deviceIndex + direction + devices.Length) % devices.Length;
            webcamName   = devices[_deviceIndex].name;
            StopWebcam();
            ReleaseGpuResources();
            StartWebcam();
        }

        public void SetWebcamByName(string name)  { webcamName = name; StopWebcam(); StartWebcam(); }
        public void SetWebcamByIndex(int index)
        {
            var d = WebCamTexture.devices;
            if (d.Length == 0) return;
            _deviceIndex = Mathf.Clamp(index, 0, d.Length - 1);
            webcamName   = d[_deviceIndex].name;
            StopWebcam();
            StartWebcam();
        }
        public string GetCurrentDeviceName() => _webcam?.deviceName ?? webcamName;
        public void ToggleFullscreen() { DrawFullscreen = !DrawFullscreen; }
        public void SetFullscreen(bool v) { DrawFullscreen = v; }

        // ── Detecção ───────────────────────────────────────────────────────────

        public void RunDetection()
        {
            if (detector == null)               { _statusMsg = "⚠ PianoDetector não atribuído."; return; }
            if (_webcam == null || !_webcam.isPlaying || _webcam.width <= 16)
                                                { _statusMsg = "⚠ Webcam não está ativa."; return; }
            try
            {
                Texture2D tex = BuildDetectionTexture();
                if (tex == null) { _statusMsg = "⚠ Falha ao capturar frame."; return; }

                LastFrameTexture = tex;
                LastDetection    = detector.Detect(LastFrameTexture);

                if (detectionMode == DetectionMode.Snapshot)
                    IsDetectionLocked = true;

                bool ok = LastDetection != null && LastDetection.confidence >= detector.minConfidence;
                _statusMsg = ok
                    ? $"✓ {LastDetection.keyCount} teclas  conf:{LastDetection.confidence:0.00}  {LastDetection.processingTimeMs:0.0}ms"
                    : $"✗ {LastDetection?.statusMessage ?? "sem resultado"}  conf:{LastDetection?.confidence:0.00}";
            }
            catch (Exception ex)
            {
                _statusMsg = $"⚠ Erro na detecção: {ex.Message}";
                Debug.LogWarning("[Detector] " + ex);
            }
        }

        public void CaptureAndDetect() => RunDetection();

        public void UnlockDetection()
        {
            IsDetectionLocked = false;
            LastDetection     = null;
            _statusMsg        = "Detecção desbloqueada.";
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

        private void ReleaseGpuResources()
        {
            if (_detectRT != null)
            {
                _detectRT.Release();
                UnityEngine.Object.Destroy(_detectRT);
                _detectRT = null;
            }
        }

        // ── Lista MIDI ─────────────────────────────────────────────────────────

        public void RefreshMidiList()
        {
            if (string.IsNullOrEmpty(_midiFolder) || !Directory.Exists(_midiFolder))
            { _midiFiles = Array.Empty<string>(); return; }
            try
            {
                _midiFiles = Directory.GetFiles(_midiFolder, "*.mid", SearchOption.TopDirectoryOnly);
                if (_midiSelIndex >= _midiFiles.Length) _midiSelIndex = -1;
            }
            catch { _midiFiles = Array.Empty<string>(); }
        }

        // ── FPS ────────────────────────────────────────────────────────────────

        private void UpdateFPS()
        {
            _fpsFrames++;
            _fpsTimer += Time.unscaledDeltaTime;
            if (_fpsTimer >= 0.5f)
            {
                CurrentFPS = _fpsFrames / _fpsTimer;
                _fpsTimer  = 0f;
                _fpsFrames = 0;
            }
        }

        // ── OnGUI ──────────────────────────────────────────────────────────────

        void OnGUI()
        {
            InitStyles();
            DrawCameraBackground();
            if (_uiMinimized)
                DrawMinimizedButton();
            else
            {
                _windowRect = GUI.Window(42, _windowRect, DrawPanelWindow, "🎹 Piano AR — Setup");
                GUI.BringWindowToFront(42);
            }
        }

        private void DrawCameraBackground()
        {
            if (_webcam == null || !_webcam.isPlaying) return;

            float aspect = (float)_webcam.width / Mathf.Max(1, _webcam.height);
            float drawW  = Screen.width;
            float drawH  = drawW / aspect;
            if (drawH > Screen.height) { drawH = Screen.height; drawW = drawH * aspect; }
            float ox = (Screen.width  - drawW) * 0.5f;
            float oy = (Screen.height - drawH) * 0.5f;

            GUI.DrawTexture(new Rect(ox, oy, drawW, drawH), _webcam, ScaleMode.ScaleToFit, false);

            if (LastDetection?.keyColumns == null || LastDetection.keyColumns.Length == 0) return;

            float scaleX = drawW / Mathf.Max(1, _webcam.width);
            bool  isGood = LastDetection.confidence >= (detector != null ? detector.minConfidence : 0.5f);
            var   prev   = GUI.color;
            GUI.color = isGood ? new Color(0.1f, 1f, 0.4f, 0.85f) : new Color(1f, 0.4f, 0.1f, 0.85f);
            foreach (int col in LastDetection.keyColumns)
                GUI.DrawTexture(new Rect(ox + col * scaleX - 1f, oy, 2f, drawH), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        private void DrawMinimizedButton()
        {
            if (GUI.Button(new Rect(Screen.width - 130, 6, 120, 28), "▲ Piano AR"))
                _uiMinimized = false;
        }

        private void DrawPanelWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"FPS: {CurrentFPS:0.0}", GUILayout.Width(72));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("▼", GUILayout.Width(26))) _uiMinimized = true;
            GUILayout.EndHorizontal();

            string[] tabs = { "🎥 Câmera", "🎹 Detecção", "🎵 MIDI", "▶ Jogar" };
            _activeTab = GUILayout.Toolbar(_activeTab, tabs);
            GUILayout.Space(4);

            switch (_activeTab)
            {
                case 0: DrawTabCamera();    break;
                case 1: DrawTabDetection(); break;
                case 2: DrawTabMidi();      break;
                case 3: DrawTabPlay();      break;
            }

            GUILayout.FlexibleSpace();
            GUILayout.Box(_statusMsg, GUILayout.ExpandWidth(true));
            GUI.DragWindow(new Rect(0, 0, PANEL_W, 22));
        }

        private void DrawTabCamera()
        {
            GUILayout.Label("① Selecione e inicie a câmera:", _titleStyle);
            GUILayout.Space(4);

            var devices = WebCamTexture.devices;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀", GUILayout.Width(28))) SwitchDevice(-1);
            string devName = devices.Length > 0 ? devices[_deviceIndex].name : "(nenhuma)";
            GUILayout.Label(devName, GUI.skin.box, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("▶", GUILayout.Width(28))) SwitchDevice(1);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            if (GUILayout.Button("🔄 Reiniciar Câmera"))
            {
                StopWebcam();
                ReleaseGpuResources();
                StartWebcam();
            }

            GUILayout.Space(10);
            bool camOk = _webcam != null && _webcam.isPlaying && _webcam.width > 16;
            GUILayout.Label(
                camOk ? $"✓ Câmera ativa  {_webcam.width}×{_webcam.height} @ {CurrentFPS:0} FPS"
                      : "✗ Câmera inativa",
                camOk ? _greenStyle : _redStyle);

            GUILayout.Space(10);
            GUILayout.Label("Resolução de detecção:");
            detectionWidth = Mathf.RoundToInt(GUILayout.HorizontalSlider(detectionWidth, 64, 640));
            GUILayout.Label($"  {detectionWidth} px de largura — menor = mais rápido");

            GUILayout.Space(8);
            GUILayout.Box("💡 Aponte a câmera para o piano com boa iluminação.\n" +
                          "Certifique-se de que o teclado inteiro está visível.");
        }

        private void DrawTabDetection()
        {
            GUILayout.Label("② Detecte o teclado do piano:", _titleStyle);
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Modo:", GUILayout.Width(50));
            if (GUILayout.Toggle(detectionMode == DetectionMode.Snapshot,   " Snapshot",  GUILayout.Width(105)))
                detectionMode = DetectionMode.Snapshot;
            if (GUILayout.Toggle(detectionMode == DetectionMode.Continuous, " Contínua",  GUILayout.Width(105)))
                detectionMode = DetectionMode.Continuous;
            GUILayout.EndHorizontal();

            GUILayout.Label(detectionMode == DetectionMode.Snapshot
                ? "  Snapshot: detecta uma vez e trava o resultado (recomendado)."
                : $"  Contínua: redetecta a cada {continuousIntervalFrames} frames.");

            GUILayout.Space(8);
            bool camOk = _webcam != null && _webcam.isPlaying && _webcam.width > 16;
            GUI.enabled = camOk && !IsDetectionLocked;
            if (GUILayout.Button("📷  Detectar Agora", _bigBtnStyle)) RunDetection();
            GUI.enabled = true;

            if (IsDetectionLocked)
            {
                GUILayout.Space(4);
                if (GUILayout.Button("🔓  Desbloquear / Redetectar")) UnlockDetection();
            }

            GUILayout.Space(10);
            bool detOk = LastDetection != null && LastDetection.confidence >= (detector != null ? detector.minConfidence : 0.5f);
            if (LastDetection != null)
            {
                GUILayout.Label(
                    detOk ? $"✓ {LastDetection.keyCount} teclas detectadas"
                          : $"✗ {LastDetection.statusMessage}",
                    detOk ? _greenStyle : _redStyle);
                GUILayout.Label($"  Confiança: {LastDetection.confidence:0.00}  |  Tempo: {LastDetection.processingTimeMs:0.0} ms");
                if (IsDetectionLocked) GUILayout.Label("  🔒 Resultado travado (Snapshot)");
            }
            else GUILayout.Label("  Aguardando detecção…");

            GUILayout.Space(8);
            GUILayout.Box("💡 Use Snapshot para travar a posição antes de jogar.\n" +
                          "O teclado deve ocupar ao menos metade da imagem.");
        }

        private void DrawTabMidi()
        {
            GUILayout.Label("③ Selecione uma música MIDI:", _titleStyle);
            GUILayout.Space(4);

            GUILayout.Label("Pasta de músicas:");
            GUILayout.Label(string.IsNullOrEmpty(_midiFolder) ? "(não configurada)" : _midiFolder,
                GUI.skin.textArea, GUILayout.Height(36));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("📁 Abrir Pasta"))
            {
                if (Directory.Exists(_midiFolder))
                    Application.OpenURL("file://" + _midiFolder.Replace("\\", "/"));
                else
                    _statusMsg = $"⚠ Pasta não existe: {_midiFolder}";
            }
            if (GUILayout.Button("🔄 Atualizar")) RefreshMidiList();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label($"Arquivos .mid encontrados: {_midiFiles.Length}");

            if (_midiFiles.Length == 0)
            {
                GUILayout.Space(4);
                GUILayout.Box("💡 Coloque arquivos .mid na pasta acima.\n" +
                              "O caminho padrão é configurado no ConfigService.");
            }
            else
            {
                _midiScroll = GUILayout.BeginScrollView(_midiScroll, GUILayout.Height(170));
                for (int i = 0; i < _midiFiles.Length; i++)
                {
                    bool sel  = (i == _midiSelIndex);
                    string lbl = Path.GetFileNameWithoutExtension(_midiFiles[i]);
                    if (GUILayout.Toggle(sel, " " + lbl) && !sel)
                    {
                        _midiSelIndex    = i;
                        SelectedMidiPath = _midiFiles[i];
                        _statusMsg       = $"🎵 MIDI: {lbl}";
                    }
                }
                GUILayout.EndScrollView();
            }
        }

        private void DrawTabPlay()
        {
            GUILayout.Label("④ Tudo pronto? Inicie o jogo!", _titleStyle);
            GUILayout.Space(8);

            bool camOk  = _webcam != null && _webcam.isPlaying && _webcam.width > 16;
            bool detOk  = LastDetection != null && LastDetection.confidence >= (detector != null ? detector.minConfidence : 0.5f);
            bool midiOk = !string.IsNullOrEmpty(SelectedMidiPath) && File.Exists(SelectedMidiPath);

            DrawChecklist("Câmera ativa", camOk);
            DrawChecklist(detOk ? $"Piano detectado  ({LastDetection.keyCount} teclas)" : "Piano detectado", detOk);
            DrawChecklist(midiOk ? $"MIDI  ({Path.GetFileNameWithoutExtension(SelectedMidiPath)})" : "MIDI selecionado", midiOk);

            GUILayout.Space(14);
            GUI.enabled = camOk && detOk && midiOk;
            if (GUILayout.Button(
                    (camOk && detOk && midiOk) ? "▶  INICIAR JOGO" : "▶  INICIAR JOGO  (complete os itens acima)",
                    _bigBtnStyle))
                OnStartGameRequested?.Invoke(SelectedMidiPath, LastDetection);
            GUI.enabled = true;

            if (!camOk)  GUILayout.Label("  • Vá para 🎥 Câmera e inicie a câmera.", _redStyle);
            if (!detOk)  GUILayout.Label("  • Vá para 🎹 Detecção e detecte o teclado.", _redStyle);
            if (!midiOk) GUILayout.Label("  • Vá para 🎵 MIDI e selecione uma música.", _redStyle);
        }

        private void DrawChecklist(string label, bool ok)
            => GUILayout.Label((ok ? "✓  " : "✗  ") + label, ok ? _greenStyle : _redStyle);

        private void InitStyles()
        {
            if (_stylesReady) return;
            _titleStyle  = new GUIStyle(GUI.skin.label)  { fontStyle = FontStyle.Bold, fontSize = 12 };
            _greenStyle  = new GUIStyle(GUI.skin.label)  { normal = { textColor = new Color(0.2f, 0.95f, 0.35f) } };
            _redStyle    = new GUIStyle(GUI.skin.label)  { normal = { textColor = new Color(1f,   0.3f,  0.2f)  } };
            _bigBtnStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 14, fixedHeight = 42 };
            _stylesReady = true;
        }

        [Header("Diagnósticos (opcional)")]
        public bool enableCsvLogging = false;
        public int  csvFlushEveryNFrames = 30;
    }
}
