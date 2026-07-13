using System.Threading.Tasks;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Persistencia de órdenes del armador de presupuestos: resuelve cliente y ubicación
    /// (find-or-create) y hace insert o update transaccional de la orden con sus ítems.
    /// Extraído de BudgetBuilderViewModel — guardar en la base no es responsabilidad de un VM.
    /// </summary>
    public interface IOrderPersistenceService
    {
        /// <summary>
        /// Guarda la orden (y sus <see cref="Order.Items"/>) en la base. Devuelve false y
        /// deja registro en el log si algo falla; nunca lanza hacia la UI.
        /// </summary>
        Task<bool> PersistAsync(Order order);
    }
}
