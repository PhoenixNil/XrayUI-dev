using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace XrayUI.Converters
{
    public class ColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is Color color ? new SolidColorBrush(color) : new SolidColorBrush();

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => value is SolidColorBrush brush ? brush.Color : default(Color);
    }
}
