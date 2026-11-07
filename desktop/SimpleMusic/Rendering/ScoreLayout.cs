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

public sealed record LyricWord(string Text, int Start, double X, double Width);

// The lyrics under one staff: which part of the text it shows and where each word is.
public sealed class LyricLine
{
    public int Sys, Start, End;
    public double Baseline, Left;
    public List<LyricWord> Words { get; } = new();
}

public sealed class ScoreLayout
{
    public double Width, Height;
    public double SysH = Metrics.SysH;    // taller when there is room for lyrics
    public double Space;                  // width of a space in the lyric font
    public List<LyricLine> Lyrics { get; } = new();
    public List<Line> Lines { get; } = new();
    public List<Item> Items { get; } = new();
    public List<Barline> Barlines { get; } = new();
    public List<BarNumber> BarNumbers { get; } = new();
    public List<Beam> Beams { get; } = new();

    static double ItemWidth(Item it) => 14 + 10 * Math.Sqrt(it.T) + (it.Dot ? 6 : 0) + (it.ShowAcc ? 10 : 0);
    static double UnitWidth(Unit u) =>
        u.Items.Sum(ItemWidth) + (u.Measure ? Metrics.BarPadL + Metrics.BarPadR : 0);

    // `roomForLyrics`: leave space under every staff even without lyrics (on
    // screen, so there is always somewhere to type them).
    public static ScoreLayout Compute(Sheet sheet, double width, bool roomForLyrics = true)
    {
        var units = BuildUnits(sheet);
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

        // Lyrics flow from staff to staff; if they need more staves than the
        // music has, empty staves are added to carry them.
        L.Space = MusicShapes.TextWidth(" ", Metrics.LyricFont, Metrics.LyricSize);
        var rows = WrapLyrics(sheet.Lyrics, sys => right - LeftOf(sys), L.Space);
        while (L.Lines.Count < rows.Count) NewLine();
        if (roomForLyrics || !string.IsNullOrWhiteSpace(sheet.Lyrics)) L.SysH = Metrics.SysH + Metrics.LyricExtra;

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

        for (int s = 0; s < rows.Count; s++)
        {
            var row = rows[s];
            var ln = L.Lines[s];
            double avail = right - ln.Left;
            // Lines that wrapped are spread across the staff. Lines that end where the
            // text ends or breaks keep the spaces exactly as typed, so spaces can be
            // used to line words up under notes.
            bool justify = !row.EndsParagraph && row.Words.Count > 1;
            double lead = row.Words.Count > 0 ? L.Space * (row.Words[0].Start - row.Start) : 0;
            double gap = justify ? (avail - lead - row.Words.Sum(w => w.Width)) / (row.Words.Count - 1) : 0;
            var lyric = new LyricLine { Sys = s, Baseline = ln.LyricBaseline, Left = ln.Left, Start = row.Start, End = row.End };
            double x = ln.Left;
            int prevEnd = row.Start;
            for (int i = 0; i < row.Words.Count; i++)
            {
                var w = row.Words[i];
                x += i == 0 || !justify ? L.Space * (w.Start - prevEnd) : gap;
                lyric.Words.Add(new LyricWord(sheet.Lyrics.Substring(w.Start, w.Length), w.Start, x, w.Width));
                x += w.Width;
                prevEnd = w.Start + w.Length;
            }
            L.Lyrics.Add(lyric);
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

    sealed record WordSpan(int Start, int Length, double Width);
    sealed record LyricRow(int Start, int End, List<WordSpan> Words, bool EndsParagraph);

    // Splits the lyrics into one row per staff, remembering where each word
    // sits in the text. Each line break starts a new staff; long lines wrap
    // onto the next staff.
    static List<LyricRow> WrapLyrics(string text, Func<int, double> widthOf, double space)
    {
        var rows = new List<LyricRow>();
        int pos = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', pos);
            int paraEnd = nl < 0 ? text.Length : nl;

            var words = new List<WordSpan>();
            for (int j = pos; j < paraEnd;)
            {
                if (char.IsWhiteSpace(text[j])) { j++; continue; }
                int k = j;
                while (k < paraEnd && !char.IsWhiteSpace(text[k])) k++;
                words.Add(new WordSpan(j, k - j, MusicShapes.TextWidth(text[j..k], Metrics.LyricFont, Metrics.LyricSize)));
                j = k;
            }

            if (words.Count == 0) rows.Add(new LyricRow(pos, paraEnd, words, true));
            int w = 0, rowStart = pos;
            while (w < words.Count)
            {
                double avail = widthOf(rows.Count);
                var row = new List<WordSpan>();
                double used = 0;
                int prevEnd = rowStart;
                while (w < words.Count)
                {
                    // Every typed space counts, so lined-up lyrics wrap where they really end.
                    double need = used + space * (words[w].Start - prevEnd) + words[w].Width;
                    if (row.Count > 0 && need > avail) break;
                    row.Add(words[w]);
                    used = need;
                    prevEnd = words[w].Start + words[w].Length;
                    w++;
                }
                bool last = w == words.Count;
                rows.Add(new LyricRow(rowStart, last ? paraEnd : row[^1].Start + row[^1].Length, row, last));
                if (!last) rowStart = words[w].Start;
            }

            if (nl < 0) break;
            pos = nl + 1;
        }
        return rows;
    }

    /* -------------------- Lyric cursor (typing on the score) -------------------- */

    static double PrefixWidth(LyricWord w, int chars) =>
        chars <= 0 ? 0 : chars >= w.Text.Length ? w.Width : MusicShapes.TextWidth(w.Text[..chars], Metrics.LyricFont, Metrics.LyricSize);

    // Where the text cursor is drawn for a position in the lyrics.
    public (int Sys, double X, double Baseline) LyricCaret(int index)
    {
        LyricLine? prev = null;
        foreach (var row in Lyrics)
        {
            if (index < row.Start && prev != null) return (prev.Sys, RowEndX(prev), prev.Baseline);
            if (index >= row.Start && index <= row.End) return (row.Sys, XInRow(row, index), row.Baseline);
            prev = row;
        }
        var lastRow = Lyrics[^1];
        return (lastRow.Sys, RowEndX(lastRow), lastRow.Baseline);
    }

    double XInRow(LyricLine row, int index)
    {
        double x = row.Left;
        int end = row.Start;
        foreach (var w in row.Words)
        {
            if (index < w.Start) return Math.Min(w.X, x + Space * (index - end));
            if (index <= w.Start + w.Text.Length) return w.X + PrefixWidth(w, index - w.Start);
            x = w.X + w.Width;
            end = w.Start + w.Text.Length;
        }
        return x + Space * Math.Max(0, index - end);
    }

    double RowEndX(LyricLine row) => XInRow(row, row.End);

    // The text position closest to a click under staff `sys`, or -1 when that
    // staff has no lyric row yet.
    public int LyricIndexAt(int sys, double x)
    {
        if (sys >= Lyrics.Count) return -1;
        var row = Lyrics[sys];
        foreach (var w in row.Words)
        {
            if (x >= w.X + w.Width) continue;
            if (x <= w.X) return w.Start;
            int best = 0;
            double bestD = double.MaxValue;
            for (int c = 0; c <= w.Text.Length; c++)
            {
                double d = Math.Abs(w.X + PrefixWidth(w, c) - x);
                if (d < bestD) { bestD = d; best = c; }
            }
            return w.Start + best;
        }
        return row.End;
    }

    public (int Start, int End) LyricRowRange(int sys) => (Lyrics[sys].Start, Lyrics[sys].End);

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
