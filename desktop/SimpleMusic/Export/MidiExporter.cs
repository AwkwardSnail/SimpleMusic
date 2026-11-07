using System.Text;
using SimpleMusic.Music;

namespace SimpleMusic.Export;

// Writes the melody as a Standard MIDI File (format 0, one track, piano),
// so it can be played or opened in other music programs.
public static class MidiExporter
{
    const int PerQuarter = 480;              // MIDI ticks per quarter note
    const int PerTick = PerQuarter / 8;      // one SimpleMusic tick is a 32nd note
    const byte Velocity = 80;

    public static void Export(Sheet sheet, string path) => File.WriteAllBytes(path, Build(sheet));

    public static byte[] Build(Sheet sheet)
    {
        var track = new List<byte>();
        void Meta(byte type, byte[] data)
        {
            track.AddRange(new byte[] { 0, 0xFF, type });
            track.AddRange(VarLen(data.Length));
            track.AddRange(data);
        }

        Meta(0x03, Encoding.UTF8.GetBytes(sheet.DisplayTitle));            // track name

        // The tempo slider counts beats of the chosen beat note; MIDI wants
        // microseconds per quarter note.
        int usPerQuarter = (int)Math.Round(60_000_000.0 * 8 / (sheet.Tempo * sheet.BeatTicks));
        Meta(0x51, new[] { (byte)(usPerQuarter >> 16), (byte)(usPerQuarter >> 8), (byte)usPerQuarter });

        if (sheet.Barred)
        {
            var (num, den) = sheet.TimeSignature;
            int clocksPerBeat = sheet.BeatTicks * 3;                         // 24 MIDI clocks = one quarter
            Meta(0x58, new[] { (byte)num, (byte)Math.Log2(den), (byte)clocksPerBeat, (byte)8 });
        }
        if (!string.IsNullOrWhiteSpace(sheet.Lyrics))
            Meta(0x01, Encoding.UTF8.GetBytes(sheet.Lyrics.Trim()));         // lyrics as a text note

        track.AddRange(new byte[] { 0, 0xC0, 0 });                           // acoustic grand piano

        int wait = 0;                                                        // time since the last event
        foreach (var n in sheet.Notes)
        {
            int length = n.Ticks * PerTick;
            if (n.Rest) { wait += length; continue; }
            byte key = (byte)Math.Clamp(Theory.Midi(n.Step, n.Acc), 0, 127);
            track.AddRange(VarLen(wait));
            track.AddRange(new byte[] { 0x90, key, Velocity });
            track.AddRange(VarLen(length));
            track.AddRange(new byte[] { 0x80, key, 0 });
            wait = 0;
        }
        track.AddRange(VarLen(wait));                                        // keep trailing rests
        track.AddRange(new byte[] { 0xFF, 0x2F, 0 });                        // end of track

        var file = new List<byte>();
        file.AddRange("MThd"u8.ToArray());
        file.AddRange(BigEndian(6, 4));
        file.AddRange(BigEndian(0, 2));                                      // format 0
        file.AddRange(BigEndian(1, 2));                                      // one track
        file.AddRange(BigEndian(PerQuarter, 2));
        file.AddRange("MTrk"u8.ToArray());
        file.AddRange(BigEndian(track.Count, 4));
        file.AddRange(track);
        return file.ToArray();
    }

    static byte[] BigEndian(int value, int bytes)
    {
        var b = new byte[bytes];
        for (int i = 0; i < bytes; i++) b[bytes - 1 - i] = (byte)(value >> (8 * i));
        return b;
    }

    // MIDI's variable-length numbers: 7 bits per byte, high bit = "more follows".
    static byte[] VarLen(int value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        while ((value >>= 7) > 0) bytes.Insert(0, (byte)((value & 0x7F) | 0x80));
        return bytes.ToArray();
    }
}
