using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace PianoARGame.Services
{
    internal static class MidiRepository
    {
        public static string[] BuildAndroidRootCandidates(string configuredDownloadsPath, string streamingAssetsPath, string streamingSubFolder, string persistentDataPath, bool includeSharedStorage)
        {
            var candidates = new List<string>();

            // Always keep app-owned and bundled locations first.
            candidates.Add(Path.Combine(persistentDataPath, "MIDI"));
            candidates.Add(Path.Combine(streamingAssetsPath, streamingSubFolder));

            if (includeSharedStorage && !string.IsNullOrWhiteSpace(configuredDownloadsPath))
            {
                candidates.Add(configuredDownloadsPath.Trim());
            }

            if (includeSharedStorage)
            {
                candidates.Add("/storage/emulated/0/Download");
                candidates.Add("/sdcard/Download");
                candidates.Add("/storage/emulated/0/Music");
            }

            return candidates
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static IEnumerable<string> EnumerateMidiFilesSafe(string root, bool recursive)
        {
            var files = new List<string>();

            if (!recursive)
            {
                try
                {
                    string[] localFiles = Directory.GetFiles(root);
                    for (int i = 0; i < localFiles.Length; i++)
                    {
                        files.Add(localFiles[i]);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }

                return files;
            }

            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                try
                {
                    string[] localFiles = Directory.GetFiles(current);
                    for (int i = 0; i < localFiles.Length; i++)
                    {
                        files.Add(localFiles[i]);
                    }

                    string[] localDirectories = Directory.GetDirectories(current);
                    for (int i = 0; i < localDirectories.Length; i++)
                    {
                        pending.Push(localDirectories[i]);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }
            }

            return files;
        }

        public static string FindFirstExistingPath(IEnumerable<string> candidates, string fallback)
        {
            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return fallback;
        }
    }
}
