namespace SimpleMusic.Music;

public enum Dur { W, H, Q, E, S }

public readonly record struct NoteValue(int Ticks, Dur Dur, bool Dot);

public sealed record BeatChoice(int Ticks, Dur Dur, bool Dot, string Name);

public static class Theory
{
    // Lengths are counted in ticks: 1 tick = a 32nd note. Every note length
    // we offer, dotted or not, is then a whole number of ticks.
    public static int BaseTicks(Dur d) => d switch { Dur.W => 32, Dur.H => 16, Dur.Q => 8, Dur.E => 4, _ => 2 };
    public static int Ticks(Dur d, bool dot) => dot ? BaseTicks(d) * 3 / 2 : BaseTicks(d);

    // Lengths that can be written as one symbol, longest first. Used to split
    // notes that cross a bar line and to fill bars with rests.
    public static readonly NoteValue[] Values =
    {
        new(48, Dur.W, true), new(32, Dur.W, false), new(24, Dur.H, true), new(16, Dur.H, false),
        new(12, Dur.Q, true), new(8, Dur.Q, false), new(6, Dur.E, true), new(4, Dur.E, false),
        new(2, Dur.S, false),
    };

    public static List<NoteValue> Decompose(int ticks)
    {
        var parts = new List<NoteValue>();
        foreach (var v in Values)
        {
            while (ticks >= v.Ticks) { parts.Add(v); ticks -= v.Ticks; }
        }
        return parts;
    }

    // Pitches are "steps": octave * 7 + letter index (C = 0 ... B = 6).
    public const string Letters = "CDEFGAB";
    static readonly int[] Semitones = { 0, 2, 4, 5, 7, 9, 11 };
    public const int E4 = 4 * 7 + 2;        // bottom line of the treble staff
    public const int B4 = 4 * 7 + 6;        // middle line
    public const int MinStep = 3 * 7 + 5;   // A3
    public const int MaxStep = 6 * 7 + 1;   // D6

    public static string StepName(int step) => $"{Letters[step % 7]}{step / 7}";
    public static int Midi(int step, int acc) => (step / 7 + 1) * 12 + Semitones[step % 7] + acc;
    public static double Frequency(int step, int acc) => 440 * Math.Pow(2, (Midi(step, acc) - 69) / 12.0);

    public static string Name(Dur d) => d switch
    {
        Dur.W => "Whole", Dur.H => "Half", Dur.Q => "Quarter", Dur.E => "Eighth", _ => "Sixteenth",
    };

    // Which note counts as one beat, as ticks.
    public static readonly int[] BeatChoices = { 32, 24, 16, 12, 8, 6, 4, 2 };

    // The same choices with the symbol to show for each.
    public static readonly BeatChoice[] BeatOptions = BeatChoices
        .Select(t => Values.First(v => v.Ticks == t))
        .Select(v => new BeatChoice(v.Ticks, v.Dur, v.Dot, BeatName(v.Ticks)))
        .ToArray();

    public static string BeatName(int ticks) => ticks switch
    {
        32 => "Whole note", 24 => "Dotted half note", 16 => "Half note", 12 => "Dotted quarter note",
        8 => "Quarter note", 6 => "Dotted eighth note", 4 => "Eighth note", _ => "Sixteenth note",
    };
}
