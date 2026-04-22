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
    }

    public class Location
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        [Required]
        public string Name { get; set; } = string.Empty;
    }
}
