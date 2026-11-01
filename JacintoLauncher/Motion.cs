#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace JacintoLauncher;

/// <summary>Page entrances. They run once on a change and then stop, so an idle launcher draws nothing.</summary>
internal static class Motion
{
    private static CancellationTokenSource? running;

    private static Animation Build(int milliseconds, params (double Cue, (AvaloniaProperty Property, double Value)[] Values)[] frames)
    {
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(milliseconds), Easing = new CubicEaseOut() };
        foreach (var (cue, values) in frames)
        {
            var frame = new KeyFrame { Cue = new Cue(cue) };
            foreach (var (property, value) in values) frame.Setters.Add(new Setter(property, value));
            animation.Children.Add(frame);
        }
        return animation;
    }

    private static readonly Animation Rise = Build(190,
        (0, new (AvaloniaProperty, double)[] { (Visual.OpacityProperty, 0), (TranslateTransform.YProperty, 10) }),
        (1, new (AvaloniaProperty, double)[] { (Visual.OpacityProperty, 1), (TranslateTransform.YProperty, 0) }));

    private static readonly Animation Ease = Build(600,
        (0, new (AvaloniaProperty, double)[] { (ScaleTransform.ScaleXProperty, 1.03), (ScaleTransform.ScaleYProperty, 1.03) }),
        (1, new (AvaloniaProperty, double)[] { (ScaleTransform.ScaleXProperty, 1), (ScaleTransform.ScaleYProperty, 1) }));

    /// <summary>The new page fades in and rises a little. A quick next change cancels the one running.</summary>
    internal static void Enter(Control control)
    {
        running?.Cancel();
        running = new CancellationTokenSource();
        _ = Rise.RunAsync(control, running.Token);
    }

    private static readonly HashSet<string> Opened = new();

    private static Animation Arrive(int delay)
    {
        var animation = Build(420,
            (0, new (AvaloniaProperty, double)[] { (Visual.OpacityProperty, 0), (TranslateTransform.YProperty, 28), (ScaleTransform.ScaleXProperty, .96), (ScaleTransform.ScaleYProperty, .96) }),
            (1, new (AvaloniaProperty, double)[] { (Visual.OpacityProperty, 1), (TranslateTransform.YProperty, 0), (ScaleTransform.ScaleXProperty, 1), (ScaleTransform.ScaleYProperty, 1) }));
        animation.Easing = new QuinticEaseOut();
        animation.Delay = TimeSpan.FromMilliseconds(delay);
        animation.FillMode = FillMode.Backward; // Hidden until its turn comes.
        return animation;
    }

    /// <summary>The first visit in a run deals a page's boxes in one after another; later visits only fade, so moving around stays quick.</summary>
    internal static void Open(Control host, string page)
    {
        if (!Opened.Add(page)) { Enter(host); return; }
        running?.Cancel();
        running = new CancellationTokenSource();
        var token = running.Token;
        host.Opacity = 0;
        // The boxes exist only after layout; until then the page stays hidden, so nothing flashes.
        void Start(object? sender, EventArgs e)
        {
            host.LayoutUpdated -= Start;
            host.Opacity = 1;
            if (token.IsCancellationRequested) return;
            var turn = 0;
            foreach (var box in host.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && (c is Expander || c.Classes.Contains("card") || c.Classes.Contains("enter"))))
                _ = Arrive(Math.Min(turn++, 9) * 65).RunAsync(box, token);
        }
        host.LayoutUpdated += Start;
        host.InvalidateMeasure();
    }

    /// <summary>The Home banner settles into place.</summary>
    internal static void Settle(Control control) => _ = Ease.RunAsync(control);
}
