using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.DesktopV2.Controls;

/// <summary>
/// 把行内差异片段渲染成一段文本，独有的词带底色高亮。
/// 只负责显示，不做任何文本处理，也不访问外部资源。
/// </summary>
public sealed class InlineDiffText : TextBlock
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<InlineSegment>), typeof(InlineDiffText),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush), typeof(Brush), typeof(InlineDiffText),
        new PropertyMetadata(Brushes.Transparent, OnChanged));

    public IReadOnlyList<InlineSegment>? Segments
    {
        get => (IReadOnlyList<InlineSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Brush HighlightBrush
    {
        get => (Brush)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        ((InlineDiffText)target).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();
        if (Segments is null) return;
        foreach (var segment in Segments)
        {
            var run = new Run(segment.Text);
            if (segment.Highlight) run.Background = HighlightBrush;
            Inlines.Add(run);
        }
    }
}
