using System.Threading;
using System.Threading.Tasks;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Asistente de texto de propósito general sobre la misma integración barata de IA
    /// del pedido automático (Pollinations). Base de los quick-wins: notas técnicas de
    /// OT, resumen del historial de un cliente y detección de datos de contacto.
    /// </summary>
    public interface IAiTextAssistant
    {
        bool IsConfigured { get; }

        /// <summary>Respuesta en texto plano; null si la IA no está configurada o falla.</summary>
        Task<string?> CompleteAsync(string systemPrompt, string userText, CancellationToken ct = default);

        /// <summary>
        /// Igual que <see cref="CompleteAsync"/> pero devuelve solo el primer objeto JSON
        /// balanceado de la respuesta (los modelos suelen envolverlo en ```json ... ```).
        /// </summary>
        Task<string?> CompleteToJsonAsync(string systemPrompt, string userText, CancellationToken ct = default);
    }
}
