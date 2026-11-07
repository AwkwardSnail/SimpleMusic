using System.Diagnostics;
using System.Media;
using System.Windows.Threading;
using SimpleMusic.Music;

namespace SimpleMusic.Audio;

// A tiny synthesiser: renders the melody to an in-memory WAV and plays it.
public static class Synth
{
    public const int Rate = 44100;

    public readonly record struct Tone(double Frequency, double Seconds, double SoundSeconds);

    public static byte[] Wav(IReadOnlyList<Tone> tones, double volume)
    {
        long total = tones.Sum(t => (long)(t.Seconds * Rate)) + Rate / 10;
        var samples = new double[total];
        long pos = 0;
        foreach (var tone in tones)
        {
            if (tone.Frequency > 0) AddTone(samples, pos, tone, volume);
            pos += (long)(tone.Seconds * Rate);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = (int)total * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write((short)Math.Clamp(s * 32767, -32768, 32767));
        w.Flush();
        return ms.ToArray();
    }

    // Triangle wave with a short attack and a gentle decay, like a soft keyboard.
    static void AddTone(double[] samples, long start, Tone tone, double vol)
    {
        double d = tone.SoundSeconds;
        const double attack = 0.012;
        double hold = Math.Max(0.02, d * 0.6);
        int n = (int)(d * Rate);
        double phase = 0, inc = tone.Frequency / Rate;
        for (int i = 0; i < n && start + i < samples.Length; i++)
        {
            double t = (double)i / Rate;
            double env = t < attack ? vol * t / attack
                : t < hold ? vol - vol * 0.5 * (t - attack) / (hold - attack)
                : d > hold ? vol * 0.5 * (1 - (t - hold) / (d - hold)) : 0;
            samples[start + i] += (4 * Math.Abs(phase - 0.5) - 1) * env;
            phase += inc;
            if (phase >= 1) phase -= 1;
        }
    }
}

public sealed class Player
{
    readonly SoundPlayer music = new();
    readonly SoundPlayer preview = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    readonly Stopwatch clock = new();
    readonly List<(int Id, double Start)> marks = new();
    double total;
    int? current;

    public bool IsPlaying { get; private set; }
    public event Action<int?>? NoteChanged;   // which note is sounding now (null = none)
    public event Action? Stopped;

    public Player() => timer.Tick += (_, _) => Tick();

    public void Play(IReadOnlyList<Note> notes, double secondsPerTick)
    {
        Stop();
        if (notes.Count == 0) return;
        var tones = new List<Synth.Tone>();
        marks.Clear();
        double t = 0;
        foreach (var n in notes)
        {
            double d = n.Ticks * secondsPerTick;
            marks.Add((n.Id, t));
            tones.Add(new Synth.Tone(n.Rest ? 0 : Theory.Frequency(n.Step, n.Acc), d, d * 0.95));
            t += d;
        }
        total = t;
        try
        {
            music.Stream = new MemoryStream(Synth.Wav(tones, 0.25));
            music.Load();
            music.Play();
        }
        catch (Exception) { /* no audio device: still run the highlight */ }
        clock.Restart();
        IsPlaying = true;
        current = null;
        Tick();
        timer.Start();
    }

    void Tick()
    {
        double e = clock.Elapsed.TotalSeconds;
        if (e >= total) { Stop(); return; }
        int? id = null;
        foreach (var m in marks)
        {
            if (m.Start > e) break;
            id = m.Id;
        }
        if (id != current)
        {
            current = id;
            NoteChanged?.Invoke(id);
        }
    }

    public void Stop()
    {
        if (!IsPlaying) return;
        timer.Stop();
        music.Stop();
        IsPlaying = false;
        current = null;
        NoteChanged?.Invoke(null);
        Stopped?.Invoke();
    }

    public void Preview(Note n)
    {
        if (IsPlaying) return;
        try
        {
            preview.Stream = new MemoryStream(Synth.Wav(new[] { new Synth.Tone(Theory.Frequency(n.Step, n.Acc), 0.3, 0.3) }, 0.18));
            preview.Play();
        }
        catch (Exception) { /* no audio device */ }
    }
}
