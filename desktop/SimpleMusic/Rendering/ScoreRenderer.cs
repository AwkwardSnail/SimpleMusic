using System.Windows;
using System.Windows.Media;
using SimpleMusic.Music;

namespace SimpleMusic.Rendering;

// Colours are named by what they mean; the screen and the PDF each decide
// the actual colour.
public enum Role { Ink, Selected, Playing, Faint, Staff, Muted, Caret, Ghost, GhostLabelBg, Placeholder }

public sealed record DrawOp(Geometry Geometry, Role? Fill, Role? Stroke = null, double Thickness = 0);

public sealed class ScoreRenderOptions
{
    public int? SelectedId;
    public int? PlayingId;
    public (int Sys, double X)? Caret;
    public int? LyricEditId;    // the note whose lyric is being typed (its room is outlined)
    public bool Print;   // leave out everything that only makes sense on screen
}

public static class ScoreRenderer
{
    static DrawOp Fill(Geometry g, Role r) => new(g, r);
    static DrawOp Stroke(Geometry g, Role r, double th) => new(g, null, r, th);

    public static List<DrawOp> Render(Sheet sheet, ScoreLayout L, ScoreRenderOptions o)
    {
        var ops = new List<DrawOp>();

        // Staves, clefs, time signature, bar numbers
        for (int s = 0; s < L.Lines.Count; s++)
        {
            double t = L.Lines[s].StaffTop;
            for (int i = 0; i < 5; i++)
                ops.Add(Stroke(MusicShapes.Line(Metrics.StaffX, t + i * 10, L.Width - Metrics.Right, t + i * 10), Role.Staff, 1));
            ops.Add(Fill(MusicShapes.TrebleClef(Metrics.StaffX + 4, t), Role.Ink));
            if (sheet.Barred && s == 0)
            {
                var (num, den) = sheet.TimeSignature;
                double x = Metrics.StaffX + Metrics.ClefW + Metrics.TsW / 2 - 4;
                ops.Add(Fill(MusicShapes.Text(num.ToString(), "Georgia", FontWeights.Bold, FontStyles.Normal, 25, x, t + 19, MusicShapes.Anchor.Center), Role.Ink));
                ops.Add(Fill(MusicShapes.Text(den.ToString(), "Georgia", FontWeights.Bold, FontStyles.Normal, 25, x, t + 39, MusicShapes.Anchor.Center), Role.Ink));
            }
        }
        foreach (var b in L.BarNumbers)
        {
            if (b.Number > 1)
                ops.Add(Fill(MusicShapes.Text(b.Number.ToString(), "Segoe UI", FontWeights.Normal, FontStyles.Italic, 11, b.X, b.StaffTop - 20), Role.Muted));
        }
        foreach (var b in L.Barlines)
        {
            if (b.Final)
            {
                ops.Add(Stroke(MusicShapes.Line(b.X - 7, b.StaffTop, b.X - 7, b.StaffTop + 40), Role.Ink, 1.3));
                ops.Add(Stroke(MusicShapes.Line(b.X - 2, b.StaffTop, b.X - 2, b.StaffTop + 40), Role.Ink, 4));
            }
            else
            {
                ops.Add(Stroke(MusicShapes.Line(b.X, b.StaffTop, b.X, b.StaffTop + 40), Role.Ink, 1.3));
            }
        }

        // Beams, notes, rests and ties
        foreach (var bm in L.Beams) BeamOps(bm, ops);
        for (int i = 0; i < L.Items.Count; i++)
        {
            var it = L.Items[i];
            var role = ItemRole(it, o);
            if (it.Rest) RestOps(it.Dur, it.Dot, it.Cx, it.StaffTop, role, ops);
            else NoteOps(it, role, ops);

            if (it.TieNext && i + 1 < L.Items.Count)
            {
                var nx = L.Items[i + 1];
                int dir = (it.Beamed ? it.StemUp : it.Step < Theory.B4) ? 1 : -1;
                double y = it.Y + dir * 6;
                if (nx.Sys == it.Sys)
                {
                    ops.Add(Fill(MusicShapes.Tie(it.Cx + 6, nx.Cx - 6, y, dir), role));
                }
                else
                {
                    ops.Add(Fill(MusicShapes.Tie(it.Cx + 6, L.Lines[it.Sys].End + 4, y, dir), role));
                    ops.Add(Fill(MusicShapes.Tie(nx.Cx - 24, nx.Cx - 6, nx.Y + dir * 6, dir), role));
                }
            }
        }

        LyricOps(L, o, ops);
        if (o.Print) return ops;

        if (sheet.Notes.Count == 0)
        {
            var ln = L.Lines[0];
            ops.Add(Fill(MusicShapes.Text("Click anywhere on the staff to place your first note", "Segoe UI",
                FontWeights.Normal, FontStyles.Normal, 15, ln.Left + 16, ln.StaffTop - 18), Role.Muted));
        }
        if (o.Caret is { } c)
        {
            double t = L.Lines[c.Sys].StaffTop;
            ops.Add(Stroke(MusicShapes.Line(c.X, t - 8, c.X, t + 48), Role.Caret, 2));
        }
        return ops;
    }

    static Geometry LyricText(string text, double x, double baseline, double scale)
    {
        var g = MusicShapes.Text(text, Metrics.LyricFont, FontWeights.Normal, FontStyles.Normal, Metrics.LyricSize, x, baseline);
        if (scale >= 0.999) return g;
        var squeezed = new GeometryGroup { Children = { g }, Transform = new ScaleTransform(scale, 1, x, baseline) };
        squeezed.Freeze();
        return squeezed;
    }

