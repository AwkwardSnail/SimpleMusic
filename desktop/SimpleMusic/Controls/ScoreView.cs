using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SimpleMusic.Music;
using SimpleMusic.Rendering;

namespace SimpleMusic.Controls;

// The score: draws the sheet and turns clicks, drags and typing into edits.
// Clicking on a staff places notes; clicking under it types lyrics there.
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

    // Lyric typing: the text cursor position, and how many staves further down
    // the first typed character should land (when clicking under an empty staff).
    int? lyricCaret;
    int lyricPadRows;

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

    public bool IsEditingLyrics => lyricCaret != null;

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
        if (lyricCaret is int c) lyricCaret = Math.Clamp(c, 0, editor.Sheet.Lyrics.Length);
        return new Size(w, layout.Height);
    }

    int? LyricEditSys => lyricCaret is int c && layout != null
        ? (lyricPadRows > 0 ? layout.Lyrics.Count - 1 + lyricPadRows : layout.LyricCaret(c).Sys)
        : null;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(RenderSize));
        if (editor == null || layout == null) return;

        // The score only needs rebuilding when the layout, selection, playing note or
        // lyric line being typed changes; the blinking cursors and hover preview go on top.
        var editSys = LyricEditSys;
        var key = (layout, editor.SelectedId, editor.PlayingId, editSys);
        if (cachedOps == null || !cacheKey.Equals(key))
        {
            cachedOps = ScoreRenderer.Render(editor.Sheet, layout, new ScoreRenderOptions
            {
                SelectedId = editor.SelectedId, PlayingId = editor.PlayingId, LyricEditSys = editSys,
            });
            cacheKey = key;
        }
        Draw(dc, cachedOps);

        if (caretOn && IsEditingLyrics && editSys is int sys)
        {
            double x, baseline;
            if (lyricPadRows > 0 && sys < layout.Lines.Count)
            {
                x = layout.Lines[sys].Left;
                baseline = layout.Lines[sys].LyricBaseline;
            }
            else
            {
                (_, x, baseline) = layout.LyricCaret(lyricCaret!.Value);
            }
            dc.DrawLine(PenFor(Role.Caret, 1.6), new Point(x, baseline - 14), new Point(x, baseline + 4));
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

    internal void StartLyrics(int sys, double x)
    {
        var text = editor!.Sheet.Lyrics;
        int index = layout!.LyricIndexAt(sys, x);
        if (index >= 0)
        {
            lyricCaret = index;
            lyricPadRows = 0;
        }
        else
        {
            // Under a staff past the end of the words: typing will start there.
            lyricCaret = text.Length;
            lyricPadRows = sys - (layout.Lyrics.Count - 1);
        }
        if (editor.SelectedId != null) editor.ClearSelection();
        editor.BeginLyricTyping();
        caretOn = true;
        InvalidateVisual();
        LyricModeChanged?.Invoke();
    }

    public void StopLyrics()
    {
        if (!IsEditingLyrics) return;
        lyricCaret = null;
        lyricPadRows = 0;
        InvalidateVisual();
        LyricModeChanged?.Invoke();
    }

    void InsertLyricText(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
        if (s.Length == 0) return;
        var text = editor!.Sheet.Lyrics;
        int i = Math.Clamp(lyricCaret!.Value, 0, text.Length);
        if (lyricPadRows > 0) { s = new string('\n', lyricPadRows) + s; lyricPadRows = 0; }
        if (text.Length + s.Length > 20000) return;
        lyricCaret = i + s.Length;
        editor.SetLyrics(text.Insert(i, s));
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
        if (editor == null || layout == null || lyricCaret is not int i) return false;
        var text = editor.Sheet.Lyrics;
        caretOn = true;

        if (ctrl)
        {
            if (key == Key.V && Clipboard.ContainsText()) { InsertLyricText(Clipboard.GetText()); return true; }
            return false;
        }

        switch (key)
        {
            case Key.Escape:
                StopLyrics();
                break;
            case Key.Enter:
                InsertLyricText("\n");
                break;
            case Key.Back:
                if (lyricPadRows > 0) { lyricPadRows--; break; }
                if (i > 0) { lyricCaret = i - 1; editor.SetLyrics(text.Remove(i - 1, 1)); }
                break;
            case Key.Delete:
                if (i < text.Length) editor.SetLyrics(text.Remove(i, 1));
                break;
            case Key.Left:
                lyricPadRows = 0;
                lyricCaret = Math.Max(0, i - 1);
                break;
            case Key.Right:
                lyricPadRows = 0;
                lyricCaret = Math.Min(text.Length, i + 1);
                break;
            case Key.Home:
            case Key.End:
            {
                lyricPadRows = 0;
                var (start, end) = layout.LyricRowRange(layout.LyricCaret(i).Sys);
                lyricCaret = key == Key.Home ? start : end;
                break;
            }
            case Key.Up:
            case Key.Down:
            {
                lyricPadRows = 0;
                var (sys, x, _) = layout.LyricCaret(i);
                int target = sys + (key == Key.Up ? -1 : 1);
                if (target < 0) lyricCaret = 0;
                else if (target >= layout.Lyrics.Count) lyricCaret = text.Length;
                else lyricCaret = layout.LyricIndexAt(target, x);
                break;
            }
            case Key.Space:
                return false; // typed as a character
            default:
                return false;
        }
        InvalidateVisual();
        return true;
    }
}
