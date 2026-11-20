using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace MoonlightWinUI;

/// <summary>
/// Windows 10 开始菜单的磁贴手感：整块卡片（背景 + 边框 + 内容 + 高光）一起放大到 105%，
/// 鼠标处的径向高光跟着走，进场淡入缩放（按序错开）。
///
/// 为了既能整体缩放、又不会“抖动 / 点不动”，这里用了一个固定尺寸的 slot：
///   slot 尺寸 = 卡片 × <see cref="HoverScale"/>，卡片居中放在里面；
///   悬停时卡片正好放大到填满 slot，所以**永远不会盖到相邻卡片**（命中区域恒定）；
///   最上面再盖一层不缩放的透明命中层，鼠标事件都归它，缩放再也不会影响 enter/exit。
/// </summary>
internal static class FluentCard
{
    private static readonly HashSet<Border> Attached = new(ReferenceEqualityComparer.Instance);

    // 静止 96% → 悬停 100%：整块放大但绝不越出格子（越出会压到邻居、引发抖动）
    private const float HoverScale = 1.05f;
    private const float RestScale = 1f;
    private const float PressScale = 0.97f;

    /// <summary>把卡片包进固定尺寸的 slot 并挂好动画；返回 slot（卡片必须还没有 Parent）。</summary>
    public static Grid CreateSlot(Border card, int index = 0)
    {
        var cardWidth = card.Width;      // NaN = 跟着父容器拉伸
        var cardHeight = card.Height;

        var slot = new Grid
        {
            // 宽度 NaN 表示跟着父容器拉伸（自适应换行面板里用）
            Width = double.IsNaN(cardWidth) ? double.NaN : cardWidth,
            Height = double.IsNaN(cardHeight) ? double.NaN : cardHeight,
            Background = new SolidColorBrush(Colors.Transparent),
        };

        if (double.IsNaN(card.Width))
        {
            // 宽度跟着格子拉伸（自适应换行面板里用）：卡片必须填满格子，不能再居中
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            card.HorizontalAlignment = HorizontalAlignment.Center;
        }

        card.VerticalAlignment = VerticalAlignment.Center;

        var glow = new Border
        {
            IsHitTestVisible = false,
            Opacity = 0,
            CornerRadius = card.CornerRadius,
            Background = BuildGlow(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        if (card.Child is UIElement content)
        {
            card.Child = null;
            var inner = new Grid();
            inner.Children.Add(content);
            inner.Children.Add(glow);
            card.Child = inner;
        }
        else
        {
            card.Child = glow;
        }

        slot.Children.Add(card);

        // 命中层：铺满 slot、不缩放，所有指针事件都归它 → 命中区域恒定
        var catcher = new Border { Background = new SolidColorBrush(Colors.Transparent) };
        slot.Children.Add(catcher);

        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        var visual = ElementCompositionPreview.GetElementVisual(card);
        card.SizeChanged += (_, e) =>
        {
            visual.CenterPoint = new Vector3(0.5f * (float)e.NewSize.Width, 0.5f * (float)e.NewSize.Height, 0);
        };

        // 卡片尺寸由外部决定时（应用卡片），slot 跟着卡片尺寸走
        if (!double.IsNaN(cardWidth))
        {
            card.SizeChanged += (_, e) =>
            {
                slot.Width = e.NewSize.Width;
                slot.Height = e.NewSize.Height;
            };
        }

        AttachAnimations(card, slot, catcher, visual, glow, index);
        Attached.Add(card);
        return slot;
    }

    /// <summary>点击时的「按下回弹」动画：放大到悬停尺寸再弹回静止尺寸。</summary>
    public static void Pulse(FrameworkElement card)
    {
        if (card is null || !Attached.Contains(card))
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(card);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.2f, 0.9f),
            new System.Numerics.Vector2(0.2f, 1f));

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0.35f, new System.Numerics.Vector3(HoverScale, HoverScale, 1f), easing);
        scale.InsertKeyFrame(1f, new System.Numerics.Vector3(RestScale, RestScale, 1f), easing);
        scale.Duration = TimeSpan.FromMilliseconds(280);
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>按 slot 找里面的卡片并播放点击动画。</summary>
    public static void PulseSlot(Grid? slot)
    {
        if (slot is null || slot.Children.Count == 0)
        {
            return;
        }

        if (slot.Children[0] is FrameworkElement card)
        {
            Pulse(card);
        }
    }
    /// <summary>已经在界面里的卡片：原地替换成 slot（应用卡片用）。</summary>
    public static void AttachInPlace(Border card, int index = 0)
    {
        if (Attached.Contains(card) || card.Parent is not Panel parent)
        {
            return;
        }

        var i = parent.Children.IndexOf(card);
        if (i < 0)
        {
            return;
        }

        parent.Children.RemoveAt(i);
        parent.Children.Insert(i, CreateSlot(card, index));
    }

