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

        // ── LED-specific default fields ──────────────────────────
        // These serve as defaults that get copied into OrderItem when a product is added.
        // From template: "Pantalla de Leds 2 mm - Para interior – FLEX – Vertical"
        public string? PixelPitchTitle { get; set; }      // e.g. "Pantalla de Leds 2 mm"
        public string? DefaultUso { get; set; }            // "IN" / "OUT" → displayed as "Para interior" / "Para exterior"
        public string? DefaultForma { get; set; }          // "PLANA" / "FLEX"
        public string? DefaultFactorForma { get; set; }    // "Vertical" / "Horizontal" / "MÓDULO"

        // "Píxeles de 2.6 mm"
        public string? DefaultPixelPitchModule { get; set; }

        // "Escalador/ controlador de leds" + "Módulos de 500 mm x 500 mm x 100 mm"
        public string? Accessories { get; set; }           // Free text for extras like "Escalador/controlador de leds"
        public string? ModuleDimensions { get; set; }      // e.g. "500 mm x 500 mm x 100 mm"

        // "Peso aprox: 44 kg x m2"
        public string? DefaultPesoPorM2 { get; set; }

        // "Consumo aprox: 4 Amperes x m2"
        public string? DefaultConsumoPorM2 { get; set; }

        // "Resolución: 384 x 384 píxeles x m2"
        public string? DefaultResolucionX { get; set; }
        public string? DefaultResolucionY { get; set; }

        // "Incluye estructura para montaje de piso tipo layher"
        public string? IncludesNote { get; set; }
    }

    public class Location
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        [Required]
        public string Name { get; set; } = string.Empty;
    }
}
