using System.Threading.Tasks;

namespace Alquitel.Core.Interfaces
{
    public interface IUpdateService
    {
        /// <summary>
        /// Checks for available updates and applies them if found.
        /// Runs silently — logs results but never blocks the UI.
        /// </summary>
        Task CheckAndApplyUpdatesAsync();

        /// <summary>
        /// Checks for updates synchronously on startup, notifies status changes,
        /// and applies/restarts immediately if an update is found.
        /// Returns true if an update was applied and the app is restarting.
        /// </summary>
        Task<bool> CheckAndApplyUpdatesOnStartupAsync(System.Action<string> onStatusChanged);

        string? CurrentVersion { get; }
    }
}
