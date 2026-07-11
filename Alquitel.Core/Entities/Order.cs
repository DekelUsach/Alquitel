using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Alquitel.Core.Entities
{
    public enum OrderStatus
    {
        Draft,
        Approved,
        SentToOF,
        SentToOT,
        Archived,
        // Agregado al final para no alterar los valores numéricos ya persistidos.
        Rejected
    }

    public class Order
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        [Required]
        public string BudgetNumber { get; set; } = string.Empty;
        public string AdminName { get; set; } = string.Empty; // La persona que armó el presupuesto

        /// <summary>
        /// Usuario que creó la orden (FK a Users). Convive con AdminName (texto libre
        /// legado): las órdenes históricas solo tienen AdminName; las nuevas llevan ambos.
        /// </summary>
        public Guid? CreatedByUserId { get; set; }

        public Guid ClientId { get; set; }
        public Client? Client { get; set; }
        public Guid LocationId { get; set; }
        public Location? Location { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime? EventDate { get; set; }

        /// <summary>
        /// Fin del lapso del evento (inclusive). Null o igual a EventDate = evento de un
        /// solo día. En los documentos el rango se imprime en palabras
        /// ("del 14 de abril al 15 de mayo de 2026").
        /// </summary>
        public DateTime? EventEndDate { get; set; }
        public OrderStatus Status { get; set; } = OrderStatus.Draft;
        public List<OrderItem> Items { get; set; } = new();

        public decimal Total => Items?.Sum(i => i.Total) ?? 0m;
    }

    public class OrderItem : INotifyPropertyChanged
    {
        private int _quantity = 1;
        private decimal _unitPrice;
        private int _dias = 1;

        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid OrderId { get; set; }
        public Guid ProductId { get; set; }
        public Product? Product { get; set; }
        public int Quantity
        {
            get => _quantity;
            set
            {
                if (_quantity == value) return;
                _quantity = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Total));
            }
        }

        public decimal UnitPrice
        {
            get => _unitPrice;
            set
            {
                if (_unitPrice == value) return;
                _unitPrice = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Total));
            }
        }

        public int Dias
        {
            get => _dias;
            set
            {
                if (_dias == value) return;
                _dias = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Total));
            }
        }

        public decimal Total => Quantity * UnitPrice * Dias;

        public string? TechnicalNotes { get; set; }

        // ── Dynamic System properties ───────────────────────
        public string? ImagePath { get; set; }
        
        // Serialized List<CustomFieldDefinition>
        public string? CustomFieldsJson { get; set; } 

        /// <summary>
        /// Immutable snapshot of Product.Description captured at order-creation time.
        /// Prevents stale rendering when the source product is later edited.
        /// </summary>
        public string? DescriptionSnapshot { get; set; }
        
        /// <summary>
        /// Flag de UI (no persistido): true cuando la cantidad pedida más lo comprometido
        /// en otras órdenes activas supera el stock del producto para la fecha del evento.
        /// Lo calcula BudgetBuilderViewModel; la grilla muestra ⚠ en la fila.
        /// </summary>
        private bool _hasStockConflict;
        [NotMapped]
        public bool HasStockConflict
        {
            get => _hasStockConflict;
            set
            {
                if (_hasStockConflict == value) return;
                _hasStockConflict = value;
                OnPropertyChanged();
            }
        }

        // Medida solicitada, variable por cliente/presupuesto
        // e.g. "Medida solicitada: 8 x 3"
        private string? _requestedMeasure;
        public string? RequestedMeasure
        {
            get => _requestedMeasure;
            set
            {
                if (_requestedMeasure == value) return;
                _requestedMeasure = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
