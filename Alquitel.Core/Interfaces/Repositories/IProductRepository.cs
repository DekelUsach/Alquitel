using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces.Repositories
{
    /// <summary>
    /// Abstracción de acceso a datos del catálogo de productos.
    /// Ver <see cref="IClientRepository"/> para la motivación (migración a backend remoto).
    /// </summary>
    public interface IProductRepository
    {
        Task<List<Product>> GetAllAsync();
        Task<Product?> GetByIdAsync(Guid id);
        Task UpsertAsync(Product product);
        /// <summary>Borrado lógico: marca IsArchived, nunca elimina físicamente.</summary>
        Task ArchiveAsync(Guid id);
    }
}
