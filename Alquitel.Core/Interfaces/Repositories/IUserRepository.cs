using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces.Repositories
{
    /// <summary>
    /// Abstracción de acceso a datos de usuarios del sistema. Mismo patrón que
    /// <see cref="IClientRepository"/>: la UI no conoce EF Core ni el proveedor.
    /// </summary>
    public interface IUserRepository
    {
        /// <summary>Usuarios activos (no archivados), ordenados por nombre.</summary>
        Task<List<User>> GetActiveAsync();
        Task<User?> GetByIdAsync(Guid id);
        Task UpsertAsync(User user);
        /// <summary>Borrado lógico: marca IsArchived, nunca elimina físicamente.</summary>
        Task ArchiveAsync(Guid id);
    }
}
