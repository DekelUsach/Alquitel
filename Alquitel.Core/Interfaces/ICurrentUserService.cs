using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Mantiene en memoria el usuario logueado durante la sesión de la aplicación.
    /// Se setea una única vez en el login (antes de mostrar MainWindow) y lo consumen
    /// los ViewModels para gates de rol y para firmar órdenes (CreatedByUserId).
    /// </summary>
    public interface ICurrentUserService
    {
        /// <summary>Usuario logueado. Null solo antes de completar el login.</summary>
        User? Current { get; }

        bool IsAdmin { get; }

        void SetCurrentUser(User user);
    }
}
