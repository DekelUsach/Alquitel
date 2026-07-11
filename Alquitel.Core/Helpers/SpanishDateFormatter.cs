using System;
using System.Globalization;

namespace Alquitel.Core.Helpers
{
    /// <summary>
    /// Formatea fechas de evento en palabras para los documentos generados:
    /// "14 de abril de 2026" para un solo día y "del 14 de abril al 15 de mayo de 2026"
    /// para un lapso. Meses siempre en minúsculas, cultura es-AR.
    /// </summary>
    public static class SpanishDateFormatter
    {
        private static readonly CultureInfo EsAr = CultureInfo.GetCultureInfo("es-AR");

        private static string MonthName(DateTime date) =>
            EsAr.DateTimeFormat.GetMonthName(date.Month).ToLower(EsAr);

        /// <summary>"14 de abril de 2026"</summary>
        public static string ToWords(DateTime date) =>
            $"{date.Day} de {MonthName(date)} de {date.Year}";

        /// <summary>
        /// Lapso en palabras. Sin fin (o fin igual al inicio) devuelve el día suelto.
        /// Mismo mes: "del 14 al 20 de abril de 2026".
        /// Meses distintos: "del 14 de abril al 15 de mayo de 2026".
        /// Años distintos: "del 30 de diciembre de 2026 al 2 de enero de 2027".
        /// </summary>
        public static string ToWordsRange(DateTime start, DateTime? end)
        {
            if (!end.HasValue || end.Value.Date <= start.Date)
                return ToWords(start);

            var e = end.Value;

            if (start.Year != e.Year)
                return $"del {start.Day} de {MonthName(start)} de {start.Year} al {e.Day} de {MonthName(e)} de {e.Year}";

            if (start.Month != e.Month)
                return $"del {start.Day} de {MonthName(start)} al {e.Day} de {MonthName(e)} de {e.Year}";

            return $"del {start.Day} al {e.Day} de {MonthName(start)} de {e.Year}";
        }
    }
}
