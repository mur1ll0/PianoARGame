using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace PianoARGame.AR
{
    [Serializable]
    public class HighScoreEntry
    {
        public string songId;
        public string timestamp;
        public int score;
        public float accuracy;
    }

    /// <summary>
    /// Gerencia pontuação e persistência de HighScores.
    /// </summary>
    public class ScoreManager : MonoBehaviour
    {
        private int currentScore = 0;
        private List<HighScoreEntry> highscores = new List<HighScoreEntry>();

        public void StartSession(string songId)
        {
            currentScore = 0;
        }

        public void AddHit(int index, int points)
        {
            currentScore += points;
        }

        public int GetCurrentScore() => currentScore;

        public void SaveHighScore(string songId)
        {
            var entry = new HighScoreEntry{
                songId = songId,
                timestamp = DateTime.UtcNow.ToString("o"),
                score = currentScore,
                accuracy = 0f
            };
            highscores.Add(entry);
            Persist();
        }

        private void Persist()
        {
            var dir = Path.Combine(Application.persistentDataPath, "HighScores");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "highscores.json");
            var json = JsonUtility.ToJson(new Wrapper{ items = highscores }, true);
            File.WriteAllText(file, json);
        }

        [Serializable]
        private class Wrapper { public List<HighScoreEntry> items; }
    }
}
