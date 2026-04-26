using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PianoARGame.AR;
using PianoARGame.Midi;

namespace PianoARGame.Gameplay
{
    /// <summary>
    /// Gerencia o spawn e movimento de objetos visuais de nota ao longo do tempo de uma música.
    /// As notas descem em direção à tecla alvo, chegando exatamente no <c>note.time</c>.
    /// </summary>
    public class SpawnManager : MonoBehaviour
    {
        [Header("Referências")]
        [SerializeField] private GameObject notePrefab;
        [Tooltip("Transform raiz do teclado; as notas são spawnadas relativas a esse objeto.")]
        [SerializeField] private Transform keyboardRoot;

        [Header("Gameplay")]
        [Tooltip("Lead time em segundos: quanto antes do hit a nota é spawnada / quanto tempo ela leva para descer.")]
        [SerializeField] private float leadTimeSeconds = 2f;
        [Tooltip("Altura (local) de onde a nota surge acima da tecla.")]
        [SerializeField] private float spawnHeight = 2f;

        // Estado interno
        private List<MappedNote> _schedule;
        private KeyInfo[] _keys;
        private int _nextIndex;
        private double _songStartDspTime;
        private bool _isPlaying;

        // Pool de objetos ativos: keyIndex -> lista de GameObjects em movimento
        private readonly Dictionary<int, List<GameObject>> _activeNotes = new();

        public event Action<int> OnNoteReachedKey; // emitido quando uma nota chega na tecla

        // -----------------------------------------------------------------------
        // API pública
        // -----------------------------------------------------------------------

        /// <summary>Inicia a sessão de gameplay com as notas mapeadas e informações de tecla.</summary>
        public void StartSong(List<MappedNote> schedule, KeyInfo[] keys)
        {
            StopSong();

            _schedule       = schedule ?? throw new ArgumentNullException(nameof(schedule));
            _keys           = keys     ?? throw new ArgumentNullException(nameof(keys));
            _nextIndex      = 0;
            _songStartDspTime = AudioSettings.dspTime;
            _isPlaying      = true;
        }

        /// <summary>Para a música e destrói notas em movimento.</summary>
        public void StopSong()
        {
            _isPlaying = false;
            foreach (var list in _activeNotes.Values)
                foreach (var go in list)
                    if (go != null) Destroy(go);
            _activeNotes.Clear();
            _schedule = null;
        }

        // -----------------------------------------------------------------------
        // MonoBehaviour
        // -----------------------------------------------------------------------

        private void Update()
        {
            if (!_isPlaying || _schedule == null) return;

            double elapsed = AudioSettings.dspTime - _songStartDspTime;

            // Spawnar notas cujo spawnTime chegou
            while (_nextIndex < _schedule.Count && _schedule[_nextIndex].spawnTime <= elapsed)
            {
                SpawnNote(_schedule[_nextIndex]);
                _nextIndex++;
            }

            // Mover notas ativas e checar chegada
            MoveActiveNotes(elapsed);

            // Encerra quando todas as notas foram spawnadas e não há mais notas ativas
            if (_nextIndex >= _schedule.Count && AllNotesGone())
                _isPlaying = false;
        }

        // -----------------------------------------------------------------------
        // Lógica interna
        // -----------------------------------------------------------------------

        private void SpawnNote(MappedNote mapped)
        {
            if (notePrefab == null) return;
            if (mapped.keyIndex < 0 || mapped.keyIndex >= _keys.Length) return;

            KeyInfo key = _keys[mapped.keyIndex];
            Vector3 targetLocal = key.pos3D;
            Vector3 spawnLocal  = targetLocal + Vector3.up * spawnHeight;

            Transform parent = keyboardRoot != null ? keyboardRoot : transform;
            var go = Instantiate(notePrefab, parent.TransformPoint(spawnLocal), Quaternion.identity, parent);

            // Armazena metadados no próprio objeto via componente auxiliar
            var data = go.AddComponent<NoteData>();
            data.keyIndex    = mapped.keyIndex;
            data.hitDspTime  = _songStartDspTime + mapped.noteEvent.time;
            data.targetLocal = targetLocal;

            if (!_activeNotes.ContainsKey(mapped.keyIndex))
                _activeNotes[mapped.keyIndex] = new List<GameObject>();
            _activeNotes[mapped.keyIndex].Add(go);
        }

        private void MoveActiveNotes(double elapsed)
        {
            Transform parent = keyboardRoot != null ? keyboardRoot : transform;

            foreach (var list in _activeNotes.Values)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var go = list[i];
                    if (go == null) { list.RemoveAt(i); continue; }

                    var data = go.GetComponent<NoteData>();
                    if (data == null) { Destroy(go); list.RemoveAt(i); continue; }

                    double remaining = data.hitDspTime - AudioSettings.dspTime;

                    if (remaining <= 0.0)
                    {
                        // Chegou na tecla
                        OnNoteReachedKey?.Invoke(data.keyIndex);
                        Destroy(go);
                        list.RemoveAt(i);
                        continue;
                    }

                    // Interpola posição: da spawn height até o target
                    float t = Mathf.Clamp01(1f - (float)(remaining / leadTimeSeconds));
                    Vector3 spawnLocal  = data.targetLocal + Vector3.up * spawnHeight;
                    Vector3 currentLocal = Vector3.Lerp(spawnLocal, data.targetLocal, t);
                    go.transform.position = parent.TransformPoint(currentLocal);
                }
            }
        }

        private bool AllNotesGone()
        {
            foreach (var list in _activeNotes.Values)
                if (list.Count > 0) return false;
            return true;
        }
    }

    /// <summary>Componente auxiliar que carrega dados de uma nota em movimento.</summary>
    internal class NoteData : MonoBehaviour
    {
        public int keyIndex;
        public double hitDspTime;
        public Vector3 targetLocal;
    }
}
