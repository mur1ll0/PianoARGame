using System;
using System.IO;
using UnityEngine;

namespace PianoARGame.Services
{
    /// <summary>
    /// Serviço simples para persistir configurações do jogo (ex: pasta de músicas MIDI).
    /// Persistência em JSON no Application.persistentDataPath/config.json
    /// </summary>
    public class ConfigService : MonoBehaviour
    {
        private const string ConfigFileName = "config.json";
        public const int CurrentSchemaVersion = 3;
        public const string GradientBackend = "GradientMVP";
        public const string OnnxCandidateABackend = "ONNX_CandidateA";
        public const string OnnxCandidateBBackend = "ONNX_CandidateB";

        [Serializable]
        private class ConfigData
        {
            public int schemaVersion = CurrentSchemaVersion;
            public string musicFolderPath;
            public string activeModelBackend = OnnxCandidateABackend;
            public CalibrationProfile calibration;
        }

        [Serializable]
        public class CalibrationProfile
        {
            public int schemaVersion = CurrentSchemaVersion;
            public string profileId;
            public string timestampUtc;
            public int frameWidth;
            public int frameHeight;
            public float qualityScore;
            public SerializableVector2[] corners;
            public float[] homography3x3;
            public bool isValid;
        }

        [Serializable]
        public struct SerializableVector2
        {
            public float x;
            public float y;

            public SerializableVector2(float x, float y)
            {
                this.x = x;
                this.y = y;
            }

            public static SerializableVector2 FromUnity(Vector2 value)
            {
                return new SerializableVector2(value.x, value.y);
            }

            public Vector2 ToUnity()
            {
                return new Vector2(x, y);
            }
        }

        private ConfigData data;

        public string DefaultMusicPath => "C:\\Users\\Murillo\\Music\\MIDI";

        void Awake()
        {
            Load();
        }

        public string GetMusicFolderPath()
        {
            if (data == null) Load();
            return string.IsNullOrEmpty(data.musicFolderPath) ? DefaultMusicPath : data.musicFolderPath;
        }

        public CalibrationProfile GetCalibrationProfile()
        {
            if (data == null) Load();
            return data.calibration;
        }

        public string GetActiveModelBackend()
        {
            if (data == null) Load();
            if (string.IsNullOrWhiteSpace(data.activeModelBackend))
            {
                data.activeModelBackend = OnnxCandidateABackend;
                Persist();
            }

            if (!data.activeModelBackend.StartsWith("ONNX", StringComparison.OrdinalIgnoreCase))
            {
                data.activeModelBackend = OnnxCandidateABackend;
                Persist();
            }

            return data.activeModelBackend;
        }

        public bool GetUseOnnxBackendPreference()
        {
            return true;
        }

        public void SetCalibrationProfile(CalibrationProfile profile)
        {
            if (data == null) data = new ConfigData();
            data.calibration = profile;
            Persist();
        }

        public void SetActiveModelBackend(string backend)
        {
            if (data == null) data = new ConfigData();
            data.activeModelBackend = string.IsNullOrWhiteSpace(backend)
                ? OnnxCandidateABackend
                : backend.StartsWith("ONNX", StringComparison.OrdinalIgnoreCase)
                    ? backend
                    : OnnxCandidateABackend;
            Persist();
        }

        public void SetUseOnnxBackendPreference(bool enabled)
        {
            SetActiveModelBackend(OnnxCandidateABackend);
        }

        public void ClearCalibrationProfile()
        {
            if (data == null) data = new ConfigData();
            data.calibration = null;
            Persist();
        }

        public void SetMusicFolderPath(string path)
        {
            if (data == null) data = new ConfigData();
            data.musicFolderPath = path;
            Persist();
        }

        private void Load()
        {
            try
            {
                var file = Path.Combine(Application.persistentDataPath, ConfigFileName);
                if (File.Exists(file))
                {
                    var json = File.ReadAllText(file);
                    data = JsonUtility.FromJson<ConfigData>(json);
                    if (data == null)
                    {
                        data = new ConfigData();
                    }
                }
                else
                {
                    data = new ConfigData { schemaVersion = CurrentSchemaVersion, musicFolderPath = DefaultMusicPath };
                    Persist();
                }

                if (string.IsNullOrWhiteSpace(data.musicFolderPath))
                {
                    data.musicFolderPath = DefaultMusicPath;
                }

                if (string.IsNullOrWhiteSpace(data.activeModelBackend))
                {
                    data.activeModelBackend = OnnxCandidateABackend;
                }

                if (!data.activeModelBackend.StartsWith("ONNX", StringComparison.OrdinalIgnoreCase))
                {
                    data.activeModelBackend = OnnxCandidateABackend;
                }

                if (data.schemaVersion <= 0)
                {
                    data.schemaVersion = CurrentSchemaVersion;
                }

                if (data.calibration != null && data.calibration.schemaVersion <= 0)
                {
                    data.calibration.schemaVersion = data.schemaVersion;
                }
            }
            catch (Exception)
            {
                data = new ConfigData { schemaVersion = CurrentSchemaVersion, musicFolderPath = DefaultMusicPath };
            }
        }

        private void Persist()
        {
            try
            {
                Directory.CreateDirectory(Application.persistentDataPath);
                var file = Path.Combine(Application.persistentDataPath, ConfigFileName);
                var json = JsonUtility.ToJson(data, true);
                File.WriteAllText(file, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Failed to persist config: " + ex.Message);
            }
        }
    }
}
