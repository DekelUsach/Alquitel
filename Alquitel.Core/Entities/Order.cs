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
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public string? TechnicalNotes { get; set; }
    }
}
