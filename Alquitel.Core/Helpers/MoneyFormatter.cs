using System.Globalization;

namespace Alquitel.Core.Helpers
{
    /// <summary>
    /// Formato monetario único para todos los documentos y bitácoras. Los presupuestos
    /// son argentinos: usar la cultura del hilo (que depende del Windows de cada puesto)
    /// hacía que el mismo documento saliera con "$", "US$" o separadores distintos según
    /// la máquina que lo generaba.
    /// </summary>
    public static class MoneyFormatter
    {
        /// <summary>Cultura es-AR compartida por los motores COM, OpenXML y los logs.</summary>
        public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-AR");

        /// <summary>Moneda completa: 1234.5 → "$ 1.234,50".</summary>
        public static string Currency(decimal value) => value.ToString("C", Culture);

        /// <summary>Entero con separador de miles (tabla de costos): 1234567 → "1.234.567".</summary>
        public static string WholeNumber(decimal value) => value.ToString("N0", Culture);
    }
}
