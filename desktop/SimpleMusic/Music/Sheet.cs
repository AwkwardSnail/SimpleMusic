using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleMusic.Music;

public sealed class Note
{
    public int Id { get; set; }
    public bool Rest { get; set; }
    public Dur Dur { get; set; } = Dur.Q;
    public bool Dot { get; set; }
    public int Step { get; set; } = Theory.B4;
    public int Acc { get; set; }   // -1 flat, 0 none, 1 sharp

    // The words sung on this note. A trailing "-" means the word carries on
    // to the next note (drawn as a hyphen between the two).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lyric { get; set; }

    public const int MaxLyric = 60;

    [JsonIgnore] public int Ticks => Theory.Ticks(Dur, Dot);

    public Note Clone() => (Note)MemberwiseClone();
}

// The whole document: the notes in order plus how to divide them into bars.
public sealed class Sheet
{
    public string Title { get; set; } = "";
    // Lyrics used to be one free text; older files are converted to one
    // syllable per note when they are opened (see Sanitize).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lyrics { get; set; }
    public List<Note> Notes { get; set; } = new();
    public bool Barred { get; set; }
    public int Beats { get; set; } = 4;
    public int BeatTicks { get; set; } = 8;
    public bool FillLast { get; set; } = true;
    public int Tempo { get; set; } = 90;

    [JsonIgnore] public int BarTicks => Beats * BeatTicks;
    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled melody" : Title.Trim();

    // A dotted beat means compound time: 2 dotted quarters per bar -> 6/8.
    [JsonIgnore]
    public (int Num, int Den) TimeSignature =>
        BeatTicks % 3 == 0 ? (Beats * 3, 32 / (BeatTicks / 3)) : (Beats, 32 / BeatTicks);

    public void Sanitize()
    {
        Notes = Notes.Where(n => n.Step >= Theory.MinStep && n.Step <= Theory.MaxStep && Enum.IsDefined(n.Dur)).ToList();
        foreach (var n in Notes)
        {
            if (n.Dur == Dur.S) n.Dot = false;
            n.Acc = Math.Clamp(n.Acc, -1, 1);
            n.Lyric = n.Rest ? null : CleanLyric(n.Lyric);
        }
        Beats = Math.Clamp(Beats, 1, 16);
        if (!Theory.BeatChoices.Contains(BeatTicks)) BeatTicks = 8;
        Tempo = Math.Clamp(Tempo, 40, 200);
        Title ??= "";
        if (Title.Length > 120) Title = Title[..120];
        if (!string.IsNullOrWhiteSpace(Lyrics) && Notes.All(n => n.Lyric == null)) SpreadOldLyrics(Lyrics);
        Lyrics = null;
    }

    public static string? CleanLyric(string? text)
    {
        if (text == null) return null;
        text = new string(text.Where(c => c >= ' ' && c != '').ToArray());
        if (text.Length > Note.MaxLyric) text = text[..Note.MaxLyric];
        return text.Trim().Length == 0 ? null : text;
    }

    // Old free-text lyrics: one syllable per sung note, words split at hyphens
    // ("Twin-kle" -> "Twin-", "kle"). Anything left over goes on the last note.
    void SpreadOldLyrics(string text)
    {
        var syllables = new List<string>();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = word.Split('-', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) syllables.Add(i < parts.Length - 1 ? parts[i] + "-" : parts[i]);
        }
        var sung = Notes.Where(n => !n.Rest).ToList();
        if (sung.Count == 0) return;
        for (int i = 0; i < Math.Min(sung.Count, syllables.Count); i++) sung[i].Lyric = CleanLyric(syllables[i]);
        if (syllables.Count > sung.Count)
            sung[^1].Lyric = CleanLyric(string.Join(" ", syllables.Skip(sung.Count - 1)).Replace("- ", "-"));
    }
}

// Remembers the sheet between runs (the app's own data folder).
public static class SheetStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleMusic");
    static string FilePath => Path.Combine(DataFolder, "sheet.json");
    static string LastFilePath => Path.Combine(DataFolder, "lastfile.txt");

    /* ---------------------- Sheet files (.smusic) ---------------------- */

    public const string FileExtension = ".smusic";
    public const string FileFilter = "SimpleMusic sheet (*.smusic)|*.smusic";
    const int FormatVersion = 1;

    // What a .smusic file holds: a marker, a format version and the sheet.
    sealed class SheetFile
    {
        public string App { get; set; } = "SimpleMusic";
        public int FormatVersion { get; set; } = SheetStore.FormatVersion;
        public Sheet? Sheet { get; set; }
    }

    public static string ToFileJson(Sheet sheet) => JsonSerializer.Serialize(new SheetFile { Sheet = sheet }, Options);

    public static Sheet ReadFile(string path)
    {
        SheetFile? file;
        try { file = JsonSerializer.Deserialize<SheetFile>(File.ReadAllText(path), Options); }
        catch (JsonException) { throw new InvalidDataException("This file isn't a SimpleMusic sheet, or it is damaged."); }
        if (file?.App != "SimpleMusic" || file.Sheet == null)
            throw new InvalidDataException("This file isn't a SimpleMusic sheet.");
        if (file.FormatVersion > FormatVersion)
            throw new InvalidDataException("This sheet was made with a newer version of SimpleMusic. Please update the app to open it.");
        file.Sheet.Sanitize();
        return file.Sheet;
    }

    // Writes to a temporary file first, so a failed save never damages the old file.
    public static void WriteFile(Sheet sheet, string path)
    {
        var tmp = path + ".saving";
        File.WriteAllText(tmp, ToFileJson(sheet));
        File.Move(tmp, path, overwrite: true);
    }

    // Which file the remembered sheet belongs to, so it reopens as that file.
    public static string? LoadLastFile()
    {
        try
        {
            var path = File.Exists(LastFilePath) ? File.ReadAllText(LastFilePath).Trim() : "";
            return path.Length > 0 && File.Exists(path) ? path : null;
        }
        catch (Exception) { return null; }
    }

    public static void SaveLastFile(string? path)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(LastFilePath, path ?? "");
        }
        catch (Exception) { /* only a convenience */ }
    }

    /* --------------------- Remembered sheet (autosave) --------------------- */

    public static Sheet Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var sheet = JsonSerializer.Deserialize<Sheet>(File.ReadAllText(FilePath), Options);
                if (sheet != null) { sheet.Sanitize(); return sheet; }
            }
        }
        catch (Exception) { /* a broken save just starts a fresh sheet */ }
        return new Sheet();
    }

    public static void Save(Sheet sheet)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(sheet, Options));
        }
        catch (Exception) { /* not being able to save must never stop the editor */ }
    }
}
