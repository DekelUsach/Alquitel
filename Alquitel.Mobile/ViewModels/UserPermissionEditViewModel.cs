using Alquitel.Core.Entities;
using Alquitel.Mobile.Data;
using Alquitel.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.ViewModels;

[QueryProperty(nameof(UserId), "userId")]
public partial class UserPermissionEditViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;
    private readonly SessionService _session;

    [ObservableProperty] private Guid _userId;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _userRoleText = string.Empty;

    // Toggles de permisos
    [ObservableProperty] private bool _canManageLocations;
    [ObservableProperty] private bool _canCreateBudgets;
    [ObservableProperty] private bool _canManageClients;
    [ObservableProperty] private bool _canSeeReports;

    public UserPermissionEditViewModel(IDbContextFactory<MobileDbContext> factory, SessionService session)
    {
        _factory = factory;
        _session = session;
    }

    partial void OnUserIdChanged(Guid value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        if (UserId == Guid.Empty || IsBusy) return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;

            using var db = _factory.CreateDbContext();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId);
            if (user == null)
            {
                ErrorMessage = "El usuario no existe.";
                return;
            }

            UserName = user.Name;
            UserRoleText = user.Role.ToString();

            // Buscar si ya tiene permisos guardados en la tabla
            var perms = await db.UserMobilePermissions.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == UserId);
            if (perms != null)
            {
                CanManageLocations = perms.CanManageLocations;
                CanCreateBudgets = perms.CanCreateBudgets;
                CanManageClients = perms.CanManageClients;
                CanSeeReports = perms.CanSeeReports;
            }
            else
            {
                // Si no hay registro, inicializar con los defaults del rol
                bool isVendedor = user.Role == UserRole.Vendedor;
                CanManageLocations = isVendedor;
                CanCreateBudgets = isVendedor;
                CanManageClients = isVendedor;
                CanSeeReports = false; // por defecto ningún no-admin ve reportes
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = DescribeDbError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!_session.IsAdmin) return;
        if (UserId == Guid.Empty || IsBusy) return;

        try
        {
            IsBusy = true;
            using var db = _factory.CreateDbContext();

            var perms = await db.UserMobilePermissions.FirstOrDefaultAsync(p => p.UserId == UserId);
            if (perms == null)
            {
                perms = new UserMobilePermission { UserId = UserId };
                db.UserMobilePermissions.Add(perms);
            }

            perms.CanManageLocations = CanManageLocations;
            perms.CanCreateBudgets = CanCreateBudgets;
            perms.CanManageClients = CanManageClients;
            perms.CanSeeReports = CanSeeReports;

            await db.SaveChangesAsync();
            await ShowAlertAsync("Guardado", $"Permisos de {UserName} actualizados con éxito.");
            await Shell.Current.GoToAsync("..");
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
}
