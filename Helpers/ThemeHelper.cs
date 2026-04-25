using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace XrayUI.Helpers
{
    public static class ThemeHelper
    {
        public static FrameworkElement? RootElement { get; set; }
        public static Window? MainWindow { get; set; }

        private static ElementTheme _currentTheme = ElementTheme.Default;
        public static ElementTheme CurrentTheme => _currentTheme;

        private static string _currentBackdrop = "Mica";
        public static string CurrentBackdrop => _currentBackdrop;

        /// <summary>Actual resolved theme (Light or Dark) based on current setting.</summary>
        public static ElementTheme ActualTheme
            => RootElement?.ActualTheme ?? ElementTheme.Default;

        public static void ApplyTheme(ElementTheme theme)
        {
            _currentTheme = theme;
            if (RootElement != null)
                RootElement.RequestedTheme = theme;
        }

        public static void ApplyBackdrop(string backdrop)
        {
            if (MainWindow is null) return;
            if (_currentBackdrop == backdrop && MainWindow.SystemBackdrop is not null) return;

            MainWindow.SystemBackdrop = backdrop switch
            {
                "Acrylic" => new DesktopAcrylicBackdrop(),
                _         => new MicaBackdrop(),
            };
            _currentBackdrop = backdrop;
        }
    }
}
