using System;
using System.Collections.Generic;

namespace Alquitel.Core.Security
{
    /// <summary>
    /// Freno de fuerza bruta del login. Sin esto, probar contraseñas contra el
    /// <c>LoginWindow</c> costaba solo el tiempo de un PBKDF2 y podía automatizarse.
    ///
    /// Política: los primeros <see cref="FreeAttempts"/> fallos no penalizan; a partir
    /// de ahí el bloqueo crece exponencialmente (2s, 4s, 8s…) hasta
    /// <see cref="MaxLockout"/>. Los fallos se olvidan tras <see cref="FailureWindow"/>
    /// sin intentos, para no castigar a alguien que se equivocó ayer.
    ///
    /// Lógica pura y determinística (el reloj entra por parámetro) para poder testearla.
    /// </summary>
    public sealed class LoginThrottle
    {
        public const int FreeAttempts = 3;
        public static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

        private sealed class Entry
        {
            public int Failures;
            public DateTimeOffset LastFailureUtc;
        }

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        /// <summary>Registra un intento fallido y devuelve el bloqueo resultante.</summary>
        public TimeSpan RegisterFailure(string key, DateTimeOffset nowUtc)
        {
            if (string.IsNullOrEmpty(key)) return TimeSpan.Zero;

            if (!_entries.TryGetValue(key, out var entry) || Expired(entry, nowUtc))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Failures++;
            entry.LastFailureUtc = nowUtc;
            return LockoutFor(entry.Failures);
        }

        /// <summary>Limpia el historial de fallos de una cuenta (login exitoso).</summary>
        public void Reset(string key)
        {
            if (!string.IsNullOrEmpty(key)) _entries.Remove(key);
        }

        /// <summary>
        /// Tiempo que falta antes de poder reintentar. <see cref="TimeSpan.Zero"/>
        /// cuando la cuenta no está bloqueada.
        /// </summary>
        public TimeSpan GetRemainingLockout(string key, DateTimeOffset nowUtc)
        {
            if (string.IsNullOrEmpty(key) || !_entries.TryGetValue(key, out var entry)) return TimeSpan.Zero;
            if (Expired(entry, nowUtc))
            {
                _entries.Remove(key);
                return TimeSpan.Zero;
            }

            var lockout = LockoutFor(entry.Failures);
            if (lockout <= TimeSpan.Zero) return TimeSpan.Zero;

            var elapsed = nowUtc - entry.LastFailureUtc;
            var remaining = lockout - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        public int FailureCount(string key, DateTimeOffset nowUtc) =>
            !string.IsNullOrEmpty(key) && _entries.TryGetValue(key, out var e) && !Expired(e, nowUtc)
                ? e.Failures
                : 0;

        private static bool Expired(Entry entry, DateTimeOffset nowUtc) =>
            nowUtc - entry.LastFailureUtc > FailureWindow;

        internal static TimeSpan LockoutFor(int failures)
        {
            if (failures <= FreeAttempts) return TimeSpan.Zero;

            int exponent = failures - FreeAttempts; // 1, 2, 3…
            // Cap del exponente antes de la potencia: 2^60 segundos desborda el double
            // en TimeSpan y devolvía un valor sin sentido tras muchos intentos.
            if (exponent > 20) return MaxLockout;

            var seconds = Math.Pow(2, exponent);
            var lockout = TimeSpan.FromSeconds(seconds);
            return lockout > MaxLockout ? MaxLockout : lockout;
        }
    }
}
