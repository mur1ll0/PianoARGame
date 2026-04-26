using UnityEngine;

namespace PianoARGame.AR
{
    /// <summary>
    /// Gerencia inicialização da sessão AR e fallback para webcam no Editor.
    /// </summary>
    public class ARSessionManager : MonoBehaviour
    {
        [Tooltip("Se true, força uso de webcam no Editor para testes")] 
        public bool useWebcamInEditor = true;

        public bool IsRunning { get; private set; }

        [Header("Optional References")]
        public TestWebcamController webcamController;

        void Start()
        {
            StartSession();
        }

        public void StartSession()
        {
            if (IsRunning)
            {
                return;
            }

#if UNITY_EDITOR
            if (useWebcamInEditor)
            {
                if (webcamController != null)
                {
                    webcamController.enabled = true;
                }

                IsRunning = true;
                Debug.Log("ARSessionManager: running in Editor webcam fallback mode.");
                return;
            }
#endif

            // Runtime AR Foundation setup hook (to be expanded with ARSession/ARPlaneManager references).
            IsRunning = true;
            Debug.Log("ARSessionManager: runtime AR session marked as running.");
        }

        public void StopSession()
        {
            if (!IsRunning)
            {
                return;
            }

#if UNITY_EDITOR
            if (useWebcamInEditor && webcamController != null)
            {
                webcamController.enabled = false;
            }
#endif

            IsRunning = false;
            Debug.Log("ARSessionManager: session stopped.");
        }
    }
}
