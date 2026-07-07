using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
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

        [ObservableProperty]
        private string _editContactName = string.Empty;

        [ObservableProperty]
        private string _editPhone = string.Empty;

        [ObservableProperty]
        private string _editEmail = string.Empty;

        [ObservableProperty]
        private string _editInternalNotes = string.Empty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public ClientsViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDialogService dialogService)
        {
            _dbContextFactory = dbContextFactory;
            _dialogService = dialogService;

            ClientsCollectionView = CollectionViewSource.GetDefaultView(Clients);
            ClientsCollectionView.Filter = FilterClient;
        }

        public async Task InitializeAsync()
        {
            await LoadClientsAsync();
        }

        private async Task LoadClientsAsync()
        {
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var clients = await db.Clients.OrderBy(c => c.CompanyName).ToListAsync();
                
                App.Current.Dispatcher.Invoke(() =>
                {
                    Clients.Clear();
                    foreach (var c in clients) Clients.Add(c);
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error al cargar clientes: {ex.Message}";
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
            IsEditing = true;
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
                _dialogService.ShowInfo("Exportar", "No hay clientes para exportar.");
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
