using SimpleMusic.Music;

namespace SimpleMusic.Rendering;

public static class Metrics
{
    public const double Half = 5;          // half the distance between staff lines
    public const double PadTop = 8;
    public const double SysH = 140;        // height of one line of music
    public const double StaffOffset = 50;  // staff top inside a line of music
    public const double StaffX = 14;
    public const double ClefW = 50;
    public const double TsW = 34;
    public const double Right = 14;
    public const double BarPadL = 12;
    public const double BarPadR = 8;

    // Lyrics sit under each staff; lines of music get this much extra room for them.
    public const string LyricFont = "Georgia";
    public const double LyricSize = 15;
    public const double LyricBaseline = 70;   // below the staff top, unless low notes need more room
    public const double LyricExtra = 24;
    public const double LyricZone = 63;       // clicks this far below the staff top go to the lyrics

    public static double YAt(double staffTop, int step) => staffTop + 40 - (step - Theory.E4) * Half;
}

// One drawable symbol. A note that crosses a bar line becomes several items.
public sealed class Item
{
    public Note? Note;
    public bool Virtual;        // a rest added only to fill the last bar
    public bool Rest;
    public int Step;
    public Dur Dur;
    public bool Dot;
    public int T;
    public bool First;
    public bool TieNext;
    public bool ShowAcc;
    public int Bar;
    public int Offset;          // ticks from the start of the bar

    public int Sys;
    public double StaffTop, X, W, Cx, Y;

    public bool Beamed, StemUp;
    public double LyricPad;     // extra room after this note so its words fit before the next note's
    public double StemX, StemEnd;
}

// In free writing one unit per note; once divided into bars, one unit per bar.
public sealed class Unit
{
    public bool Measure;
    public int Number;
    public int Used;
    public List<Item> Items { get; } = new();
}

public sealed class Line
{
    public List<Unit> Units { get; } = new();
    public double Natural, Left, StaffTop, End, LyricBaseline;
}

public sealed class Barline
{
    public double X, StaffTop;
    public bool Final;
}

public sealed record BarNumber(double X, double StaffTop, int Number);

public sealed class Beam
{
    public required List<Item> Items;
    public bool Up;
    public double X0, X1, B, Slope;
    public double YAt(double x) => B + Slope * (x - X0);
}

// The lyric under one sung note. Its text is centred on the note and may
// reach out until it meets the next note's words (or note head) or the staff end.
public sealed class LyricSlot
{
    public required Note Note;
    public int Sys;
    public double Cx, Baseline;
    public double Min, Max;              // the staff's ends
    public LyricSlot? Prev, Next;        // sung neighbours on the same staff

    public string Text => Note.Lyric ?? "";
    // A trailing hyphen is drawn between this note and the next, not after the word.
    public bool Hyphen => Text.EndsWith('-') && Text.Length > 1;
    public string Body => Hyphen ? Text[..^1] : Text;
}

public sealed class ScoreLayout
{
    public double Width, Height;
    public double SysH = Metrics.SysH;    // taller when there is room for lyrics
    public List<LyricSlot> Lyrics { get; } = new();
    public List<Line> Lines { get; } = new();
    public List<Item> Items { get; } = new();
    public List<Barline> Barlines { get; } = new();
    public List<BarNumber> BarNumbers { get; } = new();
    public List<Beam> Beams { get; } = new();

    static double NoteWidth(Item it) => 14 + 10 * Math.Sqrt(it.T) + (it.Dot ? 6 : 0) + (it.ShowAcc ? 10 : 0);
    static double ItemWidth(Item it) => NoteWidth(it) + it.LyricPad;
    static double UnitWidth(Unit u) =>
        u.Items.Sum(ItemWidth) + (u.Measure ? Metrics.BarPadL + Metrics.BarPadR : 0);

