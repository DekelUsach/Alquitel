using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Alquitel.Core.Security
{
    /// <summary>
    /// Sobre autenticado de la sesión persistida ("recordarme").
    ///
    /// El archivo de sesión anterior era un JSON plano con el Guid del usuario: editarlo
    /// a mano y poner el Guid de un Admin salteaba el login con contraseña por completo
    /// (escalada de privilegios local). Ahora el archivo lleva un MAC HMAC-SHA256 sobre
    /// (versión, usuario, huella de la contraseña, fecha de emisión) con una clave que
    /// vive fuera del archivo, así que un payload editado no valida.
    ///
    /// Atar el sobre a <see cref="Helpers.PasswordHasher.Fingerprint"/> hace que cambiar
    /// la contraseña de un usuario invalide todas sus sesiones guardadas.
    /// </summary>
    public static class SessionToken
    {
        public const int Version = 1;

        /// <summary>Tamaño en bytes de la clave de firma que debe generar la infraestructura.</summary>
        public const int KeySize = 32;

        /// <summary>Tope duro de vida de una sesión guardada, sin importar el rol.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

        /// <summary>Tope de vida para usuarios Admin (acceso a costos y facturación).</summary>
        public static readonly TimeSpan AdminMaxAge = TimeSpan.FromDays(30);

        public static string Issue(Guid userId, string? passwordFingerprint, DateTimeOffset savedAtUtc, byte[] key)
        {
            if (key == null || key.Length < 16) throw new ArgumentException("Clave de sesión inválida.", nameof(key));

            string payload = BuildPayload(userId, passwordFingerprint, savedAtUtc);
            return payload + "." + Convert.ToBase64String(Mac(payload, key));
        }

        /// <summary>
        /// Valida firma, versión y antigüedad del sobre. Devuelve false ante cualquier
        /// anomalía: token ausente, MAC que no cierra, formato raro o sesión vencida.
        /// </summary>
        public static bool TryValidate(
            string? token,
            byte[] key,
            DateTimeOffset nowUtc,
            TimeSpan maxAge,
            out Guid userId,
            out string passwordFingerprint,
            out DateTimeOffset savedAtUtc)
        {
            userId = Guid.Empty;
            passwordFingerprint = string.Empty;
            savedAtUtc = default;

            if (string.IsNullOrWhiteSpace(token) || key == null || key.Length < 16) return false;

            int lastDot = token.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == token.Length - 1) return false;

            string payload = token[..lastDot];
            byte[] presented;
            try { presented = Convert.FromBase64String(token[(lastDot + 1)..]); }
            catch (FormatException) { return false; }

            var expected = Mac(payload, key);
            if (!CryptographicOperations.FixedTimeEquals(presented, expected)) return false;

            var parts = payload.Split('|');
            if (parts.Length != 4) return false;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version != Version)
                return false;
            if (!Guid.TryParseExact(parts[1], "D", out userId)) { userId = Guid.Empty; return false; }
            if (!long.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long unix))
                return false;

            passwordFingerprint = parts[2];
            savedAtUtc = DateTimeOffset.FromUnixTimeSeconds(unix);

            // Una sesión "del futuro" es reloj corrido o manipulación: no se acepta.
            if (savedAtUtc > nowUtc.AddMinutes(5)) return false;

            var effectiveMaxAge = maxAge > MaxAge || maxAge <= TimeSpan.Zero ? MaxAge : maxAge;
            if (nowUtc - savedAtUtc > effectiveMaxAge) return false;

            return true;
        }

        private static string BuildPayload(Guid userId, string? fingerprint, DateTimeOffset savedAtUtc)
        {
            // El '|' no puede aparecer en ninguno de los campos (Guid "D" y huella hex),
            // así que el split de vuelta es inequívoco.
            var clean = (fingerprint ?? string.Empty).Replace("|", string.Empty).Replace(".", string.Empty);
            return string.Create(CultureInfo.InvariantCulture,
                $"{Version}|{userId:D}|{clean}|{savedAtUtc.ToUnixTimeSeconds()}");
        }

        private static byte[] Mac(string payload, byte[] key) =>
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
    }
}
