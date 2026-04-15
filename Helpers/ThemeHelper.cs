

namespace XrayUI.Helpers
{
    public static class ThemeHelper
    {
        public static FrameworkElement? RootElement { get; set; }

        private static ElementTheme _currentTheme = ElementTheme.Default;
        public static ElementTheme CurrentTheme => _currentTheme;

        /// <summary>Actual resolved theme (Light or Dark) based on current setting.</summary>
        public static ElementTheme ActualTheme
            => RootElement?.ActualTheme ?? ElementTheme.Default;

        public static void ApplyTheme(ElementTheme theme)
        {
            _currentTheme = theme;
            if (RootElement != null)
                RootElement.RequestedTheme = theme;
        }
    }
}
