using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SimpleMusic.Music;

namespace SimpleMusic.Rendering;

// Builds the shapes of the notation as vector geometry. Everything is a
// Geometry, so the same shapes are used on screen, in the toolbar icons
// and in the exported PDF.
public static class MusicShapes
{
    const string MusicFont = "Segoe UI Symbol";
    static readonly Dictionary<string, Geometry> RawCache = new();

    static Geometry Frozen(Geometry g)
    {
        if (g.CanFreeze) g.Freeze();
        return g;
    }

    public static Geometry Path(FormattableString d) => Frozen(Geometry.Parse(FormattableString.Invariant(d)));
    public static Geometry Line(double x1, double y1, double x2, double y2) => Frozen(new LineGeometry(new Point(x1, y1), new Point(x2, y2)));
    public static Geometry Rect(double x, double y, double w, double h, double r = 0) => Frozen(new RectangleGeometry(new Rect(x, y, w, h), r, r));
    public static Geometry Circle(double cx, double cy, double r) => Frozen(new EllipseGeometry(new Point(cx, cy), r, r));

    static Geometry Ellipse(double cx, double cy, double rx, double ry, double angle) =>
        Frozen(new EllipseGeometry(new Point(cx, cy), rx, ry, new RotateTransform(angle, cx, cy)));

    static Geometry Group(FillRule rule, params Geometry[] parts)
    {
        var g = new GeometryGroup { FillRule = rule };
        foreach (var p in parts) g.Children.Add(p);
        return Frozen(g);
    }

    /* ------------------------------ Note heads ------------------------------ */

    // Whole note: a wide oval with a counter tilted steeply, so the sides are
    // heavy and the top-right / bottom-left are thin, like an engraved head.
    // Half note: the usual slanted head with a long, narrow slanted counter.
    // Filled heads: the same slanted oval, solid.
    public static Geometry Head(Dur d, double cx, double y) => d switch
    {
        Dur.W => Group(FillRule.EvenOdd,
            Ellipse(cx, y, 7.6, 4.9, 0),
            Ellipse(cx, y, 4.1, 2.75, -58)),
        Dur.H => Group(FillRule.EvenOdd,
            Ellipse(cx, y, 6.3, 4.45, -22),
            Ellipse(cx, y, 4.9, 2.05, -33)),
        _ => Ellipse(cx, y, 6.1, 4.35, -22),
    };

    public static Geometry Flags(int count, double sx, double ey, bool up)
    {
        var parts = new Geometry[count];
        int d = up ? 1 : -1;
        for (int k = 0; k < count; k++)
        {
            double y0 = ey + d * k * 8;
            parts[k] = Path($"M{sx},{y0} C{sx + 1},{y0 + d * 7} {sx + 10},{y0 + d * 9} {sx + 7},{y0 + d * 20} C{sx + 7.5},{y0 + d * 12} {sx + 3},{y0 + d * 10.5} {sx},{y0 + d * 9} Z");
        }
        return Group(FillRule.Nonzero, parts);
    }

    /* --------------------------------- Rests -------------------------------- */

    // Whole rest hangs from the 4th line, half rest sits on the middle line;
    // both get small "ears" so they read as rests and not as plain boxes.
    // Quarter, eighth and sixteenth rests use the engraved music font symbols.
    public static Geometry Rest(Dur d, double cx, double t) => d switch
    {
        Dur.W => Group(FillRule.Nonzero, Rect(cx - 6.5, t + 10, 13, 5.6, 0.6), Rect(cx - 9, t + 9.4, 18, 1.2)),
        Dur.H => Group(FillRule.Nonzero, Rect(cx - 6.5, t + 14.4, 13, 5.6, 0.6), Rect(cx - 9, t + 19.4, 18, 1.2)),
        Dur.Q => FitGlyph("\U0001D13D", cx, t + 5, 30),
        Dur.E => FitGlyph("\U0001D13E", cx, t + 12, 17),
        _ => FitGlyph("\U0001D13F", cx, t + 12, 27),
    };

    /* ------------------------- Clef, accidentals, text ------------------------- */

    public static Geometry TrebleClef(double x, double staffTop) => FitGlyph("\U0001D11E", x + 14, staffTop - 13, 66);
    public static Geometry Sharp(double cx, double y) => FitGlyph("♯", cx, y - 11, 22);
    public static Geometry Flat(double cx, double y) => FitGlyph("♭", cx, y - 14, 19);

    public enum Anchor { Left, Center }

    public static Geometry Text(string s, string family, FontWeight weight, FontStyle style, double size,
        double x, double baseline, Anchor anchor = Anchor.Left)
    {
        var face = new Typeface(new FontFamily(family), style, weight, FontStretches.Normal);
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0);
        double ox = anchor == Anchor.Center ? x - ft.WidthIncludingTrailingWhitespace / 2 : x;
        return Group(FillRule.Nonzero, ft.BuildGeometry(new Point(ox, baseline - ft.Baseline)));
    }

    public static double TextWidth(string s, string family, double size)
    {
        var face = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0)
            .WidthIncludingTrailingWhitespace;
    }

    // A music-font symbol scaled so its outline is `height` tall, centred on
    // `cx` with its top at `top`. Placing by the outline (not by font metrics)
    // keeps it right whatever the font's own spacing is.
    public static Geometry FitGlyph(string s, double cx, double top, double height)
    {
        var raw = RawGlyph(s);
        var b = raw.Bounds;
        if (b.IsEmpty || b.Height <= 0) return Geometry.Empty;
        double k = height / b.Height;
        var tf = new TransformGroup();
        tf.Children.Add(new TranslateTransform(-b.X - b.Width / 2, -b.Y));
        tf.Children.Add(new ScaleTransform(k, k));
        tf.Children.Add(new TranslateTransform(cx, top));
        var g = new GeometryGroup { FillRule = FillRule.Nonzero, Transform = tf };
        g.Children.Add(raw);
        return Frozen(g);
    }

    static Geometry RawGlyph(string s)
    {
        lock (RawCache)
        {
            if (RawCache.TryGetValue(s, out var g)) return g;
            var face = new Typeface(new FontFamily(MusicFont), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 100, Brushes.Black, 1.0);
            g = Frozen(ft.BuildGeometry(new Point(0, 0)));
            RawCache[s] = g;
            return g;
        }
    }

    /* ------------------------------ Ties & beams ------------------------------ */

    public static Geometry Tie(double x1, double x2, double y, int dir)
    {
        double xm = (x1 + x2) / 2;
        double h = Math.Min(11, 5 + (x2 - x1) * 0.06);
        return Path($"M{x1},{y} Q{xm},{y + dir * h} {x2},{y} Q{xm},{y + dir * (h - 3)} {x1},{y} Z");
    }

    public static Geometry BeamBar(Beam bm, double xa, double xb, double offset)
    {
        const double th = 4.6;
        int d = bm.Up ? 1 : -1; // second beams sit on the note side of the first
        double ya = bm.YAt(xa) + d * offset;
        double yb = bm.YAt(xb) + d * offset;
        return Path($"M{xa},{ya} L{xb},{yb} L{xb},{yb + d * th} L{xa},{ya + d * th} Z");
    }
}
