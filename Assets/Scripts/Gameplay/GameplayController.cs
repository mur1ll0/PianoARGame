using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PianoARGame.AR;
using PianoARGame.Midi;
using PianoARGame.Services;
using PianoARGame.Gameplay;

namespace PianoARGame.Gameplay
{
    /// <summary>
    /// Controlador central da sessão de gameplay.
    /// Liga ARSessionManager → PianoDetector → KeyEstimator → MidiMapper → SpawnManager
    /// → KeyHitDetector → ScoreManager em uma única cena.
    /// </summary>
    public class GameplayController : MonoBehaviour
    {
        // -----------------------------------------------------------------------
        // Referências injetadas (via Inspector)
        // -----------------------------------------------------------------------

        [Header("Serviços AR")]
        [SerializeField] private ARSessionManager arSessionManager;
        [SerializeField] private PianoDetector pianoDetector;
        [SerializeField] private KeyEstimator keyEstimator;
        [SerializeField] private TestWebcamController webcamController;
        [SerializeField] private ConfigService configService;

        [Header("Sistemas de Gameplay")]
        [SerializeField] private SpawnManager spawnManager;
        [SerializeField] private KeyHitDetector keyHitDetector;
        [SerializeField] private TrailRendererAR trailRenderer;
        [SerializeField] private ScoreManager scoreManager;

        [Header("Configuração MIDI")]
        [SerializeField] private string midiFilePath;
        [Tooltip("Nota MIDI base (tecla índice 0). Ex: 36 = C2 para 88 teclas.")]
        [SerializeField] private int baseMidiNote = 36;
        [SerializeField] private float leadTimeSeconds = 2f;

        [Header("UI Gameplay")]
        [SerializeField] private UnityEngine.UI.Text scoreText;
        [SerializeField] private UnityEngine.UI.Text comboText;
        [SerializeField] private UnityEngine.UI.Text feedbackText;
        [SerializeField] private UnityEngine.UI.Text statusText;

        // -----------------------------------------------------------------------
        // Estado interno
        // -----------------------------------------------------------------------

        private MidiSong _song;
        private KeyInfo[] _keys;
        private List<MappedNote> _schedule;
        private bool _sessionActive;
        private int _combo;
        private double _songStartDspTime;

        // -----------------------------------------------------------------------
        // MonoBehaviour
        // -----------------------------------------------------------------------

        private void Start()
        {
            if (keyHitDetector != null)
                keyHitDetector.OnKeyHit += HandleKeyHit;
        }

        private void OnDestroy()
        {
            if (keyHitDetector != null)
                keyHitDetector.OnKeyHit -= HandleKeyHit;
        }

        // -----------------------------------------------------------------------
        // API pública — chamada pelos botões da UI
        // -----------------------------------------------------------------------

        /// <summary>Inicia o fluxo completo: detecta teclado, carrega MIDI, começa gameplay.</summary>
        public void StartGameplay()
        {
            StartCoroutine(GameplayFlow());
        }

        public void PauseGameplay()
        {
            if (spawnManager != null) spawnManager.StopSong();
            _sessionActive = false;
        }

        public void StopGameplay()
        {
            if (spawnManager != null) spawnManager.StopSong();
            if (keyHitDetector != null) keyHitDetector.StopSession();
            if (trailRenderer != null) trailRenderer.DetachAll();
            _sessionActive = false;
        }

        // -----------------------------------------------------------------------
        // Fluxo assíncrono (coroutine)
        // -----------------------------------------------------------------------

        private IEnumerator GameplayFlow()
        {
            SetStatus("Detectando teclado…");

            // 1. Obtém frame da webcam/câmera AR
            Texture2D frame = null;
            if (webcamController != null)
                frame = webcamController.LastFrameTexture;

            DetectionResult detection = null;
            if (pianoDetector != null && frame != null)
                detection = pianoDetector.Detect(frame);

            if (detection == null || detection.confidence < 0.3f)
            {
                SetStatus("Teclado não detectado. Posicione a câmera e tente novamente.");
                yield break;
            }

            // 2. Estima teclas
            int frameH = frame != null ? frame.height : 0;
            ConfigService.CalibrationProfile profile = configService != null ? configService.GetCalibrationProfile() : null;
            _keys = keyEstimator != null
                ? keyEstimator.EstimateKeys(detection, frameH, profile).ToArray()
                : System.Array.Empty<KeyInfo>();

            if (_keys.Length == 0)
            {
                SetStatus("Não foi possível estimar as teclas.");
                yield break;
            }

            SetStatus($"Teclado detectado: {_keys.Length} teclas. Carregando MIDI…");
            yield return null;

            // 3. Carrega MIDI
            if (string.IsNullOrEmpty(midiFilePath))
            {
                SetStatus("Nenhum arquivo MIDI configurado.");
                yield break;
            }

            try
            {
                _song = MidiLoader.Load(midiFilePath);
            }
            catch (System.Exception ex)
            {
                SetStatus($"Erro ao carregar MIDI: {ex.Message}");
                yield break;
            }

            // 4. Mapeia notas → teclas
            _schedule = MidiMapper.MapToKeys(_song, _keys, baseMidiNote, leadTimeSeconds);
            SetStatus($"MIDI: {_song.notes.Count} notas → {_schedule.Count} mapeadas. Iniciando…");
            yield return new WaitForSeconds(1f);

            // 5. Configura trilhas
            if (trailRenderer != null)
                foreach (var k in _keys)
                    trailRenderer.AttachToKey(k);

            // 6. Inicia sessão de score
            string songId = _song.name;
            if (scoreManager != null) scoreManager.StartSession(songId);
            _combo = 0;
            UpdateUI();

            // 7. Inicia SpawnManager e KeyHitDetector
            _songStartDspTime = AudioSettings.dspTime;

            if (spawnManager != null) spawnManager.StartSong(_schedule, _keys);
            if (keyHitDetector != null) keyHitDetector.StartSession(_schedule, _keys, _songStartDspTime);

            _sessionActive = true;
            SetStatus("Em jogo!");
        }

        // -----------------------------------------------------------------------
        // Evento de acerto
        // -----------------------------------------------------------------------

        private void HandleKeyHit(int keyIndex, float offsetMs, string accuracy)
        {
            if (!_sessionActive) return;

            int points = accuracy switch
            {
                "Perfect" => 100,
                "Good"    => 50,
                _         => 0
            };

            if (points > 0)
            {
                _combo++;
                int combo_bonus = Mathf.Min(_combo / 10, 5) * 10; // até +50 pts por combo
                points += combo_bonus;
            }
            else
            {
                _combo = 0;
            }

            if (scoreManager != null) scoreManager.AddHit(keyIndex, points);

            ShowFeedback(accuracy);
            UpdateUI();
        }

        // -----------------------------------------------------------------------
        // Utilitários de UI
        // -----------------------------------------------------------------------

        private void UpdateUI()
        {
            if (scoreText != null && scoreManager != null)
                scoreText.text = $"Score: {scoreManager.GetCurrentScore()}";
            if (comboText != null)
                comboText.text = _combo > 1 ? $"Combo x{_combo}" : "";
        }

        private void ShowFeedback(string text)
        {
            if (feedbackText == null) return;
            feedbackText.text = text;
            CancelInvoke(nameof(ClearFeedback));
            Invoke(nameof(ClearFeedback), 0.6f);
        }

        private void ClearFeedback()
        {
            if (feedbackText != null) feedbackText.text = "";
        }

        private void SetStatus(string msg)
        {
            if (statusText != null) statusText.text = msg;
            Debug.Log($"[GameplayController] {msg}");
        }
    }
}
