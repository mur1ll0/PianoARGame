using UnityEngine;

namespace PianoARGame
{
    public static class ArPianoBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureGameObjectExists()
        {
            if (Object.FindAnyObjectByType<ArPianoGame>() != null)
            {
                return;
            }

            var go = new GameObject("AR Piano Game");
            go.AddComponent<ArPianoGame>();
            Object.DontDestroyOnLoad(go);
        }
    }
}
