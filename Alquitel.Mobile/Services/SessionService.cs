using Alquitel.Core.Entities;

namespace Alquitel.Mobile.Services;

/// <summary>Usuario logueado en esta sesión de la app.</summary>
public class SessionService
{
    public User? CurrentUser { get; private set; }

    public bool IsLoggedIn => CurrentUser != null;
    public string UserName => CurrentUser?.Name ?? string.Empty;
    public bool IsAdmin => CurrentUser?.Role == UserRole.Admin;

    public void SignIn(User user) => CurrentUser = user;
    public void SignOut() => CurrentUser = null;
}
