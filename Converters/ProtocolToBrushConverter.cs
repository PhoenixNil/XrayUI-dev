using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using XrayUI.Helpers;

namespace XrayUI.Converters
{
    public partial class ProtocolToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var protocol = value?.ToString() ?? string.Empty;
            return new SolidColorBrush(ProtocolColorStore.GetColor(protocol));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
