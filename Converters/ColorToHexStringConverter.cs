using System;
using Microsoft.UI.Xaml.Data;
using Windows.UI;

namespace XrayUI.Converters
{
    public partial class ColorToHexStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is Color c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "#000000";

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
