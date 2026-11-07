using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SimpleMusic.Music;
using SimpleMusic.Rendering;

namespace SimpleMusic.Controls;

// The score: draws the sheet and turns clicks, drags and typing into edits.
// Clicking on a staff places notes; clicking under a note types its lyric.
public sealed class ScoreView : FrameworkElement
{
    static readonly Dictionary<Role, Brush> Brushes = new()
    {
        [Role.Ink] = B("#1D1B22"),
        [Role.Selected] = B("#2F5BD3"),
        [Role.Playing] = B("#E2662B"),
        [Role.Faint] = B("#A8A3AE"),
        [Role.Staff] = B("#8F8A96"),
        [Role.Muted] = B("#6B6774"),
        [Role.Caret] = B("#2F5BD3"),
        [Role.Ghost] = B("#2F5BD3"),
        [Role.GhostLabelBg] = B("#E6EDFF"),
        [Role.Placeholder] = B("#C9C4CE"),
    };
    static readonly Dictionary<(Role, double), Pen> Pens = new();

    static Brush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    static Pen PenFor(Role r, double th)
    {
        if (!Pens.TryGetValue((r, th), out var p))
        {
            p = new Pen(Brushes[r], th);
            p.Freeze();
            Pens[(r, th)] = p;
        }
        return p;
    }

    Editor? editor;
    ScoreLayout? layout;
    Point? ghost;
    (Note Note, double StaffTop)? drag;
    bool caretOn = true;
    List<DrawOp>? cachedOps;
    (ScoreLayout?, int?, int?, int?) cacheKey;

    // Lyric typing: the note whose lyric is being typed and the text cursor in it.
    int? lyricNoteId;
    int lyricCaret;

    public event Action? LyricModeChanged;

