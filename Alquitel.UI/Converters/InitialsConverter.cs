using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace Alquitel.UI.Converters
{
    /// <summary>
    /// Extrae hasta dos iniciales de un nombre ("Grupo Alquitel SRL" → "GA") para
    /// avatares de listas. Ignora tokens no alfanuméricos y devuelve "?" si no hay texto.
    /// </summary>
    public sealed class InitialsConverter : IValueConverter
    {
        public static readonly InitialsConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string s || string.IsNullOrWhiteSpace(s)) return "?";
            var letters = s
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => char.IsLetterOrDigit(t[0]))
                .Select(t => char.ToUpperInvariant(t[0]))
                .Take(2)
                .ToArray();
            return letters.Length == 0 ? "?" : new string(letters);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
