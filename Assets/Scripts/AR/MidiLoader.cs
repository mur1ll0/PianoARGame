using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// NOTE: This implementation depends on DryWetMIDI (Melanchall.DryWetMidi).
// Add DryWetMIDI to the project (Assets/Plugins or via UPM/NuGet) before building.
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace PianoARGame.AR
{
    public class MidiNoteEvent
    {
        public double time; // segundos (tempo de onset)
        public int noteNumber; // 0-127
        public int velocity;
    }

    public class MidiSong
    {
        public string name;
        public double duration;
        public List<MidiNoteEvent> notes = new List<MidiNoteEvent>();
    }

    /// <summary>
    /// MidiLoader usando DryWetMIDI para converter eventos em tempos absolutos (segundos).
    /// </summary>
    public static class MidiLoader
    {
        public static MidiSong Load(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException("MIDI file not found", path);

            var midiFile = MidiFile.Read(path);
            var tempoMap = midiFile.GetTempoMap();

            var notes = midiFile.GetNotes().ToList();

            var song = new MidiSong
            {
                name = Path.GetFileNameWithoutExtension(path)
            };

            double lastEnd = 0.0;

            foreach (var note in notes)
            {
                // Convert note start tick to MetricTimeSpan
                var metric = TimeConverter.ConvertTo<MetricTimeSpan>(note.Time, tempoMap);
                double seconds = metric.Hours * 3600.0 + metric.Minutes * 60.0 + metric.Seconds + metric.Milliseconds / 1000.0;

                var evt = new MidiNoteEvent
                {
                    time = seconds,
                    noteNumber = note.NoteNumber,
                    velocity = note.Velocity
                };

                song.notes.Add(evt);

                // compute note end time
                var endTick = note.Time + note.Length;
                var metricEnd = TimeConverter.ConvertTo<MetricTimeSpan>(endTick, tempoMap);
                double endSeconds = metricEnd.Hours * 3600.0 + metricEnd.Minutes * 60.0 + metricEnd.Seconds + metricEnd.Milliseconds / 1000.0;
                if (endSeconds > lastEnd) lastEnd = endSeconds;
            }

            song.notes = song.notes.OrderBy(n => n.time).ToList();
            song.duration = lastEnd;
            return song;
        }
    }
}
