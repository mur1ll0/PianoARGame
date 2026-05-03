using UnityEngine;
using UnityEngine.EventSystems;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace PianoARGame.UI
{
    /// <summary>
    /// Garante compatibilidade do EventSystem com o Input System ativo.
    /// Evita erro de leitura do UnityEngine.Input quando o projeto usa somente Input System.
    /// </summary>
    public static class EventSystemInputModuleFixer
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureCompatibleInputModules()
        {
#if ENABLE_INPUT_SYSTEM
            var eventSystems = Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < eventSystems.Length; i++)
            {
                var es = eventSystems[i];
                if (es == null)
                    continue;

                // Remove legacy StandaloneInputModule when Input System is enabled.
                var legacy = es.GetComponent<StandaloneInputModule>();
                if (legacy != null)
                    Object.Destroy(legacy);

                if (es.GetComponent<InputSystemUIInputModule>() == null)
                    es.gameObject.AddComponent<InputSystemUIInputModule>();
            }
#endif
        }
    }
}