    private static void AttachAnimations(Border card, Grid slot, Border catcher, Visual visual, Border glow, int index)
    {
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        // 进场：淡入 + 从 94% 放大到 100%
        visual.Opacity = 0f;
        visual.Scale = new Vector3(0.94f, 0.94f, 1f);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(240);
        fade.DelayTime = TimeSpan.FromMilliseconds(40 * index);
        fade.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;

        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(1f, new Vector3(RestScale, RestScale, 1f), easing);
        rise.Duration = TimeSpan.FromMilliseconds(280);
        rise.DelayTime = TimeSpan.FromMilliseconds(40 * index);
        rise.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Scale", rise);

        // 悬停：整块放大（正好填满 slot）+ 上浮 + 高光
        catcher.PointerEntered += (_, _) =>
        {
            AnimateScale(visual, HoverScale, 150);
            AnimateGlow(glow, 1f, 130);
            card.Translation = new Vector3(0, 0, 16);
        };

        catcher.PointerExited += (_, _) =>
        {
            AnimateScale(visual, RestScale, 200);
            AnimateGlow(glow, 0f, 180);
            card.Translation = Vector3.Zero;
        };

        // 按下轻微回弹（命中层不缩放，所以不会点不中）
        catcher.PointerPressed += (_, _) => AnimateScale(visual, PressScale, 80);

        catcher.PointerReleased += (_, _) => AnimateScale(visual, HoverScale, 140);

        catcher.PointerCaptureLost += (_, _) =>
        {
            AnimateScale(visual, RestScale, 200);
            AnimateGlow(glow, 0f, 180);
            card.Translation = Vector3.Zero;
        };

        catcher.PointerMoved += (_, e) =>
        {
            if (glow.Background is not RadialGradientBrush brush)
            {
                return;
            }

            var w = Math.Max(1, slot.ActualWidth);
            var h = Math.Max(1, slot.ActualHeight);
            var p = e.GetCurrentPoint(slot).Position;
            var center = new Point(Math.Clamp(p.X / w, 0, 1), Math.Clamp(p.Y / h, 0, 1));
            brush.Center = center;
            brush.GradientOrigin = center;
        };
    }

    private static void AnimateScale(Visual visual, float target, int milliseconds)
    {
        var compositor = visual.Compositor;
        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1f, new Vector3(target, target, 1f),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        anim.Duration = TimeSpan.FromMilliseconds(milliseconds);
        anim.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        visual.StartAnimation("Scale", anim);
    }

    private static void AnimateGlow(Border glow, float target, int milliseconds)
    {
        var visual = ElementCompositionPreview.GetElementVisual(glow);
        var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(1f, target);
        anim.Duration = TimeSpan.FromMilliseconds(milliseconds);
        anim.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        visual.StartAnimation("Opacity", anim);
    }

    private static RadialGradientBrush BuildGlow()
    {
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.8,
            RadiusY = 0.8,
        };

        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x34, 0xFF, 0xFF, 0xFF), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 1 });
        return brush;
    }
}
