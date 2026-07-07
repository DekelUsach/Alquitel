using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces.Repositories
{
    /// <summary>
    /// Abstracción de acceso a datos de ubicaciones de eventos.
    /// Ver <see cref="IClientRepository"/> para la motivación (migración a backend remoto).
    /// </summary>
    public interface ILocationRepository
    {
        Task<List<Location>> GetAllAsync();
        Task<Location?> GetByIdAsync(Guid id);
        Task UpsertAsync(Location location);
        Task DeleteAsync(Guid id);
    }
}
