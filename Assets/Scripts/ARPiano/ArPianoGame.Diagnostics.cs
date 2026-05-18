using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.InferenceEngine;
using UnityEngine;

namespace PianoARGame
{
    public sealed partial class ArPianoGame
    {
        private string EnsureDumpDirectory()
        {
            if (string.IsNullOrWhiteSpace(dumpDirectoryPath))
            {
                string folder = string.IsNullOrWhiteSpace(dumpInferenceFolderName) ? "DebugDumps/Unity" : dumpInferenceFolderName;
                string normalizedFolder = folder.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                dumpDirectoryPath = Path.Combine(Application.persistentDataPath, normalizedFolder);
            }

            Directory.CreateDirectory(dumpDirectoryPath);
            if (!dumpDirectoryLogged)
            {
                Debug.Log($"[ArPianoGame] Dump directory: {dumpDirectoryPath}");
                dumpDirectoryLogged = true;
            }

            return dumpDirectoryPath;
        }

        private void DumpInferenceArtifacts(int imageWidth, int imageHeight, float[] inputData, Color32[] resizedPixels, Tensor<float> chosenOutput, Detection? best, List<string> outputSummaries)
        {
            string directory = EnsureDumpDirectory();
            string prefix = $"unity_dump_{dumpedInferenceArtifacts:000}";

            SaveTexturePng(frameTexture, Path.Combine(directory, prefix + "_frame.png"));
            SaveResizedInputPng(resizedPixels, modelInputW, modelInputH, Path.Combine(directory, prefix + "_input.png"));
            SaveOverlayPng(imageWidth, imageHeight, best, Path.Combine(directory, prefix + "_overlay.png"));

            var builder = new StringBuilder();
            builder.AppendLine($"frame_size={imageWidth}x{imageHeight}");
            builder.AppendLine($"model_input={modelInputW}x{modelInputH}");
            builder.AppendLine($"camera_rotation={webcam.videoRotationAngle}");
            builder.AppendLine($"camera_mirrored={webcam.videoVerticallyMirrored}");
            builder.AppendLine($"confidence_threshold={confThreshold:F4}");
            builder.AppendLine($"iou_threshold={iouThreshold:F4}");
            builder.AppendLine($"best_detection={FormatDetection(best)}");
            builder.AppendLine(DescribeArrayStats("input_tensor", inputData));
            builder.AppendLine();
            builder.AppendLine("outputs:");
            for (int i = 0; i < outputSummaries.Count; i++)
            {
                builder.AppendLine(outputSummaries[i]);
            }

            if (chosenOutput != null)
            {
                builder.AppendLine();
                builder.AppendLine("chosen_output:");
                builder.AppendLine(DescribeOutputTensor("chosen", chosenOutput));
            }

            File.WriteAllText(Path.Combine(directory, prefix + "_stats.txt"), builder.ToString());
            Debug.Log($"[ArPianoGame] Dumped inference artifacts: {prefix} -> {directory}");
            dumpedInferenceArtifacts++;
        }

        private List<string> BuildOutputSummaries()
        {
            var summaries = new List<string>();
            if (worker == null)
            {
                return summaries;
            }

            for (int i = 0; i < outputNames.Length; i++)
            {
                Tensor<float> output = worker.PeekOutput(outputNames[i]) as Tensor<float>;
                if (output == null)
                {
                    summaries.Add($"- {outputNames[i]}: unavailable");
                    continue;
                }

                summaries.Add(DescribeOutputTensor(outputNames[i], output));
            }

            return summaries;
        }

        private string DescribeOutputTensor(string name, Tensor<float> output)
        {
            using Tensor<float> readable = output.ReadbackAndClone();
            float[] data = readable.DownloadToArray();
            string shapeText = DescribeShape(readable.shape);
            string stats = DescribeArrayStats(name, data);

            TensorShape shape = readable.shape;
            if (shape.rank != 3 || shape[0] != 1)
            {
                return $"- {name}: shape={shapeText}, {stats}";
            }

            int dim1 = shape[1];
            int dim2 = shape[2];
            bool featuresFirst = dim1 <= 256 && dim2 > dim1;
            int candidates = featuresFirst ? dim2 : dim1;
            int features = featuresFirst ? dim1 : dim2;
            if (features < 4 + Mathf.Max(1, numClasses))
            {
                return $"- {name}: shape={shapeText}, layout={(featuresFirst ? "features_first" : "candidates_first")}, {stats}";
            }

            float[] topScores = new float[Mathf.Min(5, candidates)];
            for (int i = 0; i < topScores.Length; i++)
            {
                topScores[i] = float.NegativeInfinity;
            }

            for (int candidate = 0; candidate < candidates; candidate++)
            {
                float bestClass = 0f;
                for (int cls = 0; cls < numClasses; cls++)
                {
                    float score = Read(data, candidate, 4 + cls, candidates, features, featuresFirst);
                    if (score > bestClass)
                    {
                        bestClass = score;
                    }
                }

                InsertTopScore(topScores, bestClass);
            }

            string topText = string.Join(", ", topScores.Where(v => !float.IsNegativeInfinity(v)).Select(v => v.ToString("F6")));
            return $"- {name}: shape={shapeText}, layout={(featuresFirst ? "features_first" : "candidates_first")}, candidates={candidates}, features={features}, {stats}, top_scores=[{topText}]";
        }

