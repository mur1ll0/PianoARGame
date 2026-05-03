using System;
using UnityEngine;

namespace PianoARGame
{
    public sealed partial class ArPianoGame
    {
        private const string AndroidMidiImportBridgeClass = "com.pianoargame.MidiImportBridge";
        private const string AndroidMidiImportResultPrefixOk = "OK|";
        private const string AndroidMidiImportResultPrefixError = "ERROR|";

        private bool androidMidiImportInProgress;

        private void ImportMidiOnAndroid()
        {
            if (Application.platform != RuntimePlatform.Android)
            {
                lastError = "Importacao via seletor nativo esta disponivel apenas no Android.";
                return;
            }

            if (androidMidiImportInProgress)
            {
                lastError = "Importacao ja em andamento.";
                return;
            }

            try
            {
                string destinationDirectory = ResolveMidiRoot();
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                if (activity == null)
                {
                    lastError = "Falha ao obter activity Android para importar MIDI.";
                    return;
                }

                using var bridge = new AndroidJavaClass(AndroidMidiImportBridgeClass);
                bridge.CallStatic("pickAndCopyMidi", activity, destinationDirectory, gameObject.name, nameof(OnAndroidMidiImportResult));
                androidMidiImportInProgress = true;
                lastError = "Selecione um ou mais arquivos MIDI para importar.";
            }
            catch (Exception ex)
            {
                androidMidiImportInProgress = false;
                lastError = "Falha ao abrir seletor de arquivo: " + ex.Message;
            }
        }

        // Invocado por UnitySendMessage a partir da activity nativa de importacao.
        public void OnAndroidMidiImportResult(string result)
        {
            androidMidiImportInProgress = false;

            if (string.IsNullOrWhiteSpace(result))
            {
                lastError = "Importacao cancelada.";
                return;
            }

            if (string.Equals(result, "CANCEL", StringComparison.OrdinalIgnoreCase))
            {
                lastError = "Importacao cancelada.";
                return;
            }

            if (result.StartsWith(AndroidMidiImportResultPrefixError, StringComparison.Ordinal))
            {
                lastError = "Falha ao importar MIDI: " + result.Substring(AndroidMidiImportResultPrefixError.Length);
                return;
            }

            if (result.StartsWith(AndroidMidiImportResultPrefixOk, StringComparison.Ordinal))
            {
                string importedInfo = result.Substring(AndroidMidiImportResultPrefixOk.Length);
                LoadMidiList();
                selectedIndex = Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, midiFiles.Count - 1));
                int separatorIndex = importedInfo.IndexOf('|');
                if (separatorIndex > 0)
                {
                    string countText = importedInfo.Substring(0, separatorIndex);
                    string details = importedInfo.Substring(separatorIndex + 1).Replace("\n", ", ");
                    if (int.TryParse(countText, out int importedCount))
                    {
                        string message = importedCount <= 1
                            ? "MIDI importado: " + details
                            : $"MIDIs importados ({importedCount}): {details}";
                        lastError = string.Empty;
                        ShowMidiImportNotification(message);
                        return;
                    }
                }

                lastError = string.Empty;
                ShowMidiImportNotification("MIDI importado: " + importedInfo);
                return;
            }

            lastError = "Resposta inesperada da importacao MIDI: " + result;
        }
    }
}
