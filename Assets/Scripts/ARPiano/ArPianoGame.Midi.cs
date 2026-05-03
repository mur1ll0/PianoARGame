using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PianoARGame.Services;
using UnityEngine;

namespace PianoARGame
{
    public sealed partial class ArPianoGame
    {
        private void LoadMidiList()
        {
            midiFiles.Clear();
            try
            {
                string root = ResolveMidiRoot();
                EnsureMidiRootDirectoryExists(root);

                IEnumerable<string> files = MidiRepository.EnumerateMidiFilesSafe(root, true)
                    .Where(f =>
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        return ext == ".mid" || ext == ".midi";
                    })
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

                midiFiles.AddRange(files);
                lastError = string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                lastError = "Sem permissao para ler a pasta MIDI no Android. Abra Configuracoes e selecione outra pasta.";
            }
            catch (Exception ex)
            {
                lastError = "Falha ao listar MIDI: " + ex.Message;
            }
        }

        private string ResolveMidiRoot()
        {
            if (Application.platform == RuntimePlatform.Android)
            {
                return GetAndroidAppMidiDirectory();
            }

            if (!string.IsNullOrWhiteSpace(activeMidiRoot))
            {
                return activeMidiRoot;
            }

            return midiDirectoryDesktop;
        }

        private void InitializeMidiRepository()
        {
            activeMidiRoot = ResolveMidiRoot();
            midiDirectoryInput = activeMidiRoot;
            LoadMidiList();
        }

        private void ApplyMidiRepository(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            if (Application.platform == RuntimePlatform.Android)
            {
                // Android always uses app-owned storage for reliable scoped-storage behavior.
                activeMidiRoot = GetAndroidAppMidiDirectory();
                midiDirectoryInput = activeMidiRoot;
                selectedIndex = 0;
                menuScroll = 0;
                menuScrollPos = Vector2.zero;
                LoadMidiList();
                return;
            }

            activeMidiRoot = root.Trim();
            midiDirectoryInput = activeMidiRoot;
            selectedIndex = 0;
            menuScroll = 0;
            menuScrollPos = Vector2.zero;
            LoadMidiList();
        }

        private string GetAndroidAppMidiDirectory()
        {
            string root = Path.Combine(Application.persistentDataPath, "MIDI");
            EnsureMidiRootDirectoryExists(root);
            return root;
        }

