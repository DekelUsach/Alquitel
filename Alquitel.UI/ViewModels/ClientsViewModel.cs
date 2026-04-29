using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Core.Interfaces;
using Alquitel.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Alquitel.UI.ViewModels
{
    public partial class ClientsViewModel : ObservableObject, IAsyncInitialization
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly IDialogService _dialogService;

        public ObservableCollection<Client> Clients { get; } = new();

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
        private string _statusMessage = string.Empty;

        public ClientsViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDialogService dialogService)
        {
            _dbContextFactory = dbContextFactory;
            _dialogService = dialogService;
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
                StatusMessage = $"Error loading clients: {ex.Message}";
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
                
                var client = await db.Clients.FindAsync(SelectedClient.Id);
                
                if (client == null)
                {
                    client = new Client { Id = SelectedClient.Id };
                    db.Clients.Add(client);
                }
                
                client.CompanyName = EditCompanyName;
                client.Cuit = EditCuit;
                client.ContactName = EditContactName;
                client.Phone = EditPhone;
                client.Email = EditEmail;

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
    }
}
