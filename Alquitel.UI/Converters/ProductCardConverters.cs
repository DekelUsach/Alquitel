using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows;
using Alquitel.Core.Entities;
using Alquitel.UI.ViewModels;

namespace Alquitel.UI.Converters
{
    public class ProductButtonTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not Product product || values[1] is not MainViewModel vm)
            {
                return "+ Agregar al Pedido";
            }

            int quantity = vm.GetSelectedQuantity(product.Id);
            return quantity > 0 ? $"En pedido ({quantity})" : "+ Agregar al Pedido";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public class ProductButtonBackgroundConverter : IMultiValueConverter
    {
        private static readonly Brush DefaultBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B2E58"));
        private static readonly Brush AddedBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5A9EEA"));

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not Product product || values[1] is not MainViewModel vm)
            {
                return DefaultBrush;
            }

            int quantity = vm.GetSelectedQuantity(product.Id);
            return quantity > 0 ? AddedBrush : DefaultBrush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public class ProductRemoveButtonVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 3 || values[0] is not Product product || values[1] is not MainViewModel vm || values[2] is not bool isMouseOver)
            {
                return Visibility.Collapsed;
            }

            int quantity = vm.GetSelectedQuantity(product.Id);
            return isMouseOver && quantity > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
