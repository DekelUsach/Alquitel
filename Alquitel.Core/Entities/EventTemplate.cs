using System;
using System.ComponentModel.DataAnnotations;

namespace Alquitel.Core.Entities
{
    /// <summary>
    /// Combo de evento: un carrito predefinido con nombre ("Casamiento clásico",
    /// "Stand de feria") que el armador puede cargar en un clic. Los ítems se guardan
    /// como JSON (lista de <see cref="EventTemplateItem"/>): son una receta, no una
    /// orden — los precios se resuelven contra el catálogo vigente al cargarlos.
    /// </summary>
    public class EventTemplate
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public string Name { get; set; } = string.Empty;

        /// <summary>JSON serializado de List&lt;EventTemplateItem&gt;.</summary>
        public string ItemsJson { get; set; } = "[]";

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        /// <summary>Quién guardó el combo (texto libre, informativo).</summary>
        public string? CreatedByName { get; set; }
    }

    /// <summary>Renglón de un combo: producto, cantidad y días sugeridos.</summary>
    public class EventTemplateItem
    {
        public Guid ProductId { get; set; }
        public int Quantity { get; set; } = 1;
        public int Dias { get; set; } = 1;
    }
}