        private static void InsertTopScore(float[] topScores, float value)
        {
            for (int i = 0; i < topScores.Length; i++)
            {
                if (value <= topScores[i])
                {
                    continue;
                }

                for (int shift = topScores.Length - 1; shift > i; shift--)
                {
                    topScores[shift] = topScores[shift - 1];
                }

                topScores[i] = value;
                return;
            }
        }

        private static string DescribeArrayStats(string name, float[] values)
        {
            if (values == null || values.Length == 0)
            {
                return $"{name}: count=0";
            }

            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;
            double sum = 0.0;
            int previewCount = Mathf.Min(12, values.Length);
            string[] preview = new string[previewCount];
            for (int i = 0; i < values.Length; i++)
            {
                float value = values[i];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }

                sum += value;
                if (i < previewCount)
                {
                    preview[i] = value.ToString("F6");
                }
            }

            double mean = sum / values.Length;
            return $"{name}: count={values.Length}, min={min:F6}, max={max:F6}, mean={mean:F6}, preview=[{string.Join(", ", preview)}]";
        }

        private static string DescribeShape(TensorShape shape)
        {
            if (shape.rank <= 0)
            {
                return "[]";
            }

            int[] dims = new int[shape.rank];
            for (int i = 0; i < shape.rank; i++)
            {
                dims[i] = shape[i];
            }

            return "[" + string.Join(", ", dims) + "]";
        }

        private static string FormatDetection(Detection? detection)
        {
            if (!detection.HasValue)
            {
                return "none";
            }

            Detection value = detection.Value;
            return $"x1={value.x1:F3}, y1={value.y1:F3}, x2={value.x2:F3}, y2={value.y2:F3}, score={value.score:F6}";
        }

        private void SaveResizedInputPng(Color32[] pixels, int width, int height, string path)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.SetPixels32(ConvertTopLeftToBottomLeft(pixels, width, height));
            texture.Apply(false, false);
            SaveTexturePng(texture, path);
            Destroy(texture);
        }

        private void SaveOverlayPng(int width, int height, Detection? best, string path)
        {
            if (correctedFramePixels == null || correctedFramePixels.Length != width * height)
            {
                return;
            }

            Color32[] copy = new Color32[correctedFramePixels.Length];
            Array.Copy(correctedFramePixels, copy, correctedFramePixels.Length);
            if (best.HasValue)
            {
                DrawRectOutline(copy, width, height, best.Value, new Color32(40, 255, 90, 255), 3);
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.SetPixels32(copy);
            texture.Apply(false, false);
            SaveTexturePng(texture, path);
            Destroy(texture);
        }

        private static void DrawRectOutline(Color32[] pixels, int width, int height, Detection rect, Color32 color, int thickness)
        {
            int xMin = Mathf.Clamp(Mathf.RoundToInt(rect.x1), 0, width - 1);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(rect.y1), 0, height - 1);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(rect.x2), 0, width - 1);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(rect.y2), 0, height - 1);

            for (int t = 0; t < thickness; t++)
            {
                int left = Mathf.Clamp(xMin - t, 0, width - 1);
                int right = Mathf.Clamp(xMax + t, 0, width - 1);
                int top = Mathf.Clamp(yMin - t, 0, height - 1);
                int bottom = Mathf.Clamp(yMax + t, 0, height - 1);

                for (int x = left; x <= right; x++)
                {
                    pixels[top * width + x] = color;
                    pixels[bottom * width + x] = color;
                }

                for (int y = top; y <= bottom; y++)
                {
                    pixels[y * width + left] = color;
                    pixels[y * width + right] = color;
                }
            }
        }

        private static void SaveTexturePng(Texture2D texture, string path)
        {
            if (texture == null)
            {
                return;
            }

            byte[] png = texture.EncodeToPNG();
            if (png == null || png.Length == 0)
            {
                return;
            }

            File.WriteAllBytes(path, png);
        }
    }
}