        private static void EnsureMidiRootDirectoryExists(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
            }
        }

        private static void LoadMidiEvents(string midiPath, List<MidiNoteEvent> outEvents, out float duration)
        {
            var midiFile = MidiFile.Read(midiPath);
            TempoMap tempoMap = midiFile.GetTempoMap();
            var active = new Dictionary<(int note, int channel), Queue<(double start, int velocity)>>();
            var tempEvents = new List<(MidiNoteEvent ev, int channel)>();
            var channelPitchSum = new Dictionary<int, double>();
            var channelPitchCount = new Dictionary<int, int>();

            long absoluteTicks = 0;
            foreach (TrackChunk chunk in midiFile.GetTrackChunks())
            {
                absoluteTicks = 0;
                foreach (MidiEvent evt in chunk.Events)
                {
                    absoluteTicks += evt.DeltaTime;

                    if (!(evt is NoteOnEvent on))
                    {
                        if (evt is NoteOffEvent off)
                        {
                            HandleNoteOff(off.NoteNumber, off.Channel, absoluteTicks, tempoMap, active, tempEvents);
                        }

                        continue;
                    }

                    if (on.Velocity > 0)
                    {
                        double start = MetricTimeToSeconds(TimeConverter.ConvertTo<MetricTimeSpan>(absoluteTicks, tempoMap));
                        (int note, int channel) key = (on.NoteNumber, on.Channel);
                        if (!active.TryGetValue(key, out Queue<(double start, int velocity)> queue))
                        {
                            queue = new Queue<(double start, int velocity)>();
                            active[key] = queue;
                        }

                        queue.Enqueue((start, on.Velocity));

                        if (!channelPitchSum.ContainsKey(on.Channel))
                        {
                            channelPitchSum[on.Channel] = 0.0;
                            channelPitchCount[on.Channel] = 0;
                        }

                        channelPitchSum[on.Channel] += on.NoteNumber;
                        channelPitchCount[on.Channel] += 1;
                    }
                    else
                    {
                        HandleNoteOff(on.NoteNumber, on.Channel, absoluteTicks, tempoMap, active, tempEvents);
                    }
                }
            }

            foreach (KeyValuePair<(int note, int channel), Queue<(double start, int velocity)>> pair in active)
            {
                while (pair.Value.Count > 0)
                {
                    (double start, int velocity) note = pair.Value.Dequeue();
                    var ev = new MidiNoteEvent
                    {
                        pitch = pair.Key.note,
                        start = (float)note.start,
                        end = (float)(note.start + 0.25),
                        velocity = note.velocity,
                        hand = pair.Key.note >= 60 ? 'R' : 'L'
                    };
                    tempEvents.Add((ev, pair.Key.channel));
                }
            }

            var channelToHand = BuildChannelHandMap(channelPitchSum, channelPitchCount);

            outEvents.Clear();
            for (int i = 0; i < tempEvents.Count; i++)
            {
                MidiNoteEvent ev = tempEvents[i].ev;
                int channel = tempEvents[i].channel;
                if (channelToHand.TryGetValue(channel, out char hand))
                {
                    ev.hand = hand;
                }
                else
                {
                    ev.hand = ev.pitch >= 60 ? 'R' : 'L';
                }

                outEvents.Add(ev);
            }

            outEvents.Sort((a, b) => a.start.CompareTo(b.start));
            duration = outEvents.Count == 0 ? 0f : outEvents.Max(e => e.end);
        }

        private static void HandleNoteOff(int noteNumber, int channel, long absoluteTicks, TempoMap tempoMap,
            Dictionary<(int note, int channel), Queue<(double start, int velocity)>> active,
            List<(MidiNoteEvent ev, int channel)> tempEvents)
        {
            (int note, int channel) key = (noteNumber, channel);
            if (!active.TryGetValue(key, out Queue<(double start, int velocity)> queue) || queue.Count == 0)
            {
                return;
            }

            (double start, int velocity) startInfo = queue.Dequeue();
            double end = MetricTimeToSeconds(TimeConverter.ConvertTo<MetricTimeSpan>(absoluteTicks, tempoMap));
            if (end <= startInfo.start)
            {
                return;
            }

            var ev = new MidiNoteEvent
            {
                pitch = noteNumber,
                start = (float)startInfo.start,
                end = (float)end,
                velocity = startInfo.velocity,
                hand = noteNumber >= 60 ? 'R' : 'L'
            };
            tempEvents.Add((ev, channel));
        }

        private static Dictionary<int, char> BuildChannelHandMap(Dictionary<int, double> sum, Dictionary<int, int> count)
        {
            var averages = new List<(int channel, double avg)>();
            foreach (KeyValuePair<int, int> kv in count)
            {
                if (kv.Value <= 0)
                {
                    continue;
                }

                averages.Add((kv.Key, sum[kv.Key] / kv.Value));
            }

            var map = new Dictionary<int, char>();
            if (averages.Count < 2)
            {
                return map;
            }

            averages.Sort((a, b) => a.avg.CompareTo(b.avg));
            int split = averages.Count / 2;
            for (int i = 0; i < averages.Count; i++)
            {
                map[averages[i].channel] = i < split ? 'L' : 'R';
            }

            return map;
        }

        private static double MetricTimeToSeconds(MetricTimeSpan metric)
        {
            return metric.Hours * 3600.0 + metric.Minutes * 60.0 + metric.Seconds + metric.Milliseconds / 1000.0;
        }
    }
}
