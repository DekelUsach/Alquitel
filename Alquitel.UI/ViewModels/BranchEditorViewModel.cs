using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Core.Parsing;

namespace Alquitel.UI.ViewModels
{
    /// <summary>Opción del combo de catálogo: producto con la descripción limpia de tags.</summary>
    public sealed record ProductOption(Product Product, string CleanDescription);

    /// <summary>
    /// Fila editable del editor de versiones: permite ajustar cantidad, días y precio
    /// de cada ítem, o excluirlo de la nueva versión, antes de crear la rama.
    /// </summary>
    public partial class BranchItemRow : ObservableObject
    {
        private readonly Action _onChanged;

        [ObservableProperty]
        private bool _include = true;

        [ObservableProperty]
        private int _quantity;

        [ObservableProperty]
        private int _dias;

        [ObservableProperty]
        private decimal _unitPrice;

        public OrderItem Source { get; }

        /// <summary>Descripción legible (sin tags BBCode de color/estilo).</summary>
        public string Description { get; }

        /// <summary>True cuando la fila se agregó desde el catálogo en este editor.</summary>
        public bool IsNew { get; }

        public string? RequestedMeasure => Source.RequestedMeasure;

        public decimal Subtotal => Include ? Quantity * UnitPrice * Dias : 0m;

        public BranchItemRow(OrderItem source, Action onChanged, bool isNew = false)
        {
            Source = source;
            _onChanged = onChanged;
            IsNew = isNew;
            _quantity = source.Quantity;
            _dias = source.Dias;
            _unitPrice = source.UnitPrice;
            Description = TagParser.StripTags(source.DescriptionSnapshot ?? source.Product?.Description)
                          ?? "Producto";
        }

        partial void OnIncludeChanged(bool value) => Recalc();
        partial void OnQuantityChanged(int value) => Recalc();
        partial void OnDiasChanged(int value) => Recalc();
        partial void OnUnitPriceChanged(decimal value) => Recalc();

        private void Recalc()
        {
            OnPropertyChanged(nameof(Subtotal));
            _onChanged();
        }
    }

    /// <summary>
    /// ViewModel del editor de ramificación (BranchEditorWindow): a partir de un
    /// presupuesto existente arma la versión siguiente ("31294" → "31294/2") con
    /// ajustes rápidos de items — editar cantidades/días/precios, excluir filas,
    /// quitar filas y agregar productos del catálogo. Al confirmar,
    /// <see cref="BuildBranchOrder"/> devuelve la orden nueva lista para cargarse
    /// en el armador de presupuestos.
    /// </summary>
    public partial class BranchEditorViewModel : ObservableObject
    {
        private readonly Order _sourceOrder;

        public string OriginalNumber => _sourceOrder.BudgetNumber;

        public string NewNumber { get; }

        public string ClientName => _sourceOrder.Client?.CompanyName ?? "—";

        public string LocationName => _sourceOrder.Location?.Name ?? "—";

        [ObservableProperty]
        private DateTime? _eventDate;

        public ObservableCollection<BranchItemRow> Items { get; } = new();

        // ── Alta de productos desde el catálogo ──────────────────────
        public ObservableCollection<ProductOption> Catalog { get; } = new();

        [ObservableProperty]
        private ProductOption? _selectedProductToAdd;

        public decimal Total => Items.Sum(i => i.Subtotal);

        public int IncludedCount => Items.Count(i => i.Include);

        public BranchEditorViewModel(Order sourceOrder, string newNumber, IEnumerable<Product>? catalog = null)
        {
            _sourceOrder = sourceOrder;
            NewNumber = newNumber;
            _eventDate = sourceOrder.EventDate;

            foreach (var item in sourceOrder.Items)
                Items.Add(new BranchItemRow(item, NotifyTotals));

            if (catalog != null)
            {
                foreach (var p in catalog.OrderBy(p => TagParser.StripTags(p.Description)))
                    Catalog.Add(new ProductOption(p, TagParser.StripTags(p.Description) ?? "Producto"));
            }
        }

        [RelayCommand]
        private void AddProduct()
        {
            if (SelectedProductToAdd == null) return;
            var p = SelectedProductToAdd.Product;

            // Mismo congelamiento que el armador: precio, imagen, campos dinámicos y
            // descripción quedan snapshoteados al momento de agregar.
            var item = new OrderItem
            {
                ProductId = p.Id,
                Product = p,
                Quantity = 1,
                Dias = Items.FirstOrDefault()?.Dias ?? 1,
                UnitPrice = p.BasePrice,
                ImagePath = p.ImagePath,
                CustomFieldsJson = p.CustomFieldsJson,
                DescriptionSnapshot = p.Description,
                RequestedMeasure = string.Empty,
            };

            Items.Add(new BranchItemRow(item, NotifyTotals, isNew: true));
            SelectedProductToAdd = null;
            NotifyTotals();
        }

        [RelayCommand]
        private void RemoveItem(BranchItemRow? row)
        {
            if (row == null) return;
            Items.Remove(row);
            NotifyTotals();
        }

        private void NotifyTotals()
        {
            OnPropertyChanged(nameof(Total));
            OnPropertyChanged(nameof(IncludedCount));
        }

        /// <summary>
        /// Construye la orden de la nueva versión: identidad nueva, número de rama,
        /// items incluidos con los valores editados y snapshots conservados. La orden
        /// original no se toca.
        /// </summary>
        public Order BuildBranchOrder(User? currentUser)
        {
            var branch = new Order
            {
                Id = Guid.NewGuid(),
                BudgetNumber = NewNumber,
                CreatedDate = DateTime.UtcNow,
                EventDate = EventDate,
                Status = OrderStatus.Draft,
                AdminName = currentUser?.Name ?? _sourceOrder.AdminName,
                CreatedByUserId = currentUser?.Id ?? _sourceOrder.CreatedByUserId,
                Client = _sourceOrder.Client ?? new Client(),
                Location = _sourceOrder.Location ?? new Location(),
                ClientId = _sourceOrder.ClientId,
                LocationId = _sourceOrder.LocationId,
            };

            foreach (var row in Items.Where(r => r.Include))
            {
                branch.Items.Add(new OrderItem
                {
                    Id = Guid.NewGuid(),
                    OrderId = branch.Id,
                    ProductId = row.Source.ProductId,
                    Product = row.Source.Product,
                    Quantity = row.Quantity,
                    Dias = row.Dias,
                    UnitPrice = row.UnitPrice,
                    TechnicalNotes = row.Source.TechnicalNotes,
                    ImagePath = row.Source.ImagePath,
                    CustomFieldsJson = row.Source.CustomFieldsJson,
                    DescriptionSnapshot = row.Source.DescriptionSnapshot,
                    RequestedMeasure = row.Source.RequestedMeasure,
                });
            }

            return branch;
        }
    }
}
