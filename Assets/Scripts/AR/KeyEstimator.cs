using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using PianoARGame.Services;

namespace PianoARGame.AR
{
    public class KeyInfo
    {
        public int index;
        public Rect bbox2D; // bbox em pixels
        public Vector3 pos3D; // posição estimada no mundo AR
        public float width;
    }

    /// <summary>
    /// Estima posições e contagem de teclas dado o resultado da detecção.
    /// </summary>
    public class KeyEstimator : MonoBehaviour
    {
        [Tooltip("Approximate keyboard width in meters used for world-space key mapping in phase-1.")]
        public float keyboardWidthMeters = 0.92f;

        public List<KeyInfo> EstimateKeys(DetectionResult detection)
        {
            return EstimateKeys(detection, 0, null);
        }

        public List<KeyInfo> EstimateKeys(DetectionResult detection, int frameHeight, ConfigService.CalibrationProfile calibrationProfile)
        {
            var keys = new List<KeyInfo>();
            if (detection == null || detection.keyColumns == null || detection.keyColumns.Length < 2)
                return keys;

            int[] cols = detection.keyColumns;
            int h = frameHeight <= 0 ? 1 : frameHeight;

            float minX = cols[0];
            float maxX = cols[cols.Length - 1];
            float span = Mathf.Max(1f, maxX - minX);

            for (int i = 0; i < cols.Length - 1; i++)
            {
                int left = cols[i];
                int right = cols[i + 1];
                float centerX = (left + right) * 0.5f;
                float x01 = Mathf.Clamp01((centerX - minX) / span);

                // Phase-1 world estimate: linear keyboard axis. Homography usage is added incrementally.
                Vector3 worldPos = new Vector3((x01 - 0.5f) * keyboardWidthMeters, 0f, 0f);

                if (calibrationProfile != null && calibrationProfile.isValid)
                {
                    worldPos.y = Mathf.Lerp(-0.02f, 0.02f, x01);
                }

                var k = new KeyInfo
                {
                    index = i,
                    bbox2D = new Rect(left, 0, Math.Max(1, right - left), h),
                    pos3D = worldPos,
                    width = Math.Max(1, right - left)
                };
                keys.Add(k);
            }

            return keys;
        }
    }
}
