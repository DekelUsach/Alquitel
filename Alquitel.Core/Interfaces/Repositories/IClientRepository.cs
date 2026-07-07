using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces.Repositories
{
    /// <summary>
    /// Abstracción de acceso a datos de clientes. Los ViewModels pueden depender de
    /// esta interfaz en lugar del DbContext de EF Core, lo que permite reemplazar el
    /// backend (SQLite local hoy, Supabase/PostgreSQL remoto mañana) sin tocar la UI.
    /// </summary>
    public interface IClientRepository
    {
        Task<List<Client>> GetAllAsync();
        Task<Client?> GetByIdAsync(Guid id);
        Task<Client?> GetByCuitAsync(string cuit);
        Task UpsertAsync(Client client);
        /// <summary>Borrado lógico: marca IsArchived, nunca elimina físicamente.</summary>
        Task ArchiveAsync(Guid id);
    }
}
