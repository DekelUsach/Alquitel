using System;
using System.ComponentModel.DataAnnotations;

namespace Alquitel.Core.Entities
{
    public class Client
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        [Required]
        public string CompanyName { get; set; } = string.Empty;
        [Required]
        public string Cuit { get; set; } = string.Empty;
        public string? ContactName { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }

        /// <summary>
        /// Soft delete flag. When true the client is hidden from UI but preserved for FK integrity.
        /// </summary>
        public bool IsArchived { get; set; } = false;

        /// <summary>
        /// Notas internas del equipo sobre el cliente (condiciones de pago, trato, etc.).
        /// Nunca se imprimen en documentos generados.
        /// </summary>
        public string? InternalNotes { get; set; }

        /// <summary>
        /// Precio especial acordado con el cliente: % de descuento que el armador aplica
        /// por defecto al seleccionarlo (editable por presupuesto). Null = sin acuerdo.
        /// </summary>
        public decimal? SpecialDiscountPercent { get; set; }
    }
}
