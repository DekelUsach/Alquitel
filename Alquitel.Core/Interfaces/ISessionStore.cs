using System;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Persiste qué usuario quedó logueado por última vez, para saltear el
    /// <c>LoginWindow</c> en relanzamientos de la app. No guarda credenciales,
    /// solo el <see cref="Entities.User.Id"/> y la fecha de guardado.
    /// </summary>
    public interface ISessionStore
    {
        /// <summary>Guarda el usuario logueado y la marca de tiempo actual (UTC).</summary>
        void Save(Guid userId);

        /// <summary>
        /// Intenta leer la sesión guardada. Devuelve <c>false</c> si no existe, está
        /// corrupta, o no se pudo leer por cualquier motivo.
        /// </summary>
        bool TryLoad(out Guid userId, out DateTimeOffset savedAtUtc);

        /// <summary>Borra la sesión guardada (logout).</summary>
        void Clear();
    }
}
