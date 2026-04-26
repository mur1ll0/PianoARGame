using System;
using UnityEngine;
using PianoARGame.Services;

namespace PianoARGame.AR
{
    /// <summary>
    /// Markerless snapshot calibration manager.
    /// Phase-1 scope: capture current frame detection, propose corners, compute simple homography,
    /// and persist calibration profile in ConfigService.
    /// </summary>
    public class CalibrationManager : MonoBehaviour
    {
        [Header("References")]
        public PianoDetector detector;
        public TestWebcamController webcamController;
        public ConfigService configService;

        [Header("Calibration")]
        [Range(0f, 1f)]
        public float minCalibrationConfidence = 0.55f;

        public DetectionResult LastDetection { get; private set; }
        public Vector2[] LastProposedCorners { get; private set; }
        public ConfigService.CalibrationProfile LastProfile { get; private set; }

        public bool RunSingleStepCalibration()
        {
            if (!TryCaptureAndDetect(out var frame, out var detection))
            {
                return false;
            }

            var corners = ProposeKeyboardCorners(frame, detection);
            var profile = ComputeCalibration(frame, detection, corners);
            if (profile == null || !profile.isValid)
            {
                return false;
            }

            LastDetection = detection;
            LastProposedCorners = corners;
            LastProfile = profile;

            if (configService != null)
            {
                configService.SetCalibrationProfile(profile);
            }

            return true;
        }

        public bool TryCaptureAndDetect(out Texture2D frame, out DetectionResult detection)
        {
            frame = null;
            detection = null;

            if (webcamController != null)
            {
                webcamController.SendMessage("CaptureAndDetect", SendMessageOptions.DontRequireReceiver);
                frame = webcamController.LastFrameTexture;
                detection = webcamController.LastDetection;
            }

            if (frame == null || detector == null)
            {
                return false;
            }

            if (detection == null)
            {
                detection = detector.Detect(frame);
            }

            return detection != null;
        }

        public Vector2[] ProposeKeyboardCorners(Texture2D frame, DetectionResult detection)
        {
            if (frame == null || detection == null)
            {
                return Array.Empty<Vector2>();
            }

            if (detection.polygon != null && detection.polygon.Length == 4)
            {
                return detection.polygon;
            }

            float minX = 0f;
            float maxX = frame.width;
            if (detection.keyColumns != null && detection.keyColumns.Length > 1)
            {
                minX = detection.keyColumns[0];
                maxX = detection.keyColumns[detection.keyColumns.Length - 1];
            }

            return new[]
            {
                new Vector2(minX, 0f),
                new Vector2(maxX, 0f),
                new Vector2(maxX, frame.height),
                new Vector2(minX, frame.height)
            };
        }

        public ConfigService.CalibrationProfile ComputeCalibration(Texture2D frame, DetectionResult detection, Vector2[] corners)
        {
            if (frame == null || detection == null || corners == null || corners.Length != 4)
            {
                return null;
            }

            float confidence = detection.confidence;
            bool valid = confidence >= minCalibrationConfidence;

            // Placeholder homography (image -> canonical keyboard plane).
            // We use a simple rectangle mapping matrix for Phase-1 and upgrade in Phase-2/3.
            float[] homography = BuildRectangleHomography(corners, frame.width, frame.height);

            return new ConfigService.CalibrationProfile
            {
                schemaVersion = ConfigService.CurrentSchemaVersion,
                profileId = Guid.NewGuid().ToString("N"),
                timestampUtc = DateTime.UtcNow.ToString("o"),
                frameWidth = frame.width,
                frameHeight = frame.height,
                qualityScore = confidence,
                corners = new[]
                {
                    ConfigService.SerializableVector2.FromUnity(corners[0]),
                    ConfigService.SerializableVector2.FromUnity(corners[1]),
                    ConfigService.SerializableVector2.FromUnity(corners[2]),
                    ConfigService.SerializableVector2.FromUnity(corners[3])
                },
                homography3x3 = homography,
                isValid = valid
            };
        }

        private float[] BuildRectangleHomography(Vector2[] corners, int frameWidth, int frameHeight)
        {
            // Simplified matrix for PR-1/PR-2 baseline. Upgraded to full RANSAC homography later.
            float left = Mathf.Min(Mathf.Min(corners[0].x, corners[1].x), Mathf.Min(corners[2].x, corners[3].x));
            float right = Mathf.Max(Mathf.Max(corners[0].x, corners[1].x), Mathf.Max(corners[2].x, corners[3].x));
            float top = Mathf.Min(Mathf.Min(corners[0].y, corners[1].y), Mathf.Min(corners[2].y, corners[3].y));
            float bottom = Mathf.Max(Mathf.Max(corners[0].y, corners[1].y), Mathf.Max(corners[2].y, corners[3].y));

            float sx = (right - left) > 0.001f ? frameWidth / (right - left) : 1f;
            float sy = (bottom - top) > 0.001f ? frameHeight / (bottom - top) : 1f;

            return new[]
            {
                sx, 0f, -left * sx,
                0f, sy, -top * sy,
                0f, 0f, 1f
            };
        }
    }
}
