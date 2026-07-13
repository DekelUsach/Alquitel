using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows;
using Alquitel.Core.Entities;
using Alquitel.UI.ViewModels;

namespace Alquitel.UI.Converters
{
    /// <summary>Texto del badge de cantidad en la fila del catálogo ("×2"). Vacío si no está en el pedido.</summary>
    public class ProductQuantityBadgeTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not Product product || values[1] is not BudgetBuilderViewModel vm)
                return string.Empty;

            int quantity = vm.GetSelectedQuantity(product.Id);
            return quantity > 0 ? $"×{quantity}" : string.Empty;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Visible cuando el producto ya forma parte del pedido actual.</summary>
    public class ProductInCartVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not Product product || values[1] is not BudgetBuilderViewModel vm)
                return Visibility.Collapsed;

            return vm.GetSelectedQuantity(product.Id) > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
