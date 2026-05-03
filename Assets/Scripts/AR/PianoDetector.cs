using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using UnityEngine;
using Unity.InferenceEngine;


namespace PianoARGame.AR
{
    public class DetectionResult
    {
        // ── Existing fields (preserved — no downstream breakage) ───────────────
        public Vector2[] polygon;       // bounding polygon em pixels
        public Pose pose;               // pose estimada do teclado
        public float confidence;
        public int[] keyColumns;        // x positions (pixels) das bordas entre teclas
        public int keyCount;

        public float processingTimeMs;
        public float gradientMean;
        public float gradientMax;
        public float detectionThreshold;
        public bool isTrackingStable;
        public float reprojectionError;
        public string statusMessage;

        // ── New optional fields (PR-1) ─────────────────────────────────────────
        public Rect   boundingBox;        // bbox 2D em pixels (Stage 1)
        public float  stage1Confidence;   // confiança bruta do modelo IA
        public Vector2[] stage1CandidateCenters; // centros dos candidatos ONNX (debug)
        public Rect[] stage1CandidateBoxes;      // caixas dos candidatos ONNX (debug)
        public float[] stage1CandidateScores;    // score keyboard_area por candidato (debug)
        public int stage1CandidateCount;         // quantidade de candidatos ONNX (debug)
        public string modelBackend;       // "GradientMVP" | "ONNX_CandidateA" | "ONNX_CandidateB"
        public float  inferenceTimeMs;    // tempo de inferência ONNX
        public int    stage2KeyCountRaw;  // contagem bruta antes de snap
        public float  periodicityScore;   // regularidade das bordas (Stage 2)
        public Rect   stage2Roi;          // ROI usada no stage-2
        public int[]  stage2PeaksRaw;     // picos brutos antes da calibração por perfil
        public int    stage2ExpectedKeyCount;
    }

    /// <summary>
    /// Detecta presença de um piano/teclado na imagem usando ONNX.
    /// Stage-1: ONNX detecta a área do teclado.
    /// Stage-2: heurística local estima contagem/bordas de teclas dentro da área detectada.
    /// </summary>
    public class PianoDetector : MonoBehaviour
    {
        private struct YoloDetection
        {
            public Rect bbox;
            public float score;
        }

        // ── Inspector: IA Backend (PR-1) ───────────────────────────────────────
        [Header("IA Backend (PR-1)")]
        [Tooltip("Arraste aqui um ModelAsset gerado a partir de .onnx (recomendado: Assets/AIModels).")]
        public Unity.InferenceEngine.ModelAsset onnxModel;

        [Tooltip("Mantido apenas por compatibilidade de cena antiga. O detector agora usa ONNX sempre.")]
        public bool useOnnxBackend = true;

        public Unity.InferenceEngine.BackendType backendType = Unity.InferenceEngine.BackendType.CPU;

        [Min(32)]
        [Tooltip("Resolução de entrada do modelo (geralmente 640 para YOLOv8n).")]
        public int inferenceInputSize = 640;

        [Range(0.1f, 0.95f)]
        public float onnxConfidenceThreshold = 0.45f;

        [Range(0.1f, 0.95f)]
        [Tooltip("IoU threshold para Non-Maximum Suppression.")]
        public float onnxIouThreshold = 0.45f;

        [Min(1)]
        [Tooltip("Numero de classes de deteccao no modelo YOLO exportado para ONNX. Para keyboard_area use 1.")]
        public int onnxNumClasses = 1;

        [Header("Key Mapping")]
        [Range(24, 128)]
        [Tooltip("Quantidade de teclas usada para gerar divisoes uniformes dentro da area detectada.")]
        public int keyCountForMapping = 88;

        // ── Estado interno do Worker ───────────────────────────────────────────
        private Unity.InferenceEngine.Worker      _worker;
        private Unity.InferenceEngine.Model       _runtimeModel;
        private Unity.InferenceEngine.ModelAsset  _loadedModelAsset;

        [SerializeField, HideInInspector] private string _lastOnnxResolveError;

        public string LastOnnxResolveError => _lastOnnxResolveError;

        // ── Ciclo de vida ──────────────────────────────────────────────────────

        void OnDestroy() => DisposeWorker();

    #if UNITY_EDITOR
        private void OnValidate()
        {
            ResolveOnnxModelReference();
        }
    #endif

        private void DisposeWorker()
        {
            _worker?.Dispose();
            _worker            = null;
            _runtimeModel      = null;
            _loadedModelAsset  = null;
        }

        /// <summary>Garante que o Worker está criado para o onnxModel atual.</summary>
        private bool EnsureWorker()
        {
            ResolveOnnxModelReference();
            if (onnxModel == null) return false;
            if (_worker != null && _loadedModelAsset == onnxModel) return true;

            DisposeWorker();
            try
            {
                _runtimeModel     = Unity.InferenceEngine.ModelLoader.Load(onnxModel);
                _worker           = new Unity.InferenceEngine.Worker(_runtimeModel, backendType);
                _loadedModelAsset = onnxModel;
                UnityEngine.Debug.Log("[PianoDetector] ONNX model carregado via Sentis.");
                return true;
            }
            catch (Exception ex)
            {
                _lastOnnxResolveError = $"Falha ao carregar modelo ONNX: {ex.Message}";
                UnityEngine.Debug.LogWarning($"[PianoDetector] Falha ao carregar modelo ONNX: {ex.Message}");
                return false;
            }
        }

