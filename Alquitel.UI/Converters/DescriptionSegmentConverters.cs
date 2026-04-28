using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Alquitel.UI.Converters
{
    // Converts "#RRGGBB" string → System.Windows.Media.Color for XAML
    public class HexToColorConverter : IValueConverter
    {
        public static readonly HexToColorConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hex)
            {
                try { return (Color)ColorConverter.ConvertFromString(hex); }
                catch { }
            }
            return Colors.Black;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // bool → FontWeight (Bold / Normal)
    public class BoolToFontWeightConverter : IValueConverter
    {
        public static readonly BoolToFontWeightConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? FontWeights.Bold : FontWeights.Normal;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // bool → FontStyle (Italic / Normal)
    public class BoolToFontStyleConverter : IValueConverter
    {
        public static readonly BoolToFontStyleConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? FontStyles.Italic : FontStyles.Normal;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
