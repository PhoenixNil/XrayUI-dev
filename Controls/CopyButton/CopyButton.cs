using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
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

        if (GetTemplateChild("CopyToClipboardSuccessAnimation") is Storyboard storyBoard)
        {
            storyBoard.Begin();
            AnnounceActionForAccessibility(this, CopiedMessage, "CopiedToClipboardActivityId");
        }
    }

    protected override void OnApplyTemplate()
    {
        Click -= CopyButton_Click;
        base.OnApplyTemplate();
        Click += CopyButton_Click;
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
