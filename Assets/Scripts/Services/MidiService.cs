using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using PianoARGame.AR;

namespace PianoARGame.Services
{
    /// <summary>
    /// Serviço para listar e carregar arquivos MIDI a partir da pasta configurada (ou StreamingAssets fallback).
    /// Usa MidiLoader.Load para parsear arquivos.
    /// </summary>
    public class MidiService : MonoBehaviour
    {
        public ConfigService configService;

        public string[] GetMidiFiles()
        {
            var path = GetMusicFolderPath();
            if (string.IsNullOrEmpty(path)) return new string[0];

            try
            {
                if (Directory.Exists(path))
                {
                    return Directory.GetFiles(path, "*.mid").OrderBy(f => f).ToArray();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("MidiService:GetMidiFiles failed: " + ex.Message);
            }

            // fallback: StreamingAssets/MIDI
            var sa = Path.Combine(Application.dataPath, "StreamingAssets", "MIDI");
            if (Directory.Exists(sa)) return Directory.GetFiles(sa, "*.mid").OrderBy(f => f).ToArray();
            return new string[0];
        }

        public MidiSong LoadMidi(string filepath)
        {
            try
            {
                return MidiLoader.Load(filepath);
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed to load MIDI: " + ex.Message);
                return null;
            }
        }

        private string GetMusicFolderPath()
        {
            if (configService != null) return configService.GetMusicFolderPath();
            // default
            return Path.Combine(Application.dataPath, "StreamingAssets", "MIDI");
        }
    }
}
