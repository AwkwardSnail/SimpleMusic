using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimpleMusic.Export;
using SimpleMusic.Music;

namespace SimpleMusic;

public partial class App : Application
{
    // Command-line options used for checking the app without clicking through it:
    //   <file>.smusic       open that sheet (double-clicking a sheet file)
    //   --demo              open a built-in example instead of the saved sheet (nothing is saved)
    //   --barred            divide the sheet into bars
    //   --free              show the sheet before it is divided into bars
    //   --snapshot <png>    render the window to an image and quit
    //   --size <W>x<H>      window size (for Store screenshots)
    //   --pdf <file>        export the sheet as PDF and quit
    //   --midi <file>       export the sheet as MIDI and quit
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        string? ArgAfter(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        bool demo = args.Contains("--demo");
        bool automated = ArgAfter("--snapshot") != null || ArgAfter("--pdf") != null || ArgAfter("--lyric-test") != null;

        // A sheet file passed on the command line: that's how double-clicking a .smusic file arrives.
        string? openPath = args.FirstOrDefault(a => !a.StartsWith("--") &&
            a.EndsWith(SheetStore.FileExtension, StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (openPath != null) openPath = Path.GetFullPath(openPath);

        // Already open? Hand the sheet to that window instead of starting a second one.
        if (!demo && !automated && !SingleInstance.TryBecomeFirst() && SingleInstance.SendToRunning(openPath ?? ""))
        {
            Shutdown();
            return;
        }

        // Normally the window starts with the sheet from last time; automated runs never touch it.
        var sheet = demo ? DemoSheet.Create() : automated ? new Sheet() : SheetStore.Load();
        string? lastFile = demo || automated ? null : SheetStore.LoadLastFile();

        if (ArgAfter("--pdf") is { } pdf)
        {
            if (openPath != null) sheet = SheetStore.ReadFile(openPath);
            if (args.Contains("--barred")) sheet.Barred = true;
            PdfExporter.Export(sheet, pdf);
            Shutdown();
            return;
        }
        if (ArgAfter("--midi") is { } midi)
        {
            if (openPath != null) sheet = SheetStore.ReadFile(openPath);
            MidiExporter.Export(sheet, midi);
            Shutdown();
            return;
        }
        if (args.Contains("--barred")) sheet.Barred = true;

        var window = new MainWindow(sheet, persist: !demo && !automated, lastFile);
        // Opening through the window asks to save unsaved work first, like the Open button.
        if (openPath != null) window.Loaded += (_, _) => window.OpenFile(openPath);
        if (!demo && !automated)
            SingleInstance.Listen(path => Dispatcher.BeginInvoke(() => window.BringForwardAndOpen(path)));
        if (args.Contains("--free")) window.Loaded += (_, _) => window.ShowFreeWriting();
        // Snapshots must not pick up a preview note from wherever the mouse happens to be.
        if (automated) window.Score.IsHitTestVisible = false;
        if (ArgAfter("--size") is { } size && size.Split('x') is [var sw, var sh] &&
            double.TryParse(sw, out var ww) && double.TryParse(sh, out var wh))
        {
            window.Width = ww;
            window.Height = wh;
        }
        if (ArgAfter("--lyric-test") is { } lyricPng)
        {
            window.ContentRendered += async (_, _) =>
            {
                await Task.Delay(300);
                await LyricTest(window);
                SaveSnapshot(window, lyricPng);
                PdfExporter.Export(sheet, Path.ChangeExtension(lyricPng, ".pdf"));
                Shutdown();
            };
        }
        else if (ArgAfter("--snapshot") is { } png)
        {
            window.ContentRendered += async (_, _) =>
            {
                await Task.Delay(400);
                SaveSnapshot(window, png);
                Shutdown();
            };
        }
        window.Show();
    }

    // Types lyrics under notes through the normal keyboard and text-input
    // events: replace a syllable, Tab to the next note, type more than fits,
    // and add to a lyric further down.
    static async Task LyricTest(MainWindow w)
    {
        var score = w.Score;
        Keyboard.Focus(score);
        void Key(Key k)
        {
            var src = PresentationSource.FromVisual(w)!;
            w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        void Type(string s)
        {
            foreach (var c in s)
            {
                // Letter keys also go through the note shortcuts first; they must not add notes.
                if (char.IsAsciiLetter(c)) Key(Enum.Parse<Key>(char.ToUpperInvariant(c).ToString()));
                else if (c == ' ') Key(System.Windows.Input.Key.Space);
                score.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                    new TextComposition(InputManager.Current, score, c.ToString())) { RoutedEvent = TextCompositionManager.TextInputEvent });
            }
        }
        async Task Settle()
        {
            score.UpdateLayout();
            await Task.Delay(50);
        }

        score.StartLyrics(0, 0);                   // under the first note: replace "Morn-"
        Key(System.Windows.Input.Key.End);
        for (int i = 0; i < 5; i++) Key(System.Windows.Input.Key.Back);
        Type("Sun-"); await Settle();
        Key(System.Windows.Input.Key.Tab);          // "ing" -> a long text that only partly fits
        Key(System.Windows.Input.Key.Back); Key(System.Windows.Input.Key.Back); Key(System.Windows.Input.Key.Back);
        Type("shine all day and all night long"); await Settle();
        score.StartLyrics(0, 10_000);              // the last lyric on the first staff
        Key(System.Windows.Input.Key.End);
        Type(" la la"); await Settle();
        Key(System.Windows.Input.Key.Escape); await Settle();
    }

    static void SaveSnapshot(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        int w = (int)content.ActualWidth, h = (int)content.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}

static class DemoSheet
{
    public static Sheet Create()
    {
        var sheet = new Sheet
        {
            Title = "Demo melody",
            Lyrics = "Morn-ing light comes o-ver the hill, the ket-tle sings and the house is still, hum it slow.",
        };
        int id = 1;
        void Add(Dur d, string letters, int octave, bool dot = false, int acc = 0)
        {
            foreach (var c in letters)
                sheet.Notes.Add(new Note { Id = id++, Dur = d, Dot = dot, Acc = acc, Step = octave * 7 + Theory.Letters.IndexOf(c) });
        }
        void AddRest(Dur d) => sheet.Notes.Add(new Note { Id = id++, Rest = true, Dur = d });

        Add(Dur.W, "E", 4);
        Add(Dur.H, "G", 4, acc: 1);
        Add(Dur.H, "C", 5);
        Add(Dur.E, "CDEF", 5);
        Add(Dur.S, "GFED", 5);
        Add(Dur.E, "C", 5, dot: true);
        Add(Dur.S, "B", 4);
        Add(Dur.Q, "A", 4);
        AddRest(Dur.Q);
        Add(Dur.E, "GAB", 4);
        AddRest(Dur.H);
        Add(Dur.Q, "D", 5, dot: true, acc: -1);
        Add(Dur.E, "C", 5);
        Add(Dur.W, "C", 5);
        AddRest(Dur.E);
        AddRest(Dur.S);
        sheet.Sanitize();   // one syllable per note
        return sheet;
    }
}
