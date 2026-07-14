using System.Globalization;
using Alquitel.Core.Entities;
using Alquitel.Mobile.Helpers;

namespace Alquitel.Mobile.Converters;

/// <summary>true cuando el valor no es null ni string vacío.</summary>
public class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? !string.IsNullOrWhiteSpace(s) : value != null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class InvertedBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>OrderStatus → texto en español.</summary>
public class StatusDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is OrderStatus s ? s.ToDisplay() : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>OrderStatus → color semántico del estado.</summary>
public class StatusColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not OrderStatus s) return Colors.Gray;
        var key = s.ToColorKey();
        if (Application.Current?.Resources.TryGetValue(key, out var color) == true && color is Color c)
            return c;
        return Colors.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool (CUIT válido) → verde/rojo para el feedback del campo.</summary>
public class CuitFeedbackColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Color.FromArgb("#16A34A") : Color.FromArgb("#DC2626");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>ApprovalStatus → texto en español.</summary>
public class ApprovalStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ApprovalStatus.Pending => "Pendiente",
        ApprovalStatus.Approved => "Aprobado",
        ApprovalStatus.Rejected => "Rechazado",
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
