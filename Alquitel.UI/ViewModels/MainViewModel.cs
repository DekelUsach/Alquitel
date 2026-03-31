using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Core.Interfaces;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Windows;

namespace Alquitel.UI.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly AlquitelDbContext _dbContext;
        private readonly IDocumentService _documentService;

        [ObservableProperty]
        private string _debugLog = "Iniciando sistema Alquitel...";

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private string _cuitInput = string.Empty;

        [ObservableProperty]
        private Order _currentOrder = new();

        [ObservableProperty]
        private Client? _selectedClient;

        public ObservableCollection<Product> AvailableProducts { get; } = new();
        public ObservableCollection<OrderItem> SelectedItems { get; } = new();

        public MainViewModel(AlquitelDbContext dbContext, IDocumentService documentService)
        {
            _dbContext = dbContext;
            _documentService = documentService;
            
            // Semilla de productos para demo
            if (!_dbContext.Products.Any())
            {
               _dbContext.Products.Add(new Product { Description = "Pantalla LED 2.6mm", Category = "Visuales", BasePrice = 1500 });
               _dbContext.Products.Add(new Product { Description = "Touch Screen 85 Pro", Category = "Interactivos", BasePrice = 800 });
               _dbContext.SaveChanges();
            }

            foreach(var p in _dbContext.Products) AvailableProducts.Add(p);
        }

        partial void OnCuitInputChanged(string value)
        {
            var client = _dbContext.Clients.FirstOrDefault(c => c.Cuit == value);
            if (client != null)
            {
                CurrentOrder.Client = client;
                OnPropertyChanged(nameof(CurrentOrder));
            }
        }

        [RelayCommand]
        private void AddProduct(Product product)
        {
            var item = new OrderItem
            {
                ProductId = product.Id,
                Product = product,
                Quantity = 1,
                UnitPrice = product.BasePrice
            };
            SelectedItems.Add(item);
            CurrentOrder.Items.Add(item);
        }

        [RelayCommand]
        private void RemoveItem(OrderItem item)
        {
            SelectedItems.Remove(item);
            CurrentOrder.Items.Remove(item);
        }

        [RelayCommand]
        private async Task GenerateBudget()
        {
            await GenerateDocument("1_PRESUPUESTOS", "31294(2) - 0326 - AV EVENTOS - MORENO - SG.docx", false);
        }

        [RelayCommand]
        private async Task GenerateOF()
        {
            await GenerateDocument("2_OF", "OF  9054 - 0326 - B + T - FERIA DEL LIBRO 2026 - SG.docx", false);
        }

        [RelayCommand]
        private async Task GenerateOT()
        {
            await GenerateDocument("3_OT", "OT  9054 - 0326 - B + T - FERIA DEL LIBRO 2026 - SG.docx", true);
        }

        private async Task GenerateDocument(string folderName, string templateName, bool isTechnical)
        {
            try
            {
                Log($"Iniciando generación de {folderName}...");
                
                string baseDir = @"C:\Alquitel";
                string targetDir = Path.Combine(baseDir, folderName);
                string templatePath = Path.Combine(baseDir, templateName);
                
                Log($"Carpeta destino: {targetDir}");
                Log($"Plantilla origen: {templatePath}");

                if (!Directory.Exists(targetDir))
                {
                    Log("Creando carpeta destino...");
                    Directory.CreateDirectory(targetDir);
                }

                string fileName = $"{folderName}_{CurrentOrder.BudgetNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.docx";
                string outputPath = Path.Combine(targetDir, fileName);

                Log($"Archivo de salida: {outputPath}");

                if (!File.Exists(templatePath))
                {
                    string msg = $"ERROR: La plantilla no existe en: {templatePath}";
                    Log(msg);
                    MessageBox.Show(msg, "Error de Plantilla", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                Log("Llamando al servicio de Word (esto puede tardar unos segundos)...");
                await _documentService.GenerateDocumentAsync(CurrentOrder, templatePath, outputPath, isTechnical);
                
                Log("¡Documento generado con éxito!");
                MessageBox.Show($"Archivo guardado correctamente en:\n{outputPath}", "Éxito", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                string errorMsg = $"ERROR CRÍTICO: {ex.Message}";
                Log(errorMsg);
                Log(ex.StackTrace ?? "");
                MessageBox.Show(errorMsg, "Error de Generación", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Log(string message)
        {
            DebugLog += $"\n[{DateTime.Now:HH:mm:ss}] {message}";
        }
    }
}
