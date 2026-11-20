using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MoonlightWinUI;

/// <summary>
/// 自适应换行面板：所有卡片**统一宽度**（<see cref="ItemWidth"/>），按可用宽度决定每行几张卡，
/// 放不下就换行。整行还有一个可选的最大占用宽度（<see cref="MaxPanelWidth"/>），
/// 它只决定"每行最多占多宽"，**不会改变卡片宽度** —— 调小它只会让每行少几张、更早换行。
/// </summary>
public sealed class AdaptiveWrapPanel : Panel
{
    /// <summary>每张卡片的固定宽度（所有卡片统一）。</summary>
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(AdaptiveWrapPanel),
        new PropertyMetadata(300.0, OnLayoutPropertyChanged));

    /// <summary>整行最大占用宽度；0 = 不限制。只影响每行几列，不影响卡片宽度。</summary>
    public static readonly DependencyProperty MaxPanelWidthProperty = DependencyProperty.Register(
        nameof(MaxPanelWidth), typeof(double), typeof(AdaptiveWrapPanel),
        new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveWrapPanel),
        new PropertyMetadata(10.0, OnLayoutPropertyChanged));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double MaxPanelWidth
    {
        get => (double)GetValue(MaxPanelWidthProperty);
        set => SetValue(MaxPanelWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((AdaptiveWrapPanel)d).InvalidateMeasure();
    }

    private (double Available, double CardWidth, int Columns) Compute(double width, int childCount)
    {
        var spacing = Math.Max(0, Spacing);
        var available = width;
        if (double.IsInfinity(available) || available <= 0)
        {
            available = Math.Max(ItemWidth, 1) * 3 + spacing * 2;
        }

        if (MaxPanelWidth > 0)
        {
            available = Math.Min(available, MaxPanelWidth);
        }

        // 卡片宽度固定，仅在窗口比一张卡还窄时收缩
        var cardWidth = Math.Min(Math.Max(1, ItemWidth), available);
        var columns = (int)Math.Floor((available + spacing) / (cardWidth + spacing));
        columns = Math.Max(1, columns);
        columns = Math.Min(columns, Math.Max(1, childCount));
        return (available, cardWidth, columns);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        if (children.Count == 0)
        {
            return new Size(0, 0);
        }

        var spacing = Math.Max(0, Spacing);
        var (available, cardWidth, columns) = Compute(availableSize.Width, children.Count);

        var rowHeight = 0.0;
        var totalHeight = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(cardWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

            var endOfRow = (i + 1) % columns == 0;
            var lastChild = i == children.Count - 1;
            if (endOfRow || lastChild)
            {
                totalHeight += rowHeight;
                if (!lastChild)
                {
                    totalHeight += spacing;
                }

                rowHeight = 0;
            }
        }

        var contentWidth = columns * cardWidth + spacing * (columns - 1);
        return new Size(Math.Min(available, contentWidth), totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        if (children.Count == 0)
        {
            return finalSize;
        }

        var spacing = Math.Max(0, Spacing);
        var (_, cardWidth, columns) = Compute(finalSize.Width, children.Count);

        var rowTop = 0.0;
        var rowHeight = 0.0;
        var indexInRow = 0;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];

            if (indexInRow == 0)
            {
                rowHeight = 0;
                for (var j = i; j < Math.Min(i + columns, children.Count); j++)
                {
                    rowHeight = Math.Max(rowHeight, children[j].DesiredSize.Height);
                }
            }

            var left = indexInRow * (cardWidth + spacing);
            child.Arrange(new Rect(left, rowTop, cardWidth, rowHeight));

            indexInRow++;
            if (indexInRow >= columns)
            {
                indexInRow = 0;
                rowTop += rowHeight + spacing;
            }
        }

        return finalSize;
    }
}
