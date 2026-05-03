using UnityEngine;

namespace PianoARGame.Parity
{
    public static class ArPianoParityBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureGameObjectExists()
        {
            if (Object.FindAnyObjectByType<ArPianoParityGame>() != null)
            {
                return;
            }

            var go = new GameObject("AR Piano Parity Game");
            go.AddComponent<ArPianoParityGame>();
            Object.DontDestroyOnLoad(go);
        }
    }
}
