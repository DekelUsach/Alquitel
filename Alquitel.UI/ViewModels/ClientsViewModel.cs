using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Core.Interfaces;
using Alquitel.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace Alquitel.UI.ViewModels
{
    public partial class ClientsViewModel : ObservableObject, IAsyncInitialization
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly IDialogService _dialogService;

        public ObservableCollection<Client> Clients { get; } = new();

        /// <summary>Vista filtrable de la colección para el buscador de la UI.</summary>
        public ICollectionView ClientsCollectionView { get; }

        [ObservableProperty]
        private string _searchText = string.Empty;

        partial void OnSearchTextChanged(string value) => ClientsCollectionView.Refresh();

        private bool FilterClient(object obj)
        {
            if (obj is not Client c) return false;
            if (string.IsNullOrWhiteSpace(SearchText)) return true;

            var q = SearchText.Trim();
            return (c.CompanyName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.Cuit?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.ContactName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        [ObservableProperty]
        private Client? _selectedClient;

        [ObservableProperty]
        private bool _isEditing;

        [ObservableProperty]
        private string _editCompanyName = string.Empty;

        [ObservableProperty]
        private string _editCuit = string.Empty;

        /// <summary>Feedback inline del CUIT en la ficha ("✓ válido" / motivo del error).</summary>
        [ObservableProperty]
        private string _cuitFeedback = string.Empty;

        [ObservableProperty]
        private bool _cuitFeedbackIsError;

        partial void OnEditCuitChanged(string value)
        {
            var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length == 0)
            {
                CuitFeedback = string.Empty;
                CuitFeedbackIsError = false;
            }
            else if (CuitValidator.IsValid(value))
            {
                CuitFeedback = "✓ CUIT válido (verificación AFIP)";
                CuitFeedbackIsError = false;
            }
            else if (digits.Length >= 11)
            {
                CuitFeedback = "✗ El CUIT no supera la verificación de AFIP (Módulo 11).";
                CuitFeedbackIsError = true;
            }
            else
            {
                CuitFeedback = string.Empty;
                CuitFeedbackIsError = false;
            }
        }

        [ObservableProperty]
        private string _editContactName = string.Empty;

        [ObservableProperty]
        private string _editPhone = string.Empty;

        [ObservableProperty]
        private string _editEmail = string.Empty;

        [ObservableProperty]
        private string _editInternalNotes = string.Empty;

        /// <summary>% de descuento acordado con el cliente (texto; vacío = sin acuerdo).</summary>
        [ObservableProperty]
        private string _editSpecialDiscount = string.Empty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        private readonly IToastService _toastService;
        private readonly IDispatcher _dispatcher;
        private readonly IAiTextAssistant _aiAssistant;

        public ClientsViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDialogService dialogService, IToastService toastService, IDispatcher dispatcher, IAiTextAssistant aiAssistant)
        {
            _dbContextFactory = dbContextFactory;
            _dialogService = dialogService;
            _toastService = toastService;
            _dispatcher = dispatcher;
            _aiAssistant = aiAssistant;

            ClientsCollectionView = CollectionViewSource.GetDefaultView(Clients);
            ClientsCollectionView.Filter = FilterClient;
        }

        public async Task InitializeAsync()
        {
            await LoadClientsAsync();
        }

        [RelayCommand]
        private async Task RefreshAsync() => await LoadClientsAsync();

        /// <summary>True mientras llegan los datos: la vista muestra skeleton rows.</summary>
        [ObservableProperty]
        private bool _isLoading;

        private async Task LoadClientsAsync()
        {
            IsLoading = true;
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var clients = await db.Clients.OrderBy(c => c.CompanyName).ToListAsync();

                _dispatcher.InvokeAsync(() =>
                {
                    Clients.Clear();
                    foreach (var c in clients) Clients.Add(c);
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error al cargar clientes: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        partial void OnSelectedClientChanged(Client? value)
        {
            if (value == null)
            {
                IsEditing = false;
                return;
            }

            EditCompanyName = value.CompanyName ?? string.Empty;
            EditCuit = value.Cuit ?? string.Empty;
            EditContactName = value.ContactName ?? string.Empty;
            EditPhone = value.Phone ?? string.Empty;
            EditEmail = value.Email ?? string.Empty;
            EditInternalNotes = value.InternalNotes ?? string.Empty;
            EditSpecialDiscount = value.SpecialDiscountPercent?.ToString("0.##") ?? string.Empty;
            ClientAiSummary = string.Empty; // el resumen es del cliente anterior
            IsEditing = true;
        }

        // ── Quick-win IA: resumen del historial del cliente ──────────

        [ObservableProperty]
        private string _clientAiSummary = string.Empty;

        [ObservableProperty]
        private bool _isSummarizing;

        /// <summary>
        /// Resume con IA el historial de pedidos del cliente seleccionado: qué alquila,
        /// con qué frecuencia y monto acumulado. Solo lectura, no toca datos.
        /// </summary>
        [RelayCommand]
        private async Task SummarizeClientHistoryAsync()
        {
            if (SelectedClient == null) return;
            if (!_aiAssistant.IsConfigured)
            {
                _toastService.ShowInfo("La IA no está configurada (falta la API key de Pollinations).");
                return;
            }

            IsSummarizing = true;
            ClientAiSummary = "Analizando historial…";
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var orders = await db.Orders.AsNoTracking().IgnoreQueryFilters()
                    .Where(o => o.ClientId == SelectedClient.Id)
                    .Include(o => o.Items).ThenInclude(i => i.Product)
                    .OrderByDescending(o => o.CreatedDate)
                    .Take(30)
                    .ToListAsync();

                if (orders.Count == 0)
                {
                    ClientAiSummary = "Este cliente todavía no tiene presupuestos registrados.";
                    return;
                }

                var sb = new StringBuilder();
                foreach (var o in orders)
                {
                    var items = string.Join(", ", o.Items.Take(6).Select(i =>
                        $"{i.Quantity}x {Alquitel.Core.Parsing.TagParser.StripTags(i.DescriptionSnapshot ?? i.Product?.Description) ?? "equipo"}"));
                    sb.AppendLine($"{o.CreatedDate:yyyy-MM-dd} | {o.Status} | total {o.GrandTotal:0} | {items}");
                }

                const string system =
                    "Sos el analista comercial de una empresa argentina de alquiler audiovisual. " +
                    "Resumí el historial de pedidos de este cliente en 3-4 oraciones en español: " +
                    "qué suele alquilar, frecuencia, monto aproximado acumulado y cualquier patrón útil " +
                    "para el vendedor. Texto plano, sin markdown ni listas.";

                var summary = await _aiAssistant.CompleteAsync(system, sb.ToString());
                ClientAiSummary = string.IsNullOrWhiteSpace(summary)
                    ? "La IA no devolvió un resumen. Reintentá en unos segundos."
                    : summary.Trim();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "SummarizeClientHistoryAsync failed");
                ClientAiSummary = "No se pudo generar el resumen.";
            }
            finally
            {
                IsSummarizing = false;
            }
        }

        [RelayCommand]
        private void NewClient()
        {
            SelectedClient = new Client { Id = Guid.NewGuid(), CompanyName = "Nuevo Cliente" };
        }

        [RelayCommand]
        private async Task SaveClientAsync()
        {
            if (SelectedClient == null) return;

            if (string.IsNullOrWhiteSpace(EditCompanyName))
            {
                _dialogService.ShowWarning("Validación", "El nombre del cliente no puede estar vacío.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(EditCuit) && !CuitValidator.IsValid(EditCuit))
            {
                if (!_dialogService.ShowConfirm("CUIT Inválido", "El CUIT ingresado no es válido según AFIP. ¿Deseas guardarlo de todos modos?"))
                {
                    return;
                }
            }

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();

                // Friendly duplicate check — otherwise the unique index surfaces
                // as a raw DbUpdateException the user can't interpret.
                var cuit = EditCuit?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(cuit))
                {
                    var duplicate = await db.Clients.IgnoreQueryFilters()
                        .AnyAsync(c => c.Cuit == cuit && c.Id != SelectedClient.Id);
                    if (duplicate)
                    {
                        _dialogService.ShowWarning("CUIT duplicado",
                            "Ya existe otro cliente registrado con ese CUIT.");
                        return;
                    }
                }

                var client = await db.Clients.FindAsync(SelectedClient.Id);

                if (client == null)
                {
                    client = new Client { Id = SelectedClient.Id };
                    db.Clients.Add(client);
                }
                
                client.CompanyName = EditCompanyName.Trim();
                client.Cuit = cuit;
                client.ContactName = EditContactName;
                client.Phone = EditPhone;
                client.Email = EditEmail;
                client.InternalNotes = string.IsNullOrWhiteSpace(EditInternalNotes) ? null : EditInternalNotes.Trim();
                client.SpecialDiscountPercent =
                    decimal.TryParse(EditSpecialDiscount?.Trim().Replace("%", ""),
                        System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.CurrentCulture, out var disc) && disc > 0
                    ? Math.Min(disc, 100m)
                    : null;

                await db.SaveChangesAsync();

                StatusMessage = "Cliente guardado exitosamente.";
                await LoadClientsAsync();
                SelectedClient = Clients.FirstOrDefault(c => c.Id == client.Id);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Error al guardar", ex.Message);
            }
        }

        [RelayCommand]
        private async Task DeleteClientAsync()
        {
            if (SelectedClient == null) return;

            if (!_dialogService.ShowConfirm("Confirmar", $"¿Estás seguro de que deseas eliminar al cliente {SelectedClient.CompanyName}?"))
                return;

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var client = await db.Clients.FindAsync(SelectedClient.Id);
                if (client != null)
                {
                    client.IsArchived = true;
                    await db.SaveChangesAsync();
                }

                StatusMessage = "Cliente eliminado.";
                SelectedClient = null;
                await LoadClientsAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Error al eliminar", ex.Message);
            }
        }

        [RelayCommand]
        private void CancelEdit()
        {
            SelectedClient = null;
        }

        /// <summary>
        /// Exporta el directorio completo de clientes a un archivo CSV
        /// (separador ';' y BOM UTF-8 para que Excel en español lo abra bien).
        /// </summary>
        [RelayCommand]
        private void ExportCsv()
        {
            if (Clients.Count == 0)
            {
                _toastService.ShowInfo("No hay clientes para exportar.");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Archivo CSV (*.csv)|*.csv",
                FileName = $"Clientes_Alquitel_{DateTime.Now:yyyyMMdd}.csv"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Empresa;CUIT;Contacto;Teléfono;Email");
                foreach (var c in Clients)
                {
                    sb.AppendLine(string.Join(";",
                        CsvField(c.CompanyName), CsvField(c.Cuit),
                        CsvField(c.ContactName), CsvField(c.Phone), CsvField(c.Email)));
                }

                File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
                StatusMessage = $"Se exportaron {Clients.Count} clientes.";
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Error al exportar", ex.Message);
            }
        }

        private static string CsvField(string? value)
        {
            var v = value ?? string.Empty;
            if (v.Contains(';') || v.Contains('"') || v.Contains('\n'))
                v = $"\"{v.Replace("\"", "\"\"")}\"";
            return v;
        }
    }
}