    // `roomForLyrics`: leave space under every staff even without lyrics (on
    // screen, so there is always somewhere to type them).
    public static ScoreLayout Compute(Sheet sheet, double width, bool roomForLyrics = true)
    {
        var units = BuildUnits(sheet);
        MakeRoomForLyrics(units);
        var L = new ScoreLayout { Width = width };
        double right = width - Metrics.Right;

        double LeftOf(int sys) => Metrics.StaffX + Metrics.ClefW + (sheet.Barred && sys == 0 ? Metrics.TsW : 0);
        Line NewLine()
        {
            var ln = new Line { Left = LeftOf(L.Lines.Count) };
            L.Lines.Add(ln);
            return ln;
        }

        var line = NewLine();
        foreach (var u in units)
        {
            double uw = UnitWidth(u);
            if (line.Units.Count > 0 && line.Natural + uw > right - line.Left) line = NewLine();
            line.Units.Add(u);
            line.Natural += uw;
        }
        int musicLines = L.Lines.Count;
        if (roomForLyrics || sheet.Notes.Any(n => !n.Rest && n.Lyric != null)) L.SysH = Metrics.SysH + Metrics.LyricExtra;

        for (int s = 0; s < L.Lines.Count; s++)
        {
            var ln = L.Lines[s];
            double avail = right - ln.Left;
            bool isLast = s == musicLines - 1;
            // Full lines are stretched to the right edge; the last one keeps its natural spacing.
            double scale = ln.Natural > 0 && (!isLast || ln.Natural > avail) ? avail / ln.Natural : 1;
            double staffTop = Metrics.PadTop + s * L.SysH + Metrics.StaffOffset;
            ln.StaffTop = staffTop;
            double x = ln.Left;
            for (int ui = 0; ui < ln.Units.Count; ui++)
            {
                var u = ln.Units[ui];
                if (u.Measure)
                {
                    if (ui == 0) L.BarNumbers.Add(new BarNumber(Metrics.StaffX + 2, staffTop, u.Number));
                    x += Metrics.BarPadL * scale;
                }
                foreach (var it in u.Items)
                {
                    it.Sys = s;
                    it.StaffTop = staffTop;
                    it.X = x;
                    it.W = ItemWidth(it) * scale;
                    it.Cx = x + (it.ShowAcc ? 10 : 0) + 10;
                    it.Y = it.Rest ? staffTop + 20 : Metrics.YAt(staffTop, it.Step);
                    L.Items.Add(it);
                    x += it.W;
                }
                if (u.Measure)
                {
                    x += Metrics.BarPadR * scale;
                    L.Barlines.Add(new Barline { X = x, StaffTop = staffTop });
                }
            }
            ln.End = x;
        }
        if (L.Barlines.Count > 0) L.Barlines[^1].Final = true;

        foreach (var g in BeamGroups(sheet, L.Items)) L.Beams.Add(LayoutBeam(g));

        foreach (var ln in L.Lines)
            ln.LyricBaseline = Math.Max(ln.StaffTop + Metrics.LyricBaseline, LowestInk(L, L.Lines.IndexOf(ln)) + 16);

        LyricSlot? prev = null;
        foreach (var it in L.Items)
        {
            if (it.Rest || !it.First || it.Note == null) continue;
            var slot = new LyricSlot
            {
                Note = it.Note, Sys = it.Sys, Cx = it.Cx, Baseline = L.Lines[it.Sys].LyricBaseline,
                Min = Metrics.StaffX, Max = right,
            };
            if (prev != null && prev.Sys == slot.Sys) { slot.Prev = prev; prev.Next = slot; }
            L.Lyrics.Add(slot);
            prev = slot;
        }

        L.Height = Metrics.PadTop + L.Lines.Count * L.SysH;
        return L;
    }

    // The lowest point the music reaches on a line (note heads, ledger
    // lines, downward stems), so lyrics never collide with it.
    static double LowestInk(ScoreLayout L, int sys)
    {
        double staffTop = L.Lines[sys].StaffTop;
        double lowest = staffTop + 40;
        foreach (var it in L.Items)
        {
            if (it.Sys != sys || it.Rest) continue;
            lowest = Math.Max(lowest, it.Y + 5);
            bool down = it.Beamed ? !it.StemUp : it.Step >= Theory.B4;
            if (it.Dur == Dur.W || !down) continue;
            lowest = Math.Max(lowest, it.Beamed ? it.StemEnd + 5 : Math.Max(it.Y + 33, staffTop + 20));
        }
        return lowest;
    }

    /* ------------------------- Lyric room under each note ------------------------- */

    const double Gap = 9;          // between neighbouring words
    const double HeadClear = 7;    // an empty neighbour still keeps its note head clear

    public static double LyricWidth(string s) =>
        s.Length == 0 ? 0 : MusicShapes.TextWidth(s, Metrics.LyricFont, Metrics.LyricSize);

