using Alquitel.Core.Entities;

namespace Alquitel.Mobile.Services;

/// <summary>Usuario logueado en esta sesión de la app.</summary>
public class SessionService
{
    public User? CurrentUser { get; private set; }
    public UserMobilePermission? CurrentPermissions { get; private set; }

    public bool IsLoggedIn => CurrentUser != null;
    public string UserName => CurrentUser?.Name ?? string.Empty;
    public bool IsAdmin => CurrentUser?.Role == UserRole.Admin;

    // ── Permisos por rol y anulaciones personalizadas ──
    // Admin: todo. Vendedor: comercial. Armador: solo ve pedidos/OTs (a menos que tenga un permiso personalizado).
    public bool CanCreateBudgets => IsAdmin || (CurrentPermissions != null ? CurrentPermissions.CanCreateBudgets : CurrentUser?.Role == UserRole.Vendedor);
    public bool CanManageClients => IsAdmin || (CurrentPermissions != null ? CurrentPermissions.CanManageClients : CurrentUser?.Role == UserRole.Vendedor);
    public bool CanManageLocations => IsAdmin || (CurrentPermissions != null ? CurrentPermissions.CanManageLocations : CurrentUser?.Role == UserRole.Vendedor);
    public bool CanChangeOrderStatus => IsAdmin || (CurrentPermissions != null ? CurrentPermissions.CanCreateBudgets : CurrentUser?.Role == UserRole.Vendedor);
    public bool CanSeeReports => IsAdmin || (CurrentPermissions != null ? CurrentPermissions.CanSeeReports : false);

    public void SignIn(User user, UserMobilePermission? permissions)
    {
        CurrentUser = user;
        CurrentPermissions = permissions;
    }

    public void SignOut()
    {
        CurrentUser = null;
        CurrentPermissions = null;
    }
}
