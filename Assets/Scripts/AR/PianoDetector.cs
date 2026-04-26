using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using UnityEngine;

namespace PianoARGame.AR
{
    public class DetectionResult
    {
        public Vector2[] polygon; // em pixels
        public Pose pose; // pose estimada do teclado
        public float confidence;
        public int[] keyColumns; // x positions (pixels) das bordas detectadas entre teclas
        public int keyCount;

        // PR-1 instrumentation fields
        public float processingTimeMs;
        public float gradientMean;
        public float gradientMax;
        public float detectionThreshold;
        public bool isTrackingStable;
        public float reprojectionError;
        public string statusMessage;
    }

    /// <summary>
    /// Detecta presença de um piano/teclado na imagem e retorna polígono e pose.
    /// Implementação inicial: stub para integração futura com ML ou visão clássica.
    /// </summary>
    public class PianoDetector : MonoBehaviour
    {
        [Range(0f,1f)]
        public float minConfidence = 0.6f;

        [Header("Diagnostics")]
        [Range(0f, 0.8f)]
        public float thresholdFactor = 0.35f;

        [Min(1)]
        public int minRequiredPeaks = 5;

        /// <summary>
        /// Implementação experimental simples: converte frame em grayscale,
        /// calcula gradiente horizontal por coluna, detecta picos correspondentes
        /// a bordas verticais (separação de teclas) e retorna colunas detectadas.
        /// Projeto para funcionar no Editor/webcam como MVP sem OpenCV.
        /// </summary>
        public DetectionResult Detect(Texture2D frame)
        {
            if (frame == null)
            {
                return new DetectionResult
                {
                    confidence = 0f,
                    keyColumns = Array.Empty<int>(),
                    keyCount = 0,
                    statusMessage = "Frame is null"
                };
            }

            var swatch = Stopwatch.StartNew();

            int w = frame.width;
            int h = frame.height;

            if (w <= 2 || h <= 2)
            {
                return new DetectionResult
                {
                    polygon = new[] { new Vector2(0, 0), new Vector2(w, 0), new Vector2(w, h), new Vector2(0, h) },
                    pose = Pose.identity,
                    confidence = 0f,
                    keyColumns = Array.Empty<int>(),
                    keyCount = 0,
                    processingTimeMs = 0f,
                    statusMessage = "Frame resolution too small"
                };
            }

            Color[] pixels = frame.GetPixels();

            // Convert to grayscale
            float[] gray = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    Color c = pixels[row + x];
                    gray[row + x] = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                }
            }

            // Compute horizontal gradient per column: sum over rows of abs(gray[x] - gray[x+1])
            int sw = w - 1;
            float[] colGrad = new float[sw];
            for (int x = 0; x < sw; x++)
            {
                float sum = 0f;
                for (int y = 0; y < h; y++)
                {
                    int idx = y * w + x;
                    sum += Math.Abs(gray[idx] - gray[idx + 1]);
                }
                colGrad[x] = sum / h;
            }

            // Smooth colGrad with simple moving average
            int smooth = Math.Max(1, w / 200); // window depends on width
            float[] smoothGrad = new float[sw];
            for (int x = 0; x < sw; x++)
            {
                int start = Math.Max(0, x - smooth);
                int end = Math.Min(sw - 1, x + smooth);
                float s = 0f;
                for (int k = start; k <= end; k++) s += colGrad[k];
                smoothGrad[x] = s / (end - start + 1);
            }

            // Compute threshold: mean + factor * (max - mean)
            float mean = 0f;
            float max = 0f;
            for (int x = 0; x < sw; x++)
            {
                mean += smoothGrad[x];
                if (smoothGrad[x] > max) max = smoothGrad[x];
            }
            mean /= sw;
            float thresh = mean + thresholdFactor * (max - mean);

            // Find peaks above threshold
            List<int> peaks = new List<int>();
            int minSep = Math.Max(3, w / 200);
            for (int x = 1; x < sw - 1; x++)
            {
                if (smoothGrad[x] > thresh && smoothGrad[x] >= smoothGrad[x - 1] && smoothGrad[x] >= smoothGrad[x + 1])
                {
                    // enforce minimum separation
                    if (peaks.Count == 0 || x - peaks[peaks.Count - 1] >= minSep)
                        peaks.Add(x);
                }
            }

            if (peaks.Count < minRequiredPeaks)
            {
                // detection failed or not confident
                swatch.Stop();
                return new DetectionResult
                {
                    polygon = new Vector2[] { new Vector2(0, 0), new Vector2(w, 0), new Vector2(w, h), new Vector2(0, h) },
                    pose = Pose.identity,
                    confidence = 0f,
                    keyColumns = Array.Empty<int>(),
                    keyCount = 0,
                    processingTimeMs = (float)swatch.Elapsed.TotalMilliseconds,
                    gradientMean = mean,
                    gradientMax = max,
                    detectionThreshold = thresh,
                    isTrackingStable = false,
                    reprojectionError = 99f,
                    statusMessage = "Not enough peaks"
                };
            }

            // Map peaks to original image x coordinates (peaks are already in pixels)
            int[] cols = peaks.Select(p => p).ToArray();

            // Estimate keyboard bounding box as full width between first and last peak
            int left = Math.Max(0, cols.First() - 10);
            int right = Math.Min(w - 1, cols.Last() + 10);

            float spread = Mathf.Clamp01((cols.Last() - cols.First()) / (float)w);
            float peakScore = Mathf.Clamp01((peaks.Count - minRequiredPeaks + 1f) / 30f);
            float dynamicRange = max > 0.0001f ? Mathf.Clamp01((max - mean) / max) : 0f;
            float confidence = Mathf.Clamp01((0.45f * spread) + (0.35f * peakScore) + (0.20f * dynamicRange));

            swatch.Stop();

            return new DetectionResult
            {
                polygon = new Vector2[] { new Vector2(left, 0), new Vector2(right, 0), new Vector2(right, h), new Vector2(left, h) },
                pose = Pose.identity,
                confidence = confidence,
                keyColumns = cols,
                keyCount = Math.Max(0, cols.Length - 1),
                processingTimeMs = (float)swatch.Elapsed.TotalMilliseconds,
                gradientMean = mean,
                gradientMax = max,
                detectionThreshold = thresh,
                isTrackingStable = confidence >= minConfidence,
                reprojectionError = Mathf.Lerp(8f, 1.5f, confidence),
                statusMessage = confidence >= minConfidence ? "Detection OK" : "Low confidence"
            };
        }
    }
}
