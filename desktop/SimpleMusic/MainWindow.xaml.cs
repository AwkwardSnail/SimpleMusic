using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.Win32;
using SimpleMusic.Audio;
using SimpleMusic.Controls;
using SimpleMusic.Export;
using SimpleMusic.Music;

namespace SimpleMusic;

public partial class MainWindow : Window
{
    Editor editor = null!;
    readonly Player player = new();
    readonly bool persist;
    readonly Dictionary<Dur, ToggleButton> durButtons;
    readonly Dictionary<Dur, NoteIcon> durIcons;
    bool updating;

    // The file this sheet belongs to (null = never saved), and what was last
    // written to it, so we know when there are unsaved changes.
    string? filePath;
    string savedJson = "";

    public MainWindow(Sheet sheet, bool persist, string? filePath = null)
    {
        InitializeComponent();
        this.persist = persist;
        durButtons = new() { [Dur.W] = DurW, [Dur.H] = DurH, [Dur.Q] = DurQ, [Dur.E] = DurE, [Dur.S] = DurS };
        durIcons = new() { [Dur.W] = IconW, [Dur.H] = IconH, [Dur.Q] = IconQ, [Dur.E] = IconE, [Dur.S] = IconS };

        BeatUnit.ItemsSource = Theory.BeatOptions;
        player.NoteChanged += id => { editor.PlayingId = id; Score.InvalidateVisual(); };
        player.Stopped += () => { BtnPlay.IsChecked = false; BtnPlay.Content = "▶  Play"; };
        Score.LyricModeChanged += UpdateStatus;

        // The baseline is what is on disk; a sheet without a file is "saved" while empty.
        string baseline = SheetStore.ToFileJson(new Sheet());
        if (filePath != null)
        {
            try { baseline = SheetStore.ToFileJson(SheetStore.ReadFile(filePath)); }
            catch (Exception) { /* the file changed or vanished: the sheet counts as unsaved */ }
        }
        Attach(sheet, filePath, baseline);

        Loaded += (_, _) => Keyboard.Focus(Score);
        Closing += (_, _) => player.Stop();
    }

    Sheet Sheet => editor.Sheet;

    // Shows a sheet in the window (at start, after New and after Open).
    void Attach(Sheet sheet, string? path, string baseline)
    {
        player.Stop();
        Score.StopLyrics();
        if (editor != null)
        {
            editor.Changed -= OnEditorChanged;
            editor.Preview -= player.Preview;
        }
        editor = new Editor(sheet);
        editor.Changed += OnEditorChanged;
        editor.Preview += player.Preview;
        filePath = path;
        savedJson = baseline;

        Score.Editor = editor;
        updating = true;
        TitleBox.Text = sheet.Title;
        Tempo.Value = sheet.Tempo;
        updating = false;
        UpdateControls();
        Save();
    }

    void OnEditorChanged()
    {
        Score.Refresh();
        UpdateControls();
        Save();
    }

    // Remembers the sheet for next time (not the same as saving it to a file)
    // and refreshes the window title.
    void Save()
    {
        if (persist)
        {
            SheetStore.Save(Sheet);
            SheetStore.SaveLastFile(filePath);
        }
        string name = filePath != null ? Path.GetFileNameWithoutExtension(filePath) : "Untitled";
        Title = $"{name}{(IsDirty ? "*" : "")} — SimpleMusic";
    }

    bool IsDirty => SheetStore.ToFileJson(Sheet) != savedJson;

    /* --------------------------------- Files --------------------------------- */

