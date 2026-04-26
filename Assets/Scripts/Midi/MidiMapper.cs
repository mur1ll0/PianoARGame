using System;
using System.Collections.Generic;
using PianoARGame.AR;

namespace PianoARGame.Midi
{
    /// <summary>
    /// Representa uma nota MIDI já mapeada para um índice de tecla.
    /// </summary>
    public class MappedNote
    {
        public MidiNoteEvent noteEvent;
        public int keyIndex;        // índice 0-based dentro do teclado detectado; -1 = fora do alcance
        public double spawnTime;    // tempo (segundos) em que a nota deve ser spawnada (time - leadTime)
    }

    /// <summary>
    /// Associa notas MIDI a índices de tecla detectados pelo KeyEstimator.
    /// A nota MIDI <c>baseMidiNote</c> é mapeada ao índice de tecla 0.
    /// Notas fora do alcance do teclado detectado são descartadas (keyIndex = -1).
    /// </summary>
    public static class MidiMapper
    {
        /// <summary>
        /// Mapeia todas as notas de <paramref name="song"/> para índices de tecla.
        /// </summary>
        /// <param name="song">MidiSong carregada pelo MidiLoader.</param>
        /// <param name="keys">Array de KeyInfo do teclado detectado.</param>
        /// <param name="baseMidiNote">Número MIDI da tecla 0 do teclado (ex: 36 = C2 para 88 teclas).</param>
        /// <param name="leadTimeSeconds">Lead time em segundos; o spawnTime será note.time - leadTime.</param>
        /// <returns>Lista de <see cref="MappedNote"/> ordenada por spawnTime.</returns>
        public static List<MappedNote> MapToKeys(MidiSong song, KeyInfo[] keys, int baseMidiNote, float leadTimeSeconds = 2f)
        {
            if (song == null) throw new ArgumentNullException(nameof(song));
            if (keys == null) throw new ArgumentNullException(nameof(keys));

            int keyCount = keys.Length;
            var result = new List<MappedNote>(song.notes.Count);

            foreach (var note in song.notes)
            {
                int keyIndex = note.noteNumber - baseMidiNote;

                if (keyIndex < 0 || keyIndex >= keyCount)
                    continue; // fora do alcance do teclado

                result.Add(new MappedNote
                {
                    noteEvent  = note,
                    keyIndex   = keyIndex,
                    spawnTime  = note.time - leadTimeSeconds
                });
            }

            result.Sort((a, b) => a.spawnTime.CompareTo(b.spawnTime));
            return result;
        }
    }
}
