using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;

namespace XrayUI.Converters
{
    public partial class ProtocolToBrushConverter : IValueConverter
    {
        // Tailwind 400-level palette — readable on both light & dark surfaces
        private static readonly Dictionary<string, Color> ProtocolColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ss"]          = Color.FromArgb(255,  96, 165, 250),  // blue
            ["shadowsocks"] = Color.FromArgb(255,  96, 165, 250),  // blue
            ["vless"]       = Color.FromArgb(255,  52, 211, 153),  // emerald
            ["vmess"]       = Color.FromArgb(255, 167, 139, 250),  // violet
            ["hysteria2"]   = Color.FromArgb(255, 251, 146,  60),  // amber
        };

        private static readonly Color FallbackColor = Color.FromArgb(255, 148, 163, 184); // slate

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var protocol = value?.ToString() ?? string.Empty;
            var baseColor = ProtocolColors.GetValueOrDefault(protocol, FallbackColor);

            return new SolidColorBrush(baseColor);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
