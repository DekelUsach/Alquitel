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
        private Order _currentOrder = new Order { Client = new Client(), Location = new Location() };

        [ObservableProperty]
        private Client? _selectedClient;

        public ObservableCollection<Product> AvailableProducts { get; } = new();
        public ObservableCollection<OrderItem> SelectedItems { get; } = new();

        public MainViewModel(AlquitelDbContext dbContext, IDocumentService documentService)
        {
            _dbContext = dbContext;
            _documentService = documentService;

            // Cargar productos existentes
            try {
                foreach(var p in _dbContext.Products.ToList()) AvailableProducts.Add(p);
            } catch (Exception ex) {
                Log("Error cargando productos: " + ex.Message);
            }
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
                Dias = 1,
                UnitPrice = product.BasePrice,
                // Valores default para LED según imagen
                Uso = "IN",
                Forma = "PLANA",
                FactorForma = "MÓDULO"
            };
            SelectedItems.Add(item);
            CurrentOrder.Items.Add(item);
            
            Log($"Producto añadido: {product.Description}");
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
            await GenerateDocument("1_PRESUPUESTOS", @"1_PRESUPUESTOS\template.docx", false);
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

                // DEBUG: Extract literal placeholder text from actual zip structure for analysis
                try {
                    using var zip = System.IO.Compression.ZipFile.OpenRead(templatePath);
                    var entry = zip.GetEntry("word/document.xml");
                    using var stream = new StreamReader(entry.Open());
                    string xml = stream.ReadToEnd();
                    var matches = System.Text.RegularExpressions.Regex.Matches(xml, @"<w:t[^>]*>(.*?)</w:t>");
                    var sb = new System.Text.StringBuilder();
                    foreach (System.Text.RegularExpressions.Match m in matches) sb.Append(m.Groups[1].Value);
                    File.WriteAllText(@"C:\Alquitel\dump.txt", sb.ToString());
                    Log("Se exportó el texto de la plantilla a dump.txt para depuración.");
                } catch (Exception zipEx) {
                    Log("Error en debug_zip: " + zipEx.Message);
                }

                if (!Directory.Exists(targetDir))
                {
                    Log("Creando carpeta destino...");
                    Directory.CreateDirectory(targetDir);
                }

                // Generar Nombre de Archivo según política corporativa:
                // [nro]- [MMdd]- [empresa]- [lugar]- [iniciales].docx
                string datePart = CurrentOrder.CreatedDate.ToString("MMdd");
                string empresaPart = string.IsNullOrWhiteSpace(CurrentOrder.Client?.CompanyName) ? "CLIENTE" : CurrentOrder.Client.CompanyName;
                string lugarPart = string.IsNullOrWhiteSpace(CurrentOrder.Location?.Name) ? "LUGAR" : CurrentOrder.Location.Name;
                string inicialesPart = GetInitials(CurrentOrder.AdminName);

                string fileName = $"{CurrentOrder.BudgetNumber}- {datePart}- {empresaPart}- {lugarPart}- {inicialesPart}.docx";
                
                // Limpiar caracteres no válidos para nombres de archivo de Windows
                foreach (char c in Path.GetInvalidFileNameChars()) { fileName = fileName.Replace(c, '_'); }
                
                string outputPath = Path.Combine(targetDir, fileName);

                Log($"Archivo de salida (Poliza Corporativa): {outputPath}");

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

        private string GetInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "NA";
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return name.Substring(0, Math.Min(2, name.Length)).ToUpper();
            return string.Join("", parts.Select(p => p[0])).ToUpper();
        }

        private void Log(string message)
        {
            DebugLog += $"\n[{DateTime.Now:HH:mm:ss}] {message}";
        }
    }
}
