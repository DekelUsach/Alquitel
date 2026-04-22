using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using System.Collections.Generic;

namespace Alquitel.UI.ViewModels
{
    public partial class CustomFieldViewModel : ObservableObject
    {
        [ObservableProperty] private string _label = string.Empty;
        [ObservableProperty] private string _value = string.Empty;
        [ObservableProperty] private bool _isBold = false;
        [ObservableProperty] private bool _isUnderline = false;
        [ObservableProperty] private string _colorHex = "#E6EDF3"; // Default TextBrush color

        public IReadOnlyList<string> AvailableColors { get; } = new[] 
        { 
            "#E6EDF3", // Default White/Gray
            "#91c991", // Green
            "#E53E3E", // Intense Red
            "#F68787", // Soft Red
            "#1F6FEB"  // Blue
        };
    }

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
        [ObservableProperty] private string? _editImagePath;

        public ObservableCollection<CustomFieldViewModel> CustomFields { get; } = new();

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
            EditImagePath = p.ImagePath;

            CustomFields.Clear();
            if (!string.IsNullOrWhiteSpace(p.CustomFieldsJson))
            {
                try
                {
                    var fields = JsonSerializer.Deserialize<List<CustomFieldDefinition>>(p.CustomFieldsJson);
                    if (fields != null)
                    {
                        foreach (var f in fields)
                        {
                            CustomFields.Add(new CustomFieldViewModel
                            {
                                Label = f.Label,
                                Value = f.Value,
                                IsBold = f.IsBold,
                                IsUnderline = f.IsUnderline,
                                ColorHex = f.ColorHex
                            });
                        }
                    }
                }
                catch { } // Ignore JSON parsing errors for bad data
            }
        }

        [RelayCommand]
        private void NewProduct()
        {
            SelectedProduct = null;
            EditDescription = string.Empty;
            EditCategory = "General";
            EditBasePrice = 0;
            EditImagePath = null;
            CustomFields.Clear();
            
            IsEditing = true;
            StatusMessage = string.Empty;
        }

        [RelayCommand]
        private void AddCustomField()
        {
            CustomFields.Add(new CustomFieldViewModel());
        }

        [RelayCommand]
        private void RemoveCustomField(CustomFieldViewModel field)
        {
            if (field != null)
                CustomFields.Remove(field);
        }

        [RelayCommand]
        private void SelectImage()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Seleccionar Imagen del Producto",
                Filter = "Archivos de imagen (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png|Todos los archivos (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                EditImagePath = dialog.FileName;
            }
        }

        [RelayCommand]
        private void RemoveImage()
        {
            EditImagePath = null;
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
            p.ImagePath = EditImagePath;

            var definitions = CustomFields.Select(cf => new CustomFieldDefinition
            {
                Label = cf.Label.Trim(),
                Value = cf.Value.Trim(),
                IsBold = cf.IsBold,
                IsUnderline = cf.IsUnderline,
                ColorHex = cf.ColorHex
            }).ToList();

            p.CustomFieldsJson = JsonSerializer.Serialize(definitions);
        }
    }
}
