using SimpleMusic.Music;

namespace SimpleMusic;

// Editing state and actions. The window and the score view call these and
// redraw when Changed fires.
public sealed class Editor
{
    public Sheet Sheet { get; }
    public int Cursor;              // insertion point (index into the notes)
    public int? SelectedId;
    public Dur Dur = Dur.Q;
    public bool Dot;
    public bool RestMode;
    public int? PlayingId;

    int nextId;
    bool dragPushed;
    bool lyricsPushed;
    readonly Stack<(List<Note> Notes, int Cursor, string Lyrics)> undo = new();
    readonly Stack<(List<Note> Notes, int Cursor, string Lyrics)> redo = new();

    public event Action? Changed;
    public event Action<Note>? Preview;     // a note was placed or changed: play it briefly

    public Editor(Sheet sheet)
    {
        Sheet = sheet;
        Cursor = sheet.Notes.Count;
        nextId = sheet.Notes.Count == 0 ? 1 : sheet.Notes.Max(n => n.Id) + 1;
    }

    List<Note> Notes => Sheet.Notes;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public Note? Selected => SelectedId is int id ? Notes.FirstOrDefault(n => n.Id == id) : null;
    public int SelectedIndex => Selected is { } n ? Notes.IndexOf(n) : -1;

    // The note that sharp/flat/arrow keys act on: the selected one,
    // or the note just before the cursor.
    public Note? EditTarget => Selected ?? (Cursor > 0 && Cursor <= Notes.Count ? Notes[Cursor - 1] : null);

    public void Notify() => Changed?.Invoke();

    /* ------------------------------- History ------------------------------- */

    (List<Note>, int, string) Snapshot() => (Notes.Select(n => n.Clone()).ToList(), Cursor, Sheet.Lyrics);

    void Restore((List<Note> Notes, int Cursor, string Lyrics) s)
    {
        Sheet.Notes = s.Notes;
        Sheet.Lyrics = s.Lyrics;
        Cursor = Math.Clamp(s.Cursor, 0, s.Notes.Count);
        SelectedId = null;
    }

    public void PushHistory()
    {
        undo.Push(Snapshot());
        redo.Clear();
        lyricsPushed = false;
    }

    /* ------------------------------- Lyrics -------------------------------- */

    // A stretch of typing counts as one step for undo.
    public void BeginLyricTyping() => lyricsPushed = false;

    public void SetLyrics(string text)
    {
        if (text == Sheet.Lyrics) return;
        if (!lyricsPushed) { PushHistory(); lyricsPushed = true; }
        Sheet.Lyrics = text;
        Notify();
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        redo.Push(Snapshot());
        Restore(undo.Pop());
        Notify();
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        undo.Push(Snapshot());
        Restore(redo.Pop());
        Notify();
    }

    /* ------------------------------- Actions ------------------------------- */

    public void InsertNote(int index, int step, bool rest)
    {
        PushHistory();
        var n = new Note { Id = nextId++, Rest = rest, Dur = Dur, Dot = Dot && Dur != Dur.S, Step = step };
        Notes.Insert(Math.Clamp(index, 0, Notes.Count), n);
        Cursor = Notes.IndexOf(n) + 1;
        SelectedId = null;
        if (!rest) Preview?.Invoke(n);
        Notify();
    }

    public void ChooseDuration(Dur d)
    {
        Dur = d;
        if (d == Dur.S) Dot = false;
        if (Selected is { } sel)
        {
            PushHistory();
            sel.Dur = d;
            sel.Dot = Dot;
        }
        Notify();
    }

    public void ToggleDot()
    {
        if (Dur == Dur.S) return;
        Dot = !Dot;
        if (Selected is { } sel && sel.Dur != Dur.S)
        {
            PushHistory();
            sel.Dot = Dot;
        }
        Notify();
    }

    public void ToggleRestMode()
    {
        RestMode = !RestMode;
        if (Selected is { } sel)
        {
            PushHistory();
            sel.Rest = RestMode;
            sel.Acc = 0;
        }
        Notify();
    }

    public void SetAccidental(int acc)
    {
        if (EditTarget is not { Rest: false } n) return;
        PushHistory();
        n.Acc = n.Acc == acc ? 0 : acc;
        Preview?.Invoke(n);
        Notify();
    }

    public void MovePitch(int delta)
    {
        if (EditTarget is not { Rest: false } n) return;
        int step = Math.Clamp(n.Step + delta, Theory.MinStep, Theory.MaxStep);
        if (step == n.Step) return;
        PushHistory();
        n.Step = step;
        Preview?.Invoke(n);
        Notify();
    }

    public void BeginDrag() => dragPushed = false;

    public void DragTo(Note n, int step)
    {
        step = Math.Clamp(step, Theory.MinStep, Theory.MaxStep);
        if (step == n.Step) return;
        if (!dragPushed) { PushHistory(); dragPushed = true; }
        n.Step = step;
        Preview?.Invoke(n);
        Notify();
    }

    public void DeleteNote(int direction)
    {
        if (Notes.Count == 0) return;
        int idx = SelectedIndex;
        if (idx < 0) idx = direction < 0 ? Cursor - 1 : Cursor;
        if (idx < 0 || idx >= Notes.Count) return;
        PushHistory();
        Notes.RemoveAt(idx);
        Cursor = idx;
        SelectedId = null;
        Notify();
    }

    public void Select(Note? n)
    {
        SelectedId = n?.Id;
        if (n != null)
        {
            Cursor = Notes.IndexOf(n) + 1;
            Dur = n.Dur;
            Dot = n.Dot;
            RestMode = n.Rest;
        }
        Notify();
    }

    public void ClearSelection()
    {
        SelectedId = null;
        Notify();
    }

    public void SetCursor(int index)
    {
        SelectedId = null;
        Cursor = Math.Clamp(index, 0, Notes.Count);
        Notify();
    }

    public void MoveSelection(int delta)
    {
        int idx = SelectedIndex;
        if (idx < 0) { SetCursor(Cursor + delta); return; }
        int next = idx + delta;
        if (next < 0) SetCursor(0);
        else if (next >= Notes.Count) SetCursor(Notes.Count);
        else Select(Notes[next]);
    }

    public int InsertIndexForTyping() => SelectedIndex >= 0 ? SelectedIndex + 1 : Cursor;

    // Pick the octave of the letter that lands closest to the previous note.
    public int StepForLetter(char letter)
    {
        int li = Theory.Letters.IndexOf(letter);
        int reference = Theory.B4;
        for (int i = Math.Min(InsertIndexForTyping(), Notes.Count) - 1; i >= 0; i--)
        {
            if (!Notes[i].Rest) { reference = Notes[i].Step; break; }
        }
        int best = -1;
        for (int oct = 3; oct <= 6; oct++)
        {
            int s = oct * 7 + li;
            if (s < Theory.MinStep || s > Theory.MaxStep) continue;
            if (best < 0 || Math.Abs(s - reference) < Math.Abs(best - reference)) best = s;
        }
        return best;
    }
}
