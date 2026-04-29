using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Alquitel.UI.ViewModels
{
    public partial class LocationsViewModel : ObservableObject, IAsyncInitialization
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly IDialogService _dialogService;

        public ObservableCollection<Location> Locations { get; } = new();

        [ObservableProperty]
        private Location? _selectedLocation;

        [ObservableProperty]
        private bool _isEditing;

        [ObservableProperty]
        private string _editName = string.Empty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public LocationsViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDialogService dialogService)
        {
            _dbContextFactory = dbContextFactory;
            _dialogService = dialogService;
        }

        public async Task InitializeAsync()
        {
            await LoadLocationsAsync();
        }

        private async Task LoadLocationsAsync()
        {
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var locations = await db.Locations.OrderBy(l => l.Name).ToListAsync();
                
                App.Current.Dispatcher.Invoke(() =>
                {
                    Locations.Clear();
                    foreach (var l in locations) Locations.Add(l);
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading locations: {ex.Message}";
            }
        }

        partial void OnSelectedLocationChanged(Location? value)
        {
            if (value == null)
            {
                IsEditing = false;
                return;
            }

            EditName = value.Name;
            IsEditing = true;
        }

        [RelayCommand]
        private void NewLocation()
        {
            SelectedLocation = new Location { Id = Guid.NewGuid(), Name = "Nueva Ubicación" };
        }

        [RelayCommand]
        private async Task SaveLocationAsync()
        {
            if (SelectedLocation == null) return;

            if (string.IsNullOrWhiteSpace(EditName))
            {
                _dialogService.ShowWarning("Validación", "El nombre de la ubicación no puede estar vacío.");
                return;
            }

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                
                var location = await db.Locations.FindAsync(SelectedLocation.Id);
                
                if (location == null)
                {
                    location = new Location { Id = SelectedLocation.Id };
                    db.Locations.Add(location);
                }
                
                location.Name = EditName;

                await db.SaveChangesAsync();

                StatusMessage = "Ubicación guardada exitosamente.";
                await LoadLocationsAsync();
                SelectedLocation = Locations.FirstOrDefault(l => l.Id == location.Id);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Error al guardar", ex.Message);
            }
        }

        [RelayCommand]
        private async Task DeleteLocationAsync()
        {
            if (SelectedLocation == null) return;

            if (!_dialogService.ShowConfirm("Confirmar", $"¿Estás seguro de que deseas eliminar la ubicación {SelectedLocation.Name}?"))
                return;

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var location = await db.Locations.FindAsync(SelectedLocation.Id);
                if (location != null)
                {
                    db.Locations.Remove(location);
                    await db.SaveChangesAsync();
                }

                StatusMessage = "Ubicación eliminada.";
                SelectedLocation = null;
                await LoadLocationsAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Error al eliminar", $"No se puede eliminar la ubicación: {ex.Message}");
            }
        }

        [RelayCommand]
        private void CancelEdit()
        {
            SelectedLocation = null;
        }
    }
}
