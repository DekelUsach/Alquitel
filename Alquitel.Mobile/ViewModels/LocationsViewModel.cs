using System.Collections.ObjectModel;
using Alquitel.Core.Entities;
using Alquitel.Mobile.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Alquitel.Mobile.Services;
using Location = Alquitel.Core.Entities.Location;

namespace Alquitel.Mobile.ViewModels;

public partial class LocationsViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;
    private readonly SessionService _session;

    public ObservableCollection<Location> Locations { get; } = new();

    [ObservableProperty] private string _newLocationName = string.Empty;
    [ObservableProperty] private bool _isRefreshing;

    public LocationsViewModel(IDbContextFactory<MobileDbContext> factory, SessionService session)
    {
        _factory = factory;
        _session = session;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!_session.CanManageLocations)
        {
            await ShowAlertAsync("Acceso denegado", "No tienes permisos para ver ubicaciones.");
            await Shell.Current.GoToAsync("//main/dashboard");
            return;
        }
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            using var db = _factory.CreateDbContext();
            var all = await db.Locations.AsNoTracking().OrderBy(l => l.Name).ToListAsync();
            Locations.Clear();
            foreach (var l in all) Locations.Add(l);
        }
        catch (Exception ex)
        {
            ErrorMessage = DescribeDbError(ex);
        }
        finally
        {
            IsBusy = false;
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!_session.CanManageLocations) return;
        if (string.IsNullOrWhiteSpace(NewLocationName)) return;
        try
        {
            IsBusy = true;
            using var db = _factory.CreateDbContext();
            db.Locations.Add(new Location { Name = NewLocationName.Trim() });
            await db.SaveChangesAsync();
            NewLocationName = string.Empty;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Error", DescribeDbError(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RenameAsync(Location? location)
    {
        if (!_session.CanManageLocations) return;
        if (location == null) return;
        var newName = await Shell.Current.DisplayPromptAsync("Renombrar ubicación", "Nuevo nombre:",
            initialValue: location.Name, accept: "Guardar", cancel: "Cancelar");
        if (string.IsNullOrWhiteSpace(newName) || newName == location.Name) return;

        try
        {
            using var db = _factory.CreateDbContext();
            var entity = await db.Locations.FirstAsync(l => l.Id == location.Id);
            entity.Name = newName.Trim();
            await db.SaveChangesAsync();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Error", DescribeDbError(ex));
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(Location? location)
    {
        if (!_session.CanManageLocations) return;
        if (location == null) return;
        if (!await ConfirmAsync("Eliminar ubicación", $"¿Eliminar \"{location.Name}\"? Si tiene pedidos asociados la base lo va a rechazar."))
            return;

        try
        {
            using var db = _factory.CreateDbContext();
            var entity = await db.Locations.FirstAsync(l => l.Id == location.Id);
            db.Locations.Remove(entity);
            await db.SaveChangesAsync();
            await LoadAsync();
        }
        catch (DbUpdateException)
        {
            await ShowAlertAsync("Ubicación en uso",
                "No se puede eliminar: hay pedidos que la referencian. Reasignalos primero desde la app de escritorio.");
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Error", DescribeDbError(ex));
        }
    }
}
