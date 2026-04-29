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

        string? CurrentVersion { get; }
    }
}