    // Each lyric centred under its note; a word that carries on gets a hyphen
    // halfway to the next note's words.
    static void LyricOps(ScoreLayout L, ScoreRenderOptions o, List<DrawOp> ops)
    {
        foreach (var s in L.Lyrics)
        {
            bool editing = !o.Print && o.LyricEditId == s.Note.Id;
            if (editing)
            {
                // The room this lyric has, as a faint box.
                double room = ScoreLayout.RoomHalf(s);
                ops.Add(Stroke(MusicShapes.Rect(s.Cx - room, s.Baseline - 15, room * 2, 21, 3), Role.Placeholder, 1));
            }
            if (s.Text.Length == 0)
            {
                // An empty lyric: a faint short line under the note, on screen only.
                if (!o.Print && !editing)
                    ops.Add(Stroke(MusicShapes.Line(s.Cx - 6, s.Baseline + 2, s.Cx + 6, s.Baseline + 2), Role.Placeholder, 1.2));
                continue;
            }
            var (left, scale, half) = ScoreLayout.LyricPlacement(s);
            if (s.Body.Length > 0) ops.Add(Fill(LyricText(s.Body, left, s.Baseline, scale), Role.Ink));
            if (!s.Hyphen) continue;

            double hw = ScoreLayout.LyricWidth("-");
            double from = s.Cx + half, x = from;
            if (s.Next is { } n && !editing)
            {
                double to = n.Cx - (n.Text.Length > 0 ? ScoreLayout.LyricPlacement(n).Half : 0);
                if (to - from > hw + 4) x = (from + to - hw) / 2;
            }
            ops.Add(Fill(LyricText("-", x, s.Baseline, 1), Role.Ink));
        }
    }

    static Role ItemRole(Item it, ScoreRenderOptions o)
    {
        if (o.Print) return Role.Ink;
        if (it.Virtual || it.Note == null) return Role.Faint;
        if (it.Note.Id == o.PlayingId) return Role.Playing;
        if (it.Note.Id == o.SelectedId) return Role.Selected;
        return Role.Ink;
    }

    public static void NoteOps(Item it, Role role, List<DrawOp> ops)
    {
        double cx = it.Cx, y = it.Y, t = it.StaffTop;
        int pos = it.Step - Theory.E4;
        for (int p = -2; p >= pos; p -= 2)
            ops.Add(Stroke(MusicShapes.Line(cx - 10, t + 40 - p * Metrics.Half, cx + 10, t + 40 - p * Metrics.Half), role, 1.3));
        for (int p = 10; p <= pos; p += 2)
            ops.Add(Stroke(MusicShapes.Line(cx - 10, t + 40 - p * Metrics.Half, cx + 10, t + 40 - p * Metrics.Half), role, 1.3));

        if (it.ShowAcc && it.Note != null)
            ops.Add(Fill(it.Note.Acc > 0 ? MusicShapes.Sharp(cx - 15, y) : MusicShapes.Flat(cx - 15, y), role));

        ops.Add(Fill(MusicShapes.Head(it.Dur, cx, y), role));

        if (it.Dur != Dur.W)
        {
            bool up = it.Beamed ? it.StemUp : it.Step < Theory.B4;
            double mid = t + 20;
            double sx = it.Beamed ? it.StemX : up ? cx + 5.4 : cx - 5.4;
            double ey = it.Beamed ? it.StemEnd : up ? Math.Min(y - 33, mid) : Math.Max(y + 33, mid);
            ops.Add(Stroke(MusicShapes.Line(sx, up ? y - 1 : y + 1, sx, ey), role, 1.4));
            int flags = it.Dur == Dur.E ? 1 : it.Dur == Dur.S ? 2 : 0;
            if (!it.Beamed && flags > 0) ops.Add(Fill(MusicShapes.Flags(flags, sx, ey, up), role));
        }
        if (it.Dot)
        {
            bool onLine = ((pos % 2) + 2) % 2 == 0;
            ops.Add(Fill(MusicShapes.Circle(cx + 11.5, onLine ? y - Metrics.Half : y, 2), role));
        }
    }

    public static void RestOps(Dur dur, bool dot, double cx, double t, Role role, List<DrawOp> ops)
    {
        ops.Add(Fill(MusicShapes.Rest(dur, cx, t), role));
        if (dot) ops.Add(Fill(MusicShapes.Circle(cx + 10, t + 15, 2), role));
    }

    static void BeamOps(Beam bm, List<DrawOp> ops)
    {
        const double gap = 7.5;
        ops.Add(Fill(MusicShapes.BeamBar(bm, bm.X0, bm.X1, 0), Role.Ink));
        var g = bm.Items;
        for (int i = 0; i < g.Count; i++)
        {
            var it = g[i];
            if (it.Dur != Dur.S) continue;
            var prev = i > 0 ? g[i - 1] : null;
            var next = i + 1 < g.Count ? g[i + 1] : null;
            if (next is { Dur: Dur.S })
                ops.Add(Fill(MusicShapes.BeamBar(bm, it.StemX, next.StemX, gap), Role.Ink));
            else if (prev is not { Dur: Dur.S })
                // A lone sixteenth gets a short stub pointing at its neighbour.
                ops.Add(Fill(next != null
                    ? MusicShapes.BeamBar(bm, it.StemX, it.StemX + 9, gap)
                    : MusicShapes.BeamBar(bm, it.StemX - 9, it.StemX, gap), Role.Ink));
        }
    }
}
