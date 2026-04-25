using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;

namespace XrayUI.Controls;

public sealed partial class CopyButton : Button
{
    public static readonly DependencyProperty CopiedMessageProperty =
        DependencyProperty.Register(
            nameof(CopiedMessage),
            typeof(string),
            typeof(CopyButton),
            new PropertyMetadata("已复制到剪贴板"));

    public static readonly DependencyProperty TextToCopyProperty =
        DependencyProperty.Register(
            nameof(TextToCopy),
            typeof(string),
            typeof(CopyButton),
            new PropertyMetadata(string.Empty));

    private FrameworkElement? _contentPresenter;
    private FrameworkElement? _copySuccessGlyph;
    private Visual? _contentVisual;
    private Visual? _glyphVisual;

    public CopyButton()
    {
        DefaultStyleKey = typeof(CopyButton);
    }

    public string CopiedMessage
    {
        get => (string)GetValue(CopiedMessageProperty);
        set => SetValue(CopiedMessageProperty, value);
    }

    public string TextToCopy
    {
        get => (string)GetValue(TextToCopyProperty);
        set => SetValue(TextToCopyProperty, value);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TextToCopy))
        {
            try
            {
                var package = new DataPackage();
                package.SetText(TextToCopy);
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }
            catch (Exception)
            {
                return;
            }
        }

        PlaySuccessAnimation();
        AnnounceActionForAccessibility(this, CopiedMessage, "CopiedToClipboardActivityId");
    }

    protected override void OnApplyTemplate()
    {
        Click -= CopyButton_Click;

        if (_contentPresenter is not null)
        {
            _contentPresenter.SizeChanged -= ContentPresenter_SizeChanged;
        }
        if (_copySuccessGlyph is not null)
        {
            _copySuccessGlyph.SizeChanged -= GlyphPresenter_SizeChanged;
        }

        base.OnApplyTemplate();

        _contentPresenter = GetTemplateChild("ContentPresenter") as FrameworkElement;
        _copySuccessGlyph = GetTemplateChild("CopySuccessGlyph") as FrameworkElement;

        if (_contentPresenter is not null)
        {
            _contentVisual = ElementCompositionPreview.GetElementVisual(_contentPresenter);
            UpdateCenterPoint(_contentPresenter, _contentVisual);
            _contentPresenter.SizeChanged += ContentPresenter_SizeChanged;
        }
        if (_copySuccessGlyph is not null)
        {
            _glyphVisual = ElementCompositionPreview.GetElementVisual(_copySuccessGlyph);
            _glyphVisual.Opacity = 0f;
            UpdateCenterPoint(_copySuccessGlyph, _glyphVisual);
            _copySuccessGlyph.SizeChanged += GlyphPresenter_SizeChanged;
        }

        Click += CopyButton_Click;
    }

    private void ContentPresenter_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_contentPresenter is not null && _contentVisual is not null)
        {
            UpdateCenterPoint(_contentPresenter, _contentVisual);
        }
    }

    private void GlyphPresenter_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_copySuccessGlyph is not null && _glyphVisual is not null)
        {
            UpdateCenterPoint(_copySuccessGlyph, _glyphVisual);
        }
    }

    private static void UpdateCenterPoint(FrameworkElement element, Visual visual)
    {
        visual.CenterPoint = new Vector3(
            (float)(element.ActualWidth / 2),
            (float)(element.ActualHeight / 2),
            0f);
    }

    private void PlaySuccessAnimation()
    {
        if (_contentVisual is null || _glyphVisual is null)
        {
            return;
        }

        var compositor = _contentVisual.Compositor;

        // Reusable easing functions (cubic bezier for spline keyframes, step for hold semantics)
        var spline033_067 = compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 0.0f), new Vector2(0.67f, 1.0f));
        var spline100_100 = compositor.CreateCubicBezierEasingFunction(new Vector2(1.0f, 0.0f), new Vector2(1.0f, 1.0f));
        var spline100_098 = compositor.CreateCubicBezierEasingFunction(new Vector2(1.0f, 0.0f), new Vector2(0.98f, 1.0f));
        var spline013_000 = compositor.CreateCubicBezierEasingFunction(new Vector2(0.13f, 0.0f), new Vector2(0.0f, 1.0f));
        var spline039_063 = compositor.CreateCubicBezierEasingFunction(new Vector2(0.39f, 0.0f), new Vector2(0.63f, 1.0f));
        var spline055_002 = compositor.CreateCubicBezierEasingFunction(new Vector2(0.55f, 0.0f), new Vector2(0.02f, 1.0f));
        var linear = compositor.CreateLinearEasingFunction();
        var step = compositor.CreateStepEasingFunction(); // 1 step => hold previous value, jump at end

        // ContentPresenter Opacity (1.433s): 1.0 -> 0.0 (133ms, spline) -> hold -> 1.0 (133ms, spline)
        var contentOpacity = compositor.CreateScalarKeyFrameAnimation();
        contentOpacity.Duration = TimeSpan.FromMilliseconds(1433);
        contentOpacity.InsertKeyFrame(0f, 1.0f);
        contentOpacity.InsertKeyFrame(133f / 1433f, 0.0f, spline033_067);
        contentOpacity.InsertKeyFrame(1300f / 1433f, 0.0f, step);
        contentOpacity.InsertKeyFrame(1.0f, 1.0f, spline100_100);
        _contentVisual.StartAnimation("Opacity", contentOpacity);

        // ContentPresenter Scale (1.2s): 1.0 -> 0.273 (133ms, spline) -> hold -> 1.0 (33ms, linear)
        var contentScale = compositor.CreateVector3KeyFrameAnimation();
        contentScale.Duration = TimeSpan.FromMilliseconds(1200);
        contentScale.InsertKeyFrame(0f, new Vector3(1.0f, 1.0f, 1.0f));
        contentScale.InsertKeyFrame(133f / 1200f, new Vector3(0.273f, 0.273f, 1.0f), spline013_000);
        contentScale.InsertKeyFrame(1167f / 1200f, new Vector3(0.273f, 0.273f, 1.0f), step);
        contentScale.InsertKeyFrame(1.0f, new Vector3(1.0f, 1.0f, 1.0f), linear);
        _contentVisual.StartAnimation("Scale", contentScale);

        // CopySuccessGlyph Opacity (1.3s): hold 0 (133ms) -> 1.0 (134ms, spline) -> hold -> 0.0 (133ms, spline)
        var glyphOpacity = compositor.CreateScalarKeyFrameAnimation();
        glyphOpacity.Duration = TimeSpan.FromMilliseconds(1300);
        glyphOpacity.InsertKeyFrame(0f, 0.0f);
        glyphOpacity.InsertKeyFrame(133f / 1300f, 0.0f, step);
        glyphOpacity.InsertKeyFrame(267f / 1300f, 1.0f, spline033_067);
        glyphOpacity.InsertKeyFrame(1167f / 1300f, 1.0f, step);
        glyphOpacity.InsertKeyFrame(1.0f, 0.0f, spline100_098);
        _glyphVisual.StartAnimation("Opacity", glyphOpacity);

        // CopySuccessGlyph Scale (0.333s): jump to 0.385 at 133ms -> 1.146 (134ms, spline) -> 1.0 (66ms, spline)
        var glyphScale = compositor.CreateVector3KeyFrameAnimation();
        glyphScale.Duration = TimeSpan.FromMilliseconds(333);
        glyphScale.InsertKeyFrame(0f, new Vector3(1.0f, 1.0f, 1.0f));
        glyphScale.InsertKeyFrame(133f / 333f, new Vector3(0.385f, 0.385f, 1.0f), step);
        glyphScale.InsertKeyFrame(267f / 333f, new Vector3(1.146f, 1.146f, 1.0f), spline039_063);
        glyphScale.InsertKeyFrame(1.0f, new Vector3(1.0f, 1.0f, 1.0f), spline055_002);
        _glyphVisual.StartAnimation("Scale", glyphScale);
    }

    private static void AnnounceActionForAccessibility(UIElement element, string announcement, string activityId)
    {
        if (FrameworkElementAutomationPeer.FromElement(element) is AutomationPeer peer)
        {
            peer.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.ImportantMostRecent,
                announcement,
                activityId);
        }
    }
}
