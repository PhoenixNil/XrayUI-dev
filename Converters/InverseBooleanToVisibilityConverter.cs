using Microsoft.UI.Xaml.Data;
using System;

namespace XrayUI.Converters
{
    public partial class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => value is not Visibility.Visible;
    }
}