    // How far a lyric may reach to each side of its note. Each neighbour keeps
    // the room its own words need (up to half the distance between the notes),
    // so two neighbouring lyrics never run into each other.
    public static double RoomHalf(LyricSlot s)
    {
        static double Occupied(LyricSlot n, double d) => Math.Max(HeadClear, Math.Min(LyricWidth(n.Body) / 2, d / 2));
        double left = s.Prev is { } p ? s.Cx - p.Cx - Occupied(p, s.Cx - p.Cx) - Gap : s.Cx - s.Min;
        double right = s.Next is { } n ? n.Cx - s.Cx - Occupied(n, n.Cx - s.Cx) - Gap : s.Max - s.Cx;
        return Math.Max(4, Math.Min(left, right));
    }

    // Notes move apart to make room for their words (as in printed music), so
    // while typing a lyric only has to fit on the staff.
    public bool LyricFits(LyricSlot s, string text)
    {
        string body = text.EndsWith('-') && text.Length > 1 ? text[..^1] : text;
        return LyricWidth(body) <= Width - Metrics.Right - Metrics.StaffX - 2 * Gap;
    }

    // Spreads the notes so each lyric keeps its full size: the distance from a
    // sung note to the next one is at least half of each one's words, plus a
    // gap (and room for a hyphen when the word carries on).
    static void MakeRoomForLyrics(List<Unit> units)
    {
        var items = units.SelectMany(u => u.Items).ToList();
        static bool Sung(Item it) => it.First && !it.Rest && it.Note != null;
        static double Half(Item it)
        {
            var text = it.Note!.Lyric ?? "";
            if (text.EndsWith('-') && text.Length > 1) text = text[..^1];
            return Math.Max(HeadClear, LyricWidth(text) / 2);
        }
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (!Sung(it) || string.IsNullOrEmpty(it.Note!.Lyric)) continue;
            int j = i + 1;
            double between = NoteWidth(it);   // distance to the next sung note as spaced now
            while (j < items.Count && !Sung(items[j])) between += NoteWidth(items[j++]);
            if (j >= items.Count) continue;
            bool hyphen = it.Note.Lyric.EndsWith('-') && it.Note.Lyric.Length > 1;
            double need = Half(it) + Gap + Half(items[j]) + (hyphen ? LyricWidth("-") + Gap : 0);
            it.LyricPad = Math.Max(0, need - between);
        }
    }

    // Where a lyric's words are drawn. Words that fitted when typed can run out
    // of room later (bars added, a narrower window, the printed page); they are
    // then squeezed sideways to fit.
    public static (double Left, double Scale, double Half) LyricPlacement(LyricSlot s)
    {
        double w = LyricWidth(s.Body);
        double scale = w / 2 > RoomHalf(s) ? RoomHalf(s) * 2 / w : 1;
        return (s.Cx - w * scale / 2, scale, w * scale / 2);
    }

    // The x of the text cursor after `chars` characters of a lyric.
    public static double LyricCaretX(LyricSlot s, int chars)
    {
        var (left, scale, half) = LyricPlacement(s);
        if (chars >= s.Body.Length) return s.Body.Length == 0 ? s.Cx : s.Cx + half + (chars > s.Body.Length ? LyricWidth("-") : 0);
        return left + LyricWidth(s.Text[..chars]) * scale;
    }

    // The character position nearest to a click at x.
    public static int LyricIndexAt(LyricSlot s, double x)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int c = 0; c <= s.Text.Length; c++)
        {
            double d = Math.Abs(LyricCaretX(s, c) - x);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    // The sung note whose lyric is nearest to a click under staff `sys`.
    public LyricSlot? LyricSlotAt(int sys, double x) =>
        Lyrics.Where(l => l.Sys == sys).MinBy(l => Math.Abs(l.Cx - x));

    /* ----------------------- Bars (this is where they happen) ----------------------- */

    static List<Unit> BuildUnits(Sheet sheet)
    {
        var units = new List<Unit>();
        if (sheet.Notes.Count == 0) return units;

        if (!sheet.Barred)
        {
            foreach (var n in sheet.Notes)
            {
                var u = new Unit();
                u.Items.Add(MakeItem(n, new NoteValue(n.Ticks, n.Dur, n.Dot), true, false));
                units.Add(u);
            }
            return units;
        }

        int barTicks = sheet.BarTicks;
        Unit NewBar()
        {
            var b = new Unit { Measure = true, Number = units.Count + 1 };
            units.Add(b);
            return b;
        }
        var bar = NewBar();

        foreach (var n in sheet.Notes)
        {
            int remaining = n.Ticks;
            bool first = true;
            while (remaining > 0)
            {
                if (bar.Used == barTicks) bar = NewBar();
                int take = Math.Min(barTicks - bar.Used, remaining);
                foreach (var part in Theory.Decompose(take))
                {
                    remaining -= part.Ticks;
                    var it = MakeItem(n, part, first, !n.Rest && remaining > 0);
                    it.Bar = bar.Number;
                    it.Offset = bar.Used;
                    bar.Items.Add(it);
                    bar.Used += part.Ticks;
                    first = false;
                }
            }
        }

        if (bar.Used < barTicks && sheet.FillLast)
        {
            // Shorter rests first, so the silence grows towards the end of the bar.
            var parts = Theory.Decompose(barTicks - bar.Used);
            parts.Reverse();
            foreach (var p in parts)
            {
                bar.Items.Add(new Item
                {
                    Virtual = true, Rest = true, Dur = p.Dur, Dot = p.Dot, T = p.Ticks,
                    First = true, Bar = bar.Number, Offset = bar.Used,
                });
                bar.Used += p.Ticks;
            }
        }
        return units;
    }

    static Item MakeItem(Note n, NoteValue part, bool first, bool tieNext) => new()
    {
        Note = n, Rest = n.Rest, Step = n.Step,
        Dur = part.Dur, Dot = part.Dot, T = part.Ticks,
        First = first, TieNext = tieNext, ShowAcc = first && !n.Rest && n.Acc != 0,
    };

    /* ----------------------------------- Beams ----------------------------------- */

    static bool Beamable(Item it) => !it.Rest && !it.Virtual && (it.Dur == Dur.E || it.Dur == Dur.S);

    // Free writing: every run of eighths/sixteenths is joined. Once divided
    // into bars, runs are also cut at bar lines and at each beat (for short
    // beats like an eighth, the whole bar is one group).
    static List<List<Item>> BeamGroups(Sheet sheet, List<Item> items)
    {
        int groupTicks = sheet.BeatTicks >= 8 ? sheet.BeatTicks : sheet.BarTicks;
        var groups = new List<List<Item>>();
        var cur = new List<Item>();
        void Flush()
        {
            if (cur.Count > 1) groups.Add(cur);
            cur = new List<Item>();
        }
        foreach (var it in items)
        {
            if (!Beamable(it)) { Flush(); continue; }
            if (cur.Count > 0)
            {
                var prev = cur[^1];
                bool split = prev.Sys != it.Sys || (sheet.Barred &&
                    (prev.Bar != it.Bar || prev.Offset / groupTicks != it.Offset / groupTicks));
                if (split) Flush();
            }
            cur.Add(it);
        }
        Flush();
        return groups;
    }

    // Decide the stem direction of a group and fit a gently sloped beam
    // that keeps every stem at least a normal stem length.
    static Beam LayoutBeam(List<Item> g)
    {
        bool up = Theory.B4 - g.Min(i => i.Step) > g.Max(i => i.Step) - Theory.B4;
        double StemX(Item it) => it.Cx + (up ? 5.4 : -5.4);
        var first = g[0];
        var last = g[^1];
        double x0 = StemX(first), x1 = StemX(last);
        double slope = x1 > x0 ? Math.Clamp((last.Y - first.Y) / (x1 - x0), -0.25, 0.25) * 0.5 : 0;
        double mid = first.StaffTop + 20;
        double b = up ? double.PositiveInfinity : double.NegativeInfinity;
        foreach (var it in g)
        {
            double dx = StemX(it) - x0;
            foreach (var v in new[] { (up ? it.Y - 31 : it.Y + 31) - slope * dx, mid - slope * dx })
                b = up ? Math.Min(b, v) : Math.Max(b, v);
        }
        var beam = new Beam { Items = g, Up = up, X0 = x0, X1 = x1, B = b, Slope = slope };
        foreach (var it in g)
        {
            it.Beamed = true;
            it.StemUp = up;
            it.StemX = StemX(it);
            it.StemEnd = beam.YAt(it.StemX);
        }
        return beam;
    }

    // Where the insertion cursor is drawn.
    public (int Sys, double X) CursorPosition(IReadOnlyList<Note> notes, int cursor)
    {
        if (cursor < notes.Count)
        {
            var it = Items.FirstOrDefault(i => i.Note == notes[cursor] && i.First);
            if (it != null) return (it.Sys, it.X + 1);
        }
        var last = Items.LastOrDefault(i => !i.Virtual);
        if (last == null) return (0, Lines[0].Left + 6);
        return (last.Sys, last.X + last.W - 2);
    }
}
