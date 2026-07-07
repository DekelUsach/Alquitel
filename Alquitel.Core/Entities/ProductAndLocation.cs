using System;
using System.ComponentModel.DataAnnotations;

namespace Alquitel.Core.Entities
{
    public class Product
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        [Required]
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = "General";
        public decimal BasePrice { get; set; }

        // Dynamic System properties
        public string? ImagePath { get; set; }
        
        // Serialized List<CustomFieldDefinition>
        public string? CustomFieldsJson { get; set; }

        /// <summary>
        /// Soft delete flag. When true the product is hidden from UI but preserved for FK integrity.
        /// </summary>
        public bool IsArchived { get; set; } = false;

        /// <summary>
        /// Cantidad física total disponible para alquilar. Null = sin control de stock
        /// (servicios, traslados, etc. no generan alertas de disponibilidad).
        /// </summary>
        public int? StockQuantity { get; set; }

        /// <summary>
        /// Costo interno por unidad y por día de alquiler. Null = sin costo cargado.
        /// Solo se usa en Reportes para calcular margen; nunca se imprime en documentos.
        /// </summary>
        public decimal? Cost { get; set; }
    }

    public class Location
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        [Required]
        public string Name { get; set; } = string.Empty;
    }
}