        private void ResolveOnnxModelReference()
        {
            _lastOnnxResolveError = onnxModel == null
                ? "Onnx Model vazio. Use um .onnx importado como ModelAsset em Assets/AIModels e arraste no campo Onnx Model."
                : null;
        }

        // ── API Pública ────────────────────────────────────────────────────────

        /// <summary>
        /// Detecta piano no frame usando ONNX obrigatório.
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
                    polygon = FullFramePolygon(w, h),
                    pose = Pose.identity,
                    confidence = 0f,
                    keyColumns = Array.Empty<int>(),
                    keyCount = 0,
                    processingTimeMs = 0f,
                    statusMessage = "Frame resolution too small"
                };
            }

            useOnnxBackend = true;

            if (!EnsureWorker())
            {
                swatch.Stop();
                return BuildEmptyOnnxResult(
                    w,
                    h,
                    (float)swatch.Elapsed.TotalMilliseconds,
                    0f,
                    string.IsNullOrWhiteSpace(_lastOnnxResolveError)
                        ? "Onnx Model ausente ou inválido"
                        : _lastOnnxResolveError);
            }

            var onnxResult = DetectWithOnnx(frame);
            return onnxResult ?? BuildEmptyOnnxResult(
                w,
                h,
                (float)swatch.Elapsed.TotalMilliseconds,
                0f,
                "Falha na inferência ONNX");
        }

        /// <summary>
        /// Modo de teste bruto do stage-1: escolhe a melhor keyboard_area sem threshold,
        /// sem NMS e sem heurística de stage-2 para máxima fluidez.
        /// </summary>
        public DetectionResult DetectStage1Raw(Texture2D frame)
        {
            if (frame == null)
            {
                return new DetectionResult
                {
                    confidence = 0f,
                    statusMessage = "Frame is null",
                    keyColumns = Array.Empty<int>(),
                    keyCount = 0
                };
            }

            int w = frame.width;
            int h = frame.height;
            if (w <= 2 || h <= 2)
            {
                return BuildEmptyOnnxResult(w, h, 0f, 0f, "Frame resolution too small");
            }

            if (!EnsureWorker())
            {
                return BuildEmptyOnnxResult(
                    w,
                    h,
                    0f,
                    0f,
                    string.IsNullOrWhiteSpace(_lastOnnxResolveError)
                        ? "Onnx Model ausente ou inválido"
                        : _lastOnnxResolveError);
            }

            var totalWatch = Stopwatch.StartNew();
            try
            {
                using var inputTensor = PreprocessFrame(frame, inferenceInputSize);

                var inferenceWatch = Stopwatch.StartNew();
                _worker.Schedule(inputTensor);
                inferenceWatch.Stop();

                if (!TryDecodeBestRawStage1Candidate(w, h, out YoloDetection bestDetection, out int candidateCount, out string decodeStatus))
                {
                    totalWatch.Stop();
                    return BuildEmptyOnnxResult(
                        w,
                        h,
                        (float)totalWatch.Elapsed.TotalMilliseconds,
                        (float)inferenceWatch.Elapsed.TotalMilliseconds,
                        decodeStatus);
                }

                totalWatch.Stop();
                return new DetectionResult
                {
                    polygon = RectToPolygon(bestDetection.bbox),
                    pose = Pose.identity,
                    confidence = bestDetection.score,
                    keyColumns = Array.Empty<int>(),
                    keyCount = 0,
                    processingTimeMs = (float)totalWatch.Elapsed.TotalMilliseconds,
                    gradientMean = 0f,
                    gradientMax = 0f,
                    detectionThreshold = 0f,
                    isTrackingStable = false,
                    reprojectionError = 0f,
                    statusMessage = "Stage-1 raw ONNX",
                    boundingBox = bestDetection.bbox,
                    stage1Confidence = bestDetection.score,
                    stage1CandidateCenters = Array.Empty<Vector2>(),
                    stage1CandidateBoxes = Array.Empty<Rect>(),
                    stage1CandidateScores = Array.Empty<float>(),
                    stage1CandidateCount = candidateCount,
                    modelBackend = GetOnnxBackendName(),
                    inferenceTimeMs = (float)inferenceWatch.Elapsed.TotalMilliseconds,
                    stage2KeyCountRaw = 0,
                    periodicityScore = 0f,
                    stage2Roi = new Rect(0f, 0f, 0f, 0f),
                    stage2PeaksRaw = Array.Empty<int>(),
                    stage2ExpectedKeyCount = 0
                };
            }
            catch (Exception ex)
            {
                totalWatch.Stop();
                return BuildEmptyOnnxResult(w, h, (float)totalWatch.Elapsed.TotalMilliseconds, 0f, $"Falha ONNX raw: {ex.Message}");
            }
        }

        private bool TryDecodeBestRawStage1Candidate(int frameWidth, int frameHeight, out YoloDetection bestDetection, out int candidateCount, out string status)
        {
            bestDetection = default;
            candidateCount = 0;
            status = "No raw ONNX candidates";

            if (TryDecodeBestRawSsdCandidate(frameWidth, frameHeight, out bestDetection, out candidateCount, out status, out bool isSsdFormat))
                return true;

            if (isSsdFormat)
                return false;

            Tensor<float> outputTensor = GetYoloCompatibleOutputTensor(out string outputReason);
            if (outputTensor == null)
            {
                status = outputReason;
                return false;
            }

            using Tensor<float> readable = outputTensor.ReadbackAndClone();
            float[] data = readable.DownloadToArray();
            var shape = readable.shape;

            if (shape.rank != 3 || shape[0] != 1)
            {
                status = $"Unexpected output shape: {shape}";
                return false;
            }

            int dim1 = shape[1];
            int dim2 = shape[2];
            bool featuresFirst = dim1 <= 256 && dim2 > dim1;
            int totalCandidates = featuresFirst ? dim2 : dim1;
            int featureCount = featuresFirst ? dim1 : dim2;
            if (featureCount < 5)
            {
                status = $"Unexpected feature count: {featureCount}";
                return false;
            }

            int availableClassAndExtraFeatures = featureCount - 4;
            int classCount = Mathf.Clamp(onnxNumClasses, 1, availableClassAndExtraFeatures);
            if (onnxNumClasses > availableClassAndExtraFeatures)
            {
                status = $"Configured onnxNumClasses={onnxNumClasses} exceeds available output features ({availableClassAndExtraFeatures})";
                return false;
            }

            float bestScore = float.MinValue;
            Rect bestRect = default;

            for (int candidate = 0; candidate < totalCandidates; candidate++)
            {
                float cx = GetOutputValue(data, featuresFirst, totalCandidates, featureCount, candidate, 0);
                float cy = GetOutputValue(data, featuresFirst, totalCandidates, featureCount, candidate, 1);
                float width = GetOutputValue(data, featuresFirst, totalCandidates, featureCount, candidate, 2);
                float height = GetOutputValue(data, featuresFirst, totalCandidates, featureCount, candidate, 3);

                float bestClassScore = 0f;
                for (int classIndex = 0; classIndex < classCount; classIndex++)
                {
                    float classScore = GetOutputValue(data, featuresFirst, totalCandidates, featureCount, candidate, 4 + classIndex);
                    if (classScore > bestClassScore)
                        bestClassScore = classScore;
                }

                Rect bbox = ConvertYoloToRect(cx, cy, width, height, frameWidth, frameHeight);
                if (bbox.width <= 1f || bbox.height <= 1f)
                    continue;

                candidateCount++;
                if (bestClassScore > bestScore)
                {
                    bestScore = bestClassScore;
                    bestRect = bbox;
                }
            }

            if (candidateCount == 0)
                return false;

            bestDetection = new YoloDetection
            {
                bbox = bestRect,
                score = Mathf.Clamp01(bestScore)
            };
            status = "OK";
            return true;
        }

        private bool TryDecodeBestRawSsdCandidate(int frameWidth, int frameHeight, out YoloDetection bestDetection, out int candidateCount, out string status, out bool isSsdFormat)
        {
            bestDetection = default;
            candidateCount = 0;
            status = "No raw SSD candidates";
            isSsdFormat = false;

            if (_runtimeModel == null || _runtimeModel.outputs == null || _runtimeModel.outputs.Count == 0)
                return false;

            bool hasBboxes = _runtimeModel.outputs.Any(output => string.Equals(output.name, "bboxes", StringComparison.OrdinalIgnoreCase));
            bool hasScores = _runtimeModel.outputs.Any(output => string.Equals(output.name, "scores", StringComparison.OrdinalIgnoreCase));
            if (!hasBboxes || !hasScores)
                return false;

            isSsdFormat = true;

            Tensor<float> bboxesTensor = _worker.PeekOutput("bboxes") as Tensor<float>;
            Tensor<float> scoresTensor = _worker.PeekOutput("scores") as Tensor<float>;
            if (bboxesTensor == null || scoresTensor == null)
            {
                status = "SSD outputs invalid: expected float tensors for bboxes/scores";
                return false;
            }

            using Tensor<float> bboxesReadable = bboxesTensor.ReadbackAndClone();
            using Tensor<float> scoresReadable = scoresTensor.ReadbackAndClone();

            var bboxesShape = bboxesReadable.shape;
            var scoresShape = scoresReadable.shape;
            if (bboxesShape.rank != 3 || bboxesShape[2] != 4 || scoresShape.rank != 2)
            {
                status = $"Unsupported SSD output shapes: bboxes={bboxesShape}, scores={scoresShape}";
                return false;
            }

            float[] bboxesData = bboxesReadable.DownloadToArray();
            float[] scoresData = scoresReadable.DownloadToArray();
            int total = Mathf.Min(bboxesShape[1], scoresShape[1]);
            if (total <= 0)
            {
                status = "SSD returned zero candidates";
                return false;
            }

            float bestScore = float.MinValue;
            Rect bestRect = default;

            for (int i = 0; i < total; i++)
            {
                int baseIndex = i * 4;
                Rect rect = ConvertSsdBoxToRect(
                    bboxesData[baseIndex],
                    bboxesData[baseIndex + 1],
                    bboxesData[baseIndex + 2],
                    bboxesData[baseIndex + 3],
                    frameWidth,
                    frameHeight);

                if (rect.width <= 1f || rect.height <= 1f)
                    continue;

                candidateCount++;
                float score = scoresData[i];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestRect = rect;
                }
            }

            if (candidateCount == 0)
                return false;

            bestDetection = new YoloDetection
            {
                bbox = bestRect,
                score = Mathf.Clamp01(bestScore)
            };
            status = "OK";
            return true;
        }

        private DetectionResult DetectWithOnnx(Texture2D frame)
        {
            if (_worker == null || _runtimeModel == null)
                return null;

            int frameWidth = frame.width;
            int frameHeight = frame.height;
            var totalWatch = Stopwatch.StartNew();

            try
            {
                using var inputTensor = PreprocessFrame(frame, inferenceInputSize);

                var inferenceWatch = Stopwatch.StartNew();
                _worker.Schedule(inputTensor);
                inferenceWatch.Stop();

                if (!TryDecodeBestDetection(frameWidth, frameHeight, out YoloDetection bestDetection, out string decodeStatus, out List<YoloDetection> debugCandidates))
                {
                    totalWatch.Stop();
                    return BuildEmptyOnnxResult(frameWidth, frameHeight, (float)totalWatch.Elapsed.TotalMilliseconds, (float)inferenceWatch.Elapsed.TotalMilliseconds, decodeStatus, debugCandidates);
                }

                BuildStage1DebugArrays(debugCandidates, out Vector2[] stage1Centers, out Rect[] stage1Boxes, out float[] stage1Scores);

                int[] keyColumns = BuildUniformKeyColumns(bestDetection.bbox, frameWidth, keyCountForMapping);
                int keyCount = keyColumns.Length > 1 ? keyColumns.Length - 1 : 0;
                float finalConfidence = Mathf.Clamp01(bestDetection.score);

                totalWatch.Stop();

                return new DetectionResult
                {
                    polygon = RectToPolygon(bestDetection.bbox),
                    pose = Pose.identity,
                    confidence = finalConfidence,
                    keyColumns = keyColumns,
                    keyCount = keyCount,
                    processingTimeMs = (float)totalWatch.Elapsed.TotalMilliseconds,
                    gradientMean = 0f,
                    gradientMax = 0f,
                    detectionThreshold = 0f,
                    isTrackingStable = finalConfidence >= onnxConfidenceThreshold,
                    reprojectionError = Mathf.Lerp(8f, 1.5f, finalConfidence),
                    statusMessage = finalConfidence >= onnxConfidenceThreshold
                        ? "ONNX detection OK"
                        : "Low-confidence ONNX candidate",
                    boundingBox = bestDetection.bbox,
                    stage1Confidence = bestDetection.score,
                    stage1CandidateCenters = stage1Centers,
                    stage1CandidateBoxes = stage1Boxes,
                    stage1CandidateScores = stage1Scores,
                    stage1CandidateCount = stage1Scores.Length,
                    modelBackend = GetOnnxBackendName(),
                    inferenceTimeMs = (float)inferenceWatch.Elapsed.TotalMilliseconds,
                    stage2KeyCountRaw = 0,
                    periodicityScore = 0f,
                    stage2Roi = new Rect(0f, 0f, 0f, 0f),
                    stage2PeaksRaw = Array.Empty<int>(),
                    stage2ExpectedKeyCount = keyCountForMapping
                };
            }
            catch (Exception ex)
            {
                totalWatch.Stop();
                UnityEngine.Debug.LogWarning($"[PianoDetector] ONNX inference falhou: {ex.Message}");
                return BuildEmptyOnnxResult(
                    frameWidth,
                    frameHeight,
                    (float)totalWatch.Elapsed.TotalMilliseconds,
                    0f,
                    $"Falha ONNX: {ex.Message}");
            }
        }

        private int[] BuildUniformKeyColumns(Rect keyboardArea, int frameWidth, int keyCount)
        {
            int clampedKeyCount = Mathf.Max(1, keyCount);
            int xMin = Mathf.Clamp(Mathf.FloorToInt(keyboardArea.xMin), 0, Mathf.Max(0, frameWidth - 1));
            int xMax = Mathf.Clamp(Mathf.CeilToInt(keyboardArea.xMax), xMin + 1, Mathf.Max(xMin + 1, frameWidth));
            float span = Mathf.Max(1f, xMax - xMin);
            float step = span / clampedKeyCount;

            int[] cols = new int[clampedKeyCount + 1];
            cols[0] = xMin;
            for (int i = 1; i < clampedKeyCount; i++)
                cols[i] = Mathf.Clamp(Mathf.RoundToInt(xMin + (i * step)), cols[i - 1] + 1, xMax - (clampedKeyCount - i));
            cols[clampedKeyCount] = xMax;
            return cols;
        }

        private Tensor<float> PreprocessFrame(Texture2D frame, int inputSize)
        {
            Color[] resizedPixels = ResizePixels(frame, inputSize, inputSize);
            float[] inputData = new float[3 * inputSize * inputSize];
            int planeSize = inputSize * inputSize;

            for (int y = 0; y < inputSize; y++)
            {
                for (int x = 0; x < inputSize; x++)
                {
                    int srcIndex = y * inputSize + x;
                    int dstIndex = y * inputSize + x;
                    Color pixel = resizedPixels[srcIndex];
                    inputData[dstIndex] = pixel.r;
                    inputData[planeSize + dstIndex] = pixel.g;
                    inputData[(2 * planeSize) + dstIndex] = pixel.b;
                }
            }

            return new Tensor<float>(new Unity.InferenceEngine.TensorShape(1, 3, inputSize, inputSize), inputData);
        }

        private Color[] ResizePixels(Texture2D source, int width, int height)
        {
            Color[] sourcePixels = source.GetPixels();
            int sourceWidth = source.width;
            int sourceHeight = source.height;
            Color[] resized = new Color[width * height];

            for (int y = 0; y < height; y++)
            {
                float sourceY = height == 1 ? 0f : (y * (sourceHeight - 1f)) / Mathf.Max(1f, height - 1f);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(sourceY), 0, sourceHeight - 1);
                int y1 = Mathf.Clamp(y0 + 1, 0, sourceHeight - 1);
                float ty = sourceY - y0;
                int dstRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    float sourceX = width == 1 ? 0f : (x * (sourceWidth - 1f)) / Mathf.Max(1f, width - 1f);
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(sourceX), 0, sourceWidth - 1);
                    int x1 = Mathf.Clamp(x0 + 1, 0, sourceWidth - 1);
                    float tx = sourceX - x0;

                    Color topLeft = sourcePixels[(y0 * sourceWidth) + x0];
                    Color topRight = sourcePixels[(y0 * sourceWidth) + x1];
                    Color bottomLeft = sourcePixels[(y1 * sourceWidth) + x0];
                    Color bottomRight = sourcePixels[(y1 * sourceWidth) + x1];
                    Color top = Color.LerpUnclamped(topLeft, topRight, tx);
                    Color bottom = Color.LerpUnclamped(bottomLeft, bottomRight, tx);
                    resized[dstRow + x] = Color.LerpUnclamped(top, bottom, ty);
                }
            }

            return resized;
        }

        private bool TryDecodeBestDetection(int frameWidth, int frameHeight, out YoloDetection bestDetection, out string status, out List<YoloDetection> debugCandidates)
        {
            bestDetection = default;
            status = "No detections above threshold";
            debugCandidates = new List<YoloDetection>();

            if (TryDecodeSsdBestDetection(frameWidth, frameHeight, out bestDetection, out string ssdStatus, out bool isSsdFormat, out List<YoloDetection> ssdDebugCandidates))
            {
                debugCandidates = ssdDebugCandidates;
                status = ssdStatus;
                return true;
            }

            if (isSsdFormat)
            {
                debugCandidates = ssdDebugCandidates;
                status = ssdStatus;
                return false;
            }

            Tensor<float> outputTensor = GetYoloCompatibleOutputTensor(out string outputReason);
            if (outputTensor == null)
            {
                status = outputReason;
                return false;
            }

            return TryDecodeBestDetectionSingleOutput(outputTensor, frameWidth, frameHeight, out bestDetection, out status, out debugCandidates);
        }

        private bool TryDecodeBestDetectionSingleOutput(Tensor<float> outputTensor, int frameWidth, int frameHeight, out YoloDetection bestDetection, out string status, out List<YoloDetection> debugCandidates)
        {
            bestDetection = default;
            status = "No detections above threshold";
            debugCandidates = new List<YoloDetection>();

            using Tensor<float> readable = outputTensor.ReadbackAndClone();
            float[] data = readable.DownloadToArray();
            var shape = readable.shape;

            if (shape.rank != 3)
            {
                status = $"Unexpected output rank: {shape.rank}";
                return false;
            }

            if (shape[0] != 1)
            {
                status = $"Unexpected batch size: {shape[0]}";
                return false;
            }

            int dim1 = shape[1];
            int dim2 = shape[2];
            bool featuresFirst = dim1 <= 256 && dim2 > dim1;
            int candidateCount = featuresFirst ? dim2 : dim1;
            int featureCount = featuresFirst ? dim1 : dim2;

            if (featureCount < 5)
            {
                status = $"Unexpected feature count: {featureCount}";
                return false;
            }

            var detections = new List<YoloDetection>();
            bool hasAnyCandidate = false;
            float bestAnyScore = float.MinValue;
            Rect bestAnyBbox = default;
            int availableClassAndExtraFeatures = featureCount - 4;
            int classCount = Mathf.Clamp(onnxNumClasses, 1, availableClassAndExtraFeatures);

            if (onnxNumClasses > availableClassAndExtraFeatures)
            {
                status = $"Configured onnxNumClasses={onnxNumClasses} exceeds available output features ({availableClassAndExtraFeatures})";
                return false;
            }

            for (int candidate = 0; candidate < candidateCount; candidate++)
            {
                float cx = GetOutputValue(data, featuresFirst, candidateCount, featureCount, candidate, 0);
                float cy = GetOutputValue(data, featuresFirst, candidateCount, featureCount, candidate, 1);
                float width = GetOutputValue(data, featuresFirst, candidateCount, featureCount, candidate, 2);
                float height = GetOutputValue(data, featuresFirst, candidateCount, featureCount, candidate, 3);

                float bestClassScore = 0f;
                for (int classIndex = 0; classIndex < classCount; classIndex++)
                {
                    float classScore = GetOutputValue(data, featuresFirst, candidateCount, featureCount, candidate, 4 + classIndex);
                    if (classScore > bestClassScore)
                        bestClassScore = classScore;
                }

                Rect bbox = ConvertYoloToRect(cx, cy, width, height, frameWidth, frameHeight);
                if (bbox.width <= 1f || bbox.height <= 1f)
                    continue;

                debugCandidates.Add(new YoloDetection
                {
                    bbox = bbox,
                    score = Mathf.Clamp01(bestClassScore)
                });

                if (!hasAnyCandidate || bestClassScore > bestAnyScore)
                {
                    hasAnyCandidate = true;
                    bestAnyScore = bestClassScore;
                    bestAnyBbox = bbox;
                }

                if (bestClassScore < onnxConfidenceThreshold)
                    continue;

                detections.Add(new YoloDetection
                {
                    bbox = bbox,
                    score = bestClassScore
                });
            }

            debugCandidates = debugCandidates
                .OrderByDescending(d => d.score)
                .Take(48)
                .ToList();

            if (detections.Count == 0)
            {
                if (hasAnyCandidate)
                {
                    bestDetection = new YoloDetection
                    {
                        bbox = bestAnyBbox,
                        score = Mathf.Clamp01(bestAnyScore)
                    };
                    status = $"Low-confidence ONNX candidate ({bestAnyScore:0.00})";
                    return true;
                }

                return false;
            }

            bestDetection = ApplyNmsAndSelectBest(detections);
            status = "OK";
            return true;
        }

        private bool TryDecodeSsdBestDetection(int frameWidth, int frameHeight, out YoloDetection bestDetection, out string status, out bool isSsdFormat, out List<YoloDetection> debugCandidates)
        {
            bestDetection = default;
            status = "";
            isSsdFormat = false;
            debugCandidates = new List<YoloDetection>();

            if (_runtimeModel == null || _runtimeModel.outputs == null || _runtimeModel.outputs.Count == 0)
                return false;

            bool hasBboxes = _runtimeModel.outputs.Any(output => string.Equals(output.name, "bboxes", StringComparison.OrdinalIgnoreCase));
            bool hasScores = _runtimeModel.outputs.Any(output => string.Equals(output.name, "scores", StringComparison.OrdinalIgnoreCase));
            if (!hasBboxes || !hasScores)
                return false;

            isSsdFormat = true;

            Tensor<float> bboxesTensor = _worker.PeekOutput("bboxes") as Tensor<float>;
            Tensor<float> scoresTensor = _worker.PeekOutput("scores") as Tensor<float>;
            if (bboxesTensor == null || scoresTensor == null)
            {
                status = "SSD outputs invalid: expected float tensors for bboxes/scores";
                return false;
            }

            using Tensor<float> bboxesReadable = bboxesTensor.ReadbackAndClone();
            using Tensor<float> scoresReadable = scoresTensor.ReadbackAndClone();

            var bboxesShape = bboxesReadable.shape;
            var scoresShape = scoresReadable.shape;
            if (bboxesShape.rank != 3 || bboxesShape[2] != 4 || scoresShape.rank != 2)
            {
                status = $"Unsupported SSD output shapes: bboxes={bboxesShape}, scores={scoresShape}";
                return false;
            }

            float[] bboxesData = bboxesReadable.DownloadToArray();
            float[] scoresData = scoresReadable.DownloadToArray();
            int candidateCount = Mathf.Min(bboxesShape[1], scoresShape[1]);
            if (candidateCount <= 0)
            {
                status = "SSD returned zero candidates";
                return false;
            }

            float bestScore = 0f;
            Rect bestRect = default;
            bool hasAnyCandidate = false;
            float bestAnyScore = float.MinValue;
            Rect bestAnyRect = default;

            for (int i = 0; i < candidateCount; i++)
            {
                float score = scoresData[i];

                int baseIndex = i * 4;
                Rect rect = ConvertSsdBoxToRect(
                    bboxesData[baseIndex],
                    bboxesData[baseIndex + 1],
                    bboxesData[baseIndex + 2],
                    bboxesData[baseIndex + 3],
                    frameWidth,
                    frameHeight);

                if (rect.width <= 1f || rect.height <= 1f)
                    continue;

                debugCandidates.Add(new YoloDetection
                {
                    bbox = rect,
                    score = Mathf.Clamp01(score)
                });

                if (!hasAnyCandidate || score > bestAnyScore)
                {
                    hasAnyCandidate = true;
                    bestAnyScore = score;
                    bestAnyRect = rect;
                }

                if (score < onnxConfidenceThreshold)
                    continue;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRect = rect;
                }
            }

            debugCandidates = debugCandidates
                .OrderByDescending(d => d.score)
                .Take(48)
                .ToList();

            if (bestScore < onnxConfidenceThreshold)
            {
                if (hasAnyCandidate)
                {
                    bestDetection = new YoloDetection
                    {
                        bbox = bestAnyRect,
                        score = Mathf.Clamp01(bestAnyScore)
                    };
                    status = $"Low-confidence SSD candidate ({bestAnyScore:0.00})";
                    return true;
                }

                status = "No SSD detections above threshold";
                return false;
            }

            bestDetection = new YoloDetection
            {
                bbox = bestRect,
                score = bestScore
            };
            status = "OK";
            return true;
        }

        private Rect ConvertSsdBoxToRect(float a, float b, float c, float d, int frameWidth, int frameHeight)
        {
            Rect xyxy = BuildRectFromCorners(a, b, c, d, frameWidth, frameHeight);
            Rect yxyx = BuildRectFromCorners(b, a, d, c, frameWidth, frameHeight);
            return xyxy.width * xyxy.height >= yxyx.width * yxyx.height ? xyxy : yxyx;
        }

        private Rect BuildRectFromCorners(float x1Raw, float y1Raw, float x2Raw, float y2Raw, int frameWidth, int frameHeight)
        {
            float maxAbs = Mathf.Max(Mathf.Abs(x1Raw), Mathf.Abs(y1Raw), Mathf.Abs(x2Raw), Mathf.Abs(y2Raw));
            bool normalized = maxAbs <= 2f;

            float x1 = normalized ? x1Raw * frameWidth : x1Raw;
            float y1 = normalized ? y1Raw * frameHeight : y1Raw;
            float x2 = normalized ? x2Raw * frameWidth : x2Raw;
            float y2 = normalized ? y2Raw * frameHeight : y2Raw;

            float xMin = Mathf.Clamp(Mathf.Min(x1, x2), 0f, frameWidth - 1f);
            float yMin = Mathf.Clamp(Mathf.Min(y1, y2), 0f, frameHeight - 1f);
            float xMax = Mathf.Clamp(Mathf.Max(x1, x2), xMin + 1f, frameWidth);
            float yMax = Mathf.Clamp(Mathf.Max(y1, y2), yMin + 1f, frameHeight);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private float GetOutputValue(float[] data, bool featuresFirst, int candidateCount, int featureCount, int candidate, int feature)
        {
            int index = featuresFirst
                ? (feature * candidateCount) + candidate
                : (candidate * featureCount) + feature;
            return data[index];
        }

        private Tensor<float> GetYoloCompatibleOutputTensor(out string status)
        {
            status = "No YOLO-compatible output tensor found";

            if (_runtimeModel != null && _runtimeModel.outputs != null)
            {
                foreach (var outputInfo in _runtimeModel.outputs)
                {
                    if (string.IsNullOrWhiteSpace(outputInfo.name))
                        continue;

                    Tensor<float> namedTensor = _worker.PeekOutput(outputInfo.name) as Tensor<float>;
                    if (namedTensor == null)
                        continue;

                    if (IsYoloCompatibleOutputShape(namedTensor.shape))
                        return namedTensor;
                }
            }

            Tensor<float> defaultTensor = _worker.PeekOutput() as Tensor<float>;
            if (defaultTensor == null)
            {
                status = "ONNX output is not a float tensor";
                return null;
            }

            if (IsYoloCompatibleOutputShape(defaultTensor.shape))
                return defaultTensor;

            status = $"Unexpected output shape: {defaultTensor.shape}";
            return null;
        }

        private bool IsYoloCompatibleOutputShape(Unity.InferenceEngine.TensorShape shape)
        {
            if (shape.rank != 3 || shape[0] != 1)
                return false;

            int dim1 = shape[1];
            int dim2 = shape[2];
            bool featuresFirst = dim1 <= 256 && dim2 > dim1;
            int featureCount = featuresFirst ? dim1 : dim2;
            return featureCount >= 5;
        }

        private Rect ConvertYoloToRect(float cx, float cy, float width, float height, int frameWidth, int frameHeight)
        {
            bool normalized = Mathf.Abs(cx) <= 2f && Mathf.Abs(cy) <= 2f && Mathf.Abs(width) <= 2f && Mathf.Abs(height) <= 2f;
            float scaleX = normalized ? frameWidth : frameWidth / (float)Mathf.Max(1, inferenceInputSize);
            float scaleY = normalized ? frameHeight : frameHeight / (float)Mathf.Max(1, inferenceInputSize);

            float centerX = cx * scaleX;
            float centerY = cy * scaleY;
            float rectWidth = Mathf.Abs(width * scaleX);
            float rectHeight = Mathf.Abs(height * scaleY);
            float xMin = Mathf.Clamp(centerX - rectWidth * 0.5f, 0f, frameWidth - 1f);
            float yMin = Mathf.Clamp(centerY - rectHeight * 0.5f, 0f, frameHeight - 1f);
            float xMax = Mathf.Clamp(centerX + rectWidth * 0.5f, xMin + 1f, frameWidth);
            float yMax = Mathf.Clamp(centerY + rectHeight * 0.5f, yMin + 1f, frameHeight);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private YoloDetection ApplyNmsAndSelectBest(List<YoloDetection> detections)
        {
            var ordered = detections.OrderByDescending(d => d.score).ToList();
            var kept = new List<YoloDetection>();

            foreach (var detection in ordered)
            {
                bool overlapsExisting = kept.Any(existing => ComputeIoU(existing.bbox, detection.bbox) > onnxIouThreshold);
                if (!overlapsExisting)
                    kept.Add(detection);
            }

            return kept.Count > 0 ? kept[0] : ordered[0];
        }

        private float ComputeIoU(Rect a, Rect b)
        {
            float xMin = Mathf.Max(a.xMin, b.xMin);
            float yMin = Mathf.Max(a.yMin, b.yMin);
            float xMax = Mathf.Min(a.xMax, b.xMax);
            float yMax = Mathf.Min(a.yMax, b.yMax);
            float intersection = Mathf.Max(0f, xMax - xMin) * Mathf.Max(0f, yMax - yMin);
            if (intersection <= 0f)
                return 0f;

            float union = a.width * a.height + b.width * b.height - intersection;
            return union <= 0f ? 0f : intersection / union;
        }

        private void BuildStage1DebugArrays(List<YoloDetection> debugCandidates, out Vector2[] centers, out Rect[] boxes, out float[] scores)
        {
            if (debugCandidates == null || debugCandidates.Count == 0)
            {
                centers = Array.Empty<Vector2>();
                boxes = Array.Empty<Rect>();
                scores = Array.Empty<float>();
                return;
            }

            var ordered = debugCandidates
                .OrderByDescending(d => d.score)
                .Take(48)
                .ToArray();

            centers = new Vector2[ordered.Length];
            boxes = new Rect[ordered.Length];
            scores = new float[ordered.Length];

            for (int i = 0; i < ordered.Length; i++)
            {
                boxes[i] = ordered[i].bbox;
                centers[i] = ordered[i].bbox.center;
                scores[i] = Mathf.Clamp01(ordered[i].score);
            }
        }

        private DetectionResult BuildEmptyOnnxResult(int frameWidth, int frameHeight, float processingTimeMs, float inferenceTimeMs, string statusMessage, List<YoloDetection> debugCandidates = null)
        {
            BuildStage1DebugArrays(debugCandidates, out Vector2[] stage1Centers, out Rect[] stage1Boxes, out float[] stage1Scores);
            return new DetectionResult
            {
                polygon = FullFramePolygon(frameWidth, frameHeight),
                pose = Pose.identity,
                confidence = 0f,
                keyColumns = Array.Empty<int>(),
                keyCount = 0,
                processingTimeMs = processingTimeMs,
                gradientMean = 0f,
                gradientMax = 0f,
                detectionThreshold = 0f,
                isTrackingStable = false,
                reprojectionError = 99f,
                statusMessage = statusMessage,
                boundingBox = new Rect(0f, 0f, 0f, 0f),
                stage1Confidence = 0f,
                stage1CandidateCenters = stage1Centers,
                stage1CandidateBoxes = stage1Boxes,
                stage1CandidateScores = stage1Scores,
                stage1CandidateCount = stage1Scores.Length,
                modelBackend = GetOnnxBackendName(),
                inferenceTimeMs = inferenceTimeMs,
                stage2KeyCountRaw = 0,
                periodicityScore = 0f,
                stage2Roi = new Rect(0f, 0f, 0f, 0f),
                stage2PeaksRaw = Array.Empty<int>(),
                stage2ExpectedKeyCount = keyCountForMapping
            };
        }

        private string GetOnnxBackendName()
        {
            string modelName = onnxModel != null ? onnxModel.name : string.Empty;
            return modelName.IndexOf("candidateb", StringComparison.OrdinalIgnoreCase) >= 0
                ? "ONNX_CandidateB"
                : "ONNX_CandidateA";
        }

        private Vector2[] FullFramePolygon(int width, int height)
        {
            return new[]
            {
                new Vector2(0, 0),
                new Vector2(width, 0),
                new Vector2(width, height),
                new Vector2(0, height)
            };
        }

        private Vector2[] RectToPolygon(Rect rect)
        {
            return new[]
            {
                new Vector2(rect.xMin, rect.yMin),
                new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax),
                new Vector2(rect.xMin, rect.yMax)
            };
        }
    }
}
