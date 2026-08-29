using System;

namespace Alquitel.Core.Helpers
{
    /// <summary>
    /// Decisión de reintento para las llamadas HTTP salientes (hoy: Pollinations).
    /// Vive en Core y sin dependencias de red para poder testear la política sin
    /// levantar un servidor: qué códigos se reintentan y cuánto se espera.
    /// </summary>
    public static class HttpRetryPolicy
    {
        /// <summary>Intentos totales por modelo (1 original + 2 reintentos).</summary>
        public const int MaxAttempts = 3;

        public static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(600);
        public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(20);

        /// <summary>
        /// True para fallos que suelen resolverse solos: rate limit, cortes del gateway
        /// y timeouts. Un 400/401/403 es configuración mal puesta y reintentarlo solo
        /// quema tiempo del usuario esperando el armador.
        /// </summary>
        public static bool IsTransient(int statusCode) =>
            statusCode == 408 ||
            statusCode == 425 ||
            statusCode == 429 ||
            (statusCode >= 500 && statusCode <= 599);

        /// <summary>
        /// Espera antes del reintento número <paramref name="attempt"/> (1-based: el
        /// primer reintento es attempt=1). Si el servidor mandó Retry-After se respeta ese
        /// valor, acotado a <see cref="MaxDelay"/>.
        /// </summary>
        public static TimeSpan DelayFor(int attempt, TimeSpan? retryAfter = null)
        {
            if (retryAfter is TimeSpan ra && ra > TimeSpan.Zero)
                return ra > MaxDelay ? MaxDelay : ra;

            if (attempt < 1) attempt = 1;
            if (attempt > 10) return MaxDelay;

            var delay = TimeSpan.FromMilliseconds(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
            return delay > MaxDelay ? MaxDelay : delay;
        }
    }
}
