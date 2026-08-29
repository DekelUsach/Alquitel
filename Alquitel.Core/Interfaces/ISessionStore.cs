using System;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Persiste qué usuario quedó logueado por última vez, para saltear el
    /// <c>LoginWindow</c> en relanzamientos de la app. No guarda credenciales:
    /// solo el <see cref="Entities.User.Id"/>, la fecha, y una huella derivada del
    /// hash de la contraseña vigente (para que cambiarla invalide la sesión).
    ///
    /// El contenido va firmado y protegido: editar el archivo a mano para poner el Id
    /// de un Admin no debe habilitar el ingreso sin contraseña.
    /// </summary>
    public interface ISessionStore
    {
        /// <summary>
        /// Guarda el usuario logueado con la marca de tiempo actual (UTC) y la huella
        /// de su contraseña (<see cref="Helpers.PasswordHasher.Fingerprint"/>).
        /// </summary>
        void Save(Guid userId, string passwordFingerprint);

        /// <summary>
        /// Intenta leer la sesión guardada. Devuelve <c>false</c> si no existe, está
        /// vencida, la firma no valida, o no se pudo leer por cualquier motivo.
        /// </summary>
        /// <param name="maxAge">Tope de antigüedad aceptado para esta sesión (por rol).</param>
        bool TryLoad(TimeSpan maxAge, out Guid userId, out string passwordFingerprint, out DateTimeOffset savedAtUtc);

        /// <summary>Borra la sesión guardada (logout).</summary>
        void Clear();
    }
}
