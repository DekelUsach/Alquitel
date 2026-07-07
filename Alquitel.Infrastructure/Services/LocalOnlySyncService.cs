using System.Threading.Tasks;
using Alquitel.Core.Interfaces;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación "solo local" de <see cref="IRemoteSyncService"/>. Es la que corre
    /// hoy: no hay backend remoto, todo vive en SQLite. Cuando exista el proyecto de
    /// Supabase, se implementa SupabaseSyncService y se cambia una línea en el DI.
    /// El proveedor declarado (sección "Database:Provider" de appsettings) se recibe
    /// por constructor para no acoplar Infrastructure a Microsoft.Extensions.Configuration.
    /// </summary>
    public class LocalOnlySyncService : IRemoteSyncService
    {
        private readonly string _provider;

        public LocalOnlySyncService(string? configuredProvider)
        {
            _provider = string.IsNullOrWhiteSpace(configuredProvider) ? "sqlite" : configuredProvider;
        }

        public bool IsRemoteConfigured =>
            !string.IsNullOrWhiteSpace(_provider) &&
            !_provider.Equals("sqlite", System.StringComparison.OrdinalIgnoreCase);

        public Task<RemoteConnectionStatus> TestConnectionAsync()
        {
            if (!IsRemoteConfigured)
            {
                return Task.FromResult(new RemoteConnectionStatus(
                    IsConfigured: false,
                    IsReachable: false,
                    Message: "Sin backend remoto configurado. La aplicación opera con la base de datos local SQLite."));
            }

            // Provider remoto declarado en config pero sin implementación disponible todavía.
            return Task.FromResult(new RemoteConnectionStatus(
                IsConfigured: true,
                IsReachable: false,
                Message: $"El proveedor '{_provider}' está declarado en la configuración, " +
                         "pero esta versión no incluye el cliente remoto. Se usa SQLite local."));
        }

        public Task PushPendingChangesAsync()
        {
            // No-op: no hay servidor al cual empujar cambios.
            AppLog.Information("PushPendingChangesAsync: sin backend remoto, no hay nada que sincronizar");
            return Task.CompletedTask;
        }
    }
}
