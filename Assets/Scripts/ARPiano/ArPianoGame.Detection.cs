using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Unity.InferenceEngine;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace PianoARGame
{
    public sealed partial class ArPianoGame
    {
        private readonly object detectionWorkerLogLock = new object();
        private readonly Queue<string> detectionWorkerPendingLogs = new Queue<string>();

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
                activeBackendType = GetEffectiveBackendType();
                worker = new Worker(runtimeModel, activeBackendType);
                outputNames = runtimeModel.outputs.Select(o => o.name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
                ResolveModelInputSize(runtimeModel, fallbackInputSize, out modelInputW, out modelInputH);
                modelInputIsStatic = IsModelInputStatic(runtimeModel);
                activeInputW = modelInputW;
                activeInputH = modelInputH;
                modelDiagnosticsLogged = false;
            }
            catch (Exception ex)
            {
                lastError = "Falha ao carregar ONNX: " + ex.Message;
            }
        }

        private void UpdateTracker()
        {
            if (mainThreadManagedId == 0)
            {
                mainThreadManagedId = Thread.CurrentThread.ManagedThreadId;
            }

            FlushDetectionWorkerLogs();
            UpdateActiveModelInputSizeForState();

            bool useThreading = ShouldUseDetectionThreading();
            if (useThreading)
            {
                ApplyDetectionResultIfAvailable();

                if (detectionThreadInitFailed)
                {
                    lastError = string.IsNullOrWhiteSpace(detectionThreadError)
                        ? "Falha ao iniciar worker de deteccao."
                        : detectionThreadError;

                    float now = Time.realtimeSinceStartup;
                    if (now >= detectionThreadRetryAtTime)
                    {
                        detectionThreadRetryAtTime = now + 1.5f;
                        RequestDetectionWorkerRestart();
                    }

                    return;
                }
            }

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

            if (!useThreading)
            {
                Detection? best = DetectKeyboard();
                ApplyDetectionResult(best);
                return;
            }

            EnsureDetectionWorkerRunning();
            if (!detectionThreadReady)
            {
                return;
            }

            PublishDetectionSnapshot(correctedFramePixels, latestCorrectedFrameWidth, latestCorrectedFrameHeight, frameCount, Time.realtimeSinceStartup);
            LogAndroidThreadingSnapshot(useThreading, "publish_snapshot");

            if (ShouldRunInferenceOnWorkerThread())
            {
                return;
            }

            TryRunMainThreadInferenceFromPreprocessed();
        }

        private void LogAndroidThreadingSnapshot(bool useThreading, string reason)
        {
            if (!enableAndroidThreadDiagnostics || Application.platform != RuntimePlatform.Android)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now < androidDiagnosticsNextLogAt)
            {
                return;
            }

            androidDiagnosticsNextLogAt = now + 2f;

            int preprocessPending;
            lock (detectionPreprocessLock)
            {
                preprocessPending = detectionPreprocessedVersion - detectionPreprocessedAppliedVersion;
            }

            Debug.Log(
                $"[ArPianoGame][AndroidThreadDiag] reason={reason} useThreading={useThreading} " +
                $"threadReady={detectionThreadReady} initFailed={detectionThreadInitFailed} " +
                $"workerInferMode={ShouldRunInferenceOnWorkerThread()} workerInferToggle={enableAndroidWorkerInference} " +
                $"backendCfg={backendType} backendActive={activeBackendType} " +
                $"mainThreadId={mainThreadManagedId} workerThreadId={workerThreadManagedId} " +
                $"renderFpsEma={renderFpsEma:0.0} camFps={measuredCameraFps:0.0} " +
                $"mainInferMs={lastMainThreadInferenceMs:0.00} workerPreMs={lastWorkerPreprocessMs:0.00} " +
                $"mainScheduleMs={lastMainScheduleMs:0.00} mainPickMs={lastMainPickOutputMs:0.00} mainDecodeMs={lastMainDecodeMs:0.00} " +
                $"mainDecodeDownloadMs={lastMainDecodeDownloadMs:0.00} mainDecodeCloneFallback={lastMainDecodeUsedCloneFallback} " +
                $"pendingPre={preprocessPending} detectInterval={detectInterval} " +
                $"inferAge={(Time.realtimeSinceStartup - lastMainThreadInferenceAtTime):0.00}s reqInferInterval={GetRequiredAndroidInferenceIntervalSeconds():0.00}s " +
                $"maxPreAge={androidMaxPreprocessedFrameAgeSeconds:0.00}s " +
                $"input={activeInputW}x{activeInputH} staticInput={modelInputIsStatic} " +
                $"gfxApi={SystemInfo.graphicsDeviceType} cpu={SystemInfo.processorType} cores={SystemInfo.processorCount}");
        }

        private bool ShouldUseDetectionThreading()
        {
            if (Application.platform == RuntimePlatform.Android)
            {
                return enableDetectionThreadingOnAndroid;
            }

            return true;
        }

        private bool ShouldRunInferenceOnWorkerThread()
        {
            if (!ShouldUseDetectionThreading())
            {
                return false;
            }

            return Application.platform == RuntimePlatform.Android && enableAndroidWorkerInference && activeBackendType == BackendType.CPU;
        }

        private BackendType GetEffectiveBackendType()
        {
            if (Application.platform == RuntimePlatform.Android && enableAndroidWorkerInference && ShouldUseDetectionThreading())
            {
                return BackendType.CPU;
            }

            return backendType;
        }

        private void ApplyDetectionResult(Detection? best)
        {
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

        private void TryRunMainThreadInferenceFromPreprocessed()
        {
            if (worker == null)
            {
                return;
            }

            if (!CanRunMainThreadInferenceNow())
            {
                return;
            }

            if (!TryReadLatestPreprocessedInput(out float[] inputData, out int imageWidth, out int imageHeight, out int inputWidth, out int inputHeight, out int frameId, out float timestamp))
            {
                return;
            }

            if (inputWidth < 16 || inputHeight < 16)
            {
                return;
            }

            if (Application.platform == RuntimePlatform.Android)
            {
                float frameAge = Time.realtimeSinceStartup - timestamp;
                if (frameAge > androidMaxPreprocessedFrameAgeSeconds)
                {
                    if (correctedFramePixels != null && correctedFramePixels.Length > 0 && latestCorrectedFrameWidth >= 16 && latestCorrectedFrameHeight >= 16)
                    {
                        PublishDetectionSnapshot(correctedFramePixels, latestCorrectedFrameWidth, latestCorrectedFrameHeight, frameCount, Time.realtimeSinceStartup);
                    }

                    LogAndroidThreadingSnapshot(true, "stale_preprocessed_continue");
                }
            }

            using Tensor<float> inputTensor = new Tensor<float>(new TensorShape(1, 3, inputHeight, inputWidth), inputData);
            float start = Time.realtimeSinceStartup;
            float scheduleStart = start;
            worker.Schedule(inputTensor);
            float scheduleEnd = Time.realtimeSinceStartup;

            float pickStart = scheduleEnd;
            Tensor<float> output = PickDetectionOutput(worker);
            float pickEnd = Time.realtimeSinceStartup;

            GetDecodeLimits(out int maxCandidatesBeforeNms, out int maxKeptAfterNms);
            float decodeStart = pickEnd;
            Detection? best = null;
            if (output != null)
            {
                best = DecodeBest(output, imageWidth, imageHeight, inputWidth, inputHeight, numClasses, confThreshold, iouThreshold, maxCandidatesBeforeNms, maxKeptAfterNms, out bool usedCloneFallback, out float downloadMs);
                lastMainDecodeUsedCloneFallback = usedCloneFallback;
                lastMainDecodeDownloadMs = downloadMs;
                if (best.HasValue)
                {
                    best = ConvertDetectionToTopLeft(best.Value, imageHeight);
                }
            }
            else
            {
                lastMainDecodeUsedCloneFallback = false;
                lastMainDecodeDownloadMs = 0f;
            }
            float decodeEnd = Time.realtimeSinceStartup;

            lastMainScheduleMs = (scheduleEnd - scheduleStart) * 1000f;
            lastMainPickOutputMs = (pickEnd - pickStart) * 1000f;
            lastMainDecodeMs = (decodeEnd - decodeStart) * 1000f;
            lastMainThreadInferenceMs = (Time.realtimeSinceStartup - start) * 1000f;
            lastMainThreadInferenceAtTime = Time.realtimeSinceStartup;

            if (enableAndroidMainInferBreakdown && Application.platform == RuntimePlatform.Android && enableAndroidThreadDiagnostics)
            {
                Debug.Log(
                    $"[ArPianoGame][AndroidInferBreakdown] totalMs={lastMainThreadInferenceMs:0.00} " +
                    $"scheduleMs={lastMainScheduleMs:0.00} pickMs={lastMainPickOutputMs:0.00} decodeMs={lastMainDecodeMs:0.00} " +
                    $"decodeDownloadMs={lastMainDecodeDownloadMs:0.00} decodeCloneFallback={lastMainDecodeUsedCloneFallback} " +
                    $"decodeCap={maxCandidatesBeforeNms}/{maxKeptAfterNms} scanMax={androidDecodeScanMaxCandidates} sample={enableAndroidFastDecodeSampling} " +
                    $"input={inputWidth}x{inputHeight} outputNull={(output == null)}");
            }

            PublishDetectionResult(best, frameId, timestamp, lastMainThreadInferenceMs, lastMainDecodeMs);
            LogAndroidThreadingSnapshot(true, "main_infer");
        }

        private bool CanRunMainThreadInferenceNow()
        {
            if (Application.platform != RuntimePlatform.Android)
            {
                return true;
            }

            float requiredInterval = GetRequiredAndroidInferenceIntervalSeconds();
            float elapsed = Time.realtimeSinceStartup - lastMainThreadInferenceAtTime;
            return elapsed >= requiredInterval;
        }

        private float GetRequiredAndroidInferenceIntervalSeconds()
        {
            float baseInterval = Mathf.Clamp(androidMinInferenceIntervalSeconds, 0.1f, 3f);
            float maxInterval = Mathf.Max(Mathf.Clamp(androidMaxInferenceIntervalSeconds, baseInterval, 6f), baseInterval * 2f, 2f);
            if (Application.platform != RuntimePlatform.Android)
            {
                return baseInterval;
            }

            float costSeconds = Mathf.Max(0f, lastMainThreadInferenceMs / 1000f);
            float multiplier = costSeconds >= 0.35f ? 3f : 2f;
            float dynamicInterval = costSeconds * multiplier;
            return Mathf.Clamp(Mathf.Max(baseInterval, dynamicInterval), baseInterval, maxInterval);
        }

        private void ApplyDetectionResultIfAvailable()
        {
            Detection result;
            bool hasDetection;
            int version;
            float inferenceMs;
            float decodeMs;

            lock (detectionOutputLock)
            {
                if (detectionOutputVersion == detectionOutputAppliedVersion)
                {
                    return;
                }

                detectionOutputAppliedVersion = detectionOutputVersion;
                version = detectionOutputVersion;
                hasDetection = detectionOutputHasDetection;
                result = detectionOutput;
                inferenceMs = detectionOutputInferMs;
                decodeMs = detectionOutputDecodeMs;
            }

            if (version <= 0)
            {
                return;
            }

            if (ShouldRunInferenceOnWorkerThread())
            {
                lastMainThreadInferenceMs = inferenceMs;
                lastMainDecodeMs = decodeMs;
                lastMainDecodeDownloadMs = decodeMs;
                lastMainScheduleMs = Mathf.Max(0f, inferenceMs - decodeMs);
                lastMainPickOutputMs = 0f;
                lastMainThreadInferenceAtTime = Time.realtimeSinceStartup;
            }

            ApplyDetectionResult(hasDetection ? result : (Detection?)null);
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

            if (Application.platform == RuntimePlatform.Android && lastMainThreadInferenceMs >= androidSlowInferenceThresholdMs)
            {
                if (lastMainThreadInferenceMs >= 350f)
                {
                    interval = Mathf.Max(interval, 30);
                }
                else if (lastMainThreadInferenceMs >= 250f)
                {
                    interval = Mathf.Max(interval, 22);
                }
                else if (lastMainThreadInferenceMs >= 180f)
                {
                    interval = Mathf.Max(interval, 16);
                }
                else
                {
                    interval = Mathf.Max(interval, 12);
                }
            }

            return interval;
        }

        private void EnsureDetectionWorkerRunning()
        {
            if (detectionWorkerRestartRequested)
            {
                StopDetectionWorker();
                detectionWorkerRestartRequested = false;
            }

            if (detectionThread != null && detectionThread.IsAlive)
            {
                return;
            }

            if (!modelInitAttempted || runtimeModel == null || worker == null)
            {
                modelInitAttempted = true;
                TryInitializeModel();
            }

            if (runtimeModel == null || worker == null)
            {
                return;
            }

            if (detectionSignal == null)
            {
                detectionSignal = new System.Threading.AutoResetEvent(false);
            }

            detectionThreadShouldRun = true;
            detectionThreadReady = false;
            detectionThreadInitFailed = false;
            detectionThreadError = string.Empty;
            detectionThread = new System.Threading.Thread(DetectionThreadLoop)
            {
                IsBackground = true,
                Name = "ArPianoDetection"
            };
            detectionThread.Start();

            if (enableAndroidThreadDiagnostics && Application.platform == RuntimePlatform.Android)
            {
                Debug.Log($"[ArPianoGame][AndroidThreadDiag] detection worker start requested; threading={ShouldUseDetectionThreading()} backendCfg={backendType} backendActive={activeBackendType} workerInferToggle={enableAndroidWorkerInference}");
            }
        }

        private void RequestDetectionWorkerRestart()
        {
            detectionWorkerRestartRequested = true;
            StopDetectionWorker();
            modelInitAttempted = false;
            runtimeModel = null;
            outputNames = Array.Empty<string>();
            modelInputW = 0;
            modelInputH = 0;
            activeInputW = 0;
            activeInputH = 0;
            modelInputIsStatic = false;
            detectionThreadInitFailed = false;
            detectionThreadError = string.Empty;
            detectionThreadRetryAtTime = 0f;
            detectionWorkerRestartRequested = false;
        }

        private void StopDetectionWorker()
        {
            detectionThreadShouldRun = false;
            detectionSignal?.Set();

            if (detectionThread != null)
            {
                if (!detectionThread.Join(750))
                {
                    Debug.LogWarning("[ArPianoGame] Detection worker did not stop within timeout.");
                }

                detectionThread = null;
            }

            detectionThreadReady = false;
            detectionThreadRetryAtTime = 0f;
            workerThreadManagedId = 0;
            lock (detectionInputLock)
            {
                detectionInputVersion = 0;
            }

            lock (detectionPreprocessLock)
            {
                detectionPreprocessedVersion = 0;
                detectionPreprocessedAppliedVersion = 0;
            }

            lock (detectionOutputLock)
            {
                detectionOutputVersion = 0;
                detectionOutputAppliedVersion = 0;
            }

            if (worker != null)
            {
                worker.Dispose();
                worker = null;
            }
        }

        private void PublishDetectionSnapshot(Color32[] sourcePixels, int width, int height, int frameId, float timestamp)
        {
            int total = width * height;
            lock (detectionInputLock)
            {
                if (detectionInputBuffer == null || detectionInputBuffer.Length != total)
                {
                    detectionInputBuffer = new Color32[total];
                }

                Array.Copy(sourcePixels, detectionInputBuffer, total);
                detectionInputWidth = width;
                detectionInputHeight = height;
                detectionInputFrameId = frameId;
                detectionInputTimestamp = timestamp;
                detectionInputVersion++;
            }

            detectionSignal?.Set();
        }

        private bool TryReadLatestDetectionSnapshot(ref int consumedVersion, ref Color32[] targetBuffer, out int width, out int height, out int frameId, out float timestamp)
        {
            width = 0;
            height = 0;
            frameId = 0;
            timestamp = 0f;

            lock (detectionInputLock)
            {
                if (detectionInputVersion == 0 || detectionInputVersion == consumedVersion || detectionInputBuffer == null)
                {
                    return false;
                }

                consumedVersion = detectionInputVersion;
                int total = detectionInputWidth * detectionInputHeight;
                if (targetBuffer == null || targetBuffer.Length != total)
                {
                    targetBuffer = new Color32[total];
                }

                Array.Copy(detectionInputBuffer, targetBuffer, total);
                width = detectionInputWidth;
                height = detectionInputHeight;
                frameId = detectionInputFrameId;
                timestamp = detectionInputTimestamp;
                return true;
            }
        }

        private void PublishPreprocessedInput(float[] inputData, int imageWidth, int imageHeight, int inputWidth, int inputHeight, int frameId, float timestamp)
        {
            if (inputData == null || inputData.Length == 0)
            {
                return;
            }

            lock (detectionPreprocessLock)
            {
                if (detectionPreprocessedInputBuffer == null || detectionPreprocessedInputBuffer.Length != inputData.Length)
                {
                    detectionPreprocessedInputBuffer = new float[inputData.Length];
                }

                Array.Copy(inputData, detectionPreprocessedInputBuffer, inputData.Length);
                detectionPreprocessedImageWidth = imageWidth;
                detectionPreprocessedImageHeight = imageHeight;
                detectionPreprocessedInputWidth = inputWidth;
                detectionPreprocessedInputHeight = inputHeight;
                detectionPreprocessedFrameId = frameId;
                detectionPreprocessedTimestamp = timestamp;
                detectionPreprocessedVersion++;
            }
        }

        private bool TryReadLatestPreprocessedInput(out float[] inputData, out int imageWidth, out int imageHeight, out int inputWidth, out int inputHeight, out int frameId, out float timestamp)
        {
            inputData = null;
            imageWidth = 0;
            imageHeight = 0;
            inputWidth = 0;
            inputHeight = 0;
            frameId = 0;
            timestamp = 0f;

            lock (detectionPreprocessLock)
            {
                if (detectionPreprocessedVersion == 0 || detectionPreprocessedVersion == detectionPreprocessedAppliedVersion || detectionPreprocessedInputBuffer == null)
                {
                    return false;
                }

                detectionPreprocessedAppliedVersion = detectionPreprocessedVersion;
                inputData = new float[detectionPreprocessedInputBuffer.Length];
                Array.Copy(detectionPreprocessedInputBuffer, inputData, inputData.Length);
                imageWidth = detectionPreprocessedImageWidth;
                imageHeight = detectionPreprocessedImageHeight;
                inputWidth = detectionPreprocessedInputWidth;
                inputHeight = detectionPreprocessedInputHeight;
                frameId = detectionPreprocessedFrameId;
                timestamp = detectionPreprocessedTimestamp;
                return true;
            }
        }

        private void PublishDetectionResult(Detection? detection, int frameId, float timestamp, float inferenceMs, float decodeMs)
        {
            lock (detectionOutputLock)
            {
                detectionOutputHasDetection = detection.HasValue;
                if (detection.HasValue)
                {
                    detectionOutput = detection.Value;
                }

                detectionOutputFrameId = frameId;
                detectionOutputTimestamp = timestamp;
                detectionOutputInferMs = inferenceMs;
                detectionOutputDecodeMs = decodeMs;
                detectionOutputVersion++;
            }
        }

        private void EnqueueDetectionWorkerError(string context, Exception ex)
        {
            string message = $"[ArPianoGame][WorkerError] context={context} threadId={Thread.CurrentThread.ManagedThreadId} backendActive={activeBackendType} backendCfg={backendType} error={ex.Message}\n{ex.StackTrace}";
            lock (detectionWorkerLogLock)
            {
                detectionWorkerPendingLogs.Enqueue(message);
                while (detectionWorkerPendingLogs.Count > 20)
                {
                    detectionWorkerPendingLogs.Dequeue();
                }
            }
        }

        private void FlushDetectionWorkerLogs()
        {
            List<string> pending = null;
            lock (detectionWorkerLogLock)
            {
                if (detectionWorkerPendingLogs.Count == 0)
                {
                    return;
                }

                pending = new List<string>(detectionWorkerPendingLogs.Count);
                while (detectionWorkerPendingLogs.Count > 0)
                {
                    pending.Add(detectionWorkerPendingLogs.Dequeue());
                }
            }

            for (int i = 0; i < pending.Count; i++)
            {
                Debug.LogError(pending[i]);
            }
        }

        private void DetectionThreadLoop()
        {
            Color32[] localPixels = null;
            Color32[] localResized = null;
            float[] localInput = null;
            int consumedVersion = -1;
            BackendType workerBackend = activeBackendType;

            try
            {
                workerThreadManagedId = Thread.CurrentThread.ManagedThreadId;
                detectionThreadReady = true;

                while (detectionThreadShouldRun)
                {
                    if (!TryReadLatestDetectionSnapshot(ref consumedVersion, ref localPixels, out int width, out int height, out int frameId, out float timestamp))
                    {
                        detectionSignal?.WaitOne(100);
                        continue;
                    }

                    try
                    {
                        GetCurrentInputSize(out int inputWidth, out int inputHeight);
                        if (inputWidth < 16 || inputHeight < 16)
                        {
                            continue;
                        }

                        long preStartTicks = Stopwatch.GetTimestamp();
                        BuildPreprocessedInput(localPixels, width, height, inputWidth, inputHeight, ref localResized, ref localInput);
                        long preEndTicks = Stopwatch.GetTimestamp();
                        lastWorkerPreprocessMs = (float)((preEndTicks - preStartTicks) * 1000.0 / Stopwatch.Frequency);

                        if (ShouldRunInferenceOnWorkerThread())
                        {
                            long inferStartTicks = Stopwatch.GetTimestamp();
                            long decodeStartTicks = 0;
                            long decodeEndTicks = 0;
                            Detection? best = null;

                            using (Tensor<float> inputTensor = new Tensor<float>(new TensorShape(1, 3, inputHeight, inputWidth), localInput))
                            {
                                worker.Schedule(inputTensor);
                                Tensor<float> output = PickDetectionOutput(worker);
                                if (output != null)
                                {
                                    decodeStartTicks = Stopwatch.GetTimestamp();
                                    GetDecodeLimits(out int maxCandidatesBeforeNms, out int maxKeptAfterNms);
                                    best = DecodeBest(output, width, height, inputWidth, inputHeight, numClasses, confThreshold, iouThreshold, maxCandidatesBeforeNms, maxKeptAfterNms, out _, out _);
                                    decodeEndTicks = Stopwatch.GetTimestamp();
                                    if (best.HasValue)
                                    {
                                        best = ConvertDetectionToTopLeft(best.Value, height);
                                    }
                                }
                            }

                            long inferEndTicks = Stopwatch.GetTimestamp();
                            float inferMs = (float)((inferEndTicks - inferStartTicks) * 1000.0 / Stopwatch.Frequency);
                            float decodeMs = decodeStartTicks > 0 && decodeEndTicks >= decodeStartTicks
                                ? (float)((decodeEndTicks - decodeStartTicks) * 1000.0 / Stopwatch.Frequency)
                                : 0f;
                            PublishDetectionResult(best, frameId, timestamp, inferMs, decodeMs);
                        }
                        else
                        {
                            PublishPreprocessedInput(localInput, width, height, inputWidth, inputHeight, frameId, timestamp);
                        }
                    }
                    catch (Exception ex)
                    {
                        EnqueueDetectionWorkerError("DetectionThreadLoop.Frame", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                detectionThreadInitFailed = true;
                detectionThreadError = $"Falha no worker de deteccao ({workerBackend}): {ex.Message}";
                detectionThreadReady = false;
                EnqueueDetectionWorkerError("DetectionThreadLoop", ex);
            }
            finally
            {
                detectionThreadReady = false;
                workerThreadManagedId = 0;
            }
        }

        private static void BuildPreprocessedInput(Color32[] sourcePixels, int sourceWidth, int sourceHeight, int inputW, int inputH, ref Color32[] resizedBuffer, ref float[] inputBuffer)
        {
            if (sourcePixels == null || sourcePixels.Length == 0 || sourceWidth < 16 || sourceHeight < 16 || inputW < 16 || inputH < 16)
            {
                return;
            }

            int planeSize = inputW * inputH;
            if (resizedBuffer == null || resizedBuffer.Length != planeSize)
            {
                resizedBuffer = new Color32[planeSize];
            }

            int inputLength = 3 * planeSize;
            if (inputBuffer == null || inputBuffer.Length != inputLength)
            {
                inputBuffer = new float[inputLength];
            }

            ResizePixelsBilinear(sourcePixels, sourceWidth, sourceHeight, inputW, inputH, resizedBuffer);
            for (int i = 0; i < planeSize; i++)
            {
                Color32 p = resizedBuffer[i];
                inputBuffer[i] = p.r / 255f;
                inputBuffer[planeSize + i] = p.g / 255f;
                inputBuffer[(2 * planeSize) + i] = p.b / 255f;
            }
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
            GetCurrentInputSize(out int currentInputWidth, out int currentInputHeight);

            if (correctedFramePixels == null || correctedFramePixels.Length == 0 || correctedWidth < 16 || correctedHeight < 16)
            {
                return null;
            }

            if (currentInputWidth < 16 || currentInputHeight < 16)
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
                Debug.Log($"[ArPianoGame] Model input resolved: {modelInputW}x{modelInputH}, active={currentInputWidth}x{currentInputHeight}, staticInput={modelInputIsStatic}, fallback={fallbackInputSize}, outputs=[{string.Join(", ", outputNames)}]");
                modelDiagnosticsLogged = true;
            }

            using Tensor<float> inputTensor = PreprocessFrame(correctedFramePixels, correctedWidth, correctedHeight, currentInputWidth, currentInputHeight, out float[] inputData, out Color32[] resizedPixels);
            worker.Schedule(inputTensor);
            Tensor<float> output = PickDetectionOutput(worker);
            if (output == null)
            {
                if (ShouldDumpInferenceArtifacts())
                {
                    DumpInferenceArtifacts(correctedWidth, correctedHeight, inputData, resizedPixels, null, null, BuildOutputSummaries());
                }

                return null;
            }

            GetDecodeLimits(out int maxCandidatesBeforeNms, out int maxKeptAfterNms);
            Detection? best = DecodeBest(output, correctedWidth, correctedHeight, currentInputWidth, currentInputHeight, numClasses, confThreshold, iouThreshold, maxCandidatesBeforeNms, maxKeptAfterNms, out _, out _);
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

        private Tensor<float> PickDetectionOutput(Worker activeWorker)
        {
            if (activeWorker == null)
            {
                return null;
            }

            for (int i = 0; i < outputNames.Length; i++)
            {
                Tensor<float> named = activeWorker.PeekOutput(outputNames[i]) as Tensor<float>;
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

            Tensor<float> fallback = activeWorker.PeekOutput() as Tensor<float>;
            return fallback;
        }

        private Detection? DecodeBest(Tensor<float> outputTensor, int imageW, int imageH, int inputW, int inputH, int classes, float conf, float iou, int maxCandidatesBeforeNms, int maxKeptAfterNms, out bool usedCloneFallback, out float downloadMs)
        {
            usedCloneFallback = false;
            downloadMs = 0f;

            TensorShape shape = outputTensor.shape;
            long downloadStartTicks = Stopwatch.GetTimestamp();
            if (!TryDownloadTensorData(outputTensor, ref shape, out float[] data, out usedCloneFallback))
            {
                downloadMs = (float)((Stopwatch.GetTimestamp() - downloadStartTicks) * 1000.0 / Stopwatch.Frequency);
                return null;
            }

            downloadMs = (float)((Stopwatch.GetTimestamp() - downloadStartTicks) * 1000.0 / Stopwatch.Frequency);

            if (shape.rank != 3 || shape[0] != 1)
            {
                return null;
            }

            int dim1 = shape[1];
            int dim2 = shape[2];
            bool featuresFirst = dim1 <= 256 && dim2 > dim1;
            int candidates = featuresFirst ? dim2 : dim1;
            int features = featuresFirst ? dim1 : dim2;
            if (features < 4 + Mathf.Max(1, classes))
            {
                return null;
            }

            int candidateStep = GetDecodeCandidateStep(candidates);
            bool singleClass = classes <= 1;

            decodeCandidates.Clear();
            for (int c = 0; c < candidates; c += candidateStep)
            {
                float bestClass;
                if (singleClass)
                {
                    bestClass = Read(data, c, 4, candidates, features, featuresFirst);
                }
                else
                {
                    bestClass = 0f;
                    for (int cls = 0; cls < classes; cls++)
                    {
                        float s = Read(data, c, 4 + cls, candidates, features, featuresFirst);
                        if (s > bestClass)
                        {
                            bestClass = s;
                        }
                    }
                }

                if (bestClass < conf)
                {
                    continue;
                }

                float cx = Read(data, c, 0, candidates, features, featuresFirst);
                float cy = Read(data, c, 1, candidates, features, featuresFirst);
                float bw = Read(data, c, 2, candidates, features, featuresFirst);
                float bh = Read(data, c, 3, candidates, features, featuresFirst);

                CxCyWhToXyxy(cx, cy, bw, bh, imageW, imageH, inputW, inputH, out float x1, out float y1, out float x2, out float y2);
                if ((x2 - x1) > 1f && (y2 - y1) > 1f)
                {
                    Detection candidate = new Detection(x1, y1, x2, y2, bestClass);
                    if (maxCandidatesBeforeNms > 0)
                    {
                        AddCandidateTopK(decodeCandidates, candidate, maxCandidatesBeforeNms);
                    }
                    else
                    {
                        decodeCandidates.Add(candidate);
                    }
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
                    if (maxKeptAfterNms > 0 && decodeKept.Count >= maxKeptAfterNms)
                    {
                        break;
                    }
                }
            }

            return decodeKept[0];
        }

        private static bool TryDownloadTensorData(Tensor<float> outputTensor, ref TensorShape shape, out float[] data, out bool usedCloneFallback)
        {
            data = null;
            usedCloneFallback = false;

            try
            {
                using Tensor<float> readable = outputTensor.ReadbackAndClone();
                if (readable == null)
                {
                    return false;
                }

                shape = readable.shape;
                data = readable.DownloadToArray();
                return data != null && data.Length > 0;
            }
            catch
            {
                usedCloneFallback = true;
            }

            try
            {
                data = outputTensor.DownloadToArray();
                return data != null && data.Length > 0;
            }
            catch
            {
                data = null;
                return false;
            }
        }

        private int GetDecodeCandidateStep(int candidates)
        {
            if (Application.platform != RuntimePlatform.Android || !enableAndroidFastDecodeSampling)
            {
                return 1;
            }

            int scanLimit = Mathf.Clamp(androidDecodeScanMaxCandidates, 128, 5000);
            if (candidates <= scanLimit)
            {
                return 1;
            }

            return Mathf.Max(1, Mathf.CeilToInt(candidates / (float)scanLimit));
        }

        private static void AddCandidateTopK(List<Detection> collection, Detection candidate, int maxCount)
        {
            if (maxCount <= 0)
            {
                collection.Add(candidate);
                return;
            }

            if (collection.Count < maxCount)
            {
                collection.Add(candidate);
                return;
            }

            int minIndex = 0;
            float minScore = collection[0].score;
            for (int i = 1; i < collection.Count; i++)
            {
                float score = collection[i].score;
                if (score < minScore)
                {
                    minScore = score;
                    minIndex = i;
                }
            }

            if (candidate.score > minScore)
            {
                collection[minIndex] = candidate;
            }
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

        private static bool IsModelInputStatic(Model model)
        {
            if (model == null || model.inputs == null || model.inputs.Count == 0)
            {
                return false;
            }

            DynamicTensorShape shape = model.inputs[0].shape;
            return !shape.isRankDynamic && shape.rank >= 4 && shape.IsStatic();
        }

        private void GetDecodeLimits(out int maxCandidatesBeforeNms, out int maxKeptAfterNms)
        {
            if (Application.platform == RuntimePlatform.Android)
            {
                maxCandidatesBeforeNms = Mathf.Clamp(androidDecodeMaxCandidates, 16, 1000);
                maxKeptAfterNms = Mathf.Clamp(androidDecodeMaxKept, 8, 300);
                return;
            }

            maxCandidatesBeforeNms = 0;
            maxKeptAfterNms = 0;
        }

        private void UpdateActiveModelInputSizeForState()
        {
            if (modelInputW < 16 || modelInputH < 16)
            {
                return;
            }

            int targetW = modelInputW;
            int targetH = modelInputH;

            if (Application.platform == RuntimePlatform.Android && enableAndroidAdaptiveInputSize && !modelInputIsStatic)
            {
                int configured = state == GameState.Game ? androidGameInputSize : androidAlignInputSize;
                int clamped = Mathf.Clamp(configured, 160, Mathf.Min(modelInputW, modelInputH));
                targetW = clamped;
                targetH = clamped;
            }

            if (activeInputW != targetW || activeInputH != targetH)
            {
                activeInputW = targetW;
                activeInputH = targetH;

                lock (detectionPreprocessLock)
                {
                    detectionPreprocessedVersion = 0;
                    detectionPreprocessedAppliedVersion = 0;
                }

                if (enableAndroidThreadDiagnostics && Application.platform == RuntimePlatform.Android)
                {
                    Debug.Log($"[ArPianoGame][AndroidThreadDiag] input profile updated: activeInput={activeInputW}x{activeInputH} staticInput={modelInputIsStatic} state={state}");
                }
            }
        }

        private void GetCurrentInputSize(out int inputW, out int inputH)
        {
            inputW = activeInputW > 0 ? activeInputW : modelInputW;
            inputH = activeInputH > 0 ? activeInputH : modelInputH;
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
