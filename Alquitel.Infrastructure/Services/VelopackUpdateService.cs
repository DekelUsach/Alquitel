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
        private readonly string? _githubRepoUrl;

        public string? CurrentVersion { get; }

        public VelopackUpdateService(string? githubRepoUrl)
        {
            _githubRepoUrl = githubRepoUrl;
            CurrentVersion = Assembly.GetEntryAssembly()
                ?.GetName().Version?.ToString(3);
        }

        public bool IsUpdateCheckEnabled => !string.IsNullOrWhiteSpace(_githubRepoUrl);

        public async Task<bool> CheckAndApplyUpdatesOnStartupAsync(Action<string> onStatusChanged)
        {
            if (!IsUpdateCheckEnabled)
            {
                AppLog.Information("Update check skipped on startup — GithubRepoUrl not configured.");
                return false;
            }

            try
            {
                onStatusChanged("Buscando actualizaciones...");

                var source = new GithubSource(_githubRepoUrl!, string.Empty, prerelease: false);
                var mgr = new UpdateManager(source);

                // Timeout de 4 segundos para el chequeo de actualizaciones (evita quedarse colgado si no hay internet o es muy lento)
                var checkTask = mgr.CheckForUpdatesAsync();
                var timeoutTask = Task.Delay(4000);

                var completedTask = await Task.WhenAny(checkTask, timeoutTask).ConfigureAwait(false);
                if (completedTask == timeoutTask)
                {
                    AppLog.Warning("Update check timed out after 4 seconds.");
                    return false;
                }

                var update = await checkTask.ConfigureAwait(false);
                if (update == null)
                {
                    AppLog.Information("No updates available on startup.");
                    return false;
                }

                AppLog.Information("Update found on startup: {Version}. Downloading…",
                    update.TargetFullRelease.Version);
                onStatusChanged($"Nueva versión encontrada ({update.TargetFullRelease.Version}).\nDescargando actualización...");

                await mgr.DownloadUpdatesAsync(update).ConfigureAwait(false);

                AppLog.Information("Update downloaded on startup. Applying and restarting.");
                onStatusChanged("Instalando y reiniciando aplicación...");

                // Dar un breve momento para que se lea el mensaje antes de reiniciar
                await Task.Delay(1000).ConfigureAwait(false);

                mgr.ApplyUpdatesAndRestart(update);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Update check failed on startup.");
                return false;
            }
        }

        public async Task CheckAndApplyUpdatesAsync()
        {
            if (!IsUpdateCheckEnabled)
            {
                AppLog.Information("Update check skipped — GithubRepoUrl not configured.");
                return;
            }

            try
            {
                // Repo público: sin accessToken (empty) alcanza — GitHub permite 60
                // requests/hora sin auth por IP, de sobra para chequeos de update.
                var source = new GithubSource(_githubRepoUrl!, string.Empty, prerelease: false);
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
