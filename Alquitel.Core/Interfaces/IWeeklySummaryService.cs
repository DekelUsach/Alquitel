using System;
using System.Threading.Tasks;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Generador del resumen semanal ("el papelito del lunes"): un .docx simple con las
    /// métricas de la semana anterior (presupuestos emitidos/aprobados, monto, top
    /// productos, eventos próximos). Se genera con OpenXML puro — no requiere Word.
    /// </summary>
    public interface IWeeklySummaryService
    {
        /// <summary>
        /// Genera el resumen de la semana [<paramref name="weekStart"/>,
        /// <paramref name="weekEndExclusive"/>) y devuelve la ruta del .docx creado.
        /// </summary>
        Task<string> GenerateAsync(DateTime weekStart, DateTime weekEndExclusive);
    }
}