    // Before replacing the sheet: offer to save unsaved changes. False = cancel.
    bool ConfirmDiscard()
    {
        if (!IsDirty) return true;
        string name = filePath != null ? Path.GetFileName(filePath) : "this sheet";
        var answer = MessageBox.Show(this, $"Save the changes to {name} first?", "SimpleMusic",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SaveToFile(saveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    void NewSheet()
    {
        if (!ConfirmDiscard()) return;
        var sheet = new Sheet();
        Attach(sheet, null, SheetStore.ToFileJson(sheet));
    }

    public void OpenFile(string? path = null)
    {
        if (!ConfirmDiscard()) return;
        if (path == null)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open a sheet",
                Filter = SheetStore.FileFilter,
                InitialDirectory = filePath != null ? Path.GetDirectoryName(filePath) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }
        try
        {
            var sheet = SheetStore.ReadFile(path);
            Attach(sheet, path, SheetStore.ToFileJson(sheet));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{Path.GetFileName(path)} could not be opened:\n{ex.Message}", "SimpleMusic",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Another launch of SimpleMusic asked this window to show itself (and open a sheet).
    public void BringForwardAndOpen(string path)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;   // a quick nudge so Windows really brings it to the front
        Topmost = false;
        if (path.EndsWith(SheetStore.FileExtension, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            OpenFile(path);
    }

    // Returns false when the person cancelled or the save failed.
    bool SaveToFile(bool saveAs)
    {
        string? path = saveAs ? null : filePath;
        if (path == null)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var dlg = new SaveFileDialog
            {
                Title = saveAs ? "Save a copy as" : "Save sheet",
                Filter = SheetStore.FileFilter,
                DefaultExt = SheetStore.FileExtension,
                FileName = new string(Sheet.DisplayTitle.Where(c => !invalid.Contains(c)).ToArray()),
                InitialDirectory = filePath != null ? Path.GetDirectoryName(filePath) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        try
        {
            SheetStore.WriteFile(Sheet, path);
            filePath = path;
            savedJson = SheetStore.ToFileJson(Sheet);
            Save();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The sheet could not be saved:\n{ex.Message}", "SimpleMusic", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    void OnOpen(object sender, RoutedEventArgs e) => OpenFile();
    void OnSave(object sender, RoutedEventArgs e) => SaveToFile(saveAs: false);
    void OnSaveAs(object sender, RoutedEventArgs e) => SaveToFile(saveAs: true);

    /* ------------------------------- Controls ------------------------------- */

    void UpdateControls()
    {
        updating = true;
        foreach (var (d, b) in durButtons)
        {
            b.IsChecked = editor.Dur == d;
            durIcons[d].Dotted = editor.Dot && d != Dur.S;
        }
        BtnDot.IsChecked = editor.Dot;
        BtnDot.IsEnabled = editor.Dur != Dur.S;
        BtnRest.IsChecked = editor.RestMode;
        var target = editor.EditTarget;
        BtnSharp.IsChecked = target?.Acc == 1;
        BtnFlat.IsChecked = target?.Acc == -1;
        BtnSharp.IsEnabled = BtnFlat.IsEnabled = target is { Rest: false };
        BtnUndo.IsEnabled = editor.CanUndo;
        BtnRedo.IsEnabled = editor.CanRedo;
        BtnDelete.IsEnabled = Sheet.Notes.Count > 0;

        var (num, den) = Sheet.TimeSignature;
        TsNum.Text = num.ToString();
        TsDen.Text = den.ToString();
        BeatsText.Text = Sheet.Beats.ToString();
        BeatUnit.SelectedIndex = Array.IndexOf(Theory.BeatChoices, Sheet.BeatTicks);
        BeatUnit.ToolTip = $"One beat is a {Theory.BeatName(Sheet.BeatTicks).ToLowerInvariant()}";
        FillLast.IsChecked = Sheet.FillLast;
        BtnBar.Content = Sheet.Barred ? "Back to free writing" : "Divide into bars";
        BtnBar.Background = (System.Windows.Media.Brush)FindResource(Sheet.Barred ? "InkBrush" : "AccentBrush");
        BtnBar.BorderBrush = BtnBar.Background;
        TitlePlaceholder.Visibility = TitleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        UpdateStatus();
        updating = false;
    }

    static string Fmt(double v) => Math.Round(v, 2).ToString("0.##");

    void UpdateStatus()
    {
        var inl = StatusText.Inlines;
        inl.Clear();
        void T(string s) => inl.Add(new Run(s));
        void Bold(string s) => inl.Add(new Run(s) { FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("InkBrush") });

        if (notice != null)
        {
            Bold("✓ " + notice);
            return;
        }
        if (Score.IsEditingLyrics)
        {
            Bold("Typing lyrics");
            T(" · Tab moves to the next note · Esc goes back to writing notes");
            return;
        }
        int n = Sheet.Notes.Count;
        if (n == 0) { T("Empty sheet. Choose a note length above, then click on the staff. Click under a note to add its lyric."); return; }
        double beats = Sheet.Notes.Sum(x => x.Ticks) / (double)Sheet.BeatTicks;
        if (!Sheet.Barred)
        {
            Bold(n.ToString()); T(n == 1 ? " note · " : " notes · ");
            Bold(Fmt(beats)); T($" beats long (counting a {Theory.BeatName(Sheet.BeatTicks).ToLowerInvariant()} as one beat)");
        }
        else
        {
            var (num, den) = Sheet.TimeSignature;
            int full = (int)Math.Floor(beats / Sheet.Beats);
            double rest = beats - full * Sheet.Beats;
            Bold($"{num}/{den}"); T(" · ");
            if (rest < 1e-9) { Bold(full.ToString()); T(full == 1 ? " bar, the music fits exactly" : " bars, the music fits exactly"); }
            else
            {
                Bold(full.ToString()); T(full == 1 ? " full bar + a last bar with " : " full bars + a last bar with ");
                Bold(Fmt(rest)); T($" of {Sheet.Beats} beats" + (Sheet.FillLast ? " (filled with rests)" : ""));
            }
        }
        if (editor.Selected is { } sel)
        {
            T(" · Selected: ");
            Bold(sel.Rest ? "rest" : Theory.StepName(sel.Step) + (sel.Acc > 0 ? "♯" : sel.Acc < 0 ? "♭" : ""));
            T($" ({Theory.Name(sel.Dur).ToLowerInvariant()}{(sel.Dot ? ", dotted" : "")})");
        }
    }

    void OnDurClick(object sender, RoutedEventArgs e) => editor.ChooseDuration(Enum.Parse<Dur>((string)((FrameworkElement)sender).Tag));
    void OnDotClick(object sender, RoutedEventArgs e) => editor.ToggleDot();
    void OnRestClick(object sender, RoutedEventArgs e) => editor.ToggleRestMode();
    void OnSharpClick(object sender, RoutedEventArgs e) => SetAccidental(1);
    void OnFlatClick(object sender, RoutedEventArgs e) => SetAccidental(-1);
    void OnUndo(object sender, RoutedEventArgs e) => editor.Undo();
    void OnRedo(object sender, RoutedEventArgs e) => editor.Redo();
    void OnDelete(object sender, RoutedEventArgs e) => editor.DeleteNote(-1);

    void SetAccidental(int acc)
    {
        editor.SetAccidental(acc);
        UpdateControls(); // the toggle flips itself on click; put it back if nothing changed
    }

    void OnPlayClick(object sender, RoutedEventArgs e) => TogglePlay();

    void TogglePlay()
    {
        if (player.IsPlaying) { player.Stop(); return; }
        if (Sheet.Notes.Count == 0) { BtnPlay.IsChecked = false; return; }
        int start = Math.Max(0, editor.SelectedIndex);
        editor.SelectedId = null;
        editor.Notify();
        player.Play(Sheet.Notes.Skip(start).ToList(), 60.0 / Sheet.Tempo / Sheet.BeatTicks);
        BtnPlay.IsChecked = true;
        BtnPlay.Content = "■  Stop";
    }

    void OnTempoChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TempoText == null) return;
        TempoText.Text = ((int)Tempo.Value).ToString();
        if (updating) return;
        Sheet.Tempo = (int)Tempo.Value;
        Save();
    }

    void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        TitlePlaceholder.Visibility = TitleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        if (updating) return;
        Sheet.Title = TitleBox.Text;
        Save();
    }

    void OnTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape) { Keyboard.Focus(Score); e.Handled = true; }
    }


    void OnNewSheet(object sender, RoutedEventArgs e) => NewSheet();

    void OnExportPdf(object sender, RoutedEventArgs e) => ExportPdf();
    void OnExportMidi(object sender, RoutedEventArgs e) => ExportMidi();

    void OnExportMenu(object sender, RoutedEventArgs e)
    {
        var menu = BtnExport.ContextMenu;
        menu.PlacementTarget = BtnExport;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void ExportMidi()
    {
        player.Stop();
        var invalid = Path.GetInvalidFileNameChars();
        var dlg = new SaveFileDialog
        {
            Title = "Export as MIDI",
            Filter = "MIDI file (*.mid)|*.mid",
            DefaultExt = ".mid",
            FileName = new string(Sheet.DisplayTitle.Where(c => !invalid.Contains(c)).ToArray()),
            InitialDirectory = filePath != null ? Path.GetDirectoryName(filePath) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            MidiExporter.Export(Sheet, dlg.FileName);
            ShowNotice($"Exported {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The MIDI file could not be saved:\n{ex.Message}", "SimpleMusic", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // A short message in the status line (e.g. after exporting), gone after a few seconds.
    string? notice;
    System.Windows.Threading.DispatcherTimer? noticeTimer;

    void ShowNotice(string text)
    {
        notice = text;
        noticeTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        noticeTimer.Stop();
        noticeTimer.Tick -= ClearNotice;
        noticeTimer.Tick += ClearNotice;
        noticeTimer.Start();
        UpdateStatus();
    }

    void ClearNotice(object? sender, EventArgs e)
    {
        noticeTimer!.Stop();
        notice = null;
        UpdateStatus();
    }

    void ExportPdf()
    {
        player.Stop();
        var invalid = Path.GetInvalidFileNameChars();
        var dlg = new SaveFileDialog
        {
            Title = "Export as PDF",
            Filter = "PDF document (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            FileName = new string(Sheet.DisplayTitle.Where(c => !invalid.Contains(c)).ToArray()),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            PdfExporter.Export(Sheet, dlg.FileName);
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The PDF could not be saved:\n{ex.Message}", "SimpleMusic", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /* --------------------------------- Bars --------------------------------- */

    void SetBeats(int beats)
    {
        Sheet.Beats = Math.Clamp(beats, 1, 16);
        editor.Notify();
    }

    void OnBeatsMinus(object sender, RoutedEventArgs e) => SetBeats(Sheet.Beats - 1);
    void OnBeatsPlus(object sender, RoutedEventArgs e) => SetBeats(Sheet.Beats + 1);

    void OnBeatUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || BeatUnit.SelectedItem is not BeatChoice choice) return;
        Sheet.BeatTicks = choice.Ticks;
        editor.Notify();
        Dispatcher.BeginInvoke(() => Keyboard.Focus(Score));
    }

    void OnFillLastClick(object sender, RoutedEventArgs e)
    {
        Sheet.FillLast = FillLast.IsChecked == true;
        editor.Notify();
    }

    void OnBarClick(object sender, RoutedEventArgs e)
    {
        Sheet.Barred = !Sheet.Barred;
        editor.Notify();
    }

    public void ShowFreeWriting()
    {
        Sheet.Barred = false;
        editor.Notify();
    }

    /* ------------------------------- Keyboard ------------------------------- */

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool shift = mods.HasFlag(ModifierKeys.Shift);

        // File shortcuts work everywhere, even while typing the title.
        if (ctrl && key is Key.S or Key.O or Key.N)
        {
            if (key == Key.S) SaveToFile(saveAs: shift);
            else if (key == Key.O) OpenFile();
            else NewSheet();
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBox) return;

        // While typing lyrics on the score, keys write text instead of notes.
        if (Score.IsEditingLyrics)
        {
            if (Score.HandleLyricKey(key, ctrl)) { e.Handled = true; return; }
            if (!ctrl) return;
        }

        if (ctrl)
        {
            if (key == Key.Z) { if (shift) editor.Redo(); else editor.Undo(); }
            else if (key == Key.Y) editor.Redo();
            else if (key == Key.P) ExportPdf();
            else if (key == Key.M) ExportMidi();
            else return;
            e.Handled = true;
            return;
        }
        if (mods.HasFlag(ModifierKeys.Alt)) return;

        switch (key)
        {
            case >= Key.A and <= Key.G:
                editor.InsertNote(editor.InsertIndexForTyping(), editor.StepForLetter((char)('A' + (key - Key.A))), false);
                break;
            case Key.R:
                editor.InsertNote(editor.InsertIndexForTyping(), Theory.B4, true);
                break;
            case Key.D3 when shift:
            case Key.OemPlus or Key.Add:
                SetAccidental(1);
                break;
            case Key.OemMinus or Key.Subtract:
                SetAccidental(-1);
                break;
            case Key.D1 or Key.NumPad1: editor.ChooseDuration(Dur.W); break;
            case Key.D2 or Key.NumPad2: editor.ChooseDuration(Dur.H); break;
            case Key.D3 or Key.NumPad3: editor.ChooseDuration(Dur.Q); break;
            case Key.D4 or Key.NumPad4: editor.ChooseDuration(Dur.E); break;
            case Key.D5 or Key.NumPad5: editor.ChooseDuration(Dur.S); break;
            case Key.OemPeriod or Key.Decimal: editor.ToggleDot(); break;
            case Key.Up or Key.Down:
                editor.MovePitch((key == Key.Up ? 1 : -1) * (shift ? 7 : 1));
                break;
            case Key.Left or Key.Right:
                editor.MoveSelection(key == Key.Left ? -1 : 1);
                break;
            case Key.Home: editor.SetCursor(0); break;
            case Key.End: editor.SetCursor(Sheet.Notes.Count); break;
            case Key.Back: editor.DeleteNote(-1); break;
            case Key.Delete: editor.DeleteNote(1); break;
            case Key.Escape: editor.ClearSelection(); break;
            case Key.Space: TogglePlay(); break;
            default: return;
        }
        e.Handled = true;
    }
}
