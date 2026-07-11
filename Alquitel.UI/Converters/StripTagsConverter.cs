using System;
using System.Globalization;
using System.Windows.Data;
using Alquitel.Core.Parsing;

namespace Alquitel.UI.Converters
{
    /// <summary>
    /// Quita los tags BBCode de estilo ([red], [b], etc.) para mostrar descripciones
    /// de producto como texto plano en listas y grillas.
    /// </summary>
    public class StripTagsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string s ? TagParser.StripTags(s) ?? string.Empty : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
