using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;
using XrayUI.Helpers;

namespace XrayUI.Converters
{
    public partial class ProtocolToBrushConverter : IValueConverter
    {
        private static readonly Dictionary<Color, SolidColorBrush> _brushCache = new();

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var protocol = value?.ToString() ?? string.Empty;
            var color = ProtocolColorStore.GetColor(protocol);
            if (!_brushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                _brushCache[color] = brush;
            }
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
