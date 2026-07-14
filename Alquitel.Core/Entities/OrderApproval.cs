using System;
using System.ComponentModel.DataAnnotations;

namespace Alquitel.Core.Entities
{
    public enum ApprovalStatus
    {
        Pending = 0,
        Approved = 1,
        Rejected = 2,
    }

    /// <summary>
    /// Link de aprobación de un presupuesto: el cliente final abre la URL pública
    /// (Supabase Edge Function) identificada por <see cref="Token"/> y aprueba o rechaza.
    /// La Edge Function actualiza esta fila y el estado de la orden; la app de escritorio
    /// ve el cambio al releer las órdenes de la base compartida.
    /// </summary>
    public class OrderApproval
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Orden a la que aprueba/rechaza este link.</summary>
        public Guid OrderId { get; set; }

        /// <summary>Token opaco de la URL pública. Único; conocerlo es la autorización.</summary>
        public Guid Token { get; set; } = Guid.NewGuid();

        public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Momento de la respuesta del cliente (null mientras está pendiente).</summary>
        public DateTime? RespondedAt { get; set; }

        /// <summary>IP desde la que respondió el cliente (registro de auditoría).</summary>
        public string? ClientIp { get; set; }
    }
}
