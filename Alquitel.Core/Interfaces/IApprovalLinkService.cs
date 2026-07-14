using System;
using System.Threading.Tasks;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Generación de links públicos de aprobación de presupuestos (§4 de
    /// PENDING_FEATURES). El link apunta a la Edge Function "aprobar" del proyecto
    /// Supabase; el cliente final aprueba o rechaza desde el navegador sin llamar.
    /// </summary>
    public interface IApprovalLinkService
    {
        /// <summary>True cuando hay URL de Supabase configurada (sin ella no hay portal).</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Crea (o reutiliza si hay uno pendiente) el link de aprobación para la orden.
        /// La orden debe estar persistida. Devuelve la URL pública, o null si falla.
        /// </summary>
        Task<string?> CreateApprovalLinkAsync(Guid orderId);

        /// <summary>Última aprobación registrada para la orden (o null si nunca se generó link).</summary>
        Task<OrderApproval?> GetLatestForOrderAsync(Guid orderId);
    }
}
