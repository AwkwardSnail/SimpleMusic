using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using SimpleMusic.Music;
using SimpleMusic.Rendering;

namespace SimpleMusic.Controls;

// Toolbar icon for a note length or a rest, drawn with the same shapes as the score.
public sealed class NoteIcon : FrameworkElement
{
    public static readonly DependencyProperty DurProperty = DependencyProperty.Register(
        nameof(Dur), typeof(Dur), typeof(NoteIcon), new FrameworkPropertyMetadata(Dur.Q, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DottedProperty = DependencyProperty.Register(
        nameof(Dotted), typeof(bool), typeof(NoteIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsRestProperty = DependencyProperty.Register(
        nameof(IsRest), typeof(bool), typeof(NoteIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(NoteIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Dur Dur { get => (Dur)GetValue(DurProperty); set => SetValue(DurProperty, value); }
    public bool Dotted { get => (bool)GetValue(DottedProperty); set => SetValue(DottedProperty, value); }
    public bool IsRest { get => (bool)GetValue(IsRestProperty); set => SetValue(IsRestProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(24, 32);

    protected override void OnRender(DrawingContext dc)
    {
        var brush = Foreground;
        if (IsRest)
        {
            dc.DrawGeometry(brush, null, MusicShapes.Rest(Dur.Q, 12, -4));
            return;
        }

        // Drawn at score size around the notehead, then scaled into the icon box.
        bool whole = Dur == Dur.W;
        dc.PushTransform(whole ? new MatrixTransform(1.25, 0, 0, 1.25, 11.5, 18) : new MatrixTransform(0.82, 0, 0, 0.82, 9, 27));
        dc.DrawGeometry(brush, null, MusicShapes.Head(Dur, 0, 0));
        if (!whole)
        {
            dc.DrawLine(new Pen(brush, 1.6), new Point(5.4, -1), new Point(5.4, -33));
            int flags = Dur == Dur.E ? 1 : Dur == Dur.S ? 2 : 0;
            if (flags > 0) dc.DrawGeometry(brush, null, MusicShapes.Flags(flags, 5.4, -33, true));
        }
        if (Dotted && Dur != Dur.S) dc.DrawGeometry(brush, null, MusicShapes.Circle(whole ? 11.5 : 10.5, 0, 2.1));
        dc.Pop();
    }
}
