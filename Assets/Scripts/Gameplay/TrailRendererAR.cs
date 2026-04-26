using System.Collections.Generic;
using UnityEngine;
using PianoARGame.AR;

namespace PianoARGame.Gameplay
{
    /// <summary>
    /// Gerencia trilhas visuais (LineRenderer) por tecla, ancoradas à pose estimada do teclado.
    /// Cada tecla recebe uma trilha persistente que acumula posições enquanto está ativa,
    /// e a trilha inteira segue o teclado quando a pose é atualizada.
    /// </summary>
    public class TrailRendererAR : MonoBehaviour
    {
        // -----------------------------------------------------------------------
        // Configurações serializadas
        // -----------------------------------------------------------------------

        [Header("Aparência da trilha")]
        [SerializeField] private Material trailMaterial;
        [SerializeField] private Gradient trailColorGradient;
        [SerializeField] private AnimationCurve trailWidthCurve = AnimationCurve.Linear(0f, 0.02f, 1f, 0.005f);
        [Tooltip("Número de posições máximas armazenadas por trilha.")]
        [SerializeField] private int maxTrailPositions = 60;
        [Tooltip("Comprimento máximo (m) da trilha antes de ser truncada.")]
        [SerializeField] private float maxTrailLength = 1f;

        [Header("Referências")]
        [Tooltip("Transform raiz do teclado; todas as trilhas são filhas desse objeto.")]
        [SerializeField] private Transform keyboardRoot;

        // -----------------------------------------------------------------------
        // Estado interno
        // -----------------------------------------------------------------------

        // keyIndex -> LineRenderer
        private readonly Dictionary<int, LineRenderer> _trails = new();

        // keyIndex -> posições locais acumuladas (queue)
        private readonly Dictionary<int, Queue<Vector3>> _positions = new();

        // -----------------------------------------------------------------------
        // API pública
        // -----------------------------------------------------------------------

        /// <summary>Cria (ou reutiliza) a trilha para a tecla dada.</summary>
        public void AttachToKey(KeyInfo key)
        {
            if (_trails.ContainsKey(key.index)) return;

            var go = new GameObject($"Trail_Key{key.index}");
            Transform parent = keyboardRoot != null ? keyboardRoot : transform;
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = key.pos3D;

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace    = false; // posições em espaço local do pai
            lr.material         = trailMaterial != null ? trailMaterial : CreateDefaultMaterial();
            lr.colorGradient    = trailColorGradient.colorKeys.Length > 0 ? trailColorGradient : DefaultGradient();
            lr.widthCurve       = trailWidthCurve;
            lr.positionCount    = 1;
            lr.SetPosition(0, go.transform.localPosition);
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows   = false;

            _trails[key.index]    = lr;
            _positions[key.index] = new Queue<Vector3>();
            _positions[key.index].Enqueue(go.transform.localPosition);
        }

        /// <summary>Remove e destrói a trilha de uma tecla.</summary>
        public void DetachFromKey(int keyIndex)
        {
            if (_trails.TryGetValue(keyIndex, out var lr))
            {
                if (lr != null) Destroy(lr.gameObject);
                _trails.Remove(keyIndex);
                _positions.Remove(keyIndex);
            }
        }

        /// <summary>Remove todas as trilhas.</summary>
        public void DetachAll()
        {
            foreach (var lr in _trails.Values)
                if (lr != null) Destroy(lr.gameObject);
            _trails.Clear();
            _positions.Clear();
        }

        /// <summary>
        /// Adiciona um ponto à trilha de uma tecla (chamar a cada frame em que a tecla está ativa).
        /// </summary>
        public void AddPoint(int keyIndex, Vector3 worldPosition)
        {
            if (!_trails.TryGetValue(keyIndex, out var lr)) return;

            Transform parent = keyboardRoot != null ? keyboardRoot : transform;
            Vector3 localPos = parent.InverseTransformPoint(worldPosition);

            var queue = _positions[keyIndex];
            queue.Enqueue(localPos);

            // Truncar por comprimento máximo
            float totalLength = 0f;
            Vector3[] arr = queue.ToArray();
            for (int i = arr.Length - 1; i > 0; i--)
            {
                totalLength += Vector3.Distance(arr[i], arr[i - 1]);
                if (totalLength > maxTrailLength)
                {
                    // Remove do início até que o comprimento seja aceitável
                    while (queue.Count > (arr.Length - i)) queue.Dequeue();
                    break;
                }
            }

            if (queue.Count > maxTrailPositions) queue.Dequeue();

            // Atualizar LineRenderer
            arr = queue.ToArray();
            lr.positionCount = arr.Length;
            lr.SetPositions(arr);
        }

        /// <summary>
        /// Atualiza a pose do teclado; mover o keyboardRoot já reposiciona todas as trilhas
        /// automaticamente pois são filhas. Chame este método se o root não for gerenciado
        /// externamente.
        /// </summary>
        public void UpdatePose(Pose pose)
        {
            if (keyboardRoot == null) return;
            keyboardRoot.SetPositionAndRotation(pose.position, pose.rotation);
        }

        // -----------------------------------------------------------------------
        // Utilitários privados
        // -----------------------------------------------------------------------

        private static Material CreateDefaultMaterial()
        {
            var mat = new Material(Shader.Find("Sprites/Default"));
            mat.color = new Color(0.2f, 0.8f, 1f, 0.85f);
            return mat;
        }

        private static Gradient DefaultGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(new Color(0.2f, 0.8f, 1f), 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.9f, 0.3f), new GradientAlphaKey(0f, 1f) }
            );
            return g;
        }
    }
}
