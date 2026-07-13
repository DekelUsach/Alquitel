using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Bitácora multi-usuario de presupuestos. Registra eventos (generó, editó, cambió
    /// estado…) firmados por el usuario logueado y los expone para la ficha de la orden.
    /// Nunca lanza hacia el llamador: un fallo de auditoría no debe romper la operación.
    /// </summary>
    public interface IOrderAuditService
    {
        Task LogAsync(Guid orderId, string eventType, string? detail = null);
        Task<List<OrderAuditEvent>> GetForOrderAsync(Guid orderId);
    }
}
