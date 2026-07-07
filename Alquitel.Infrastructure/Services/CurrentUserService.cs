using System;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación en memoria de <see cref="ICurrentUserService"/>. Registrada como
    /// Singleton: el usuario se setea una vez en el login y vive toda la sesión.
    /// </summary>
    public class CurrentUserService : ICurrentUserService
    {
        public User? Current { get; private set; }

        public bool IsAdmin => Current?.Role == UserRole.Admin;

        public void SetCurrentUser(User user)
        {
            Current = user ?? throw new ArgumentNullException(nameof(user));
            AppLog.Information("Sesión iniciada: {User} ({Role})", user.Name, user.Role);
        }
    }
}
