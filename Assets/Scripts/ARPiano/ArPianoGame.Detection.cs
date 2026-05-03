using System;
using System.Collections.Generic;
using System.Linq;
using Unity.InferenceEngine;
using UnityEngine;

namespace PianoARGame
{
    public sealed partial class ArPianoGame
    {
        private void TryInitializeModel()
        {
            if (onnxModel == null && !string.IsNullOrWhiteSpace(resourcesModelPath))
            {
                onnxModel = Resources.Load<ModelAsset>(resourcesModelPath);
            }

            if (onnxModel == null)
            {
                lastError = "Onnx Model vazio. Defina no Inspector ou mantenha piano_SGD.onnx em Assets/Resources/AIModels.";
                return;
            }

            try
            {
                runtimeModel = ModelLoader.Load(onnxModel);
                worker = new Worker(runtimeModel, backendType);
                outputNames = runtimeModel.outputs.Select(o => o.name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
                ResolveModelInputSize(runtimeModel, fallbackInputSize, out modelInputW, out modelInputH);
                modelDiagnosticsLogged = false;
            }
            catch (Exception ex)
            {
                lastError = "Falha ao carregar ONNX: " + ex.Message;
            }
        }

        private void UpdateTracker()
        {
            if (webcam == null || !webcam.isPlaying)
            {
                return;
            }

            if (!webcam.didUpdateThisFrame)
            {
                return;
            }

            frameCount++;
            int effectiveDetectInterval = GetEffectiveDetectInterval(keyboardArea.HasValue);
            bool shouldDetect = frameCount % effectiveDetectInterval == 0;
            if (!shouldDetect)
            {
                return;
            }

            RefreshCorrectedFrame(out _, out _, false);

            if (correctedFramePixels == null || correctedFramePixels.Length == 0 || latestCorrectedFrameWidth < 16 || latestCorrectedFrameHeight < 16)
            {
                return;
            }

            Detection? best = DetectKeyboard();
            if (best.HasValue)
            {
                Detection d = best.Value;
                bestConf = d.score;
                keyboardArea = SmoothRect(keyboardArea, d, 0.25f);
                stableHits = Mathf.Min(200, stableHits + 1);
            }
            else
            {
                stableHits = Mathf.Max(0, stableHits - 1);
            }
        }

        private int GetEffectiveDetectInterval(bool hasKeyboardArea)
        {
            int maxInterval = Application.platform == RuntimePlatform.Android ? 15 : 12;
            if (adaptiveDetectInterval <= 0 || lastConfiguredDetectInterval != detectInterval)
            {
                adaptiveDetectInterval = Mathf.Clamp(Mathf.Max(1, detectInterval), 1, maxInterval);
                lastConfiguredDetectInterval = detectInterval;
            }

            if (renderFpsEma < 28f)
            {
                adaptiveDetectInterval = Mathf.Min(maxInterval, adaptiveDetectInterval + 1);
            }
            else if (renderFpsEma > 40f)
            {
                adaptiveDetectInterval = Mathf.Max(3, adaptiveDetectInterval - 1);
            }

            int interval = Mathf.Clamp(adaptiveDetectInterval, 1, maxInterval);

            if (!hasKeyboardArea)
            {
                int minSearchInterval = Application.platform == RuntimePlatform.Android ? 4 : 3;
                interval = Mathf.Max(interval, minSearchInterval);
            }

            if (hasKeyboardArea && state == GameState.Game)
            {
                interval = Mathf.Max(interval, 8);
            }

            return interval;
        }

        private Detection? DetectKeyboard()
        {
            if (worker == null)
            {
                if (!modelInitAttempted)
                {
                    modelInitAttempted = true;
                    TryInitializeModel();
                }

                if (worker == null)
                {
                    return null;
                }
            }

            if (webcam == null || !webcam.isPlaying)
            {
                return null;
            }

            if (webcam.width < 16 || webcam.height < 16)
            {
                return null;
            }

            if (correctedFramePixels == null || correctedFramePixels.Length == 0 || latestCorrectedFrameWidth < 16 || latestCorrectedFrameHeight < 16)
            {
                if (webcam.didUpdateThisFrame)
                {
                    RefreshCorrectedFrame(out _, out _, false);
                }
            }

            int correctedWidth = latestCorrectedFrameWidth;
            int correctedHeight = latestCorrectedFrameHeight;

            if (correctedFramePixels == null || correctedFramePixels.Length == 0 || correctedWidth < 16 || correctedHeight < 16)
            {
                return null;
            }

            if (!cameraDiagnosticsLogged)
            {
                Debug.Log($"[ArPianoGame] Webcam actual: {webcam.width}x{webcam.height}, corrected={correctedWidth}x{correctedHeight}, rotation={webcam.videoRotationAngle}, mirrored={webcam.videoVerticallyMirrored}");
                cameraDiagnosticsLogged = true;
            }

            if (!modelDiagnosticsLogged)
            {
                Debug.Log($"[ArPianoGame] Model input resolved: {modelInputW}x{modelInputH}, fallback={fallbackInputSize}, outputs=[{string.Join(", ", outputNames)}]");
                modelDiagnosticsLogged = true;
            }

            using Tensor<float> inputTensor = PreprocessFrame(correctedFramePixels, correctedWidth, correctedHeight, modelInputW, modelInputH, out float[] inputData, out Color32[] resizedPixels);
            worker.Schedule(inputTensor);
            Tensor<float> output = PickDetectionOutput();
            if (output == null)
            {
                if (ShouldDumpInferenceArtifacts())
                {
                    DumpInferenceArtifacts(correctedWidth, correctedHeight, inputData, resizedPixels, null, null, BuildOutputSummaries());
                }

                return null;
            }

            Detection? best = DecodeBest(output, correctedWidth, correctedHeight, modelInputW, modelInputH, numClasses, confThreshold, iouThreshold);
            if (best.HasValue)
            {
                best = ConvertDetectionToTopLeft(best.Value, correctedHeight);
            }

            if (ShouldDumpInferenceArtifacts())
            {
                DumpInferenceArtifacts(correctedWidth, correctedHeight, inputData, resizedPixels, output, best, BuildOutputSummaries());
            }

            return best;
        }

        private void RefreshCorrectedFrame(out int correctedWidth, out int correctedHeight, bool updateFrameTexture)
        {
            int sourceWidth = webcam.width;
            int sourceHeight = webcam.height;
            int rotation = NormalizeRotation(webcam.videoRotationAngle);
            bool swapAxes = rotation == 90 || rotation == 270;

            correctedWidth = swapAxes ? sourceHeight : sourceWidth;
            correctedHeight = swapAxes ? sourceWidth : sourceHeight;
            latestCorrectedFrameWidth = correctedWidth;
            latestCorrectedFrameHeight = correctedHeight;

            if (updateFrameTexture)
            {
                EnsureFrameTexture(correctedWidth, correctedHeight);
            }

            int sourceTotal = sourceWidth * sourceHeight;
            if (sourceFramePixels == null || sourceFramePixels.Length != sourceTotal)
            {
                sourceFramePixels = new Color32[sourceTotal];
            }

            webcam.GetPixels32(sourceFramePixels);
            int total = correctedWidth * correctedHeight;
            if (correctedFramePixels == null || correctedFramePixels.Length != total)
            {
                correctedFramePixels = new Color32[total];
            }

            bool mirrored = webcam.videoVerticallyMirrored;
            for (int y = 0; y < sourceHeight; y++)
            {
                for (int x = 0; x < sourceWidth; x++)
                {
                    int sampleY = mirrored ? (sourceHeight - 1 - y) : y;
                    int srcIndex = sampleY * sourceWidth + x;
                    int dstX;
                    int dstY;

                    switch (rotation)
                    {
                        case 90:
                            dstX = sourceHeight - 1 - y;
                            dstY = x;
                            break;
                        case 180:
                            dstX = sourceWidth - 1 - x;
                            dstY = sourceHeight - 1 - y;
                            break;
                        case 270:
                            dstX = y;
                            dstY = sourceWidth - 1 - x;
                            break;
                        default:
                            dstX = x;
                            dstY = y;
                            break;
                    }

                    correctedFramePixels[dstY * correctedWidth + dstX] = sourceFramePixels[srcIndex];
                }
            }

            if (updateFrameTexture && frameTexture != null)
            {
                frameTexture.SetPixels32(correctedFramePixels);
                frameTexture.Apply(false, false);
            }
        }

        private static int NormalizeRotation(int angle)
        {
            int normalized = angle % 360;
            if (normalized < 0)
            {
                normalized += 360;
            }

            if (normalized >= 315 || normalized < 45)
            {
                return 0;
            }

            if (normalized < 135)
            {
                return 90;
            }

            if (normalized < 225)
            {
                return 180;
            }

            return 270;
        }

        private void EnsureFrameTexture(int width, int height)
        {
            if (frameTexture != null && frameTexture.width == width && frameTexture.height == height)
            {
                return;
            }

            if (frameTexture != null)
            {
                Destroy(frameTexture);
            }

            frameTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        }

        private Tensor<float> PreprocessFrame(Color32[] sourcePixels, int sourceWidth, int sourceHeight, int inputW, int inputH, out float[] inputData, out Color32[] resizedPixels)
        {
            EnsurePreprocessBuffers(inputW, inputH);
            ResizePixelsBilinear(sourcePixels, sourceWidth, sourceHeight, inputW, inputH, resizedPixelsBuffer);
            resizedPixels = resizedPixelsBuffer;
            int planeSize = inputW * inputH;
            inputData = inputTensorBuffer;

            for (int i = 0; i < planeSize; i++)
            {
                Color32 p = resizedPixels[i];
                inputData[i] = p.r / 255f;
                inputData[planeSize + i] = p.g / 255f;
                inputData[(2 * planeSize) + i] = p.b / 255f;
            }

            return new Tensor<float>(new TensorShape(1, 3, inputH, inputW), inputData);
        }

        private void EnsurePreprocessBuffers(int inputW, int inputH)
        {
            int planeSize = inputW * inputH;
            int resizedLength = planeSize;
            int inputLength = 3 * planeSize;

            if (resizedPixelsBuffer == null || resizedPixelsBuffer.Length != resizedLength)
            {
                resizedPixelsBuffer = new Color32[resizedLength];
            }

            if (inputTensorBuffer == null || inputTensorBuffer.Length != inputLength)
            {
                inputTensorBuffer = new float[inputLength];
            }
        }

        private bool ShouldDumpInferenceArtifacts()
        {
            return dumpInferenceArtifacts && dumpedInferenceArtifacts < dumpInferenceArtifactLimit;
        }

        private Tensor<float> PickDetectionOutput()
        {
            if (worker == null)
            {
                return null;
            }

            for (int i = 0; i < outputNames.Length; i++)
            {
                Tensor<float> named = worker.PeekOutput(outputNames[i]) as Tensor<float>;
                if (named == null)
                {
                    continue;
                }

                TensorShape shape = named.shape;
                if (shape.rank == 3 && shape[0] == 1)
                {
                    int dim1 = shape[1];
                    int dim2 = shape[2];
                    bool featuresFirst = dim1 <= 256 && dim2 > dim1;
                    int featureCount = featuresFirst ? dim1 : dim2;
                    if (featureCount >= 5)
                    {
                        return named;
                    }
                }
            }

            Tensor<float> fallback = worker.PeekOutput() as Tensor<float>;
            return fallback;
        }

        private Detection? DecodeBest(Tensor<float> outputTensor, int imageW, int imageH, int inputW, int inputH, int classes, float conf, float iou)
        {
            using Tensor<float> readable = outputTensor.ReadbackAndClone();
            TensorShape shape = readable.shape;
            if (shape.rank != 3 || shape[0] != 1)
            {
                return null;
            }

            float[] data = readable.DownloadToArray();
            int dim1 = shape[1];
            int dim2 = shape[2];
            bool featuresFirst = dim1 <= 256 && dim2 > dim1;
            int candidates = featuresFirst ? dim2 : dim1;
            int features = featuresFirst ? dim1 : dim2;
            if (features < 4 + Mathf.Max(1, classes))
            {
                return null;
            }

            decodeCandidates.Clear();
            for (int c = 0; c < candidates; c++)
            {
                float cx = Read(data, c, 0, candidates, features, featuresFirst);
                float cy = Read(data, c, 1, candidates, features, featuresFirst);
                float bw = Read(data, c, 2, candidates, features, featuresFirst);
                float bh = Read(data, c, 3, candidates, features, featuresFirst);

                float bestClass = 0f;
                for (int cls = 0; cls < classes; cls++)
                {
                    float s = Read(data, c, 4 + cls, candidates, features, featuresFirst);
                    if (s > bestClass)
                    {
                        bestClass = s;
                    }
                }

                if (bestClass < conf)
                {
                    continue;
                }

                CxCyWhToXyxy(cx, cy, bw, bh, imageW, imageH, inputW, inputH, out float x1, out float y1, out float x2, out float y2);
                if ((x2 - x1) > 1f && (y2 - y1) > 1f)
                {
                    decodeCandidates.Add(new Detection(x1, y1, x2, y2, bestClass));
                }
            }

            if (decodeCandidates.Count == 0)
            {
                return null;
            }

            decodeCandidates.Sort((a, b) => b.score.CompareTo(a.score));
            decodeKept.Clear();
            for (int i = 0; i < decodeCandidates.Count; i++)
            {
                Detection det = decodeCandidates[i];
                bool suppress = false;
                for (int k = 0; k < decodeKept.Count; k++)
                {
                    if (IoU(det, decodeKept[k]) > iou)
                    {
                        suppress = true;
                        break;
                    }
                }

                if (!suppress)
                {
                    decodeKept.Add(det);
                }
            }

            return decodeKept[0];
        }

        private static float Read(float[] data, int candidate, int feature, int candidates, int features, bool featuresFirst)
        {
            int index = featuresFirst ? feature * candidates + candidate : candidate * features + feature;
            return data[index];
        }

        private static void CxCyWhToXyxy(float cx, float cy, float bw, float bh, int imageW, int imageH, int inputW, int inputH, out float x1, out float y1, out float x2, out float y2)
        {
            bool normalized = Mathf.Abs(cx) <= 2f && Mathf.Abs(cy) <= 2f && Mathf.Abs(bw) <= 2f && Mathf.Abs(bh) <= 2f;
            float sx = normalized ? imageW : imageW / (float)Mathf.Max(1, inputW);
            float sy = normalized ? imageH : imageH / (float)Mathf.Max(1, inputH);
            float centerX = cx * sx;
            float centerY = cy * sy;
            float width = Mathf.Abs(bw * sx);
            float height = Mathf.Abs(bh * sy);

            x1 = Mathf.Clamp(centerX - 0.5f * width, 0f, imageW - 1f);
            y1 = Mathf.Clamp(centerY - 0.5f * height, 0f, imageH - 1f);
            x2 = Mathf.Clamp(centerX + 0.5f * width, x1 + 1f, imageW);
            y2 = Mathf.Clamp(centerY + 0.5f * height, y1 + 1f, imageH);
        }

        private static Detection ConvertDetectionToTopLeft(Detection detection, int imageHeight)
        {
            float topY = Mathf.Clamp(imageHeight - detection.y2, 0f, imageHeight - 1f);
            float bottomY = Mathf.Clamp(imageHeight - detection.y1, topY + 1f, imageHeight);
            return new Detection(detection.x1, topY, detection.x2, bottomY, detection.score);
        }

        private static float IoU(Detection a, Detection b)
        {
            float x1 = Mathf.Max(a.x1, b.x1);
            float y1 = Mathf.Max(a.y1, b.y1);
            float x2 = Mathf.Min(a.x2, b.x2);
            float y2 = Mathf.Min(a.y2, b.y2);

            float inter = Mathf.Max(0f, x2 - x1) * Mathf.Max(0f, y2 - y1);
            if (inter <= 0f)
            {
                return 0f;
            }

            float areaA = Mathf.Max(0f, a.x2 - a.x1) * Mathf.Max(0f, a.y2 - a.y1);
            float areaB = Mathf.Max(0f, b.x2 - b.x1) * Mathf.Max(0f, b.y2 - b.y1);
            float union = areaA + areaB - inter;
            return union <= 0f ? 0f : inter / union;
        }

        private static void ResolveModelInputSize(Model model, int fallbackSize, out int inputW, out int inputH)
        {
            inputW = fallbackSize;
            inputH = fallbackSize;

            if (model == null || model.inputs == null || model.inputs.Count == 0)
            {
                return;
            }

            DynamicTensorShape shape = model.inputs[0].shape;
            if (shape.isRankDynamic || shape.rank < 4)
            {
                return;
            }

            if (!shape.IsStatic())
            {
                return;
            }

            TensorShape staticShape = shape.ToTensorShape();
            int h = staticShape[2];
            int w = staticShape[3];
            if (h > 0 && w > 0)
            {
                inputW = w;
                inputH = h;
            }
        }

        private static void ResizePixelsBilinear(Color32[] sourcePixels, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, Color32[] targetPixels)
        {
            for (int y = 0; y < targetHeight; y++)
            {
                float sampleY = targetHeight == 1 ? 0f : (y + 0.5f) * sourceHeight / targetHeight - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(sampleY), 0, sourceHeight - 1);
                int y1 = Mathf.Clamp(y0 + 1, 0, sourceHeight - 1);
                float ty = Mathf.Clamp01(sampleY - y0);

                for (int x = 0; x < targetWidth; x++)
                {
                    float sampleX = targetWidth == 1 ? 0f : (x + 0.5f) * sourceWidth / targetWidth - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(sampleX), 0, sourceWidth - 1);
                    int x1 = Mathf.Clamp(x0 + 1, 0, sourceWidth - 1);
                    float tx = Mathf.Clamp01(sampleX - x0);

                    Color32 c00 = ReadPixelTopLeft(sourcePixels, sourceWidth, sourceHeight, x0, y0);
                    Color32 c01 = ReadPixelTopLeft(sourcePixels, sourceWidth, sourceHeight, x1, y0);
                    Color32 c10 = ReadPixelTopLeft(sourcePixels, sourceWidth, sourceHeight, x0, y1);
                    Color32 c11 = ReadPixelTopLeft(sourcePixels, sourceWidth, sourceHeight, x1, y1);
                    targetPixels[y * targetWidth + x] = BilinearSample(c00, c01, c10, c11, tx, ty);
                }
            }
        }

