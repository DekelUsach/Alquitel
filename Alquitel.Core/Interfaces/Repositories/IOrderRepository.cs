using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces.Repositories
{
    /// <summary>
    /// Abstracción de acceso a datos de órdenes/presupuestos.
    /// Ver <see cref="IClientRepository"/> para la motivación (migración a backend remoto).
    /// </summary>
    public interface IOrderRepository
    {
        /// <summary>Incluye Client, Location e Items. Ignora filtros de archivado para historial exacto.</summary>
        Task<List<Order>> GetRecentAsync(int count);
        Task<Order?> GetByIdAsync(Guid id);
        Task<Order?> GetByBudgetNumberAsync(string budgetNumber);
        // Nota: el guardado de órdenes pasa EXCLUSIVAMENTE por IOrderPersistenceService
        // (resolución de cliente/ubicación, transacción, renumeración y concurrencia).
        // El UpsertAsync que vivía acá era un segundo camino de guardado sin esas
        // garantías y sin callers; se eliminó para que no vuelva a usarse por error.

        /// <summary>
        /// Todos los números de presupuesto existentes (incluye archivados). Base para
        /// la numeración en serie y el cálculo de versiones ("31294", "31294/2", ...).
        /// </summary>
        Task<List<string>> GetAllBudgetNumbersAsync();

        /// <summary>
        /// Resumen de actividad de un usuario: presupuestos creados, monto acumulado y
        /// último presupuesto. Matchea por CreatedByUserId y, para órdenes legadas sin
        /// FK, por AdminName.
        /// </summary>
        Task<UserOrderStats> GetUserStatsAsync(Guid userId, string userName);

        /// <summary>
        /// Cantidad total de un producto ya comprometida en órdenes activas
        /// (Approved/SentToOF/SentToOT) cuyo rango de alquiler
        /// [EventDate, EventDate + Dias) se solapa con [from, to).
        /// Excluye <paramref name="excludeOrderId"/> para no contar la orden en edición.
        /// </summary>
        Task<int> GetCommittedQuantityAsync(Guid productId, DateTime from, DateTime to, Guid excludeOrderId);

        /// <summary>
        /// Detalle de compromisos de un producto en órdenes activas cuyo rango se solapa
        /// con [from, to): qué órdenes lo usan, cuántas unidades y en qué fechas. Base del
        /// calendario de disponibilidad de stock.
        /// </summary>
        Task<List<ProductCommitment>> GetCommitmentsAsync(Guid productId, DateTime from, DateTime to);
    }

    /// <summary>Compromiso de stock de un producto en una orden activa: [Start, End) exclusivo.</summary>
    public record ProductCommitment(
        Guid OrderId,
        string BudgetNumber,
        string? ClientName,
        DateTime Start,
        DateTime End,
        int Quantity);

    /// <summary>Resumen de actividad de un usuario sobre las órdenes del sistema.</summary>
    public record UserOrderStats(
        int OrdersCount,
        decimal TotalAmount,
        DateTime? LastOrderDate,
        string? LastBudgetNumber);
}
