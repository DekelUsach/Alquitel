using System;
using System.Security.Cryptography;
using System.Text;

namespace Alquitel.Core.Helpers
{
    /// <summary>
    /// Hashing de contraseñas con PBKDF2 (Rfc2898). Formato del hash persistido:
    /// "iteraciones.saltBase64.hashBase64". Nunca se guarda texto plano.
    /// </summary>
    public static class PasswordHasher
    {
        public const int CurrentIterations = 210_000;

        /// <summary>Longitud mínima exigida al definir una contraseña nueva.</summary>
        public const int MinPasswordLength = 8;

        // Cotas defensivas sobre el conteo de iteraciones LEÍDO de la base: un valor
        // absurdo (negativo, 0, o 10^9) venía de una fila corrupta o manipulada y
        // reventaba Pbkdf2 con una excepción no capturada o colgaba el login.
        private const int MinAcceptedIterations = 1_000;
        private const int MaxAcceptedIterations = 2_000_000;

        private const int SaltSize = 16;
        private const int HashSize = 32;

        public static string Hash(string password)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));

            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, CurrentIterations, HashAlgorithmName.SHA256, HashSize);
            return $"{CurrentIterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        }

        public static bool Verify(string password, string? storedHash)
        {
            if (password == null) return false;
            if (!TryParse(storedHash, out int iterations, out byte[] salt, out byte[] expected))
                return false;

            try
            {
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (Exception)
            {
                // Un hash almacenado corrupto es "contraseña incorrecta", nunca un crash
                // del login (que dejaría a todo el equipo sin poder entrar a la app).
                return false;
            }
        }

        /// <summary>
        /// True cuando el hash guardado es válido pero usa menos iteraciones que las
        /// vigentes: el llamador puede re-hashear con la contraseña ya verificada.
        /// </summary>
        public static bool NeedsRehash(string? storedHash)
        {
            if (!TryParse(storedHash, out int iterations, out _, out _)) return false;
            return iterations < CurrentIterations;
        }

        /// <summary>
        /// Huella pública y estable de un hash almacenado, para atar una sesión guardada
        /// a la contraseña vigente sin copiar el hash a disco: cambiar la contraseña
        /// cambia la huella e invalida las sesiones persistidas.
        /// </summary>
        public static string Fingerprint(string? storedHash)
        {
            var material = storedHash ?? string.Empty;
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("alquitel-session-v1|" + material));
            return Convert.ToHexString(bytes);
        }

        /// <summary>
        /// Valida una contraseña NUEVA. Devuelve null si es aceptable, o el motivo del
        /// rechazo listo para mostrar al usuario.
        /// </summary>
        public static string? ValidateNewPassword(string? password)
        {
            if (string.IsNullOrWhiteSpace(password))
                return "La contraseña no puede estar vacía.";
            if (password.Length < MinPasswordLength)
                return $"La contraseña debe tener al menos {MinPasswordLength} caracteres.";
            if (password.Trim().Length != password.Length)
                return "La contraseña no puede empezar ni terminar con espacios.";
            return null;
        }

        private static bool TryParse(string? storedHash, out int iterations, out byte[] salt, out byte[] expected)
        {
            iterations = 0;
            salt = Array.Empty<byte>();
            expected = Array.Empty<byte>();

            if (string.IsNullOrWhiteSpace(storedHash)) return false;

            var parts = storedHash.Split('.');
            if (parts.Length != 3) return false;

            // int.Parse lanzaba OverflowException (no capturada) ante "99999999999999".
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out iterations))
                return false;
            if (iterations < MinAcceptedIterations || iterations > MaxAcceptedIterations)
                return false;

            try
            {
                salt = Convert.FromBase64String(parts[1]);
                expected = Convert.FromBase64String(parts[2]);
            }
            catch (FormatException)
            {
                return false;
            }

            return salt.Length >= 8 && expected.Length >= 16;
        }
    }
}
