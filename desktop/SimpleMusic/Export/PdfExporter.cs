using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using SimpleMusic.Music;
using SimpleMusic.Rendering;

namespace SimpleMusic.Export;

// Writes the score as a vector PDF (A4). The notation is already geometry,
// so every symbol becomes a PDF path: no fonts need to be embedded.
public static class PdfExporter
{
    const double PrintW = 660;          // layout width in score units
    const double PageW = 595.28, PageH = 841.89, Margin = 48;

    public static void Export(Sheet sheet, string path)
    {
        var L = ScoreLayout.Compute(sheet, PrintW, roomForLyrics: false);
        var ops = ScoreRenderer.Render(sheet, L, new ScoreRenderOptions { Print = true });
        double k = (PageW - 2 * Margin) / PrintW;
        double usable = (PageH - 2 * Margin) / k;

        var header = new List<DrawOp>
        {
            new(MusicShapes.Text(sheet.DisplayTitle, "Georgia", FontWeights.Bold, FontStyles.Normal, 26, PrintW / 2, 30, MusicShapes.Anchor.Center), Role.Ink),
        };
        double headH = 50;
        if (sheet.Barred)
        {
            var (num, den) = sheet.TimeSignature;
            header.Add(new(MusicShapes.Text($"{num}/{den} · {sheet.Tempo} beats per minute", "Segoe UI", FontWeights.Normal,
                FontStyles.Normal, 12, PrintW / 2, 52, MusicShapes.Anchor.Center), Role.Muted));
            headH = 66;
        }

        // One block per line of music (with its lyrics), so a page break never cuts through a staff.
        var pages = new List<string>();
        var sb = PageStart(k);
        foreach (var op in header) Emit(sb, op);
        double y = headH;
        double sysH = L.SysH;
        for (int s = 0; s < L.Lines.Count; s++)
        {
            double top = Metrics.PadTop + s * sysH;
            if (y + sysH > usable && y > 0)
            {
                pages.Add(sb.ToString());
                sb = PageStart(k);
                y = 0;
            }
            sb.Append("q\n1 0 0 1 0 ").Append(F(y - top)).Append(" cm\n")
              .Append("0 ").Append(F(top)).Append(' ').Append(F(PrintW)).Append(' ').Append(F(sysH)).Append(" re W n\n");
            foreach (var op in ops)
            {
                var b = op.Geometry.Bounds;
                if (!b.IsEmpty && b.Bottom >= top - 1 && b.Top <= top + sysH + 1) Emit(sb, op);
            }
            sb.Append("Q\n");
            y += sysH;
        }
        pages.Add(sb.ToString());
        Write(path, pages, sheet.DisplayTitle);
    }

    // Page coordinates are flipped so y grows downwards like on screen.
    static StringBuilder PageStart(double k) => new StringBuilder()
        .Append("1 0 0 -1 0 ").Append(F(PageH)).Append(" cm\n")
        .Append(F(k)).Append(" 0 0 ").Append(F(k)).Append(' ').Append(F(Margin)).Append(' ').Append(F(Margin)).Append(" cm\n")
        .Append("1 J 1 j\n");

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static string Rgb(Role r) => r switch
    {
        Role.Staff => "0.3 0.3 0.3",
        Role.Muted => "0.4 0.4 0.4",
        _ => "0 0 0",
    };

    static bool EvenOdd(Geometry g) => g switch
    {
        GeometryGroup gg => gg.FillRule == FillRule.EvenOdd,
        PathGeometry pg => pg.FillRule == FillRule.EvenOdd,
        StreamGeometry st => st.FillRule == FillRule.EvenOdd,
        _ => false,
    };

    static void Emit(StringBuilder sb, DrawOp op)
    {
        var flat = op.Geometry.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
        if (flat.Figures.Count == 0) return;
        if (op.Fill is Role f) sb.Append(Rgb(f)).Append(" rg\n");
        else if (op.Stroke is Role s) sb.Append(Rgb(s)).Append(" RG ").Append(F(op.Thickness)).Append(" w\n");
        else return;

        foreach (var fig in flat.Figures)
        {
            Point(sb, fig.StartPoint, "m");
            foreach (var seg in fig.Segments)
            {
                if (seg is LineSegment ls) Point(sb, ls.Point, "l");
                else if (seg is PolyLineSegment pl) foreach (var p in pl.Points) Point(sb, p, "l");
            }
            if (fig.IsClosed) sb.Append("h\n");
        }
        sb.Append(op.Fill != null ? (EvenOdd(op.Geometry) ? "f*\n" : "f\n") : "S\n");
    }

    static void Point(StringBuilder sb, Point p, string op) =>
        sb.Append(F(p.X)).Append(' ').Append(F(p.Y)).Append(' ').Append(op).Append('\n');

    static void Write(string path, List<string> pages, string title)
    {
        int count = pages.Count;
        int objects = 3 + 2 * count;
        var offsets = new long[objects + 1];
        using var fs = File.Create(path);
        void Raw(string s) => fs.Write(Encoding.Latin1.GetBytes(s));
        void Obj(int id, string body)
        {
            offsets[id] = fs.Position;
            Raw($"{id} 0 obj\n{body}\nendobj\n");
        }

        Raw("%PDF-1.4\n%âãÏÓ\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, count).Select(i => $"{4 + 2 * i} 0 R"));
        Obj(2, $"<< /Type /Pages /Kids [{kids}] /Count {count} >>");
        Obj(3, $"<< /Title {PdfText(title)} /Creator (Simple Music Melody) /Producer (Simple Music Melody) >>");
        for (int i = 0; i < count; i++)
        {
            int pageId = 4 + 2 * i, contentId = 5 + 2 * i;
            Obj(pageId, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(PageW)} {F(PageH)}] /Resources << >> /Contents {contentId} 0 R >>");
            var data = Deflate(pages[i]);
            offsets[contentId] = fs.Position;
            Raw($"{contentId} 0 obj\n<< /Length {data.Length} /Filter /FlateDecode >>\nstream\n");
            fs.Write(data);
            Raw("\nendstream\nendobj\n");
        }
        long xref = fs.Position;
        var x = new StringBuilder($"xref\n0 {objects + 1}\n0000000000 65535 f \n");
        for (int id = 1; id <= objects; id++) x.Append(offsets[id].ToString("D10")).Append(" 00000 n \n");
        x.Append($"trailer\n<< /Size {objects + 1} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Raw(x.ToString());
    }

    static byte[] Deflate(string s)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(Encoding.ASCII.GetBytes(s));
        return ms.ToArray();
    }

    // Unicode text string (UTF-16BE with byte-order mark), so any title works.
    static string PdfText(string s) =>
        "<FEFF" + string.Concat(Encoding.BigEndianUnicode.GetBytes(s).Select(b => b.ToString("X2"))) + ">";
}
