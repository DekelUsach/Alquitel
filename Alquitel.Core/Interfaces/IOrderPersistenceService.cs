using System.Threading.Tasks;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces
{
    /// <summary>Resultado de un intento de guardado de orden.</summary>
    public enum OrderPersistResult
    {
        /// <summary>Guardada correctamente (el BudgetNumber pudo haber sido renumerado).</summary>
        Saved,
        /// <summary>Otro usuario modificó la orden desde que se cargó (conflicto de concurrencia).</summary>
        Conflict,
        /// <summary>Falló por otro motivo; queda registro en el log.</summary>
        Error,
    }

    /// <summary>
    /// Persistencia de órdenes del armador de presupuestos: resuelve cliente y ubicación
    /// (find-or-create) y hace insert o update transaccional de la orden con sus ítems.
    /// Extraído de BudgetBuilderViewModel — guardar en la base no es responsabilidad de un VM.
    /// </summary>
    public interface IOrderPersistenceService
    {
        /// <summary>
        /// Guarda la orden (y sus <see cref="Order.Items"/>) en la base. Ante una colisión
        /// de número de presupuesto (dos usuarios simultáneos) renumera y reintenta,
        /// mutando <see cref="Order.BudgetNumber"/>. Ante edición concurrente devuelve
        /// <see cref="OrderPersistResult.Conflict"/> salvo que <paramref name="forceOverwrite"/>
        /// sea true. Nunca lanza hacia la UI.
        /// </summary>
        Task<OrderPersistResult> PersistAsync(Order order, bool forceOverwrite = false);
    }
}
