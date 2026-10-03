using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace XrayUI.Helpers
{
    /// <summary>
    /// Desktop acrylic that keeps its blur while the window is inactive and takes its light/dark
    /// from the window's own content. The stock DesktopAcrylicBackdrop does neither: it drops to a
    /// flat fallback color the moment its window loses activation — which is exactly how the tray
    /// panel's slide-out begins — and it can disagree with an app theme forced against the system's.
    /// </summary>
    internal sealed partial class ActiveAcrylicBackdrop : SystemBackdrop
    {
        private DesktopAcrylicController? _controller;
        private SystemBackdropConfiguration? _configuration;

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            if (!DesktopAcrylicController.IsSupported()) return;

            _configuration = new SystemBackdropConfiguration
            {
                IsInputActive  = true,
                IsHighContrast = new AccessibilitySettings().HighContrast,
                Theme          = ToBackdropTheme((xamlRoot.Content as FrameworkElement)?.ActualTheme),
            };
            // Base rather than Default: the more opaque kind, so text stays legible over whatever
            // happens to be behind the panel.
            _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
            _controller.SetSystemBackdropConfiguration(_configuration);
            _controller.AddSystemBackdropTarget(connectedTarget);
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            base.OnTargetDisconnected(disconnectedTarget);
            if (_controller is null) return;

            _controller.RemoveSystemBackdropTarget(disconnectedTarget);
            _controller.Dispose();
            _controller = null;
            _configuration = null;
        }

        // The base implementation would install the default configuration, bringing back the
        // activation tracking this class exists to drop; only the theme is meant to follow.
        protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
            => SyncTheme((xamlRoot.Content as FrameworkElement)?.ActualTheme);

        /// <summary>Re-reads the theme for a window whose content theme changed while hidden.</summary>
        public void SyncTheme(ElementTheme? actualTheme)
        {
            if (_configuration is not null)
                _configuration.Theme = ToBackdropTheme(actualTheme);
        }

        private static SystemBackdropTheme ToBackdropTheme(ElementTheme? theme) => theme switch
        {
            ElementTheme.Dark  => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _                  => SystemBackdropTheme.Default,
        };
    }
}