        private static Color32 ReadPixelTopLeft(Color32[] pixels, int width, int height, int x, int y)
        {
            int clampedX = Mathf.Clamp(x, 0, width - 1);
            int clampedY = Mathf.Clamp(y, 0, height - 1);
            int bottomToTopY = height - 1 - clampedY;
            return pixels[bottomToTopY * width + clampedX];
        }

        private static Color32[] ConvertTopLeftToBottomLeft(Color32[] pixels, int width, int height)
        {
            var converted = new Color32[pixels.Length];
            for (int y = 0; y < height; y++)
            {
                int sourceRow = y * width;
                int destinationRow = (height - 1 - y) * width;
                Array.Copy(pixels, sourceRow, converted, destinationRow, width);
            }

            return converted;
        }

        private static Color32 BilinearSample(Color32 c00, Color32 c01, Color32 c10, Color32 c11, float tx, float ty)
        {
            float topR = Mathf.Lerp(c00.r, c01.r, tx);
            float topG = Mathf.Lerp(c00.g, c01.g, tx);
            float topB = Mathf.Lerp(c00.b, c01.b, tx);
            float topA = Mathf.Lerp(c00.a, c01.a, tx);

            float bottomR = Mathf.Lerp(c10.r, c11.r, tx);
            float bottomG = Mathf.Lerp(c10.g, c11.g, tx);
            float bottomB = Mathf.Lerp(c10.b, c11.b, tx);
            float bottomA = Mathf.Lerp(c10.a, c11.a, tx);

            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(topR, bottomR, ty)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(topG, bottomG, ty)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(topB, bottomB, ty)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(topA, bottomA, ty)));
        }
    }
}
