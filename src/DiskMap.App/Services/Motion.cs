using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace DiskMap.Services;

/// <summary>Small composition animations in the Fluent motion style (fast in, gentle settle).</summary>
internal static class Motion
{
    private static CompositionEasingFunction Decelerate(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

    /// <summary>Fades and slides an element in when it becomes visible, and fades it out when collapsed.</summary>
    public static void EnableShowHide(UIElement element, float fromY = -8)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        var show = c.CreateAnimationGroup();
        var fadeIn = c.CreateScalarKeyFrameAnimation();
        fadeIn.Target = "Opacity";
        fadeIn.InsertKeyFrame(0, 0);
        fadeIn.InsertKeyFrame(1, 1, Decelerate(c));
        fadeIn.Duration = TimeSpan.FromMilliseconds(260);
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertKeyFrame(0, new Vector3(0, fromY, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, Decelerate(c));
        slide.Duration = TimeSpan.FromMilliseconds(320);
        show.Add(fadeIn);
        show.Add(slide);
        ElementCompositionPreview.SetImplicitShowAnimation(element, show);

        var fadeOut = c.CreateScalarKeyFrameAnimation();
        fadeOut.Target = "Opacity";
        fadeOut.InsertKeyFrame(1, 0);
        fadeOut.Duration = TimeSpan.FromMilliseconds(140);
        ElementCompositionPreview.SetImplicitHideAnimation(element, fadeOut);
    }

    /// <summary>Animates layout moves (e.g. content shifting when a banner opens above it).</summary>
    public static void EnableReposition(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        var offset = c.CreateVector3KeyFrameAnimation();
        offset.Target = "Offset";
        offset.InsertExpressionKeyFrame(1, "this.FinalValue", Decelerate(c));
        offset.Duration = TimeSpan.FromMilliseconds(320);
        var collection = c.CreateImplicitAnimationCollection();
        collection["Offset"] = offset;
        visual.ImplicitAnimations = collection;
    }

    /// <summary>Quick "content changed" cue: fade up from slightly transparent with a small slide.</summary>
    public static void Refresh(UIElement element, float fromX = 8)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0.2f);
        fade.InsertKeyFrame(1, 1, Decelerate(c));
        fade.Duration = TimeSpan.FromMilliseconds(240);
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(fromX, 0, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, Decelerate(c));
        slide.Duration = TimeSpan.FromMilliseconds(300);
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
    }
}
