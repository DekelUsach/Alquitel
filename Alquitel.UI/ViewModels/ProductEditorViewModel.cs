using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Alquitel.UI.ViewModels
{
    public partial class ProductEditorViewModel : ObservableObject
    {
        private readonly AlquitelDbContext _dbContext;

        public ObservableCollection<Product> Products { get; } = new();

        [ObservableProperty]
        private Product? _selectedProduct;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isEditing;

        // ── Editable fields (bound to the form) ─────────────────
        [ObservableProperty] private string _editDescription = string.Empty;
        [ObservableProperty] private string _editCategory = "General";
        [ObservableProperty] private decimal _editBasePrice;
        [ObservableProperty] private string _editPixelPitchTitle = string.Empty;
        [ObservableProperty] private string _editDefaultUso = "IN";
        [ObservableProperty] private string _editDefaultForma = "PLANA";
        [ObservableProperty] private string _editDefaultFactorForma = "MÓDULO";
        [ObservableProperty] private string _editDefaultPixelPitchModule = string.Empty;
        [ObservableProperty] private string _editAccessories = string.Empty;
        [ObservableProperty] private string _editModuleDimensions = string.Empty;
        [ObservableProperty] private string _editDefaultPesoPorM2 = string.Empty;
        [ObservableProperty] private string _editDefaultConsumoPorM2 = string.Empty;
        [ObservableProperty] private string _editDefaultResolucionX = string.Empty;
        [ObservableProperty] private string _editDefaultResolucionY = string.Empty;
        [ObservableProperty] private string _editIncludesNote = string.Empty;

        public ProductEditorViewModel(AlquitelDbContext dbContext)
        {
            _dbContext = dbContext;
            LoadProducts();
        }

        private void LoadProducts()
        {
            Products.Clear();
            foreach (var p in _dbContext.Products.OrderBy(p => p.Category).ThenBy(p => p.Description).ToList())
                Products.Add(p);
        }

        partial void OnSelectedProductChanged(Product? value)
        {
            if (value == null)
            {
                IsEditing = false;
                return;
            }
            PopulateForm(value);
            IsEditing = true;
        }

        private void PopulateForm(Product p)
        {
            EditDescription = p.Description;
            EditCategory = p.Category;
            EditBasePrice = p.BasePrice;
            EditPixelPitchTitle = p.PixelPitchTitle ?? string.Empty;
            EditDefaultUso = p.DefaultUso ?? "IN";
            EditDefaultForma = p.DefaultForma ?? "PLANA";
            EditDefaultFactorForma = p.DefaultFactorForma ?? "MÓDULO";
            EditDefaultPixelPitchModule = p.DefaultPixelPitchModule ?? string.Empty;
            EditAccessories = p.Accessories ?? string.Empty;
            EditModuleDimensions = p.ModuleDimensions ?? string.Empty;
            EditDefaultPesoPorM2 = p.DefaultPesoPorM2 ?? string.Empty;
            EditDefaultConsumoPorM2 = p.DefaultConsumoPorM2 ?? string.Empty;
            EditDefaultResolucionX = p.DefaultResolucionX ?? string.Empty;
            EditDefaultResolucionY = p.DefaultResolucionY ?? string.Empty;
            EditIncludesNote = p.IncludesNote ?? string.Empty;
        }

        [RelayCommand]
        private void NewProduct()
        {
            SelectedProduct = null;
            EditDescription = string.Empty;
            EditCategory = "General";
            EditBasePrice = 0;
            EditPixelPitchTitle = string.Empty;
            EditDefaultUso = "IN";
            EditDefaultForma = "PLANA";
            EditDefaultFactorForma = "MÓDULO";
            EditDefaultPixelPitchModule = string.Empty;
            EditAccessories = string.Empty;
            EditModuleDimensions = string.Empty;
            EditDefaultPesoPorM2 = string.Empty;
            EditDefaultConsumoPorM2 = string.Empty;
            EditDefaultResolucionX = string.Empty;
            EditDefaultResolucionY = string.Empty;
            EditIncludesNote = string.Empty;
            IsEditing = true;
            StatusMessage = string.Empty;
        }

        [RelayCommand]
        private void SaveProduct()
        {
            if (string.IsNullOrWhiteSpace(EditDescription))
            {
                StatusMessage = "✗ La descripción no puede estar vacía.";
                return;
            }

            try
            {
                Product target;
                if (SelectedProduct != null)
                {
                    target = SelectedProduct;
                }
                else
                {
                    target = new Product();
                    _dbContext.Products.Add(target);
                }

                ApplyFormToProduct(target);
                _dbContext.SaveChanges();

                LoadProducts();
                SelectedProduct = Products.FirstOrDefault(p => p.Id == target.Id);
                StatusMessage = "✓ Producto guardado correctamente.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Error al guardar: {ex.Message}";
            }
        }

        [RelayCommand]
        private void DeleteProduct()
        {
            if (SelectedProduct == null) return;

            try
            {
                _dbContext.Products.Remove(SelectedProduct);
                _dbContext.SaveChanges();
                SelectedProduct = null;
                IsEditing = false;
                LoadProducts();
                StatusMessage = "✓ Producto eliminado.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Error al eliminar: {ex.Message}";
            }
        }

        [RelayCommand]
        private void CancelEdit()
        {
            if (SelectedProduct != null)
                PopulateForm(SelectedProduct);
            else
                IsEditing = false;
            StatusMessage = string.Empty;
        }

        private void ApplyFormToProduct(Product p)
        {
            p.Description = EditDescription.Trim();
            p.Category = EditCategory.Trim();
            p.BasePrice = EditBasePrice;
            p.PixelPitchTitle = NullIfEmpty(EditPixelPitchTitle);
            p.DefaultUso = NullIfEmpty(EditDefaultUso);
            p.DefaultForma = NullIfEmpty(EditDefaultForma);
            p.DefaultFactorForma = NullIfEmpty(EditDefaultFactorForma);
            p.DefaultPixelPitchModule = NullIfEmpty(EditDefaultPixelPitchModule);
            p.Accessories = NullIfEmpty(EditAccessories);
            p.ModuleDimensions = NullIfEmpty(EditModuleDimensions);
            p.DefaultPesoPorM2 = NullIfEmpty(EditDefaultPesoPorM2);
            p.DefaultConsumoPorM2 = NullIfEmpty(EditDefaultConsumoPorM2);
            p.DefaultResolucionX = NullIfEmpty(EditDefaultResolucionX);
            p.DefaultResolucionY = NullIfEmpty(EditDefaultResolucionY);
            p.IncludesNote = NullIfEmpty(EditIncludesNote);
        }

        private static string? NullIfEmpty(string s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
