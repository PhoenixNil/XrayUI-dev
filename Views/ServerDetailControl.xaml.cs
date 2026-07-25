using System;
using Microsoft.UI.Xaml.Automation;
using Windows.System;
using XrayUI.Helpers;

namespace XrayUI.Views
{
    public sealed partial class ServerDetailControl
    {
        public ServerDetailViewModel ViewModel { get; set; } = null!;

        // The three AI service Borders share this handler. Without a guard, each Border's
        // Loaded fire would re-add AIShadowCastGrid to all three Shadows — three Borders ×
        // three receivers = nine entries, with duplicates causing wasted compositor work.
        private bool _shadowsWired;

        public ServerDetailControl()
        {
            this.InitializeComponent();
            ApplyLocalizedAttachedProperties();
        }

        private void ApplyLocalizedAttachedProperties()
        {
            void SetTooltipAndName(FrameworkElement element, string text)
            {
                ToolTipService.SetToolTip(element, text);
                AutomationProperties.SetName(element, text);
            }

            SetTooltipAndName(OpenAiLinkButton,    Loc.Format("ServerDetail_OpenInBrowser", "OpenAI"));
            SetTooltipAndName(ClaudeLinkButton,    Loc.Format("ServerDetail_OpenInBrowser", "Claude"));
            SetTooltipAndName(GeminiLinkButton,    Loc.Format("ServerDetail_OpenInBrowser", "Gemini"));
            ToolTipService.SetToolTip(RetestLatencyButton,  L.ServerDetail_RetestLatency);
            SetTooltipAndName(CopyShareLinkButton, L.ServerDetail_CopyShareLink);
        }

        // The group subtitle is ~19px tall, so hiding it would leave the card that much shorter and
        // strand dead space at the bottom of the pane. The gap above the AI section absorbs it:
        // 16px here restores the pre-group spacing exactly (the 12 + 2 + 12 this section used to
        // carry before it was tightened to make room for the subtitle).
        public static Thickness AiSectionMargin(Visibility groupVisibility)
            => groupVisibility == Visibility.Visible
                ? new Thickness(0)
                : new Thickness(0, 16, 0, 0);

        private void ShadowRect_Loaded(object sender, RoutedEventArgs e)
        {
            if (_shadowsWired) return;
            _shadowsWired = true;
            Shadow1.Receivers.Add(AIShadowCastGrid);
            Shadow2.Receivers.Add(AIShadowCastGrid);
            Shadow3.Receivers.Add(AIShadowCastGrid);
        }

        private async void AiLinkButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url))
                return;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return;

            await Launcher.LaunchUriAsync(uri);
        }
    }
}
