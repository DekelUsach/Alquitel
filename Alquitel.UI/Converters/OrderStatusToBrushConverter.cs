using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Alquitel.Core.Entities;

namespace Alquitel.UI.Converters
{
    /// <summary>
    /// Color semántico por estado de presupuesto para el pool de Seguimiento:
    /// Aprobado verde, OF azul, OT violeta, Rechazado rojo, Borrador/Archivado grises.
    /// Los tonos son de saturación media para funcionar en tema claro y oscuro.
    /// </summary>
    public sealed class OrderStatusToBrushConverter : IValueConverter
    {
        private static SolidColorBrush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static readonly SolidColorBrush Draft = Frozen("#8B949E");     // gris neutro
        private static readonly SolidColorBrush Approved = Frozen("#27AE60");  // verde éxito
        private static readonly SolidColorBrush SentToOF = Frozen("#0D84E7");  // azul facturación
        private static readonly SolidColorBrush SentToOT = Frozen("#8957E5");  // violeta OT
        private static readonly SolidColorBrush Rejected = Frozen("#D46A6A");  // rojo rechazo
        private static readonly SolidColorBrush Archived = Frozen("#6B7280");  // gris apagado

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value switch
            {
                OrderStatus.Approved => Approved,
                OrderStatus.SentToOF => SentToOF,
                OrderStatus.SentToOT => SentToOT,
                OrderStatus.Rejected => Rejected,
                OrderStatus.Archived => Archived,
                _ => Draft,
            };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
