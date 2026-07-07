using System.Threading.Tasks;

namespace Alquitel.Core.Interfaces
{
    /// <summary>Resultado de una prueba de conectividad con el backend remoto.</summary>
    public record RemoteConnectionStatus(bool IsConfigured, bool IsReachable, string Message);

    /// <summary>
    /// Contrato del futuro servicio de sincronización con una base de datos en servidor
    /// (Supabase/PostgreSQL u otra). Hoy la app funciona 100% con SQLite local; esta
    /// interfaz existe para que la UI y los servicios puedan programarse contra ella
    /// desde ya, y la implementación real se enchufe después sin cambios de arquitectura.
    /// </summary>
    public interface IRemoteSyncService
    {
        /// <summary>True cuando hay un backend remoto configurado en appsettings (Database:Provider != "sqlite").</summary>
        bool IsRemoteConfigured { get; }

        /// <summary>Prueba de conexión al backend remoto. Con SQLite local devuelve NotConfigured.</summary>
        Task<RemoteConnectionStatus> TestConnectionAsync();

        /// <summary>
        /// Empuja los cambios locales pendientes al servidor. La implementación local
        /// es un no-op; la futura implementación Supabase hará upsert por tabla usando
        /// los Guid de las entidades como claves globales.
        /// </summary>
        Task PushPendingChangesAsync();
    }
}
