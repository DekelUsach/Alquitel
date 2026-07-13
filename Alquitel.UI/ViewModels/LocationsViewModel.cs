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
        private readonly IDispatcher _dispatcher;

        public ObservableCollection<Location> Locations { get; } = new();

        [ObservableProperty]
        private Location? _selectedLocation;

        [ObservableProperty]
        private bool _isEditing;

        [ObservableProperty]
        private string _editName = string.Empty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public LocationsViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDialogService dialogService, IDispatcher dispatcher)
        {
            _dbContextFactory = dbContextFactory;
            _dialogService = dialogService;
            _dispatcher = dispatcher;
        }

        public async Task InitializeAsync()
        {
            await LoadLocationsAsync();
        }

        [RelayCommand]
        private async Task RefreshAsync() => await LoadLocationsAsync();

        private async Task LoadLocationsAsync()
        {
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var locations = await db.Locations.OrderBy(l => l.Name).ToListAsync();
                
                // IDispatcher en lugar de App.Current.Dispatcher: permite testear el VM sin WPF.
                _dispatcher.InvokeAsync(() =>
                {
                    Locations.Clear();
                    foreach (var l in locations) Locations.Add(l);
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error al cargar ubicaciones: {ex.Message}";
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

        /// <summary>Nombre de la ubicación centinela que recibe las órdenes de ubicaciones borradas.</summary>
        private const string FallbackLocationName = "(Sin ubicación)";

        [RelayCommand]
        private async Task DeleteLocationAsync()
        {
            if (SelectedLocation == null) return;

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();

                // La FK Order→Location es Cascade: borrar sin reasignar eliminaría
                // en silencio todos los presupuestos que usan esta ubicación.
                var orderCount = await db.Orders.CountAsync(o => o.LocationId == SelectedLocation.Id);

                string prompt = orderCount == 0
                    ? $"¿Eliminar la ubicación \"{SelectedLocation.Name}\"?"
                    : $"La ubicación \"{SelectedLocation.Name}\" está usada por {orderCount} presupuesto(s).\n\n" +
                      $"Si la eliminás, esos presupuestos pasan a \"{FallbackLocationName}\" y no se pierden.\n\n¿Continuar?";

                if (!_dialogService.ShowConfirm("Eliminar ubicación", prompt))
                    return;

                var location = await db.Locations.FindAsync(SelectedLocation.Id);
                if (location != null)
                {
                    if (orderCount > 0)
                    {
                        if (string.Equals(location.Name, FallbackLocationName, StringComparison.OrdinalIgnoreCase))
                        {
                            _dialogService.ShowWarning("Eliminar ubicación",
                                $"\"{FallbackLocationName}\" no puede eliminarse mientras tenga presupuestos asociados.");
                            return;
                        }

                        var fallback = await db.Locations
                            .FirstOrDefaultAsync(l => l.Name == FallbackLocationName);
                        if (fallback == null)
                        {
                            fallback = new Location { Name = FallbackLocationName };
                            db.Locations.Add(fallback);
                            await db.SaveChangesAsync();
                        }

                        await db.Orders
                            .Where(o => o.LocationId == location.Id)
                            .ExecuteUpdateAsync(s => s.SetProperty(o => o.LocationId, fallback.Id));
                    }

                    db.Locations.Remove(location);
                    await db.SaveChangesAsync();
                }

                StatusMessage = orderCount > 0
                    ? $"Ubicación eliminada. {orderCount} presupuesto(s) reasignados a \"{FallbackLocationName}\"."
                    : "Ubicación eliminada.";
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
