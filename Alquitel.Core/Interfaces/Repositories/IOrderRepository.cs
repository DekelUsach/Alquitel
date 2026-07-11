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
        Task UpsertAsync(Order order);

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
    }

    /// <summary>Resumen de actividad de un usuario sobre las órdenes del sistema.</summary>
    public record UserOrderStats(
        int OrdersCount,
        decimal TotalAmount,
        DateTime? LastOrderDate,
        string? LastBudgetNumber);
}
