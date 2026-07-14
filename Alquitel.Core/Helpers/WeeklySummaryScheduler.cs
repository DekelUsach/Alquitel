using System;

namespace Alquitel.Core.Helpers
{
    /// <summary>
    /// Lógica de calendario del resumen semanal ("el papelito del lunes"): decide si
    /// corresponde generar el resumen de la semana anterior en este arranque de la app.
    /// Sin schedulers externos: se evalúa al iniciar sesión.
    /// </summary>
    public static class WeeklySummaryScheduler
    {
        /// <summary>Lunes de la semana a la que pertenece <paramref name="date"/>.</summary>
        public static DateTime WeekStart(DateTime date)
        {
            // DayOfWeek: Sunday=0 … Saturday=6. Semana laboral argentina: arranca lunes.
            int daysFromMonday = ((int)date.DayOfWeek + 6) % 7;
            return date.Date.AddDays(-daysFromMonday);
        }

        /// <summary>
        /// True si el resumen de la semana pasada todavía no se generó en esta semana.
        /// <paramref name="lastGenerated"/> es la fecha del último resumen generado
        /// (null = nunca).
        /// </summary>
        public static bool ShouldGenerate(DateTime? lastGenerated, DateTime today)
        {
            if (lastGenerated == null) return true;
            return WeekStart(lastGenerated.Value) < WeekStart(today);
        }

        /// <summary>Rango [lunes, lunes siguiente) de la semana ANTERIOR a <paramref name="today"/>.</summary>
        public static (DateTime Start, DateTime EndExclusive) PreviousWeekRange(DateTime today)
        {
            var thisMonday = WeekStart(today);
            return (thisMonday.AddDays(-7), thisMonday);
        }
    }
}
