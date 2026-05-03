using UnityEngine;

namespace PianoARGame.UI
{
    internal static class UiScaleCalculator
    {
        public static float Compute(bool isMobilePlatform, int screenWidth, int screenHeight, float dpi)
        {
            float shortest = Mathf.Min(screenWidth, screenHeight);
            float sizeScale = shortest / 720f;
            float dpiScale = dpi > 0f ? dpi / 180f : 1f;

            return isMobilePlatform
                ? Mathf.Clamp(Mathf.Max(sizeScale, dpiScale), 1.1f, 2.3f)
                : Mathf.Clamp(sizeScale, 0.9f, 1.25f);
        }
    }
}
