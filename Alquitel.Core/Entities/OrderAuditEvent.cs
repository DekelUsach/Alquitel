using System;
using System.ComponentModel.DataAnnotations;

namespace Alquitel.Core.Entities
{
    /// <summary>
    /// Evento de la bitácora de un presupuesto: quién hizo qué y cuándo. Sin FK dura a
    /// Orders a propósito: si una orden se elimina, su historial sigue disponible.
    /// </summary>
    public class OrderAuditEvent
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid OrderId { get; set; }

        /// <summary>Usuario que ejecutó la acción (nombre al momento del evento).</summary>
        public string UserName { get; set; } = string.Empty;

        public Guid? UserId { get; set; }

        /// <summary>"Creado", "Editado", "Documento generado (OT)", "Estado: X → Y", …</summary>
        public string EventType { get; set; } = string.Empty;

        public string? Detail { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
