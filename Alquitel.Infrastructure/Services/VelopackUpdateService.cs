using System;
using System.Reflection;
using System.Threading.Tasks;
using Alquitel.Core.Interfaces;
using Velopack;
using Velopack.Sources;

namespace Alquitel.Infrastructure.Services
{
    public sealed class VelopackUpdateService : IUpdateService
    {
        private readonly string? _updateUrl;

        public string? CurrentVersion { get; }

        public VelopackUpdateService(string? updateUrl)
        {
            _updateUrl = updateUrl;
            CurrentVersion = Assembly.GetEntryAssembly()
                ?.GetName().Version?.ToString(3);
        }

        public async Task CheckAndApplyUpdatesAsync()
        {
            if (string.IsNullOrWhiteSpace(_updateUrl))
            {
                AppLog.Information("Update check skipped — UpdateUrl not configured.");
                return;
            }

            try
            {
                var source = new SimpleWebSource(_updateUrl);
                var mgr = new UpdateManager(source);

                var update = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
                if (update == null)
                {
                    AppLog.Information("No updates available.");
                    return;
                }

                AppLog.Information("Update found: {Version}. Downloading…",
                    update.TargetFullRelease.Version);

                await mgr.DownloadUpdatesAsync(update).ConfigureAwait(false);

                // La actualización se aplica cuando el usuario cierra la app: reiniciar
                // acá en caliente le cortaba la sesión en medio de un presupuesto.
                AppLog.Information("Update downloaded. Applying on next restart.");
                mgr.WaitExitThenApplyUpdates(update);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Update check failed — will retry next launch.");
            }
        }
    }
}
