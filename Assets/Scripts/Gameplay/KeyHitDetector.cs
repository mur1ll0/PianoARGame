using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using PianoARGame.AR;
using PianoARGame.Midi;

namespace PianoARGame.Gameplay
{
    /// <summary>
    /// Detecta pressões de tecla via mouse (Editor) ou toque (mobile) e emite
    /// <see cref="OnKeyHit"/> com índice da tecla, offset de tempo (ms) e
    /// banda de precisão ("Perfect", "Good", "Miss").
    /// </summary>
    public class KeyHitDetector : MonoBehaviour
    {
        // -----------------------------------------------------------------------
        // Eventos e constantes
        // -----------------------------------------------------------------------

        /// <summary>Disparado quando o usuário pressiona uma tecla.
        /// Parâmetros: keyIndex, offsetMs (+ = tarde, - = adiantado), accuracy ("Perfect"/"Good"/"Miss").</summary>
        public event Action<int, float, string> OnKeyHit;

        private const float PerfectWindowMs = 80f;
        private const float GoodWindowMs    = 160f;

        // -----------------------------------------------------------------------
        // Campos serializados
        // -----------------------------------------------------------------------

        [Header("Configuração")]
        [Tooltip("Câmera usada para converter toque em Ray. Se nulo, usa Camera.main.")]
        [SerializeField] private Camera inputCamera;

        [Tooltip("Layer mask para os colliders de tecla.")]
        [SerializeField] private LayerMask keyLayerMask = Physics.DefaultRaycastLayers;

        [Tooltip("Distância máxima do raycast.")]
        [SerializeField] private float raycastMaxDistance = 10f;

        // -----------------------------------------------------------------------
        // Estado de runtime
        // -----------------------------------------------------------------------

        private KeyInfo[] _keys;
        private double _songStartDspTime;
        private List<MappedNote> _schedule;

        // Para cada keyIndex guarda o MappedNote mais próximo do tempo atual
        // (o que será "julgado" quando o usuário tocar)
        private readonly Dictionary<int, double> _pendingHitTimes = new();

        // -----------------------------------------------------------------------
        // API pública
        // -----------------------------------------------------------------------

        /// <summary>Inicializa o detector com as notas e teclas da música atual.</summary>
        public void StartSession(List<MappedNote> schedule, KeyInfo[] keys, double songStartDspTime)
        {
            _schedule         = schedule;
            _keys             = keys;
            _songStartDspTime = songStartDspTime;
            _pendingHitTimes.Clear();

            // Indexa a primeira ocorrência de cada keyIndex para julgamento rápido
            if (schedule != null)
                foreach (var mn in schedule)
                    if (!_pendingHitTimes.ContainsKey(mn.keyIndex))
                        _pendingHitTimes[mn.keyIndex] = mn.noteEvent.time;
        }

        public void StopSession()
        {
            _schedule = null;
            _pendingHitTimes.Clear();
        }

        // -----------------------------------------------------------------------
        // MonoBehaviour
        // -----------------------------------------------------------------------

        private void Update()
        {
            if (_keys == null || _keys.Length == 0) return;

#if UNITY_EDITOR || UNITY_STANDALONE
            HandleMouseInput();
#else
            HandleTouchInput();
#endif
        }

        // -----------------------------------------------------------------------
        // Input handling
        // -----------------------------------------------------------------------

        private void HandleMouseInput()
        {
            if (!Input.GetMouseButtonDown(0)) return;
            if (IsPointerOverUI()) return;

            Camera cam = ResolveCamera();
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            TryHit(ray);
        }

        private void HandleTouchInput()
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch t = Input.GetTouch(i);
                if (t.phase != TouchPhase.Began) continue;
                if (IsPointerOverUI(t.fingerId)) continue;

                Camera cam = ResolveCamera();
                Ray ray = cam.ScreenPointToRay(t.position);
                TryHit(ray);
            }
        }

        private void TryHit(Ray ray)
        {
            if (Physics.Raycast(ray, out RaycastHit hit, raycastMaxDistance, keyLayerMask))
            {
                var marker = hit.collider.GetComponentInParent<KeyColliderMarker>();
                if (marker != null)
                    EvaluateHit(marker.keyIndex);
            }
        }

        private void EvaluateHit(int keyIndex)
        {
            double nowSong = AudioSettings.dspTime - _songStartDspTime; // tempo atual relativo à música
            float offsetMs;
            string accuracy;

            if (_pendingHitTimes.TryGetValue(keyIndex, out double expectedTime))
            {
                offsetMs = (float)((nowSong - expectedTime) * 1000.0);
                float absOffset = Mathf.Abs(offsetMs);

                if (absOffset <= PerfectWindowMs)
                    accuracy = "Perfect";
                else if (absOffset <= GoodWindowMs)
                    accuracy = "Good";
                else
                    accuracy = "Miss";

                // Avança para a próxima nota pendente nessa tecla
                AdvancePendingHit(keyIndex, expectedTime);
            }
            else
            {
                offsetMs = 999f;
                accuracy = "Miss";
            }

            OnKeyHit?.Invoke(keyIndex, offsetMs, accuracy);
        }

        private void AdvancePendingHit(int keyIndex, double usedTime)
        {
            // Procura a próxima nota para esse keyIndex com tempo maior
            if (_schedule == null) return;

            double nextTime = double.MaxValue;
            bool found = false;
            foreach (var mn in _schedule)
            {
                if (mn.keyIndex == keyIndex && mn.noteEvent.time > usedTime)
                {
                    if (mn.noteEvent.time < nextTime)
                    {
                        nextTime = mn.noteEvent.time;
                        found = true;
                    }
                }
            }

            if (found)
                _pendingHitTimes[keyIndex] = nextTime;
            else
                _pendingHitTimes.Remove(keyIndex);
        }

        // -----------------------------------------------------------------------
        // Utilitários
        // -----------------------------------------------------------------------

        private Camera ResolveCamera() => inputCamera != null ? inputCamera : Camera.main;

        private static bool IsPointerOverUI()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private static bool IsPointerOverUI(int fingerId)
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(fingerId);
        }
    }

    /// <summary>
    /// Marcador leve colocado no GameObject de collider de cada tecla para identificar seu índice.
    /// Adicione este componente ao prefab de tecla e configure <see cref="keyIndex"/>.
    /// </summary>
    public class KeyColliderMarker : MonoBehaviour
    {
        public int keyIndex;
    }
}
