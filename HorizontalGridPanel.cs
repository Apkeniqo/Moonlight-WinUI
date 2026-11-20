using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MoonlightWinUI;

/// <summary>
/// 横向滚动、固定行数（默认 2 行）的网格面板：先按行填满一列，再向右开下一列。
/// 用在外层横向 ScrollViewer 里 —— 两行排列、仍然横着滚。
/// </summary>
public sealed class HorizontalGridPanel : Panel
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(int), typeof(HorizontalGridPanel),
        new PropertyMetadata(2, OnLayoutPropertyChanged));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(HorizontalGridPanel),
        new PropertyMetadata(20.0, OnLayoutPropertyChanged));

    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HorizontalGridPanel)d).InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        if (children.Count == 0)
        {
            return new Size(0, 0);
        }

        var rows = Math.Max(1, Rows);
        var spacing = Math.Max(0, Spacing);
        var itemWidth = 0.0;
        var itemHeight = 0.0;

        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            itemWidth = Math.Max(itemWidth, children[i].DesiredSize.Width);
            itemHeight = Math.Max(itemHeight, children[i].DesiredSize.Height);
        }

        var columns = (int)Math.Ceiling(children.Count / (double)rows);
        var width = columns * itemWidth + Math.Max(0, columns - 1) * spacing;
        var height = rows * itemHeight + (rows - 1) * spacing;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        if (children.Count == 0)
        {
            return finalSize;
        }

        var rows = Math.Max(1, Rows);
        var spacing = Math.Max(0, Spacing);
        var itemWidth = 0.0;
        var itemHeight = 0.0;

        for (var i = 0; i < children.Count; i++)
        {
            itemWidth = Math.Max(itemWidth, children[i].DesiredSize.Width);
            itemHeight = Math.Max(itemHeight, children[i].DesiredSize.Height);
        }

        for (var i = 0; i < children.Count; i++)
        {
            var column = i / rows;
            var row = i % rows;
            var x = column * (itemWidth + spacing);
            var y = row * (itemHeight + spacing);
            children[i].Arrange(new Rect(x, y, itemWidth, itemHeight));
        }

        return finalSize;
    }
}