    public ScoreView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Cross;
        var blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        blink.Tick += (_, _) => { caretOn = !caretOn; InvalidateVisual(); };
        blink.Start();
    }

    public Editor? Editor
    {
        get => editor;
        set { editor = value; Refresh(); }
    }

    public bool IsEditingLyrics => lyricNoteId != null;

    public void Refresh()
    {
        caretOn = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 900 : Math.Max(320, available.Width);
        if (editor == null) return new Size(w, Metrics.SysH);
        layout = ScoreLayout.Compute(editor.Sheet, w);
        if (IsEditingLyrics)
        {
            // The note may have gone (undo, delete); then typing lyrics ends.
            if (EditSlot is { } slot) lyricCaret = Math.Clamp(lyricCaret, 0, slot.Text.Length);
            else Dispatcher.BeginInvoke(StopLyrics);
        }
        return new Size(w, layout.Height);
    }

    LyricSlot? EditSlot => lyricNoteId is int id ? layout?.Lyrics.FirstOrDefault(l => l.Note.Id == id) : null;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(RenderSize));
        if (editor == null || layout == null) return;

        // The score only needs rebuilding when the layout, selection, playing note or
        // lyric line being typed changes; the blinking cursors and hover preview go on top.
        var key = (layout, editor.SelectedId, editor.PlayingId, lyricNoteId);
        if (cachedOps == null || !cacheKey.Equals(key))
        {
            cachedOps = ScoreRenderer.Render(editor.Sheet, layout, new ScoreRenderOptions
            {
                SelectedId = editor.SelectedId, PlayingId = editor.PlayingId, LyricEditId = lyricNoteId,
            });
            cacheKey = key;
        }
        Draw(dc, cachedOps);

        if (caretOn && EditSlot is { } slot)
        {
            double x = ScoreLayout.LyricCaretX(slot, lyricCaret);
            dc.DrawLine(PenFor(Role.Caret, 1.6), new Point(x, slot.Baseline - 14), new Point(x, slot.Baseline + 4));
        }
        else if (caretOn && !IsEditingLyrics && editor.SelectedId == null && editor.PlayingId == null)
        {
            var c = layout.CursorPosition(editor.Sheet.Notes, editor.Cursor);
            double t = layout.Lines[c.Sys].StaffTop;
            dc.DrawLine(PenFor(Role.Caret, 2), new Point(c.X, t - 8), new Point(c.X, t + 48));
        }

        if (ghost is { } g && drag == null && editor.PlayingId == null)
        {
            dc.PushOpacity(0.45);
            Draw(dc, GhostOps(g));
            dc.Pop();
        }
    }

    static void Draw(DrawingContext dc, List<DrawOp> ops)
    {
        foreach (var op in ops)
        {
            dc.DrawGeometry(op.Fill is Role f ? Brushes[f] : null,
                op.Stroke is Role s ? PenFor(s, op.Thickness) : null, op.Geometry);
        }
    }

    /* ------------------------------ Hit testing ------------------------------ */

    int SystemAt(double y) => Math.Clamp((int)Math.Floor((y - Metrics.PadTop) / layout!.SysH), 0, layout.Lines.Count - 1);

    bool InLyricZone(Point p) => p.Y >= layout!.Lines[SystemAt(p.Y)].StaffTop + Metrics.LyricZone;

    static int StepAt(double y, double staffTop) =>
        Math.Clamp((int)Math.Round((staffTop + 40 - y) / Metrics.Half) + Theory.E4, Theory.MinStep, Theory.MaxStep);

    Item? HitItem(Point p)
    {
        foreach (var it in layout!.Items)
        {
            if (it.Virtual || it.Note == null) continue;
            double tolY = it.Rest ? 20 : 7;
            if (Math.Abs(p.X - it.Cx) <= 9 && Math.Abs(p.Y - it.Y) <= tolY) return it;
        }
        return null;
    }

    // Where a click should insert: before the first note that starts to the right of it.
    int InsertIndexAt(int sys, double x)
    {
        foreach (var it in layout!.Items)
        {
            if (it.Note == null || !it.First) continue;
            if (it.Sys > sys || (it.Sys == sys && it.Cx > x)) return editor!.Sheet.Notes.IndexOf(it.Note);
        }
        return editor!.Sheet.Notes.Count;
    }

    List<DrawOp> GhostOps(Point p)
    {
        var ops = new List<DrawOp>();
        int sys = SystemAt(p.Y);
        var ln = layout!.Lines[sys];
        double t = ln.StaffTop;
        double x = Math.Max(p.X, ln.Left + 8);
        bool dot = editor!.Dot && editor.Dur != Dur.S;
        if (editor.RestMode)
        {
            ScoreRenderer.RestOps(editor.Dur, dot, x, t, Role.Ghost, ops);
            return ops;
        }
        int step = StepAt(p.Y, t);
        var it = new Item { Cx = x, Y = Metrics.YAt(t, step), StaffTop = t, Step = step, Dur = editor.Dur, Dot = dot };
        ScoreRenderer.NoteOps(it, Role.Ghost, ops);
        double ly = Math.Min(it.Y, t) - 22;
        ops.Add(new DrawOp(MusicShapes.Rect(x - 15, ly - 12, 30, 17, 5), Role.GhostLabelBg));
        ops.Add(new DrawOp(MusicShapes.Text(Theory.StepName(step), "Segoe UI", FontWeights.SemiBold, FontStyles.Normal, 12, x, ly, MusicShapes.Anchor.Center), Role.Ghost));
        return ops;
    }

    /* --------------------------------- Mouse --------------------------------- */

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (editor == null || layout == null) return;
        Focus();
        var p = e.GetPosition(this);
        e.Handled = true;

        if (InLyricZone(p))
        {
            StartLyrics(SystemAt(p.Y), p.X);
            return;
        }
        if (IsEditingLyrics) StopLyrics();

        var hit = HitItem(p);
        if (hit?.Note is { } note)
        {
            editor.Select(note);
            if (!note.Rest)
            {
                drag = (note, hit.StaffTop);
                editor.BeginDrag();
                CaptureMouse();
            }
            return;
        }
        int sys = SystemAt(p.Y);
        editor.InsertNote(InsertIndexAt(sys, p.X), StepAt(p.Y, layout.Lines[sys].StaffTop), editor.RestMode);
        ghost = p;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (editor == null || layout == null) return;
        var p = e.GetPosition(this);
        if (drag is { } d)
        {
            editor.DragTo(d.Note, StepAt(p.Y, d.StaffTop));
            return;
        }
        if (InLyricZone(p))
        {
            Cursor = Cursors.IBeam;
            ghost = null;
            InvalidateVisual();
            return;
        }
        var hit = HitItem(p);
        Cursor = hit == null ? Cursors.Cross : hit.Rest ? Cursors.Hand : Cursors.SizeNS;
        ghost = hit == null ? p : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        drag = null;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => drag = null;

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (drag != null) return;
        ghost = null;
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsEditingLyrics) StopLyrics();
    }

    /* --------------------------------- Lyrics -------------------------------- */

    // Starts typing the lyric of the note nearest to a click under staff `sys`.
    internal void StartLyrics(int sys, double x)
    {
        if (layout!.LyricSlotAt(sys, x) is { } slot) EditLyric(slot, ScoreLayout.LyricIndexAt(slot, x));
        else StopLyrics();
    }

    void EditLyric(LyricSlot slot, int caret)
    {
        lyricNoteId = slot.Note.Id;
        lyricCaret = Math.Clamp(caret, 0, slot.Text.Length);
        if (editor!.SelectedId != null) editor.ClearSelection();
        editor.BeginLyricTyping();
        caretOn = true;
        InvalidateVisual();
        LyricModeChanged?.Invoke();
    }

    public void StopLyrics()
    {
        if (!IsEditingLyrics) return;
        lyricNoteId = null;
        InvalidateVisual();
        LyricModeChanged?.Invoke();
    }

    // Moves to the lyric of the next (+1) or previous (-1) sung note.
    void MoveLyric(int dir, bool toStart)
    {
        if (EditSlot is not { } slot) return;
        int i = layout!.Lyrics.IndexOf(slot) + dir;
        if (i < 0 || i >= layout.Lyrics.Count) return;
        var to = layout.Lyrics[i];
        EditLyric(to, toStart ? 0 : to.Text.Length);
    }

    // Types text at the cursor, character by character, while it still fits
    // under the note. What doesn't fit is left out.
    void InsertLyricText(string s)
    {
        if (EditSlot is not { } slot) return;
        s = s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        var text = slot.Text;
        int i = Math.Clamp(lyricCaret, 0, text.Length);
        foreach (var c in s)
        {
            var next = text.Insert(i, c.ToString());
            if (next.Length > Note.MaxLyric || !ScoreLayout.LyricFits(slot, next)) break;
            text = next;
            i++;
        }
        if (text == slot.Text) return;
        SetLyricText(slot, text, i);
    }

    void SetLyricText(LyricSlot slot, string text, int caret)
    {
        lyricCaret = caret;
        editor!.SetLyric(slot.Note, text);
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (!IsEditingLyrics) return;
        var s = new string(e.Text.Where(c => c >= ' ' && c != '\u007F').ToArray());
        if (s.Length == 0) return;
        InsertLyricText(s);
        e.Handled = true;
    }

    // Keys that move or delete while typing lyrics. Returns false for keys that
    // produce characters, which then arrive through OnTextInput.
    public bool HandleLyricKey(Key key, bool ctrl)
    {
        if (editor == null || layout == null || EditSlot is not { } slot) return false;
        var text = slot.Text;
        int i = Math.Clamp(lyricCaret, 0, text.Length);
        caretOn = true;

        if (ctrl)
        {
            if (key == Key.V && Clipboard.ContainsText()) { InsertLyricText(Clipboard.GetText()); return true; }
            return false;
        }

        switch (key)
        {
            case Key.Escape:
            case Key.Enter:
                StopLyrics();
                break;
            case Key.Tab:
                MoveLyric(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1, toStart: false);
                break;
            case Key.Back:
                if (i > 0) SetLyricText(slot, text.Remove(i - 1, 1), i - 1);
                else MoveLyric(-1, toStart: false);
                break;
            case Key.Delete:
                if (i < text.Length) SetLyricText(slot, text.Remove(i, 1), i);
                break;
            case Key.Left:
                if (i > 0) lyricCaret = i - 1;
                else MoveLyric(-1, toStart: false);
                break;
            case Key.Right:
                if (i < text.Length) lyricCaret = i + 1;
                else MoveLyric(1, toStart: true);
                break;
            case Key.Home:
                lyricCaret = 0;
                break;
            case Key.End:
                lyricCaret = text.Length;
                break;
            case Key.Up:
            case Key.Down:
                break;   // nothing above or below a lyric; keeps the note's pitch safe
            default:
                return false;   // typed as a character (Space too)
        }
        InvalidateVisual();
        return true;
    }
}
