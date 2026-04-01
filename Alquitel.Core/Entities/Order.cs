using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Alquitel.Core.Entities
{
    public enum OrderStatus
    {
        Draft,
        Approved,
        SentToOF,
        SentToOT,
        Archived
    }

    public class Order
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        [Required]
        public string BudgetNumber { get; set; } = string.Empty;
        public string AdminName { get; set; } = string.Empty; // La persona que armó el presupuesto

        public Guid ClientId { get; set; }
        public Client? Client { get; set; }
        public Guid LocationId { get; set; }
        public Location? Location { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime? EventDate { get; set; }
        public OrderStatus Status { get; set; } = OrderStatus.Draft;
        public List<OrderItem> Items { get; set; } = new();
    }

    public class OrderItem
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid OrderId { get; set; }
        public Guid ProductId { get; set; }
        public Product? Product { get; set; }
        public int Quantity { get; set; } = 1;
        public decimal UnitPrice { get; set; }
        public int Dias { get; set; } = 1; // Días de aquiler
        public decimal Total => Quantity * Dias * UnitPrice;

        public string? TechnicalNotes { get; set; }

        // Campos Específicos para Pantallas LED (según plantilla de presupuesto)
        public string? PixelPitchTitle { get; set; }
        public string? Uso { get; set; }
        public string? FactorForma { get; set; }
        public string? Forma { get; set; }
        public string? PixelPitchModule { get; set; }
        
        public string? PesoPorM2 { get; set; }
        public string? ConsumoPorM2 { get; set; }
        
        public string? ResolucionPorM2X { get; set; }
        public string? ResolucionPorM2Y { get; set; }
        
        public string? Dimension1 { get; set; }
        public string? Dimension1Type { get; set; }
        public string? Dimension2 { get; set; }
        public string? Dimension2Type { get; set; }
        
        public string? CantRackEnergia { get; set; }
    }
}
