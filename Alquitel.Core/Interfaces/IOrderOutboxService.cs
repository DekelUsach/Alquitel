using System.Threading.Tasks;
using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Cola de salida para órdenes que no pudieron persistirse (típicamente por caída de
    /// internet en modo servidor/Supabase). El documento ya se generó y el usuario siguió
    /// trabajando: la orden queda encolada en disco y se reintenta en segundo plano hasta
    /// que la base vuelva a responder.
    /// </summary>
    public interface IOrderOutboxService
    {
        /// <summary>Encola la orden (con sus ítems) para reintento. Nunca lanza.</summary>
        void Enqueue(Order order);

        /// <summary>Cantidad de órdenes pendientes de subir.</summary>
        int PendingCount { get; }

        /// <summary>
        /// Reintenta persistir todo lo encolado. Devuelve cuántas órdenes se guardaron.
        /// Lo llama el timer interno; también puede invocarse manualmente.
        /// </summary>
        Task<int> RetryPendingAsync();
    }
}
