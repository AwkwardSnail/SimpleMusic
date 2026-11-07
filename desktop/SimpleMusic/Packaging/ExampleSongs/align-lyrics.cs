#:project ../../SimpleMusic.csproj
#:property TargetFramework=net10.0-windows10.0.19041.0
#:property UseWPF=true
#:property PublishAot=false
// Lines the example songs' lyrics up under their notes, one syllable per note,
// using only spaces and hyphens (lyrics in SimpleMusic are free text).
// Run from this folder, after make-examples.ps1:
//   dotnet run align-lyrics.cs [scoreWidth] [--free <copy.smusic>]
// The width is the score area the alignment is made for; 1502 matches the
// Store screenshots (window 1600 x 1000). Other widths spread the notes
// differently, so the words drift a little there. --free also writes a copy
// of Twinkle before it is divided into bars, aligned for that view.
using System.IO;
using SimpleMusic.Music;
using SimpleMusic.Rendering;

double width = args.Length > 0 && !args[0].StartsWith("--")
    ? double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 1502;
int freeArg = Array.IndexOf(args, "--free");
string folder = Directory.GetCurrentDirectory();

const string Twinkle =
    "Twin-kle, twin-kle, lit-tle star, how I won-der what you are! Up a-bove the world so high, " +
    "like a dia-mond in the sky. Twin-kle, twin-kle, lit-tle star, how I won-der what you are!";
Align("Happy Birthday to You.smusic", null,
    "Hap-py birth-day to you, hap-py birth-day to you, hap-py birth-day, dear some-one, hap-py birth-day to you!");
Align("Twinkle Twinkle Little Star.smusic", null, Twinkle);
if (freeArg >= 0 && freeArg + 1 < args.Length)
    Align("Twinkle Twinkle Little Star.smusic", Path.GetFullPath(args[freeArg + 1]), Twinkle);

// Aligns `file`; with `freeCopy`, aligns it as free writing (no bars) and saves it there instead.
void Align(string file, string? freeCopy, string syllableText)
{
    string path = Path.Combine(folder, file);
    var sheet = SheetStore.ReadFile(path);
    if (freeCopy != null) { sheet.Barred = false; path = freeCopy; }

    // Syllables in singing order; true = the word carries on (hyphen after it).
    var syllables = new List<(string Text, bool Hyphen)>();
    foreach (var word in syllableText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = word.Split('-');
        for (int i = 0; i < parts.Length; i++) syllables.Add((parts[i], i < parts.Length - 1));
    }

    var layout = ScoreLayout.Compute(sheet, width);
    var sung = layout.Items.Where(it => it.Note is { Rest: false } && it.First).ToList();
    if (sung.Count != syllables.Count)
        throw new InvalidDataException($"{file}: {sung.Count} notes but {syllables.Count} syllables.");

    double W(string s) => MusicShapes.TextWidth(s, Metrics.LyricFont, Metrics.LyricSize);
    double space = W(" "), hyphen = W("-");
    var lines = new List<string>();

    foreach (var group in sung.Select((it, i) => (it, syl: syllables[i])).GroupBy(p => p.it.Sys))
    {
        var line = new System.Text.StringBuilder();
        double cur = layout.Lines[group.Key].Left;   // where the next character would start
        bool hyphenPending = false;
        void Spaces(int n) { if (n > 0) { line.Append(' ', n); cur += n * space; } }

        foreach (var (it, syl) in group)
        {
            // Centre the letters (not trailing punctuation) under the note head.
            string letters = syl.Text.TrimEnd(',', '.', '!', '?', ';', ':');
            double target = it.Cx - W(letters) / 2;
            if (hyphenPending)
            {
                double gap = target - cur;
                if (gap >= hyphen + 2 * space)
                {
                    Spaces((int)Math.Round((gap - hyphen) / 2 / space));
                    line.Append('-');
                    cur += hyphen;
                    Spaces(Math.Max(1, (int)Math.Round((target - cur) / space)));
                }
                else
                {
                    line.Append('-');                 // no room: keep the word together
                    cur += hyphen;
                }
            }
            else
            {
                int minimum = line.Length == 0 ? 0 : 1;
                Spaces(Math.Max(minimum, (int)Math.Round((target - cur) / space)));
            }
            line.Append(syl.Text);
            cur += W(syl.Text);
            hyphenPending = syl.Hyphen;
        }
        lines.Add(line.ToString().TrimEnd());
    }

    sheet.Lyrics = string.Join("\n", lines);
    SheetStore.WriteFile(sheet, path);
    Console.WriteLine($"{Path.GetFileName(path)}{(freeCopy != null ? " (free writing)" : "")}: {syllables.Count} syllables on {lines.Count} line(s)");
}
