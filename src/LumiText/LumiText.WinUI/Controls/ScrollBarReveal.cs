using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace LumiText.WinUI.Controls;

/// <summary>
/// 滚动条的"滑出/收起"行为：闲置藏于右缘、滚动时细条滑出指示进度、停下向右缘滑回；
/// 悬停变粗（8px）、离开后再收起。
/// </summary>
/// <remarks>
/// <para>配套模板见应用侧的 <c>ScrollBarStyle.xaml</c>（App 级隐式样式，全程序通用）——那套模板
/// 刻意没有 VisualState，显隐/宽细全部由这里驱动，系统"始终显示滚动条"设置影响不到。</para>
/// <para><b>只认自家的模板</b>：认门标志是模板里 VerticalThumb 的 <c>Tag="vthumb"</c>。
/// 拿不到这个滑块就整体 no-op——不然换个宿主（没合并该样式的场景）会把默认模板的滑块
/// 也平移到右缘外、宽度动画照改，砸了别人家的滚动条。</para>
/// <para>用法：编辑器与只读宿主（<see cref="LumiEditor"/> / <see cref="LumiDocumentView"/>）
/// 在 Loaded 里自带；列表、设置页等其它宿主各自 Loaded 后调 <see cref="AttachTo"/>。
/// 一个滚动条只挂一次（用 Tag 做标记）。</para>
/// </remarks>
public static class ScrollBarReveal
{
    private const double ThinWidth = 4;
    private const double ThickWidth = 8;

    /// <summary>"收起"位：滑块整体滑到右缘之外，出现/消失就是滑出/滑回。</summary>
    private const double RetractedShift = 12;

    private const string AttachedMarker = "scroll-reveal";

    /// <summary>自家模板的标志物：ScrollBarStyle.xaml 里 VerticalThumb 的 Tag。</summary>
    private const string OurThumbMarker = "vthumb";

    /// <summary>滚动停止后滑块收起的等待时长。</summary>
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(800);

    /// <summary>给 <paramref name="root"/> 自身或其内部的第一个 ScrollViewer 挂上行为。</summary>
    public static void AttachTo(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        ScrollViewer? viewer = root as ScrollViewer ?? FindAll<ScrollViewer>(root).FirstOrDefault();

        if (viewer is not null)
        {
            Attach(viewer);
        }
    }

    private static void Attach(ScrollViewer viewer)
    {
        ScrollBar? bar = FindAll<ScrollBar>(viewer)
            .FirstOrDefault(static candidate => candidate.Orientation == Orientation.Vertical);

        if (bar is null || (bar.Tag as string) == AttachedMarker)
        {
            return;
        }

        bar.Tag = AttachedMarker;

        Thumb? thumb = null;
        bool pointerOver = false;

        // 滑块不在装载时解析：那时候滚动条自己的模板还没应用，找 Thumb 只会拿到 null。
        // 只认 Tag=vthumb 的滑块（自家模板的标志）：不是自家模板就全程 no-op，
        // 免得把默认样式的滑块也平移到右缘外。
        Thumb? GetThumb() => thumb ??= FindAll<Thumb>(bar)
            .FirstOrDefault(static candidate => (candidate.Tag as string) == OurThumbMarker);

        TranslateTransform? GetShift() => GetThumb()?.RenderTransform as TranslateTransform;

        var hideTimer = viewer.DispatcherQueue.CreateTimer();
        hideTimer.Interval = HideDelay;
        hideTimer.IsRepeating = false;
        hideTimer.Tick += (_, _) =>
        {
            if (!pointerOver)
            {
                AnimateShift(GetShift(), RetractedShift, 280);
            }
        };

        void RestartHideTimer()
        {
            hideTimer.Stop();
            hideTimer.Start();
        }

        viewer.ViewChanged += (_, _) =>
        {
            // 有别的方式在滚（滚轮/按键/光标跟随）：细条滑出指示进度，并推迟收起。
            if (!pointerOver)
            {
                AnimateShift(GetShift(), 0, 150);
            }

            RestartHideTimer();
        };

        bar.PointerEntered += (_, _) =>
        {
            pointerOver = true;
            hideTimer.Stop();

            if (GetThumb() is not { } current)
            {
                return;
            }

            // 细条在时由细变粗；没在时直接滑出粗条。
            AnimateWidth(current, ThickWidth, 167);
            AnimateShift(GetShift(), 0, 120);
        };

        bar.PointerExited += (_, _) =>
        {
            pointerOver = false;

            if (GetThumb() is { } current)
            {
                AnimateWidth(current, ThinWidth, 167);
            }

            RestartHideTimer();
        };
    }

    private static void AnimateShift(TranslateTransform? transform, double to, double milliseconds)
    {
        if (transform is null)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "X");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private static void AnimateWidth(FrameworkElement target, double to, double milliseconds)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            // 宽度是布局属性，动画必须显式允许（官方模板同款做法）。
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Width");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>深度优先枚举 <paramref name="root"/> 的可视树后代。</summary>
    internal static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindAll<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
